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
    private static void MapAuthentication(WebApplication app, ProxyHostController controller)
    {
        app.MapGet("/api/auth/status", () =>
        {
            try
            {
                bool loginEnabled = controller.Database.HasDashboardLogins();
                return Results.Json(new { loginEnabled });
            }
            catch (Exception ex) when (ex is IOException or DbException)
            {
                return DatabaseUnavailable(ex);
            }
        });

        app.MapPost("/api/auth/login", async (DashboardLoginWrite request, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
                return Results.Unauthorized();

            try
            {
                ProxyUser? user = controller.Database.FindProxyUserByDashboardUsername(request.Username);
                if (user is null || !user.IsEnabled || !user.DashboardLoginEnabled
                    || !DashboardPasswords.Verify(user, request.Password))
                    return Results.Json(new { error = "The username or password is not valid." }, statusCode: StatusCodes.Status401Unauthorized);

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
