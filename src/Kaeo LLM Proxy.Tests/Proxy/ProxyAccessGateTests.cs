using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// HTTP-level tests for the proxy access gate: RequireApiKey enforcement, open-mode behavior,
/// per-key model restrictions on chat/discovery endpoints, and the guarantee that a Kaeo client
/// key is never relayed to the upstream. Uses the real HttpListener pipeline against a stub
/// upstream, mirroring <see cref="E2EProxyIntegrationTests"/>.
/// </summary>
[Collection("AppLogger")]
public sealed class ProxyAccessGateTests : IAsyncDisposable
{
    private readonly int _proxyPort;
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly ModuleHost _moduleHost;
    private readonly StatisticsService _stats;
    private readonly McpServerService _mcpServer;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _proxy;
    private readonly StubUpstream _upstream;
    private readonly ProxyAccessService _access;
    private readonly HttpClient _client;

    public ProxyAccessGateTests()
    {
        (_upstream, int stubPort) = StubUpstream.StartOnFreePort();

        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-gate-{Guid.NewGuid():N}.db");
        _settings = new AppSettings
        {
            ListenAddress = "localhost",
            MaxLogEntries = 100,
            MaxConcurrentRequests = 4,
            ModelMappings =
            [
                new ModelMapping
                {
                    ProxyName = "alpha",
                    ModelName = "alpha",
                    UpstreamUrl = $"http://localhost:{stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                },
                new ModelMapping
                {
                    ProxyName = "beta",
                    ModelName = "beta",
                    UpstreamUrl = $"http://localhost:{stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                },
            ],
        };
        _settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(_settings.Logging);
        _moduleHost = new ModuleHost(_database, _settings);
        _stats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours);
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _stats);
        _access = new ProxyAccessService(_database);
        _handler = new OllamaProxyHandler(_settings, _stats, _moduleHost, _mcpServer, access: _access);
        _proxy = new ProxyServer(_handler);

        for (int attempt = 0; ; attempt++)
        {
            _proxyPort = GetFreePort();
            try
            {
                _proxy.Start(_proxyPort, "localhost", _settings.MaxConcurrentRequests);
                break;
            }
            catch (HttpListenerException) when (attempt < 4) { }
        }

        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_proxyPort}"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _proxy.Dispose();
        _handler.Dispose();
        await _mcpServer.DisposeAsync();
        _stats.Dispose();
        _database.Dispose();
        _upstream.Dispose();

