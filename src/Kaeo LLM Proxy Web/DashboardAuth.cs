using System.Globalization;
using System.Net;
using System.Security.Claims;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Dashboard authentication is login-based: every request must carry a valid dashboard session
/// cookie issued by /api/auth/login (or first-run /api/auth/setup) unless the RequireLogin setting
/// is turned off, in which case every caller gets full admin access. Proxy API keys never grant
/// general dashboard access; they may stand in for the cookie on the model registration surface
/// (<c>/api/mappings</c>) only, and only for the bits their user's roles carry, so a service
/// account can register models without a password. Sessions and API keys are re-validated against
/// the user record on every request so disabling a user, changing the password, or turning off
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
    internal const string ApiKeyItemKey = "Kaeo.DashboardApiKeyId";
    internal const string ApiKeyAuthenticationType = "Kaeo.DashboardApiKey";
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

    /// <summary>
    /// True when the caller authenticated with a proxy API key rather than a dashboard session
    /// cookie. Such a caller is confined to the registration surface.
    /// </summary>
    public static bool IsApiKeyCaller(HttpContext context) => context.Items.ContainsKey(ApiKeyItemKey);

    public static bool TryAuthorize(
        HttpContext context,
        AppSettings settings,
        AppDatabase database,
        ProxyAccessService access,
        out string? error)
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

        // No usable session: a service account may drive the registration surface with an API key.
        if (IsRegistrationApiRequest(context.Request) && TryAuthorizeApiKey(context, access))
            return true;

        error = "Sign in with a dashboard username and password.";
        return false;
    }

    /// <summary>
    /// Resolves a proxy API key into dashboard roles for the registration surface. The user needs
    /// to be enabled and to hold dashboard roles; a dashboard password is deliberately not
    /// required, because a service account exists only to call the API.
    /// </summary>
    private static bool TryAuthorizeApiKey(HttpContext context, ProxyAccessService access)
    {
        string? plainText = ReadApiKey(context.Request);
        if (plainText is null)
            return false;

        ProxyAccessPrincipal? principal = access.Authenticate(plainText);
        string? roles = principal is null ? null : DashboardRoles.Normalize(principal.User.DashboardRole);
        if (principal is null || roles is null)
            return false;

        context.Items[RoleItemKey] = roles;
        context.Items[UserNameItemKey] = principal.User.Name;
        context.Items[ApiKeyItemKey] = principal.Key.Id;
        // A claims principal lets the endpoints attribute the call to this user in the audit log.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier,
                principal.User.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, principal.User.Name),
        ], ApiKeyAuthenticationType));
        return true;
    }

    /// <summary>
    /// The proxy API key a request carries, if any. Mirrors the proxy's own extraction order so a
    /// client that already talks to the proxy can point at the dashboard unchanged.
    /// </summary>
    public static string? ReadApiKey(HttpRequest request)
    {
        string authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            string token = authorization[bearer.Length..].Trim();
            if (token.Length > 0)
                return token;
        }

        string apiKey = request.Headers["x-api-key"].ToString().Trim();
        return apiKey.Length > 0 ? apiKey : null;
    }

    /// <summary>
    /// The routes an API key may reach: the model registration surface and nothing else. Kept
    /// deliberately narrow so a leaked service-account key cannot touch users, settings, or logs.
    /// </summary>
    public static bool IsRegistrationApiRequest(HttpRequest request)
        => request.Path.StartsWithSegments("/api/mappings", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the registration API's OpenAPI document, which is the one route on this surface
    /// that is not open to every signed-in session. Only reads count, so a write aimed at this
    /// path still needs the registration write grant instead of the read one.
    /// </summary>
    public static bool IsRegistrationSpecRequest(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && request.Path.Equals(RegistrationApiSpec.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The capability an API-key caller needs on the registration surface. Reading needs
    /// <see cref="DashboardCapability.ViewRegistration"/>; changing anything needs
    /// <see cref="DashboardCapability.ManageRegistration"/>.
    /// </summary>
    public static DashboardCapability RequiredRegistrationCapability(HttpRequest request)
    {
        bool write = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
        return write ? DashboardCapability.ManageRegistration : DashboardCapability.ViewRegistration;
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
            // The OpenAPI contract is the one part of this surface with a deliberate grant: it
            // describes what a service account may call, so only the roles that hand those keys out
            // or use them need to read it. An API key needs the same bit, which is why the spec is
            // kept under /api/mappings rather than beside the dashboard's other routes.
            if (IsRegistrationSpecRequest(request))
                return DashboardCapability.ViewRegistration;

            // Reading the mapping list is harmless and the Usage filters need it; changing it is
            // the registration job. The list itself is scoped per role by the endpoint.
            return write ? DashboardCapability.ManageRegistration : DashboardCapability.None;
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
        DashboardCapability.ViewModels => "Models Read Only",
        DashboardCapability.ViewOwnModels => "Own Models Read Only",
        DashboardCapability.ViewRegistration => "Registration Read Only",
        DashboardCapability.ManageRegistration => "Registration Read-Write",
        _ => "Admin",
    };
}