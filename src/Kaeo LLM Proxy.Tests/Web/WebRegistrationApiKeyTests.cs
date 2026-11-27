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
/// Endpoint tests for the service-account path: a proxy API key may stand in for the dashboard
/// session cookie on the model registration surface (/api/mappings and everything under it) and
/// nowhere else. The narrowness is the whole point — a leaked service-account key must not reach
/// users, keys, settings, credentials, modules, logs, or the dashboard tabs.
/// </summary>
[Collection("AppLogger")]
public sealed class WebRegistrationApiKeyTests
{
    private const string RegistrationWriteRole = DashboardRoles.RegistrationReadWrite;
    private const string RegistrationReadRole = DashboardRoles.RegistrationReadOnly;

    [Fact]
    public async Task ApiKey_RegistrationReadWrite_CanListCreateAndDeleteMappings()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, RegistrationWriteRole);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings/discovery", key)).StatusCode);

        HttpResponseMessage created = await SendAsync(b, HttpMethod.Post, "/api/mappings", key, new
        {
            OllamaName = "service-seed",
            LlamaCppName = "upstream-model",
            upstreamUrl = "http://127.0.0.1:11434",
            upstreamType = 2,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        int id;
        using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
            id = doc.RootElement.GetProperty("id").GetInt32();

        Assert.Contains(b.Controller.Settings.ModelMappings, m => m.Id == id && m.ProxyName == "service-seed");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Put, $"/api/mappings/{id}", key, new
        {
            OllamaName = "service-renamed",
            LlamaCppName = "upstream-model",
        })).StatusCode);
        Assert.Contains(b.Controller.Settings.ModelMappings, m => m.Id == id && m.ProxyName == "service-renamed");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Delete, $"/api/mappings/{id}", key, null)).StatusCode);
        Assert.DoesNotContain(b.Controller.Settings.ModelMappings, m => m.Id == id);
    }

    [Fact]
    public async Task ApiKey_RegistrationReadOnly_ReadsButCannotChangeAnything()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, RegistrationReadRole);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings/discovery", key)).StatusCode);

        HttpResponseMessage create = await SendAsync(b, HttpMethod.Post, "/api/mappings", key, new
        {
            OllamaName = "nope",
            LlamaCppName = "nope",
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("Registration Read-Write", await create.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(b, HttpMethod.Post, "/api/mappings/discover", key, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(b, HttpMethod.Delete, "/api/mappings/1", key, null)).StatusCode);
        Assert.Empty(b.Controller.Settings.ModelMappings);
    }

    [Fact]
    public async Task ApiKey_ReachesNothingOutsideTheRegistrationSurface()
    {
        await using Bundle b = await CreateBundleAsync();

        // An Admin user's key is still confined to registration: the key is a machine credential,
        // not a second way to sign in.
        string key = await CreateServiceAccountKeyAsync(b, DashboardRoles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings", key)).StatusCode);

        foreach (string path in new[]
        {
            "/api/users", "/api/keys/1/reveal", "/api/settings", "/api/credentials",
            "/api/modules", "/api/logs", "/api/usage", "/api/status", "/api/syslog", "/api/streams",
        })
        {
            HttpResponseMessage response = await GetAsync(b, path, key);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
        }

        // /api/auth/me sits outside the gate; a key must not make it report an identity either.
        using JsonDocument me = JsonDocument.Parse(await (await GetAsync(b, "/api/auth/me", key)).Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, me.RootElement.GetProperty("role").GetString() ?? string.Empty);
    }

    [Fact]
    public async Task ApiKey_WithoutARegistrationRole_IsRefused()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, DashboardRoles.ReadOnly);

        // Read Only is a person's role: it carries no registration capability, so a key holding it
        // is authenticated but refused.
        HttpResponseMessage response = await GetAsync(b, "/api/mappings", key);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Registration Read Only", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiKey_IsRejectedWhenUnknownOrDisabled()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, RegistrationWriteRole);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(b, "/api/mappings", "kaeo-not-a-real-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(b, "/api/mappings", null)).StatusCode);

        ProxyUser user = b.Database.LoadProxyUsers().Single(u => u.DashboardRole == RegistrationWriteRole);
        user.IsEnabled = false;
        b.Database.UpdateProxyUser(user);
        b.Controller.Access.InvalidateCache();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(b, "/api/mappings", key)).StatusCode);
    }

    [Fact]
    public async Task ApiKey_WorksFromTheXApiKeyHeader_AndNeedsNoDashboardLogin()
    {
        await using Bundle b = await CreateBundleAsync();

        // No dashboard username and no password at all: that is what a service account looks like.
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Service Account",
            dashboardLoginEnabled = false,
            dashboardRole = RegistrationWriteRole,
        });
        created.EnsureSuccessStatusCode();
        int userId;
        using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
            userId = doc.RootElement.GetProperty("id").GetInt32();

        HttpResponseMessage keyResponse = await b.Client.PostAsJsonAsync($"/api/users/{userId}/keys", new { label = "ci" });
        keyResponse.EnsureSuccessStatusCode();
        string key;
        using (JsonDocument doc = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync()))
            key = doc.RootElement.GetProperty("plainText").GetString()!;

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/mappings");
        request.Headers.Add("x-api-key", key);
        Assert.Equal(HttpStatusCode.OK, (await b.Anonymous.SendAsync(request)).StatusCode);

        // The service account also still works through the dashboard login it does not have.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(b, "/api/users", key)).StatusCode);
    }

    [Fact]
    public async Task MappingList_StaysReadableByAnySignedInSession()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Read Only Person",
            dashboardLoginEnabled = true,
            dashboardUsername = "readonly.person",
            dashboardPassword = "read only password 1",
            dashboardRole = DashboardRoles.ReadOnly,
        });
        created.EnsureSuccessStatusCode();

        using HttpClient client = DashboardTestSession.CreateClient(b.Host.Url!, useCookies: true);
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "readonly.person", password = "read only password 1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The Usage filters and the user editor's model picker are built from this list, so the
        // cookie route stays open to any session even though the API-key route needs a grant.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/mappings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/mappings", new { OllamaName = "x" })).StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_IsServedToRegistrationKeys_AndRefusedToEveryoneElse()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, RegistrationReadRole);

        HttpResponseMessage response = await GetAsync(b, "/api/mappings/openapi.json", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.StartsWith("3.", doc.RootElement.GetProperty("openapi").GetString());
            Assert.True(doc.RootElement.GetProperty("paths").TryGetProperty("/api/mappings", out _));
        }

        // Anonymous callers are told to sign in; a role without the registration grant is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(b, "/api/mappings/openapi.json", null)).StatusCode);
        string plainKey = await CreateServiceAccountKeyAsync(b, DashboardRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(b, "/api/mappings/openapi.json", plainKey)).StatusCode);

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Plain Read Only",
            dashboardLoginEnabled = true,
            dashboardUsername = "plain.readonly",
            dashboardPassword = "plain read only password 1",
            dashboardRole = DashboardRoles.ReadOnly,
        });
        created.EnsureSuccessStatusCode();

        using HttpClient client = DashboardTestSession.CreateClient(b.Host.Url!, useCookies: true);
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "plain.readonly", password = "plain read only password 1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/mappings/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_IsNotServedWhenTheToggleIsOff()
    {
        await using Bundle b = await CreateBundleAsync();
        string key = await CreateServiceAccountKeyAsync(b, RegistrationReadRole);

        // The document is advertised while it is published...
        Assert.True(await RegistrationExplorerEnabledAsync(b));

        b.Controller.Settings.EnableRegistrationApiSpec = false;

        // ...and switching it off both stops serving it and hides it from the caller's explorer state.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(b, "/api/mappings/openapi.json", key)).StatusCode);
        Assert.False(await RegistrationExplorerEnabledAsync(b));

        // The routes the document describes keep working; only the description goes away.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(b, "/api/mappings", key)).StatusCode);
    }

    [Fact]
    public async Task ExplorerState_IsReportedOnlyToSignedInCallers()
    {
        await using Bundle b = await CreateBundleAsync();

        // The login page probes this endpoint, so an anonymous caller must not learn the topology.
        // A null property is omitted from the payload entirely, so both shapes mean "not told".
        using (JsonDocument anonymous = JsonDocument.Parse(await (await b.Anonymous.GetAsync("/api/auth/me")).Content.ReadAsStringAsync()))
        {
            bool present = anonymous.RootElement.TryGetProperty("explorers", out JsonElement anonymousExplorers);
            Assert.True(!present || anonymousExplorers.ValueKind == JsonValueKind.Null);
        }

        using JsonDocument signedIn = JsonDocument.Parse(await (await b.Client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        JsonElement explorers = signedIn.RootElement.GetProperty("explorers");
        Assert.Equal("proxy", explorers.EnumerateObject().Select(p => p.Name).First());

        // The proxy explorer defaults to off; its paths are reported either way so the Dashboard card
        // can show them as soon as the toggle is saved.
        JsonElement proxy = explorers.GetProperty("proxy");
        Assert.False(proxy.GetProperty("enabled").GetBoolean());
        Assert.Equal("/scalar", proxy.GetProperty("scalarPath").GetString());
        Assert.Equal("/openapi/v1/openapi.json", proxy.GetProperty("specPath").GetString());

        JsonElement registration = explorers.GetProperty("registration");
        Assert.True(registration.GetProperty("enabled").GetBoolean());
        Assert.Equal("/api/mappings/openapi.json", registration.GetProperty("path").GetString());
    }

    [Fact]
    public async Task McpExplorer_IsReportedWithTheConfiguredEndpointWhileTheServerIsStopped()
    {
        await using Bundle b = await CreateBundleAsync();

        McpServerSettings mcp = b.Controller.McpServer.LoadSettings();
        mcp.Enabled = false;
        mcp.EnableApiExplorer = true;
        mcp.ListenAddress = "localhost";
        mcp.ListenPort = 7420;
        b.Controller.McpServer.SaveSettings(mcp);

        // A stopped service reports an empty endpoint, so the state has to come from the saved
        // settings: the Dashboard still owes the caller a usable link, marked as stopped.
        Assert.False(b.Controller.McpServer.IsRunning);

        // Both payloads carry it, because the Dashboard polls /api/status and the Help tab reads the
        // identity once.
        foreach (string path in new[] { "/api/auth/me", "/api/status" })
        {
            using JsonDocument doc = JsonDocument.Parse(await (await b.Client.GetAsync(path)).Content.ReadAsStringAsync());
            JsonElement state = doc.RootElement.GetProperty("explorers").GetProperty("mcp");
            Assert.True(state.GetProperty("enabled").GetBoolean());
            Assert.Equal("localhost", state.GetProperty("listenAddress").GetString());
            Assert.Equal(7420, state.GetProperty("listenPort").GetInt32());
            Assert.Equal("/scalar", state.GetProperty("scalarPath").GetString());
        }
    }

    [Fact]
    public async Task RolesEndpoint_AdvertisesTheRegistrationRoles()
    {
        await using Bundle b = await CreateBundleAsync();

        using JsonDocument doc = JsonDocument.Parse(await (await b.Client.GetAsync("/api/auth/roles")).Content.ReadAsStringAsync());
        Dictionary<string, string[]> roles = doc.RootElement.GetProperty("roles").EnumerateArray()
            .ToDictionary(
                role => role.GetProperty("key").GetString()!,
                role => role.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToArray());

        Assert.Equal(["ViewRegistration"], roles[RegistrationReadRole]);
        Assert.Equal(["ViewRegistration", "ManageRegistration"], roles[RegistrationWriteRole]);
    }

    private static async Task<string> CreateServiceAccountKeyAsync(Bundle b, string role)
    {
        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/users", new
        {
            name = $"Service {role}",
            dashboardLoginEnabled = false,
            dashboardRole = role,
        });
        created.EnsureSuccessStatusCode();
        int userId;
        using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
            userId = doc.RootElement.GetProperty("id").GetInt32();

        HttpResponseMessage keyResponse = await b.Client.PostAsJsonAsync($"/api/users/{userId}/keys", new { label = "service" });
        keyResponse.EnsureSuccessStatusCode();
        using JsonDocument keyDoc = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
        return keyDoc.RootElement.GetProperty("plainText").GetString()!;
    }

    private static Task<HttpResponseMessage> GetAsync(Bundle b, string path, string? key)
        => SendAsync(b, HttpMethod.Get, path, key, null);

    /// <summary>
    /// Reads the registration entry from the explorer state that /api/auth/me reports. The endpoint
    /// resolves the session cookie itself and has no API-key fallback, so this reads it as the admin.
    /// </summary>
    private static async Task<bool> RegistrationExplorerEnabledAsync(Bundle b)
    {
        using JsonDocument doc = JsonDocument.Parse(await (await b.Client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        JsonElement explorers = doc.RootElement.GetProperty("explorers");
        return explorers.ValueKind == JsonValueKind.Object
            && explorers.GetProperty("registration").GetProperty("enabled").GetBoolean();
    }

    private static async Task<HttpResponseMessage> SendAsync(Bundle b, HttpMethod method, string path, string? key, object? body)
    {
        using HttpRequestMessage request = new(method, path);
        if (key is not null)
            request.Headers.Add("Authorization", $"Bearer {key}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await b.Anonymous.SendAsync(request);
    }

    private static async Task<Bundle> CreateBundleAsync()
    {
        string dbDirectory = Path.Combine(Path.GetTempPath(), $"kaeo-registration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbDirectory);
        string dbPath = Path.Combine(dbDirectory, "kaeo.db");
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
            try
            {
                string? directory = Path.GetDirectoryName(DbPath);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A lingering file handle is not worth failing teardown over.
            }
        }
    }
}
