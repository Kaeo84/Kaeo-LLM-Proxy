using System.Data.Common;
using System.Security.Claims;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy.Web;

internal static partial class DashboardEndpoints
{
    private static void MapAuthentication(WebApplication app, AppSettings settings, ProxyHostController controller)
    {
        app.MapGet("/api/auth/status", () =>
        {
            try
            {
                bool loginEnabled = controller.Database.HasDashboardLogins();
                return Results.Json(new
                {
                    loginEnabled,
                    setupRequired = !loginEnabled,
                    requireLogin = settings.WebDashboard.RequireLogin,
                });
            }
            catch (Exception ex) when (ex is IOException or DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        // First-run bootstrap: when no dashboard login exists yet, the local machine may create
        // the initial administrator account. Restricted to loopback so a network-exposed fresh
        // instance cannot be claimed remotely; remote admins use the Windows app's Users tab.
        app.MapPost("/api/auth/setup", async (DashboardSetupWrite request, HttpContext context) =>
        {
            if (!DashboardAuth.IsRequestLoopback(context))
                return Results.Json(new { error = "First-run setup is only available from the local machine. Create dashboard logins from the Windows app's Users tab instead." }, statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "A display name is required." });
            string username = request.Username?.Trim() ?? string.Empty;
            if (username.Length is < 3 or > 128)
                return Results.BadRequest(new { error = "Dashboard usernames must be between 3 and 128 characters." });
            if (request.Password is null || request.Password.Length < 12 || request.Password.Length > 1024)
                return Results.BadRequest(new { error = "Dashboard passwords must be between 12 and 1024 characters." });

            try
            {
                ProxyUser user;
                lock (Sync)
                {
                    if (controller.Database.HasDashboardLogins())
                        return Results.Json(new { error = "Dashboard setup is already complete. Sign in instead." }, statusCode: StatusCodes.Status409Conflict);

                    user = new ProxyUser
                    {
                        Name = request.Name.Trim(),
                        CreatedUtc = DateTime.UtcNow,
                        IsEnabled = true,
                        DashboardLoginEnabled = true,
                        DashboardUsername = username,
                        DashboardRole = DashboardAuth.AdminRole,
                    };
                    user.DashboardPasswordHash = DashboardPasswords.Hash(user, request.Password);
                    controller.Database.InsertProxyUser(user);
                    controller.Access.InvalidateCache();
                }

                List<Claim> claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Claim(ClaimTypes.Name, user.DashboardUsername),
                    new Claim(DashboardAuth.PasswordHashClaim, user.DashboardPasswordHash),
                ];
                ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
                return Results.Json(new { role = DashboardAuth.AdminRole, username = user.DashboardUsername });
            }
            catch (Exception ex) when (ex is IOException or DbException)
            {
                return DatabaseUnavailable(ex);
            }
        }).RequireRateLimiting("dashboard-login");

        app.MapPost("/api/auth/login", async (DashboardLoginWrite request, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
                return Results.Unauthorized();

            try
            {
                ProxyUser? user = controller.Database.FindProxyUserByDashboardUsername(request.Username);
                if (user is null || !DashboardPasswords.Verify(user, request.Password))
                    return Results.Json(new { error = "The username or password is not valid." }, statusCode: StatusCodes.Status401Unauthorized);
                if (!user.IsEnabled)
                    return Results.Json(new { error = "This account is disabled. Enable it from the proxy's Users tab." }, statusCode: StatusCodes.Status403Forbidden);
                if (!user.DashboardLoginEnabled)
                    return Results.Json(new { error = "Dashboard sign-in is not enabled for this account. Enable it from the proxy's Users tab." }, statusCode: StatusCodes.Status403Forbidden);

                string? role = DashboardAuth.NormalizeRole(user.DashboardRole);
                if (role is null)
                    return Results.Json(new { error = "This dashboard account has an invalid role. Contact an administrator." }, statusCode: StatusCodes.Status403Forbidden);

                List<Claim> claims =
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Claim(ClaimTypes.Name, user.DashboardUsername),
                    new Claim(DashboardAuth.PasswordHashClaim, user.DashboardPasswordHash),
                ];
                ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
                return Results.Json(new { role, username = user.DashboardUsername });
            }
            catch (Exception ex) when (ex is IOException or DbException)
            {
                return DatabaseUnavailable(ex);
            }
        }).RequireRateLimiting("dashboard-login");

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
    }
}
