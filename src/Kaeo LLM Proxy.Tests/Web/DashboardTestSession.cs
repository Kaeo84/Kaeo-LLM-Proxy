using System.Net;
using System.Net.Http.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Web;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Shared login helper for the Kestrel dashboard bundles. Dashboard access is login-only, so
/// each bundle seeds an administrator with a dashboard login and signs a cookie-container
/// HttpClient in; an anonymous client (no cookie handling) is available for auth-boundary
/// assertions and manual cookie flows.
/// </summary>
internal static class DashboardTestSession
{
    public const string AdminUsername = "bundle.admin";
    public const string AdminPassword = "bundle admin password";

    public static ProxyUser SeedAdmin(AppDatabase database)
    {
        ProxyUser admin = new()
        {
            Name = "Bundle Admin",
            CreatedUtc = DateTime.UtcNow,
            IsEnabled = true,
            DashboardLoginEnabled = true,
            DashboardUsername = AdminUsername,
            DashboardRole = "admin",
        };
        admin.DashboardPasswordHash = DashboardPasswords.Hash(admin, AdminPassword);
        database.InsertProxyUser(admin);
        return admin;
    }

    public static HttpClient CreateClient(string url, bool useCookies, bool allowAutoRedirect = true, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { UseCookies = useCookies, AllowAutoRedirect = allowAutoRedirect };
        if (useCookies)
            handler.CookieContainer = new CookieContainer();
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    public static async Task SignInAdminAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = AdminUsername, password = AdminPassword });
        response.EnsureSuccessStatusCode();
    }
}