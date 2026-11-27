using System.Net;
using System.Security.Claims;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Dashboard authentication is login-based: every request must carry a valid dashboard session
/// cookie issued by /api/auth/login (or first-run /api/auth/setup) unless the RequireLogin setting
/// is turned off, in which case every caller gets full admin access. Proxy API keys authenticate
/// model requests only and never grant dashboard access. Sessions are re-validated against the
/// user record on every API request so disabling a user, changing the password, or turning off
/// the login revokes access immediately.
/// </summary>
internal static class DashboardAuth
{
    public static bool IsLoopbackAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return true;
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(address, out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>True when the request originates from the local machine (first-run setup guard).</summary>
    public static bool IsRequestLoopback(HttpContext context)
        => context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

    /// <summary>
    /// The dashboard user id carried by the session cookie, or null when the caller is anonymous.
    /// Used to attribute web traffic in the log so a request row names its dashboard user.
    /// </summary>
    public static int? ResolveUserId(HttpContext context)
        => int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId) ? userId : null;

    internal const string RoleItemKey = "Kaeo.DashboardRole";
    internal const string UserNameItemKey = "Kaeo.DashboardUsername";
    internal const string PasswordHashClaim = "Kaeo.DashboardPasswordHash";
    internal const string AdminRole = "admin";
    internal const string ReadOnlyRole = "readOnly";

    public static string? NormalizeRole(string? role)
    {
        if (string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase))
            return AdminRole;
        if (string.Equals(role, ReadOnlyRole, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "readonly", StringComparison.OrdinalIgnoreCase))
            return ReadOnlyRole;
        return null;
    }

    /// <summary>True when the authenticated dashboard session holds the Admin role.</summary>
        public static bool IsAdmin(HttpContext context)
            => string.Equals(context.Items[RoleItemKey] as string, AdminRole, StringComparison.Ordinal);

        public static bool TryAuthorize(HttpContext context, AppSettings settings, AppDatabase database, out string? error)
    {
        error = null;
        if (!settings.WebDashboard.RequireLogin)
        {
            // Login disabled by configuration: everyone who can reach the dashboard is an admin.
            context.Items[RoleItemKey] = AdminRole;
            context.Items[UserNameItemKey] = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirstValue(ClaimTypes.Name)
                : null;
            return true;
        }

        if (context.User.Identity?.IsAuthenticated == true
            && int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out int userId))
        {
            ProxyUser? user = database.FindProxyUser(userId);
            string? claimedPasswordHash = context.User.FindFirstValue(PasswordHashClaim);
            string? role = user is not null && user.IsEnabled && user.DashboardLoginEnabled
                && string.Equals(claimedPasswordHash, user.DashboardPasswordHash, StringComparison.Ordinal)
                ? NormalizeRole(user.DashboardRole)
                : null;
            if (user is not null && role is not null)
            {
                context.Items[RoleItemKey] = role;
                context.Items[UserNameItemKey] = user.DashboardUsername;
                return true;
            }
        }

        error = "Sign in with a dashboard username and password.";
        return false;
    }

    public static bool IsAuthenticationEndpoint(HttpRequest request)
        => request.Path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/auth/setup", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>Assets the login page itself needs; everything else stays behind sign-in.</summary>
    public static bool IsPublicAsset(PathString path)
        => path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/js/login.js", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadOnlyRequestAllowed(HttpRequest request)
        => (HttpMethods.IsGet(request.Method)
                && (request.Path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
                    || request.Path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase)
                    || request.Path.Equals("/api/logs/detail", StringComparison.OrdinalIgnoreCase)
                    || request.Path.Equals("/api/usage", StringComparison.OrdinalIgnoreCase)))
            || (HttpMethods.IsPost(request.Method)
                && request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase));
}