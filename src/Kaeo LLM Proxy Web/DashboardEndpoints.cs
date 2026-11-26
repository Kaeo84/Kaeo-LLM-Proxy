using System.Globalization;
using System.Security.Claims;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Maps the dashboard's JSON Minimal API endpoints. All routes are under <c>/api</c> and guarded
/// by the auth middleware in <see cref="WebDashboardHost"/>. No Razor, no postbacks — every
/// response is JSON consumed by the jQuery/AJAX front end (Phase 4).
/// </summary>
internal static partial class DashboardEndpoints
{
    // Serializes admin mutations (mappings/credentials/settings) against each other. The proxy
    // reads these collections on its own path; dashboard edits are infrequent control-plane ops.
    private static readonly object Sync = new();

    public static void MapAll(WebApplication app, AppSettings settings, ProxyHostController controller)
    {
        MapAuthentication(app, settings, controller);
        app.MapGet("/api/auth/me", (HttpContext context) => Results.Json(new
        {
            role = context.Items[DashboardAuth.RoleItemKey]?.ToString() ?? DashboardAuth.AdminRole,
            username = context.Items[DashboardAuth.UserNameItemKey]?.ToString(),
            loginRequired = settings.WebDashboard.RequireLogin,
        }));
        MapStatus(app, settings, controller);
        MapProxy(app, controller);
        MapLogs(app, controller);
        MapObservability(app, controller);
        MapUsage(app, controller);
        MapStats(app, controller);
        MapMcp(app, controller);
        MapSettings(app, settings, controller);
        MapMappings(app, controller);
        MapCredentials(app, controller);
        MapModules(app, controller);
        MapUsers(app, controller);
    }

    private static void MapStatus(WebApplication app, AppSettings settings, ProxyHostController controller)
    {
        app.MapGet("/api/status", () => Results.Json(new
        {
            version = typeof(AppSettings).Assembly.GetName().Version?.ToString(),
            proxy = new
            {
                isRunning = controller.Server.IsRunning,
                listenAddress = controller.Server.ListenAddress,
                listenPort = controller.Server.ListenPort,
                // StartProxy never throws; a failed bind surfaces here so the dashboard can show it.
                error = controller.LastStartError,
            },
            mcp = new
            {
                isRunning = controller.McpServer.IsRunning,
                status = controller.McpServer.Status,
                endpointUrl = controller.McpServer.EndpointUrl,
            },
            modules = controller.ModuleHost.GetRegistryEntries().Select(e => new
            {
                id = e.Id,
                name = e.Name,
                isEnabled = e.IsEnabled,
                isRunning = RunningState(controller, e.Id),
            }).ToList(),
            dashboard = new
            {
                authMode = settings.WebDashboard.RequireLogin ? "login" : "open",
                scheme = settings.WebDashboard.Scheme,
            },
            // Which database this instance is actually writing to. Each build output resolves its
            // own file, so two launches (or a portable copy) can silently diverge — surfacing the
            // path here makes that mismatch visible instead of looking like lost edits.
            databasePath = controller.Database.DatabasePath,
            databaseDegraded = controller.Database.IsDegraded,
        }));
    }

    private static void MapProxy(WebApplication app, ProxyHostController controller)
    {
        app.MapPost("/api/proxy/start", () =>
        {
            controller.StartProxy();
            return Results.Json(new { isRunning = controller.Server.IsRunning, error = controller.LastStartError });
        });

        app.MapPost("/api/proxy/stop", async () =>
        {
            await controller.Server.StopAsync();
            return Results.Json(new { isRunning = controller.Server.IsRunning, error = (string?)null });
        });

        // Stop-then-start on the configured endpoint. The dashboard runs on its own port, so
        // restarting the proxy listener never interrupts the page that asked for it.
        app.MapPost("/api/proxy/restart", async () =>
        {
            await controller.RestartProxyAsync();
            return Results.Json(new { isRunning = controller.Server.IsRunning, error = controller.LastStartError });
        });
    }

