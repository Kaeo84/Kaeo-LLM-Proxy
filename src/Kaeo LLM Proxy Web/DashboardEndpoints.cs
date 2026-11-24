using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
        MapStatus(app, settings, controller);
        MapProxy(app, controller);
        MapLogs(app, controller);
        MapStats(app, controller);
        MapMcp(app, controller);
        MapSettings(app, settings);
        MapMappings(app, controller);
        MapCredentials(app, controller);
        MapModules(app, controller);
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
                authMode = DashboardAuth.AuthMode(settings.WebDashboard),
                scheme = settings.WebDashboard.Scheme,
            },
        }));
    }

    private static void MapProxy(WebApplication app, ProxyHostController controller)
    {
        app.MapPost("/api/proxy/start", () =>
        {
            controller.StartProxy();
            return Results.Json(new { isRunning = controller.Server.IsRunning });
        });

        app.MapPost("/api/proxy/stop", async () =>
        {
            await controller.Server.StopAsync();
            return Results.Json(new { isRunning = controller.Server.IsRunning });
        });
    }

    private static void MapLogs(WebApplication app, ProxyHostController controller)
    {
        // Incremental poll. `after` is a timestamp-ticks cursor; entries strictly newer than it are
        // returned oldest-first. RequestLog has no numeric id, so ticks are the cursor (documented).
        app.MapGet("/api/logs", (string? source, long? after, int? limit) =>
        {
            StatisticsService stats = StatsForSource(controller, source);
            int take = Math.Clamp(limit ?? 100, 1, 1000);
            long cursor = after ?? 0;

            List<RequestLog> entries = stats.GetRecentLogs()
                .Where(e => e.Timestamp.Ticks > cursor)
                .OrderBy(e => e.Timestamp.Ticks)
                .ThenBy(e => e.RequestId, StringComparer.Ordinal)
                .Take(take)
                .ToList();

            long next = entries.Count > 0 ? entries[^1].Timestamp.Ticks : cursor;
            return Results.Json(new { entries, nextAfter = next });
        });
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
    }

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