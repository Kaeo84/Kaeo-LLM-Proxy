using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Users tab API: proxy users (name/email contact records) and their API keys. Keys authenticate
/// by hash, and the plaintext is also kept under reversible encryption so an administrator can
/// reveal it or replace it in place; every non-admin surface exposes only the non-secret prefix.
/// Hashes never leave the database layer.
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

            string? dashboardRoles = NormalizeRoles(write, DashboardRoles.ReadOnly);
            if (dashboardRoles is null)
                return Results.BadRequest(new { error = RolesError });

            ProxyUser user = new()
            {
                Name = write.Name.Trim(),
                Email = write.Email?.Trim() ?? string.Empty,
                CreatedUtc = DateTime.UtcNow,
                IsEnabled = write.IsEnabled ?? true,
                DashboardLoginEnabled = write.DashboardLoginEnabled ?? false,
                DashboardUsername = write.DashboardUsername?.Trim() ?? string.Empty,
                DashboardRole = dashboardRoles,
                AllowedModels = NormalizeModelList(write.AllowedModels),
                McpAccess = write.McpAccess ?? false,
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
                    if (write.DashboardRole is not null || write.DashboardRoles is not null)
                    {
                        string? dashboardRoles = NormalizeRoles(write, user.DashboardRole);
                        if (dashboardRoles is null)
                            return Results.BadRequest(new { error = RolesError });
                        user.DashboardRole = dashboardRoles;
                    }
                    if (write.AllowedModels is not null) user.AllowedModels = NormalizeModelList(write.AllowedModels);
                    if (write.McpAccess is { } mcpAccess) user.McpAccess = mcpAccess;
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

        // Creates a key and returns the plaintext once. The plaintext is also stored under
        // reversible encryption so an administrator can reveal it later (GET .../reveal) or
        // replace it in place (POST .../regenerate).
        app.MapPost("/api/users/{id:int}/keys", (int id, KeyWrite write) =>
        {
            try
            {
                lock (Sync)
                {
                    if (controller.Database.FindProxyUser(id) is null)
                        return Results.NotFound(new { error = $"User {id} not found." });

                    (string plainText, string hash, string prefix, string? secret) = controller.Access.GenerateKeyMaterial();
                    ProxyApiKey key = new()
                    {
                        UserId = id,
                        Label = write.Label?.Trim() ?? string.Empty,
                        KeyHash = hash,
                        KeyPrefix = prefix,
                        KeySecret = secret,
                        CreatedUtc = DateTime.UtcNow,
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

        // User Admin and above: hands the stored plaintext key back. Read Only sessions never reach
        // here (the middleware capability check blocks the path) but the check makes that explicit.
        app.MapGet("/api/keys/{id:int}/reveal", (int id, HttpContext context) =>
        {
            if (!DashboardAuth.Can(context, DashboardCapability.ManageUsers))
                return Results.Json(new { error = "Revealing API keys requires the User Admin role." },
                    statusCode: StatusCodes.Status403Forbidden);

            try
            {
                ProxyApiKey? key = controller.Database.LoadProxyApiKeys().FirstOrDefault(k => k.Id == id);
                if (key is null)
                    return Results.NotFound(new { error = $"Key {id} not found." });

                string? plainText = controller.Access.RevealKey(key);
                if (plainText is null)
                {
                    return Results.Json(
                        new
                        {
                            error = "This key was created before key recovery was available (or its protection key file changed), so it cannot be shown. Regenerate it to get a key you can copy again.",
                            canReveal = false,
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }

                Serilog.Log.Information(
                    "Dashboard admin {Admin} revealed API key {KeyId} ({KeyPrefix})",
                    context.Items[DashboardAuth.UserNameItemKey]?.ToString() ?? "unknown",
                    key.Id,
                    key.KeyPrefix);

                return Results.Json(new { key = KeyView(key), plainText });
            }
            catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        // User Admin and above: replaces a key's secret material in place. Label and the owner's
        // grants survive; the previous key stops authenticating immediately.
        app.MapPost("/api/keys/{id:int}/regenerate", (int id, HttpContext context) =>
        {
            if (!DashboardAuth.Can(context, DashboardCapability.ManageUsers))
                return Results.Json(new { error = "Regenerating API keys requires the User Admin role." },
                    statusCode: StatusCodes.Status403Forbidden);

            try
            {
                lock (Sync)
                {
                    ProxyApiKey? key = controller.Database.LoadProxyApiKeys().FirstOrDefault(k => k.Id == id);
                    if (key is null)
                        return Results.NotFound(new { error = $"Key {id} not found." });

                    (string plainText, string hash, string prefix, string? secret) = controller.Access.GenerateKeyMaterial();
                    DateTime createdUtc = DateTime.UtcNow;

                    controller.Database.UpdateProxyApiKeyMaterial(id, hash, prefix, secret, createdUtc);
                    controller.Access.InvalidateCache();

                    key.KeyHash = hash;
                    key.KeyPrefix = prefix;
                    key.KeySecret = secret;
                    key.CreatedUtc = createdUtc;
                    key.LastUsedUtc = null;

                    Serilog.Log.Information(
                        "Dashboard admin {Admin} regenerated API key {KeyId} ({KeyPrefix})",
                        context.Items[DashboardAuth.UserNameItemKey]?.ToString() ?? "unknown",
                        key.Id,
                        key.KeyPrefix);

                    return Results.Json(new { key = KeyView(key), plainText });
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

                    controller.Database.UpdateProxyApiKeyAccess(id, label, key.AllowedModels, key.McpAccess);
                    controller.Access.InvalidateCache();

                    key.Label = label;
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
        dashboardRoles = DashboardRoles.Parse(user.DashboardRole),
        roleLabel = DashboardRoles.Describe(user.DashboardRole),
        allowedModels = user.AllowedModels,
        mcpAccess = user.McpAccess,
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
    // canReveal tells the UI whether a reveal is possible or the key must be regenerated first.
    private static object KeyView(ProxyApiKey key) => new
    {
        id = key.Id,
        userId = key.UserId,
        label = key.Label,
        keyPrefix = key.KeyPrefix,
        createdUtc = key.CreatedUtc,
        lastUsedUtc = key.LastUsedUtc,
        canReveal = !string.IsNullOrEmpty(key.KeySecret),
    };

    private const string RolesError =
        "Dashboard roles must be any of: Admin, Read Only, Dashboard Read Only, Dashboard Read-Write, User Admin, Model Admin, Test Console.";

    /// <summary>
    /// Resolves the role list from a write payload: the array wins, then the legacy single value,
    /// then the fallback. Returns null when nothing valid was supplied so the caller can reject it
    /// rather than silently dropping the user's access.
    /// </summary>
    private static string? NormalizeRoles(UserWrite write, string? fallback)
    {
        if (write.DashboardRoles is not null)
            return DashboardRoles.Normalize(string.Join(',', write.DashboardRoles));
        if (write.DashboardRole is not null)
            return DashboardRoles.Normalize(write.DashboardRole);
        return DashboardRoles.Normalize(fallback) ?? DashboardRoles.ReadOnly;
    }

    private static List<string> NormalizeModelList(List<string>? models) =>
        models is null
            ? []
            : [.. models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static IResult DatabaseUnavailable(Exception ex) =>
        Results.Json(new { error = $"The application database is unavailable: {ex.Message}" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}