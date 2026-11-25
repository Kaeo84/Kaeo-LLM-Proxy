using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Endpoint tests for the ASP.NET Core dashboard: real Kestrel on ephemeral ports driven by an
/// HttpClient, mirroring the HttpListener E2E pattern. Covers status, incremental log polling,
/// mappings CRUD round-trip, settings validation, auth enforcement, credential secret redaction,
/// proxy control, and an HTTPS smoke test with a self-signed certificate.
/// </summary>
[Collection("AppLogger")]
public sealed class WebDashboardEndpointTests
{
    [Fact]
    public async Task Status_OnLoopback_IsOpen_AndReturnsShape()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("proxy", out _));
        Assert.True(doc.RootElement.TryGetProperty("dashboard", out JsonElement dash));
        Assert.Equal("open", dash.GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task Root_ServesDashboardIndexHtml()
    {
        await using Bundle b = await CreateBundleAsync();
        HttpResponseMessage response = await b.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("js/app.js", html);
        Assert.Contains("lib/jquery-3.7.1.min.js", html);
    }

    [Fact]
    public async Task StaticAssets_AreServedFromWwwroot()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage js = await b.Client.GetAsync("/js/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage css = await b.Client.GetAsync("/css/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage jquery = await b.Client.GetAsync("/lib/jquery-3.7.1.min.js");
        Assert.Equal(HttpStatusCode.OK, jquery.StatusCode);
        Assert.Contains("jQuery", await jquery.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logs_IncrementalPoll_ReturnsEntriesThenEmptyAfterCursor()
    {
        await using Bundle b = await CreateBundleAsync();
        b.Controller.Stats.AddLog(new RequestLog
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.Now,
            Method = "POST",
            OllamaPath = "/api/chat",
            Model = "test-model",
            Status = RequestStatus.Success,
            StatusCode = 200,
        });

        using JsonDocument first = JsonDocument.Parse(await b.Client.GetStringAsync("/api/logs?source=proxy"));
        JsonElement entries = first.RootElement.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        long next = first.RootElement.GetProperty("nextAfter").GetInt64();
        Assert.True(next > 0);

        using JsonDocument second = JsonDocument.Parse(await b.Client.GetStringAsync($"/api/logs?source=proxy&after={next}"));
        Assert.Equal(0, second.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Mappings_CrudRoundTrip_PersistsToDatabase()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage created = await b.Client.PostAsJsonAsync("/api/mappings", new ModelMapping
        {
            ProxyName = "proxy-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:1",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int id = createdDoc.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        using JsonDocument list = JsonDocument.Parse(await b.Client.GetStringAsync("/api/mappings"));
        Assert.Contains(list.RootElement.EnumerateArray(), e => e.GetProperty("id").GetInt32() == id);
        Assert.Contains(b.Database.LoadModelMappings(), m => m.Id == id);

        HttpResponseMessage updated = await b.Client.PutAsJsonAsync($"/api/mappings/{id}", new ModelMapping
        {
            ProxyName = "renamed",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:2",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains(b.Database.LoadModelMappings(), m => m.Id == id && m.ProxyName == "renamed");

        HttpResponseMessage deleted = await b.Client.DeleteAsync($"/api/mappings/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain(b.Database.LoadModelMappings(), m => m.Id == id);

        HttpResponseMessage missing = await b.Client.DeleteAsync($"/api/mappings/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Settings_Put_ValidatesAndApplies()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage bad = await b.Client.PutAsJsonAsync("/api/settings", new { listenPort = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        HttpResponseMessage good = await b.Client.PutAsJsonAsync("/api/settings", new { maxLogEntries = 250 });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await good.Content.ReadAsStringAsync());
        Assert.Equal(250, doc.RootElement.GetProperty("maxLogEntries").GetInt32());
        Assert.Equal(250, b.Controller.Settings.MaxLogEntries);
    }

    [Fact]
    public async Task Settings_Get_NeverExposesPassphraseOrSecrets()
    {
        await using Bundle b = await CreateBundleAsync(configureSettings: s =>
        {
            s.SecurityPassphrase = "hash:should-not-leak";
            s.WebDashboard.ApiKey = "dashboard-key-should-not-leak";
        });

        string body = await b.Client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("should-not-leak", body);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("securityPassphrase", out _));
        Assert.True(doc.RootElement.GetProperty("webDashboard").GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task Auth_ReadOnlyKey_CanReadLogs_ButCannotAccessOtherApiEndpoints()
    {
        await using Bundle b = await CreateBundleAsync(wd =>
        {
            wd.ApiKey = "admin-key";
            wd.ReadOnlyApiKey = "viewer-key";
            wd.RequireApiKey = true;
        });

        using HttpRequestMessage identityRequest = new(HttpMethod.Get, "/api/auth/me");
        identityRequest.Headers.Add("X-Dashboard-Api-Key", "viewer-key");
        HttpResponseMessage identity = await b.Client.SendAsync(identityRequest);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        using JsonDocument identityDoc = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
        Assert.Equal("readOnly", identityDoc.RootElement.GetProperty("role").GetString());

        using HttpRequestMessage logsRequest = new(HttpMethod.Get, "/api/logs?source=proxy&after=0&limit=10");
        logsRequest.Headers.Add("X-Dashboard-Api-Key", "viewer-key");
        HttpResponseMessage logs = await b.Client.SendAsync(logsRequest);
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);

        using HttpRequestMessage settingsRequest = new(HttpMethod.Get, "/api/settings");
        settingsRequest.Headers.Add("X-Dashboard-Api-Key", "viewer-key");
        HttpResponseMessage settings = await b.Client.SendAsync(settingsRequest);
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);

        using HttpRequestMessage mutationRequest = new(HttpMethod.Post, "/api/credentials")
        {
            Content = JsonContent.Create(new { name = "blocked", secret = "secret" }),
        };
        mutationRequest.Headers.Add("X-Dashboard-Api-Key", "viewer-key");
        HttpResponseMessage mutation = await b.Client.SendAsync(mutationRequest);
        Assert.Equal(HttpStatusCode.Forbidden, mutation.StatusCode);

        using HttpRequestMessage adminRequest = new(HttpMethod.Get, "/api/settings");
        adminRequest.Headers.Add("X-Dashboard-Api-Key", "admin-key");
        HttpResponseMessage admin = await b.Client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        string settingsJson = await admin.Content.ReadAsStringAsync();
        Assert.DoesNotContain("viewer-key", settingsJson);
        using JsonDocument settingsDoc = JsonDocument.Parse(settingsJson);
        Assert.True(settingsDoc.RootElement.GetProperty("webDashboard").GetProperty("hasReadOnlyApiKey").GetBoolean());

        using HttpRequestMessage clearRequest = new(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new { webDashboard = new { clearReadOnlyApiKey = true } }),
        };
        clearRequest.Headers.Add("X-Dashboard-Api-Key", "admin-key");
        HttpResponseMessage clear = await b.Client.SendAsync(clearRequest);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        using HttpRequestMessage revokedRequest = new(HttpMethod.Get, "/api/logs?source=proxy&after=0&limit=1");
        revokedRequest.Headers.Add("X-Dashboard-Api-Key", "viewer-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Client.SendAsync(revokedRequest)).StatusCode);
    }

    [Fact]
    public async Task Auth_ReadOnlyKey_RequiresDistinctAdministratorKey()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage withoutAdmin = await b.Client.PutAsJsonAsync("/api/settings",
            new { webDashboard = new { readOnlyApiKey = "viewer-key" } });
        Assert.Equal(HttpStatusCode.BadRequest, withoutAdmin.StatusCode);

        HttpResponseMessage sameKey = await b.Client.PutAsJsonAsync("/api/settings",
            new { webDashboard = new { apiKey = "same-key", readOnlyApiKey = "same-key" } });
        Assert.Equal(HttpStatusCode.BadRequest, sameKey.StatusCode);
    }

    [Fact]
    public async Task Auth_RequireApiKey_OnLoopback_EnforcesBearerToken()
    {
        await using Bundle b = await CreateBundleAsync(wd =>
        {
            wd.RequireApiKey = true;
            wd.ApiKey = "test-key";
        });

        HttpResponseMessage anonymous = await b.Client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        HttpResponseMessage rejected = await b.Client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var correct = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        correct.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        HttpResponseMessage accepted = await b.Client.SendAsync(correct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var header = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        header.Headers.Add("X-Dashboard-Api-Key", "test-key");
        HttpResponseMessage viaHeader = await b.Client.SendAsync(header);
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
    }

    [Fact]
    public async Task Auth_RequireApiKey_WithoutConfiguredKey_RejectsAll()
    {
        await using Bundle b = await CreateBundleAsync(wd => wd.RequireApiKey = true);
        HttpResponseMessage response = await b.Client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Credentials_PostThenGet_NeverLeaksSecretMaterial()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage posted = await b.Client.PostAsJsonAsync("/api/credentials", new
        {
            name = "cred-1",
            description = "test credential",
            secret = "SUPER-SECRET-VALUE",
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        string body = await b.Client.GetStringAsync("/api/credentials");
        Assert.DoesNotContain("SUPER-SECRET-VALUE", body);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement cred = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "cred-1");
        Assert.True(cred.GetProperty("hasSecretMaterial").GetBoolean());
        Assert.False(cred.TryGetProperty("secret", out _));
        Assert.False(cred.TryGetProperty("privateKey", out _));

        HttpResponseMessage deleted = await b.Client.DeleteAsync("/api/credentials/cred-1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Proxy_StartStop_TogglesIsRunning()
    {
        await using Bundle b = await CreateBundleAsync();

        HttpResponseMessage started = await b.Client.PostAsync("/api/proxy/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using JsonDocument startDoc = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        Assert.True(startDoc.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.True(b.Controller.Server.IsRunning);

        HttpResponseMessage stopped = await b.Client.PostAsync("/api/proxy/stop", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        using JsonDocument stopDoc = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
        Assert.False(stopDoc.RootElement.GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task McpAndModules_ReturnOk()
    {
        await using Bundle b = await CreateBundleAsync();
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/modules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/stats")).StatusCode);
    }

    [Fact]
    public async Task Https_WithSelfSignedCertificate_ServesStatus()
    {
        string certPath = Path.Combine(Path.GetTempPath(), $"kaeo-web-{Guid.NewGuid():N}.pfx");
        using (RSA rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            await File.WriteAllBytesAsync(certPath, cert.Export(X509ContentType.Pfx, "test-pass"));
        }

        try
        {
            await using Bundle b = await CreateBundleAsync(wd =>
            {
                wd.Scheme = "Https";
                wd.CertificatePath = certPath;
                wd.CertificatePassword = "test-pass";
            }, certPath: certPath);

            Assert.StartsWith("https://", b.Host.Url);
            HttpResponseMessage response = await b.Client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TryDelete(certPath);
        }
    }

    private static async Task<Bundle> CreateBundleAsync(
        Action<WebDashboardSettings>? configureDashboard = null,
        Action<AppSettings>? configureSettings = null,
        string? certPath = null)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-web-{Guid.NewGuid():N}.db");
        var settings = new AppSettings
        {
            ListenPort = GetFreePort(),
            ListenAddress = "localhost",
            AutoStartProxy = false,
            MaxLogEntries = 100,
        };
        settings.Logging.ApplicationDatabasePath = dbPath;
        configureSettings?.Invoke(settings);

        var database = new AppDatabase(settings.Logging);
        var moduleHost = new ModuleHost(database, settings);
        var controller = new ProxyHostController(settings, database, moduleHost);

        settings.WebDashboard.Enabled = true;
        settings.WebDashboard.Port = GetFreePort();
        settings.WebDashboard.ListenAddress = "localhost";
        configureDashboard?.Invoke(settings.WebDashboard);

        var host = new WebDashboardHost(settings, controller);
        await host.StartAsync();

        return new Bundle
        {
            Host = host,
            Controller = controller,
            Database = database,
            Client = CreateClient(host.Url!),
            DbPath = dbPath,
            CertPath = certPath,
        };
    }

    private static HttpClient CreateClient(string url)
    {
        var handler = new HttpClientHandler();
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp artifacts.
        }
    }

    private sealed class Bundle : IAsyncDisposable
    {
        public required WebDashboardHost Host { get; init; }
        public required ProxyHostController Controller { get; init; }
        public required AppDatabase Database { get; init; }
        public required HttpClient Client { get; init; }
        public required string DbPath { get; init; }
        public string? CertPath { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Controller.DisposeAsync();
            Database.Dispose();
            TryDelete(DbPath);
            TryDelete(DbPath + "-shm");
            TryDelete(DbPath + "-wal");
            if (CertPath is not null)
                TryDelete(CertPath);
        }
    }
}