    private static void MapLogs(WebApplication app, ProxyHostController controller)
    {
        // Two modes sharing a timestamp-ticks cursor (RequestLog has no numeric id):
        //  - `after` omitted: fresh load — the newest `limit` entries straight from the database
        //    (the full retention window, not just the in-memory queue), newest first.
        //  - `after` supplied: incremental poll of the in-memory window — strictly newer entries,
        //    oldest first.
        // Read Only sessions only ever see requests attributed to their own user (via API key).
        app.MapGet("/api/logs", (HttpContext context, string? source, long? after, int? limit) =>
        {
            string role = context.Items[DashboardAuth.RoleItemKey]?.ToString() ?? DashboardAuth.AdminRole;
            int? callerId = int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int parsedId)
                ? parsedId
                : null;
            if (role == DashboardAuth.ReadOnlyRole && callerId is null)
                return Results.Json(new { error = "Sign in with a dashboard username and password." }, statusCode: StatusCodes.Status401Unauthorized);
            int? filterUserId = role == DashboardAuth.ReadOnlyRole ? callerId : null;

            int take = Math.Clamp(limit ?? 100, 1, 1000);

            if (after is null)
            {
                try
                {
                    IReadOnlyList<RequestLog> recent = controller.Database.QueryRecent(take, LogSourceFor(source), filterUserId);
                    Dictionary<int, string> aliases = BuildRequestorAliases(controller.Database.LoadProxyUsers());
                    foreach (RequestLog entry in recent)
                        AttachRequestor(entry, aliases);
                    long newest = recent.Count > 0 ? recent[0].Timestamp.Ticks : 0;
                    return Results.Json(new { entries = recent, nextAfter = newest });
                }
                catch (Exception ex) when (ex is IOException or SqliteException)
                {
                    Serilog.Log.Warning(ex, "Log query failed");
                    return Results.Json(new { error = "Log data is temporarily unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            StatisticsService stats = StatsForSource(controller, source);
            long cursor = after.Value;
            List<RequestLog> entries = stats.GetRecentLogs()
                .Where(e => e.Timestamp.Ticks > cursor && (filterUserId is null || e.UserId == filterUserId))
                .OrderBy(e => e.Timestamp.Ticks)
                .ThenBy(e => e.RequestId, StringComparer.Ordinal)
                .Take(take)
                .ToList();

            Dictionary<int, string> requestorAliases = BuildRequestorAliases(controller.Database.LoadProxyUsers());
            foreach (RequestLog entry in entries)
                AttachRequestor(entry, requestorAliases);

            long next = entries.Count > 0 ? entries[^1].Timestamp.Ticks : cursor;
            return Results.Json(new { entries, nextAfter = next });
        });

        // Full bodies/headers for the detail view, loaded on demand from the store. Read Only
        // callers get 404 for entries that are not theirs (no existence leak).
        app.MapGet("/api/logs/detail", (HttpContext context, string? source, string? timestamp) =>
        {
            if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
                return Results.Json(new { error = "timestamp must be an ISO 8601 date/time." }, statusCode: StatusCodes.Status400BadRequest);

            string role = context.Items[DashboardAuth.RoleItemKey]?.ToString() ?? DashboardAuth.AdminRole;
            int? callerId = int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int parsedId)
                ? parsedId
                : null;
            if (role == DashboardAuth.ReadOnlyRole && callerId is null)
                return Results.Json(new { error = "Sign in with a dashboard username and password." }, statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                RequestLog? full = controller.Database.LoadFullLogEntry(parsed.ToLocalTime(), LogSourceFor(source));
                if (full is null || (role == DashboardAuth.ReadOnlyRole && full.UserId != callerId))
                    return Results.Json(new { error = "Log entry not found." }, statusCode: StatusCodes.Status404NotFound);
                AttachRequestor(full, BuildRequestorAliases(controller.Database.LoadProxyUsers()));
                return Results.Json(full);
            }
            catch (Exception ex) when (ex is IOException or SqliteException)
            {
                Serilog.Log.Warning(ex, "Log detail query failed");
                return Results.Json(new { error = "Log data is temporarily unavailable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static LogSource LogSourceFor(string? source) => source?.ToLowerInvariant() switch
    {
        "mcp" => LogSource.Mcp,
        "nonproxied" or "non-proxied" => LogSource.NonProxied,
        _ => LogSource.Proxy,
    };

    /// <summary>User id → the local part of their email address (the reference shown in the
    /// Requests view). Users without an email are omitted, so their entries show blank.</summary>
    private static Dictionary<int, string> BuildRequestorAliases(IEnumerable<ProxyUser> users)
    {
        Dictionary<int, string> aliases = [];
        foreach (ProxyUser user in users)
        {
            string? alias = EmailLocalPart(user.Email);
            if (alias is not null)
                aliases[user.Id] = alias;
        }
        return aliases;
    }

    private static string? EmailLocalPart(string? email)
    {
        string value = email?.Trim() ?? string.Empty;
        int at = value.IndexOf('@');
        string local = at >= 0 ? value[..at] : value;
        return local.Length == 0 ? null : local;
    }

    private static void AttachRequestor(RequestLog entry, IReadOnlyDictionary<int, string> aliases)
    {
        entry.Requestor = entry.UserId is int userId && aliases.TryGetValue(userId, out string? alias)
            ? alias
            : null;
    }

    private static void MapStats(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/stats", () => Results.Json(new
        {
            proxy = SourceStats(controller.Stats),
            mcp = SourceStats(controller.McpStats),
            nonProxied = SourceStats(controller.NonProxiedStats),
        }));
    }

    private static void MapMcp(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/mcp", () =>
        {
            McpServerService mcp = controller.McpServer;
            McpServerSettings s = mcp.LoadSettings();
            return Results.Json(new
            {
                isRunning = mcp.IsRunning,
                status = mcp.Status,
                listenAddress = mcp.ListenAddress,
                listenPort = mcp.ListenPort,
                endpointUrl = mcp.EndpointUrl,
                settings = new
                {
                    enabled = s.Enabled,
                    listenAddress = s.ListenAddress,
                    listenPort = s.ListenPort,
                    enableApiExplorer = s.EnableApiExplorer,
                    authCredentialName = s.AuthCredentialName,
                    collectRequestDetails = s.CollectRequestDetails,
                    collectResponseDetails = s.CollectResponseDetails,
                },
                toolModules = controller.ModuleHost.LoadedModules
                    .Where(l => l.Module is IMcpToolModule)
                    .Select(l => new { id = l.Entry.Id, name = l.Entry.Name })
                    .ToList(),
            });
        });

        // The dashboard and the MCP server live in the same process, so these drive the exact
        // instance the proxy exposes. Start forces the listener up even when the persisted Enabled
        // flag is off — an explicit Start click is a stronger signal than the saved default.
        app.MapPost("/api/mcp/start", async () =>
        {
            await controller.McpServer.StartAsync(forceStart: true);
            return Results.Json(McpState(controller));
        });

        app.MapPost("/api/mcp/stop", async () =>
        {
            await controller.McpServer.StopAsync();
            return Results.Json(McpState(controller));
        });

        app.MapPost("/api/mcp/restart", async () =>
        {
            await controller.McpServer.RestartAsync();
            return Results.Json(McpState(controller));
        });
    }

    private static object McpState(ProxyHostController controller) => new
    {
        isRunning = controller.McpServer.IsRunning,
        status = controller.McpServer.Status,
        endpointUrl = controller.McpServer.EndpointUrl,
    };

    private static object SourceStats(StatisticsService stats) => new
    {
        totalRequests = stats.TotalRequests,
        totalErrors = stats.TotalErrors,
        totalPromptTokens = stats.TotalPromptTokens,
        totalCompletionTokens = stats.TotalCompletionTokens,
        requestsPerSecond = stats.RequestsPerSecond,
    };

    private static StatisticsService StatsForSource(ProxyHostController controller, string? source)
        => source?.ToLowerInvariant() switch
        {
            "mcp" => controller.McpStats,
            "nonproxied" or "non-proxied" => controller.NonProxiedStats,
            _ => controller.Stats,
        };

    private static bool RunningState(ProxyHostController controller, int id)
        => controller.ModuleHost.LoadedModules.FirstOrDefault(l => l.Entry.Id == id)?.Module is IRunnableModule r && r.IsRunning;
}