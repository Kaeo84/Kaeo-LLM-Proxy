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
    internal const string AdminRole = DashboardRoles.Admin;
    internal const string ReadOnlyRole = DashboardRoles.ReadOnly;

    /// <summary>
    /// Canonical role list for storage, or null when nothing valid was supplied. Callers treat
    /// null as "no dashboard access" so an unrecognised value fails closed.
    /// </summary>
    public static string? NormalizeRole(string? role) => DashboardRoles.Normalize(role);

    /// <summary>The capabilities the current session holds (set by <see cref="TryAuthorize"/>).</summary>
    public static DashboardCapability Capabilities(HttpContext context)
        => context.Items[RoleItemKey] is string roles
            ? DashboardRoles.CapabilitiesFor(roles)
            : DashboardCapability.None;

    /// <summary>True when the current session holds every bit of <paramref name="capability"/>.</summary>
    public static bool Can(HttpContext context, DashboardCapability capability)
        => capability == DashboardCapability.None || (Capabilities(context) & capability) == capability;

    /// <summary>True when the authenticated dashboard session holds the Admin role.</summary>
    public static bool IsAdmin(HttpContext context)
        => context.Items[RoleItemKey] is string roles
            && DashboardRoles.HasRole(roles, DashboardRoles.Admin);

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
            string? roles = user is not null && user.IsEnabled && user.DashboardLoginEnabled
                && string.Equals(claimedPasswordHash, user.DashboardPasswordHash, StringComparison.Ordinal)
                ? DashboardRoles.Normalize(user.DashboardRole)
                : null;
            if (user is not null && roles is not null)
            {
                context.Items[RoleItemKey] = roles;
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
            || request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase);

    /// <summary>Assets the login page itself needs; everything else stays behind sign-in.</summary>
    public static bool IsPublicAsset(PathString path)
        => path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/js/login.js", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capability an <c>/api</c> route requires, or <see cref="DashboardCapability.None"/> when
    /// any signed-in dashboard user may call it. A route with no entry requires Admin, so a newly
    /// added endpoint cannot be exposed by omission.
    /// </summary>
    public static DashboardCapability RequiredCapability(HttpRequest request)
    {
        string path = request.Path.HasValue ? request.Path.Value! : "/";
        bool write = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);

        // Model and MCP lifecycle controls share one capability: they are the same "press the
        // buttons on the dashboard" trust level.
        if (path.StartsWith("/api/proxy/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/webdashboard/", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/mcp/", StringComparison.OrdinalIgnoreCase) && write))
        {
            return DashboardCapability.DashboardControl;
        }

        if (path.StartsWith("/api/testchat", StringComparison.OrdinalIgnoreCase))
            return DashboardCapability.TestConsole;

        if (path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/keys/", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardCapability.ManageUsers;
        }

        if (path.StartsWith("/api/mappings", StringComparison.OrdinalIgnoreCase))
        {
            // Reading the mapping list is harmless and the Usage filters need it; changing it is
            // the Model Admin's job.
            return write ? DashboardCapability.ManageModels : DashboardCapability.None;
        }

        if (path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/credentials", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/modules", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardCapability.ManageSettings;
        }

        if (path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/logs/detail", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardCapability.ViewLogs;
        }

        // System logs and the live stream monitor expose payload text and other users' in-flight
        // bytes, so they stay Admin-only rather than following the Requests log grant.
        if (path.Equals("/api/syslog", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/streams", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/streams/detail", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardCapability.Admin;
        }

        if (path.Equals("/api/auth/roles", StringComparison.OrdinalIgnoreCase))
            return DashboardCapability.None;

        if (path.Equals("/api/usage", StringComparison.OrdinalIgnoreCase))
            return DashboardCapability.ViewUsage;

        if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/stats", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardCapability.ViewDashboard;
        }

        // Unknown route: require Admin rather than silently allowing it.
        return DashboardCapability.Admin;
    }

    /// <summary>
    /// The capability check for a signed-in session, with the message the UI should show when it
    /// fails. Returns null when the request is allowed.
    /// </summary>
    public static string? Authorize(HttpContext context, DashboardCapability required)
    {
        if (required == DashboardCapability.None || Can(context, required))
            return null;

        string roles = context.Items[RoleItemKey] as string ?? string.Empty;
        return required == DashboardCapability.Admin
            ? "This action requires the Admin role."
            : $"This action requires the {DescribeCapability(required)} permission; your roles are {DashboardRoles.Describe(roles)}.";
    }

    private static string DescribeCapability(DashboardCapability capability) => capability switch
    {
        DashboardCapability.ViewLogs or DashboardCapability.ViewUsage => "Read Only",
        DashboardCapability.ViewDashboard => "Dashboard Read Only",
        DashboardCapability.DashboardControl => "Dashboard Read-Write",
        DashboardCapability.ManageUsers => "User Admin",
        DashboardCapability.ManageModels => "Model Admin",
        DashboardCapability.TestConsole => "Test Console",
        _ => "Admin",
    };
}