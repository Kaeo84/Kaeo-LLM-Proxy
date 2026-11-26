using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Users tab API: proxy users (name/email contact records) and their API keys. Key material is
/// write-only — the plaintext is returned exactly once, by the create-key response; every other
/// surface exposes only the non-secret prefix. Hashes never leave the database layer.
/// </summary>
internal static partial class DashboardEndpoints
{
    private static void MapUsers(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/users", () =>
        {
            try
            {
                List<ProxyUser> users = [.. controller.Database.LoadProxyUsers()];
                List<ProxyApiKey> keys = [.. controller.Database.LoadProxyApiKeys()];
                return Results.Json(users.Select(u => UserView(u, keys.Where(k => k.UserId == u.Id))).ToList());
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapPost("/api/users", (UserWrite write) =>
        {
            if (string.IsNullOrWhiteSpace(write.Name))
                return Results.BadRequest(new { error = "User name is required." });
            if (!string.IsNullOrWhiteSpace(write.Email) && !write.Email.Contains('@'))
                return Results.BadRequest(new { error = "Email address looks invalid." });

            string? dashboardRole = DashboardAuth.NormalizeRole(write.DashboardRole ?? DashboardAuth.ReadOnlyRole);
            if (dashboardRole is null)
                return Results.BadRequest(new { error = "Dashboard role must be Admin or Read Only." });

            ProxyUser user = new()
            {
                Name = write.Name.Trim(),
                Email = write.Email?.Trim() ?? string.Empty,
                CreatedUtc = DateTime.UtcNow,
                IsEnabled = write.IsEnabled ?? true,
                DashboardLoginEnabled = write.DashboardLoginEnabled ?? false,
                DashboardUsername = write.DashboardUsername?.Trim() ?? string.Empty,
                DashboardRole = dashboardRole,
            };
            if (!TryConfigureDashboardPassword(user, write.DashboardPassword, out string? credentialError))
                return Results.BadRequest(new { error = credentialError });

            try
            {
                lock (Sync)
                {
                    if (!ValidateDashboardLogin(user, controller.Database.LoadProxyUsers(), null, out credentialError))
                        return Results.BadRequest(new { error = credentialError });
                    controller.Database.InsertProxyUser(user);
                    controller.Access.InvalidateCache();
                }

                return Results.Json(UserView(user, []));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Conflict(new { error = "That dashboard username is already in use." });
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapPut("/api/users/{id:int}", (int id, UserWrite write) =>
        {
            if (write.Name is not null && string.IsNullOrWhiteSpace(write.Name))
                return Results.BadRequest(new { error = "User name cannot be empty." });
            if (!string.IsNullOrWhiteSpace(write.Email) && !write.Email.Contains('@'))
                return Results.BadRequest(new { error = "Email address looks invalid." });

            try
            {
                lock (Sync)
                {
                    ProxyUser? user = controller.Database.FindProxyUser(id);
                    if (user is null)
                        return Results.NotFound(new { error = $"User {id} not found." });

                    if (write.Name is not null) user.Name = write.Name.Trim();
                    if (write.Email is not null) user.Email = write.Email.Trim();
                    if (write.IsEnabled is { } enabled) user.IsEnabled = enabled;
                    if (write.DashboardUsername is not null) user.DashboardUsername = write.DashboardUsername.Trim();
                    if (write.DashboardLoginEnabled is { } loginEnabled) user.DashboardLoginEnabled = loginEnabled;
                    if (write.DashboardRole is not null)
                    {
                        string? dashboardRole = DashboardAuth.NormalizeRole(write.DashboardRole);
                        if (dashboardRole is null)
                            return Results.BadRequest(new { error = "Dashboard role must be Admin or Read Only." });
                        user.DashboardRole = dashboardRole;
                    }
                    if (!TryConfigureDashboardPassword(user, write.DashboardPassword, out string? credentialError))
                        return Results.BadRequest(new { error = credentialError });
                    if (!ValidateDashboardLogin(user, controller.Database.LoadProxyUsers(), id, out credentialError))
                        return Results.BadRequest(new { error = credentialError });

                    controller.Database.UpdateProxyUser(user);
                    controller.Access.InvalidateCache();
                    return Results.Json(UserView(user, controller.Database.LoadProxyApiKeys().Where(k => k.UserId == id)));
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Conflict(new { error = "That dashboard username is already in use." });
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapDelete("/api/users/{id:int}", (int id) =>
        {
            try
            {
                lock (Sync)
                {
                    if (controller.Database.FindProxyUser(id) is null)
                        return Results.NotFound(new { error = $"User {id} not found." });

                    controller.Database.DeleteProxyUser(id);
                    controller.Access.InvalidateCache();
                }
                return Results.Ok();
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        // Creates a key and returns the plaintext ONCE. There is no endpoint that can reveal it
        // again — a lost key is deleted and replaced.
        app.MapPost("/api/users/{id:int}/keys", (int id, KeyWrite write) =>
        {
            try
            {
                lock (Sync)
                {
                    if (controller.Database.FindProxyUser(id) is null)
                        return Results.NotFound(new { error = $"User {id} not found." });

                    (string plainText, string hash, string prefix) = ProxyAccessService.GenerateKey();
                    ProxyApiKey key = new()
                    {
                        UserId = id,
                        Label = write.Label?.Trim() ?? string.Empty,
                        KeyHash = hash,
                        KeyPrefix = prefix,
                        CreatedUtc = DateTime.UtcNow,
                        AllowedModels = NormalizeModelList(write.AllowedModels),
                        McpAccess = write.McpAccess ?? false,
                    };

                    controller.Database.InsertProxyApiKey(key);
                    controller.Access.InvalidateCache();

                    return Results.Json(new
                    {
                        key = KeyView(key),
                        plainText,
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapPut("/api/keys/{id:int}", (int id, KeyWrite write) =>
        {
            try
            {
                lock (Sync)
                {
                    ProxyApiKey? key = controller.Database.LoadProxyApiKeys().FirstOrDefault(k => k.Id == id);
                    if (key is null)
                        return Results.NotFound(new { error = $"Key {id} not found." });

                    string label = write.Label?.Trim() ?? key.Label;
                    List<string> models = write.AllowedModels is not null ? NormalizeModelList(write.AllowedModels) : key.AllowedModels;
                    bool mcpAccess = write.McpAccess ?? key.McpAccess;

                    controller.Database.UpdateProxyApiKeyAccess(id, label, models, mcpAccess);
                    controller.Access.InvalidateCache();

                    key.Label = label;
                    key.AllowedModels = models;
                    key.McpAccess = mcpAccess;
                    return Results.Json(KeyView(key));
                }
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapDelete("/api/keys/{id:int}", (int id) =>
        {
            try
            {
                lock (Sync)
                {
                    if (!controller.Database.LoadProxyApiKeys().Any(k => k.Id == id))
                        return Results.NotFound(new { error = $"Key {id} not found." });

                    controller.Database.DeleteProxyApiKey(id);
                    controller.Access.InvalidateCache();
                }
                return Results.Ok();
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });
    }

    private static object UserView(ProxyUser user, IEnumerable<ProxyApiKey> keys) => new
    {
        id = user.Id,
        name = user.Name,
        email = user.Email,
        createdUtc = user.CreatedUtc,
        isEnabled = user.IsEnabled,
        dashboardLoginEnabled = user.DashboardLoginEnabled,
        dashboardUsername = user.DashboardUsername,
        dashboardRole = user.DashboardRole,
        hasDashboardPassword = !string.IsNullOrEmpty(user.DashboardPasswordHash),
        keys = keys.Select(KeyView).ToList(),
    };

    private static bool TryConfigureDashboardPassword(ProxyUser user, string? password, out string? error)
    {
        error = null;
        if (password is null)
            return true;
        if (password.Length < 12 || password.Length > 1024)
        {
            error = "Dashboard passwords must be between 12 and 1024 characters.";
            return false;
        }

        user.DashboardPasswordHash = DashboardPasswords.Hash(user, password);
        return true;
    }

    private static bool ValidateDashboardLogin(ProxyUser user, IEnumerable<ProxyUser> users, int? exceptId, out string? error)
    {
        error = null;
        if (!user.DashboardLoginEnabled)
            return true;
        if (user.DashboardUsername.Length is < 3 or > 128)
        {
            error = "Dashboard usernames must be between 3 and 128 characters.";
            return false;
        }
        if (string.IsNullOrEmpty(user.DashboardPasswordHash))
        {
            error = "Set a dashboard password before enabling this login.";
            return false;
        }
        if (users.Any(existing => existing.Id != exceptId
            && existing.DashboardLoginEnabled
            && string.Equals(existing.DashboardUsername, user.DashboardUsername, StringComparison.OrdinalIgnoreCase)))
        {
            error = "That dashboard username is already in use.";
            return false;
        }
        return true;
    }

    // Metadata only: prefix is a short non-secret fragment; hash and plaintext never appear.
    private static object KeyView(ProxyApiKey key) => new
    {
        id = key.Id,
        userId = key.UserId,
        label = key.Label,
        keyPrefix = key.KeyPrefix,
        createdUtc = key.CreatedUtc,
        lastUsedUtc = key.LastUsedUtc,
        allowedModels = key.AllowedModels,
        mcpAccess = key.McpAccess,
    };

    private static List<string> NormalizeModelList(List<string>? models) =>
        models is null
            ? []
            : [.. models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static IResult DatabaseUnavailable(Exception ex) =>
        Results.Json(new { error = $"The application database is unavailable: {ex.Message}" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}