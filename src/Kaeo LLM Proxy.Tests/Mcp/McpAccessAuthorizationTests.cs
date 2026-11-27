using System.Net;
using System.Net.Sockets;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// HTTP-level tests for MCP authentication: the configured shared credential keeps full access,
/// proxy API keys are accepted as bearer tokens, an ungranted Read Only key is refused with 403,
/// and Admin keys bypass the per-key MCP grant.
/// </summary>
[Collection("AppLogger")]
public sealed class McpAccessAuthorizationTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly StatisticsService _stats;
    private readonly ProxyAccessService _access;
    private readonly McpServerHost _host;
    private readonly HttpClient _client;

    public McpAccessAuthorizationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-mcp-auth-{Guid.NewGuid():N}.db");
        _settings = new AppSettings { ListenAddress = "localhost", MaxLogEntries = 50, MaxConcurrentRequests = 4 };
        _settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(_settings.Logging);
        _stats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours);
        _access = new ProxyAccessService(_database);

        McpServerSettings mcpSettings = new()
        {
            Enabled = true,
            ListenAddress = "localhost",
            ListenPort = GetFreePort(),
            AuthCredentialName = null,
        };

        _host = new McpServerHost(
            mcpSettings,
            new FakeSecrets(),
            static _ => new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "test", Version = "1.0.0" },
            },
            _stats,
            _access);

        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{mcpSettings.ListenPort}"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task InitializeAsync() => await _host.StartAsync();

    private string CreateKey(string role = "readOnly", bool mcpAccess = false)
    {
        // MCP access is a user-level grant; the key inherits it.
        ProxyUser user = new()
        {
            Name = "Tester",
            Email = "t@example.com",
            CreatedUtc = DateTime.UtcNow,
            IsEnabled = true,
            DashboardRole = role,
            AllowedModels = [],
            McpAccess = mcpAccess,
        };
        _database.InsertProxyUser(user);

        (string plainText, string hash, string prefix) = ProxyAccessService.GenerateKey();
        _database.InsertProxyApiKey(new ProxyApiKey
        {
            UserId = user.Id,
            Label = "mcp-test",
            KeyHash = hash,
            KeyPrefix = prefix,
            CreatedUtc = DateTime.UtcNow,
        });
        _access.InvalidateCache();
        return plainText;
    }

    private async Task<HttpResponseMessage> PostInitializeAsync(string? bearer)
    {
        HttpRequestMessage request = new(HttpMethod.Post, McpServerHost.McpPath)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task MissingBearer_IsRejected401()
    {
        using HttpResponseMessage response = await PostInitializeAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownKey_IsRejected401()
    {
        using HttpResponseMessage response = await PostInitializeAsync("kaeo-" + new string('0', 48));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyKey_WithoutMcpGrant_IsRejected403()
    {
        string key = CreateKey();
        using HttpResponseMessage response = await PostInitializeAsync(key);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyKey_WithMcpGrant_PassesTheAuthGate()
    {
        string key = CreateKey(mcpAccess: true);
        using HttpResponseMessage response = await PostInitializeAsync(key);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminKey_WithoutExplicitGrant_PassesTheAuthGate()
    {
        string key = CreateKey(role: "admin");
        using HttpResponseMessage response = await PostInitializeAsync(key);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public IReadOnlyList<string> ListCredentialNames() => [];
        public string? ResolveSecret(string credentialName) => null;
        public CredentialMaterial? ResolveCredential(string credentialName) => null;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();

        foreach (string path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}