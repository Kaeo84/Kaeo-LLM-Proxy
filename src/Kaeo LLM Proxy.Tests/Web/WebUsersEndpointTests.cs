using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Endpoint tests for the Users tab API: user CRUD, key create/update/delete, the write-only
/// guarantee for key material (plaintext returned exactly once; hashes never exposed), and the
/// proxySecurity settings toggle. Mirrors the Kestrel Bundle pattern of
/// <see cref="WebDashboardEndpointTests"/>.
/// </summary>
[Collection("AppLogger")]
public sealed class WebUsersEndpointTests
{
    [Fact]
    public async Task DashboardLogin_UsesHashedPasswordAndCookieSession_AndCanLogout()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "correct horse battery";

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Dashboard Admin",
            dashboardLoginEnabled = true,
            dashboardUsername = "admin.person",
            dashboardPassword = password,
            dashboardRole = "admin",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string userJson = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, userJson);
        Assert.DoesNotContain("dashboardPasswordHash", userJson);
        using JsonDocument userDoc = JsonDocument.Parse(userJson);
        Assert.True(userDoc.RootElement.GetProperty("hasDashboardPassword").GetBoolean());
        Assert.Equal("admin", userDoc.RootElement.GetProperty("dashboardRole").GetString());

        using JsonDocument status = JsonDocument.Parse(await (await b.Anonymous.GetAsync("/api/auth/status")).Content.ReadAsStringAsync());
        Assert.True(status.RootElement.GetProperty("loginEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Anonymous.GetAsync("/api/settings")).StatusCode);

        HttpResponseMessage wrongPassword = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "admin.person", password = "incorrect password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "ADMIN.PERSON", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage identityRequest = new(HttpMethod.Get, "/api/auth/me");
        identityRequest.Headers.Add("Cookie", cookie);
        HttpResponseMessage identity = await b.Anonymous.SendAsync(identityRequest);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        using JsonDocument identityDoc = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
        Assert.Equal("admin", identityDoc.RootElement.GetProperty("role").GetString());
        Assert.Equal("admin.person", identityDoc.RootElement.GetProperty("username").GetString());

        using HttpRequestMessage logoutRequest = new(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("Cookie", cookie);
        HttpResponseMessage logout = await b.Anonymous.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), value => value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DashboardLogin_ReportsDisabledAccounts_OnlyAfterCorrectPassword()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "correct horse battery";

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Disabled Person",
            dashboardLoginEnabled = true,
            dashboardUsername = "person",
            dashboardPassword = password,
            dashboardRole = "admin",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        ProxyUser user = b.Database.LoadProxyUsers().Single(u => u.DashboardUsername == "person");
        user.IsEnabled = false;
        b.Database.UpdateProxyUser(user);

        // A disabled account with a wrong password must stay indistinguishable from a bad username.
        HttpResponseMessage wrongPasswordWhileDisabled = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "person", password = "incorrect password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordWhileDisabled.StatusCode);

        HttpResponseMessage disabledLogin = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "person", password });
        Assert.Equal(HttpStatusCode.Forbidden, disabledLogin.StatusCode);
        Assert.Contains("disabled", await disabledLogin.Content.ReadAsStringAsync());

        user.IsEnabled = true;
        user.DashboardLoginEnabled = false;
        b.Database.UpdateProxyUser(user);
        HttpResponseMessage loginDisabledLogin = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "person", password });
        Assert.Equal(HttpStatusCode.Forbidden, loginDisabledLogin.StatusCode);
        Assert.Contains("not enabled", await loginDisabledLogin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DashboardLogin_RequiresStrongPasswordAndUniqueUsername()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage weakPassword = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Weak",
            dashboardLoginEnabled = true,
            dashboardUsername = "weak-user",
            dashboardPassword = "too-short",
            dashboardRole = "admin",
        });
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);

        HttpResponseMessage first = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "First",
            dashboardLoginEnabled = true,
            dashboardUsername = "unique-user",
            dashboardPassword = "long enough password",
            dashboardRole = "admin",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "unique-user", password = "long enough password" });
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        using HttpRequestMessage duplicateRequest = new(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(new
            {
                name = "Duplicate",
                dashboardLoginEnabled = true,
                dashboardUsername = "UNIQUE-USER",
                dashboardPassword = "another long password",
                dashboardRole = "readOnly",
            }),
        };
        duplicateRequest.Headers.Add("Cookie", cookie);
        HttpResponseMessage duplicate = await b.Anonymous.SendAsync(duplicateRequest);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task DashboardLogin_ReadOnlyRoleCanReadLogsButCannotReadSettings()
    {
        await using Bundle b = await CreateBundleAsync();
        const string password = "read only password";
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Log Reader",
            dashboardLoginEnabled = true,
            dashboardUsername = "logs",
            dashboardPassword = password,
            dashboardRole = "readOnly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        HttpResponseMessage login = await b.Anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "logs", password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];

        using HttpRequestMessage logsRequest = new(HttpMethod.Get, "/api/logs?source=proxy&after=0&limit=1");
        logsRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.SendAsync(logsRequest)).StatusCode);

        using HttpRequestMessage settingsRequest = new(HttpMethod.Get, "/api/settings");
        settingsRequest.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Anonymous.SendAsync(settingsRequest)).StatusCode);
    }

    [Fact]
    public async Task Users_CreateListUpdateDelete_RoundTrip()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users",
            new { name = "Alice", email = "alice@example.com" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int id = createdDoc.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.Equal("Alice", createdDoc.RootElement.GetProperty("name").GetString());
        Assert.True(createdDoc.RootElement.GetProperty("isEnabled").GetBoolean());

        HttpResponseMessage list = await b.Client.GetAsync("/api/users");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        // The bundle's seeded dashboard administrator plus Alice.
        Assert.Equal(2, listDoc.RootElement.GetArrayLength());

        HttpResponseMessage updated = await b.Client.PutAsJsonAsync($"/api/users/{id}",
            new { name = "Alicia", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using JsonDocument updatedDoc = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("Alicia", updatedDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("alice@example.com", updatedDoc.RootElement.GetProperty("email").GetString());
        Assert.False(updatedDoc.RootElement.GetProperty("isEnabled").GetBoolean());

        HttpResponseMessage deleted = await b.Client.DeleteAsync($"/api/users/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using JsonDocument afterDoc = JsonDocument.Parse(await (await b.Client.GetAsync("/api/users")).Content.ReadAsStringAsync());
        Assert.Equal(1, afterDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Users_Validation_RejectsMissingNameAndBadEmail()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage noName = await b.Client.PostAsJsonAsync("/api/users", new { email = "x@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        HttpResponseMessage badEmail = await b.Client.PostAsJsonAsync("/api/users", new { name = "Bob", email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);
    }

    [Fact]
    public async Task Users_MutationsOnMissingId_Return404()
    {
        await using Bundle b = await CreateBundleAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync("/api/users/999", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync("/api/users/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync("/api/users/999/keys", new { label = "k" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync("/api/keys/999", new { label = "k" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync("/api/keys/999")).StatusCode);
    }

    [Fact]
    public async Task Keys_CreateReturnsPlaintextOnce_AndListNeverExposesSecrets()
    {
        await using Bundle b = await CreateBundleAsync();
        int userId = await CreateUserAsync(b, "Alice");

        HttpResponseMessage created = await b.Client.PostAsJsonAsync($"/api/users/{userId}/keys",
            new { label = "laptop", allowedModels = new[] { "alpha", " Alpha ", "beta" } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string createdBody = await created.Content.ReadAsStringAsync();
        using JsonDocument createdDoc = JsonDocument.Parse(createdBody);
        string plainText = createdDoc.RootElement.GetProperty("plainText").GetString()!;
        Assert.Matches("^kaeo-[0-9a-f]{48}$", plainText);

        JsonElement key = createdDoc.RootElement.GetProperty("key");
        int keyId = key.GetProperty("id").GetInt32();
        Assert.Equal("laptop", key.GetProperty("label").GetString());
        Assert.Equal(plainText[..13], key.GetProperty("keyPrefix").GetString());
        // Normalization: trimmed + case-insensitive dedupe.
        Assert.Equal(2, key.GetProperty("allowedModels").GetArrayLength());
        Assert.False(key.TryGetProperty("keyHash", out _));
        Assert.False(key.TryGetProperty("plainText", out _));

        string listBody = await (await b.Client.GetAsync("/api/users")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(plainText, listBody);
        Assert.DoesNotContain(ProxyAccessService.HashKey(plainText), listBody);
        Assert.DoesNotContain("keyHash", listBody);
        Assert.True(keyId > 0);
    }

    [Fact]
    public async Task Keys_UpdateLabelAndModels_NullModelsKeepsExisting()
    {
        await using Bundle b = await CreateBundleAsync();
        int userId = await CreateUserAsync(b, "Alice");
        (int keyId, _) = await CreateKeyAsync(b, userId, ["alpha"]);

        HttpResponseMessage labelOnly = await b.Client.PutAsJsonAsync($"/api/keys/{keyId}", new { label = "renamed" });
        Assert.Equal(HttpStatusCode.OK, labelOnly.StatusCode);
        using JsonDocument labelDoc = JsonDocument.Parse(await labelOnly.Content.ReadAsStringAsync());
        Assert.Equal("renamed", labelDoc.RootElement.GetProperty("label").GetString());
        Assert.Equal(1, labelDoc.RootElement.GetProperty("allowedModels").GetArrayLength());
        Assert.Equal("alpha", labelDoc.RootElement.GetProperty("allowedModels")[0].GetString());

        HttpResponseMessage cleared = await b.Client.PutAsJsonAsync($"/api/keys/{keyId}", new { allowedModels = Array.Empty<string>() });
        using JsonDocument clearedDoc = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.Equal(0, clearedDoc.RootElement.GetProperty("allowedModels").GetArrayLength());
    }

    [Fact]
    public async Task Keys_Delete_RemovesKey_AndUserDeleteCascades()
    {
        await using Bundle b = await CreateBundleAsync();
        int userId = await CreateUserAsync(b, "Alice");
        (int keyId, string plainText) = await CreateKeyAsync(b, userId, []);

        Assert.Equal(HttpStatusCode.OK, (await b.Client.DeleteAsync($"/api/keys/{keyId}")).StatusCode);
        Assert.Null(b.Controller.Access.Authenticate(plainText));

        (_, string secondPlain) = await CreateKeyAsync(b, userId, []);
        Assert.NotNull(b.Controller.Access.Authenticate(secondPlain));

        Assert.Equal(HttpStatusCode.OK, (await b.Client.DeleteAsync($"/api/users/{userId}")).StatusCode);
        Assert.Empty(b.Database.LoadProxyApiKeys());
        Assert.Null(b.Controller.Access.Authenticate(secondPlain));
    }

    [Fact]
    public async Task Settings_ProxySecurity_DefaultsOff_AndTogglesViaPut()
    {
        await using Bundle b = await CreateBundleAsync();

        using JsonDocument before = JsonDocument.Parse(await (await b.Client.GetAsync("/api/settings")).Content.ReadAsStringAsync());
        Assert.False(before.RootElement.GetProperty("proxySecurity").GetProperty("requireApiKey").GetBoolean());

        HttpResponseMessage put = await b.Client.PutAsJsonAsync("/api/settings",
            new { proxySecurity = new { requireApiKey = true } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using JsonDocument after = JsonDocument.Parse(await (await b.Client.GetAsync("/api/settings")).Content.ReadAsStringAsync());
        Assert.True(after.RootElement.GetProperty("proxySecurity").GetProperty("requireApiKey").GetBoolean());
        Assert.True(b.Controller.Settings.ProxySecurity.RequireApiKey);
    }

    private static async Task<int> CreateUserAsync(Bundle b, string name)
    {
        HttpResponseMessage response = await b.Client.PostAsJsonAsync("/api/users", new { name });
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task<(int Id, string PlainText)> CreateKeyAsync(Bundle b, int userId, string[] allowedModels)
    {
        HttpResponseMessage response = await b.Client.PostAsJsonAsync($"/api/users/{userId}/keys",
            new { label = "test", allowedModels });
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("key").GetProperty("id").GetInt32(),
                doc.RootElement.GetProperty("plainText").GetString()!);
    }

    private static async Task<Bundle> CreateBundleAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-users-{Guid.NewGuid():N}.db");
        var settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            AutoStartProxy = false,
            MaxLogEntries = 100,
        };
        settings.Logging.ApplicationDatabasePath = dbPath;

        var database = new AppDatabase(settings.Logging);
        DashboardTestSession.SeedAdmin(database);
        var moduleHost = new ModuleHost(database, settings);
        var controller = new ProxyHostController(settings, database, moduleHost);

        settings.WebDashboard.Enabled = true;
        settings.WebDashboard.Port = GetFreePort();
        settings.WebDashboard.ListenAddress = "localhost";

        var host = new WebDashboardHost(settings, controller);
        await host.StartAsync();

        HttpClient client = DashboardTestSession.CreateClient(host.Url!, useCookies: true);
        await DashboardTestSession.SignInAdminAsync(client);

        return new Bundle
        {
            Host = host,
            Controller = controller,
            Database = database,
            Settings = settings,
            Client = client,
            Anonymous = DashboardTestSession.CreateClient(host.Url!, useCookies: false),
            DbPath = dbPath,
        };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Bundle : IAsyncDisposable
    {
        public required WebDashboardHost Host { get; init; }
        public required ProxyHostController Controller { get; init; }
        public required AppDatabase Database { get; init; }
        public required AppSettings Settings { get; init; }
        public required HttpClient Client { get; init; }
        public required HttpClient Anonymous { get; init; }
        public required string DbPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Anonymous.Dispose();
            await Host.DisposeAsync();
            await Controller.DisposeAsync();
            Database.Dispose();
            foreach (string path in new[] { DbPath, DbPath + "-shm", DbPath + "-wal" })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
            }
        }
    }
}