        foreach (string path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string CreateKey(List<string>? allowedModels = null, bool userEnabled = true)
    {
        ProxyUser user = new() { Name = "Tester", Email = "t@example.com", CreatedUtc = DateTime.UtcNow, IsEnabled = userEnabled };
        _database.InsertProxyUser(user);
        (string plainText, string hash, string prefix) = ProxyAccessService.GenerateKey();
        _database.InsertProxyApiKey(new ProxyApiKey
        {
            UserId = user.Id,
            Label = "gate-test",
            KeyHash = hash,
            KeyPrefix = prefix,
            CreatedUtc = DateTime.UtcNow,
            AllowedModels = allowedModels ?? [],
        });
        _access.InvalidateCache();
        return plainText;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? key = null, string? body = null)
    {
        HttpRequestMessage request = new(method, path);
        if (key is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static string ChatBody(string model) =>
        $"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}],\"stream\":false}}";

    [Fact]
    public async Task RequireApiKey_NoKey_ModelEndpointsReturn401_ButProbesStayPublic()
    {
        _settings.ProxySecurity.RequireApiKey = true;

        HttpResponseMessage tags = await _client.SendAsync(Request(HttpMethod.Get, "/api/tags"));
        Assert.Equal(HttpStatusCode.Unauthorized, tags.StatusCode);

        HttpResponseMessage chat = await _client.SendAsync(Request(HttpMethod.Post, "/api/chat", body: ChatBody("alpha")));
        Assert.Equal(HttpStatusCode.Unauthorized, chat.StatusCode);

        HttpResponseMessage health = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        HttpResponseMessage version = await _client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
    }

    [Fact]
    public async Task RequireApiKey_InvalidKey_Returns401()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/tags", key: "kaeo-" + new string('0', 48)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequireApiKey_DisabledUsersKey_Returns401()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        string key = CreateKey(userEnabled: false);
        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Get, "/api/tags", key));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequireApiKey_ValidKey_Succeeds()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        string key = CreateKey();
        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Get, "/api/tags", key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task XApiKeyHeader_AuthenticatesToo()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        string key = CreateKey();
        HttpRequestMessage request = new(HttpMethod.Get, "/api/tags");
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenMode_NoKey_StaysOpen()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task OpenMode_UnknownKey_IsIgnored()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/tags", key: "kaeo-" + new string('0', 48)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RestrictedKey_DiscoveryListsOnlyAllowedModels()
    {
        string key = CreateKey(["alpha"]);

        HttpResponseMessage tags = await _client.SendAsync(Request(HttpMethod.Get, "/api/tags", key));
        using JsonDocument tagsDoc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync());
        JsonElement tagModels = tagsDoc.RootElement.GetProperty("models");
        Assert.Equal(1, tagModels.GetArrayLength());
        Assert.Equal("alpha", tagModels[0].GetProperty("name").GetString());

        HttpResponseMessage models = await _client.SendAsync(Request(HttpMethod.Get, "/v1/models", key));
        using JsonDocument modelsDoc = JsonDocument.Parse(await models.Content.ReadAsStringAsync());
        JsonElement data = modelsDoc.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("alpha", data[0].GetProperty("id").GetString());

        HttpResponseMessage ps = await _client.SendAsync(Request(HttpMethod.Get, "/api/ps", key));
        using JsonDocument psDoc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync());
        Assert.Equal(1, psDoc.RootElement.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task RestrictedKey_DisallowedModelLookup_Returns403()
    {
        string key = CreateKey(["alpha"]);

        HttpResponseMessage lookup = await _client.SendAsync(Request(HttpMethod.Get, "/v1/models/beta", key));
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
        Assert.Equal("model_access_denied", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        HttpResponseMessage show = await _client.SendAsync(
            Request(HttpMethod.Post, "/api/show", key, "{\"model\":\"beta\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, show.StatusCode);
    }

    [Fact]
    public async Task RestrictedKey_DisallowedChat_Returns403_WithoutCallingUpstream()
    {
        string key = CreateKey(["alpha"]);
        int before = _upstream.RequestCount;

        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Post, "/api/chat", key, ChatBody("beta")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, _upstream.RequestCount);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("model_access_denied", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RestrictedKey_AllowedChat_Succeeds_AndKeyIsNotRelayedUpstream()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        string key = CreateKey(["alpha"]);

        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Post, "/api/chat", key, ChatBody("alpha")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("kaeo-", _upstream.LastAuthorization ?? "");
    }

    [Fact]
    public async Task RestrictedKey_PassthroughDisallowedModel_Returns403()
    {
        string key = CreateKey(["alpha"]);
        HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Post, "/v1/chat/completions", key, ChatBody("beta")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RestrictedKey_PassthroughAllowedModel_Succeeds_AndKeyIsNotRelayedUpstream()
    {
        _settings.ProxySecurity.RequireApiKey = true;
        string key = CreateKey(["alpha"]);

        HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Post, "/v1/chat/completions", key, ChatBody("alpha")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("kaeo-", _upstream.LastAuthorization ?? "");
    }

    [Fact]
    public async Task RestrictedKey_CompactDisallowedModel_Returns403()
    {
        string key = CreateKey(["alpha"]);
        HttpResponseMessage response = await _client.SendAsync(
            Request(HttpMethod.Post, "/v1/chat/completions/compact", key, ChatBody("beta")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnrestrictedKey_ChatSucceeds_ForAnyModel()
    {
        string key = CreateKey();
        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Post, "/api/chat", key, ChatBody("beta")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ValidKey_ChatLogIsAttributedToTheKeysUser()
    {
        string key = CreateKey();
        ProxyUser user = _database.LoadProxyUsers().Single(u => u.Name == "Tester");
        HttpResponseMessage response = await _client.SendAsync(Request(HttpMethod.Post, "/api/chat", key, ChatBody("alpha")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The stats writer persists asynchronously; poll briefly for the attributed row.
        long? userId = null;
        bool found = false;
        for (int attempt = 0; attempt < 50 && !found; attempt++)
        {
            using (SqliteConnection connection = new($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT user_id FROM requests ORDER BY id DESC LIMIT 1;";
                object? result = await command.ExecuteScalarAsync();
                if (result is not null)
                {
                    found = true;
                    userId = result is DBNull ? null : Convert.ToInt64(result);
                }
            }
            if (!found) await Task.Delay(100);
        }

        Assert.True(found, "The chat request was never persisted.");
        Assert.Equal(user.Id, userId);
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Minimal OpenAI-compatible upstream that records the Authorization header it saw.</summary>
    private sealed class StubUpstream : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private volatile string? _lastAuthorization;
        private int _requestCount;

        public string? LastAuthorization => _lastAuthorization;
        public int RequestCount => Volatile.Read(ref _requestCount);

        private StubUpstream(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static (StubUpstream Server, int Port) StartOnFreePort()
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = GetFreePort();
                try { return (new StubUpstream(port), port); }
                catch (HttpListenerException) when (attempt < 4) { }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                Interlocked.Increment(ref _requestCount);
                _lastAuthorization = ctx.Request.Headers["Authorization"];
                using (StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8))
                    await reader.ReadToEndAsync();

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                string json = """
                {
                  "id":"stub","object":"chat.completion",
                  "choices":[{"index":0,"message":{"role":"assistant","content":"Test"},"finish_reason":"stop"}],
                  "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}
                }
                """;
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                    await ctx.Response.OutputStream.WriteAsync(err);
                }
                catch { /* response already closed */ }
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
            _cts.Dispose();
        }
    }
}