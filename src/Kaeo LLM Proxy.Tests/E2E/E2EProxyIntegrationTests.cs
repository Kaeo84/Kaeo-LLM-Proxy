using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// End-to-end integration tests that stand up the real Kaeo LLM Proxy against a minimal
/// stub upstream server, then send actual HTTP requests through the proxy and verify the
/// response.  The stub answers every chat‑completions POST with a single token "Test",
/// confirming that the full request pipeline (listener → handler → upstream translation →
/// reply translation) works end‑to‑end.
/// </summary>
[Collection("AppLogger")]
public sealed class E2EProxyIntegrationTests : IAsyncDisposable
{
    /// <summary>
    /// Ports are probed from the OS at construction time instead of hard-coded constants, so the
    /// suite cannot collide with other processes (or parallel runs) already owning a fixed port.
    /// The probe is inherently TOCTOU — see <see cref="GetFreePort"/> — so both binds retry.
    /// </summary>
    private readonly int _stubPort;
    private readonly int _proxyPort;

    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly ModuleHost _moduleHost;
    private readonly StatisticsService _stats;
    private readonly McpServerService _mcpServer;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _proxy;

    private readonly StubUpstreamServer _upstream;

    public E2EProxyIntegrationTests()
    {
        (StubUpstreamServer upstream, int stubPort) = StartStubOnFreePort();
        _upstream = upstream;
        _stubPort = stubPort;
        _upstream.Start();

        _proxyPort = GetFreePort();
        while (_proxyPort == _stubPort)
            _proxyPort = GetFreePort();

        // ── database + settings ─────────────────────────────────────────
        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-e2e-{Guid.NewGuid():N}.db");
        _settings = new AppSettings
        {
            ListenPort = _proxyPort,
            ListenAddress = "localhost",
            MaxLogEntries = 100,
            MaxConcurrentRequests = 4,
            ModelMappings =
            [
                new ModelMapping
                {
                    ProxyName         = "test-model",
                    ModelName         = "test-model",
                    UpstreamUrl       = $"http://localhost:{_stubPort}/v1",
                    UpstreamType      = UpstreamType.OpenAI,
                    IsEnabled         = true,
                    ContextWindowTokens = 32_768,
                },
            ],
        };
        _settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(_settings.Logging);
        _moduleHost = new ModuleHost(_database, _settings);

        // ── services ────────────────────────────────────────────────────
        _stats    = new StatisticsService(_settings.MaxLogEntries, _database,
                                          _settings.Logging.LogRetentionHours);
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _stats, new ProxyAccessService(_database));
        _handler  = new OllamaProxyHandler(_settings, _stats, _moduleHost, _mcpServer);
        _proxy    = new ProxyServer(_handler);

        // Same TOCTOU window as the stub bind: if another process claimed the probed port before
        // the listener could reserve it, retry on a fresh one.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _proxy.Start(_proxyPort, "localhost", _settings.MaxConcurrentRequests);
                break;
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                _proxyPort = GetFreePort();
                _settings.ListenPort = _proxyPort;
            }
        }
    }

    /// <summary>
    /// xUnit awaits this instead of blocking a thread-pool thread on
    /// <c>_mcpServer.DisposeAsync().GetAwaiter().GetResult()</c> (VSTHRD002).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _proxy.Dispose();
        _handler.Dispose();
        await _mcpServer.DisposeAsync();
        _stats.Dispose();
        _database.Dispose();
        _upstream.Dispose();

        TryDeleteFile(_dbPath);
        TryDeleteFile(_dbPath + "-shm");
        TryDeleteFile(_dbPath + "-wal");
    }

    /// <summary>
    /// Best-effort cleanup: a locked or missing temp database must not fail an otherwise passing
    /// test, but the reason is surfaced on stderr instead of being silently swallowed.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"E2E cleanup: could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// OS-assigned loopback port. The port is only guaranteed free between the probe releasing it
    /// and the real server re-binding it, so every bind in this class retries on conflict.
    /// </summary>
    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static (StubUpstreamServer Server, int Port) StartStubOnFreePort()
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = GetFreePort();
            try
            {
                return (new StubUpstreamServer(port), port);
            }
            catch (HttpListenerException) when (attempt < 4)
            {
            }
        }
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SimpleChat_ReturnsTest()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream = false,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/api/chat", content);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        string? reply = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Assert.Equal("Test", reply);
    }

    [Fact]
    public async Task StreamingChat_ReturnsTestToken()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream = true,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/api/chat", content);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();

        // Ollama NDJSON stream — each line is a JSON chunk.
        // The last chunk with done=true should carry message.content = "Test".
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? contentToken = null;
        foreach (string line in lines)
        {
            using JsonDocument chunk = JsonDocument.Parse(line);
            if (chunk.RootElement.TryGetProperty("message", out JsonElement msg)
                && msg.TryGetProperty("content", out JsonElement c)
                && c.GetString() is { Length: > 0 } text)
            {
                contentToken = text;
            }
        }

        Assert.Equal("Test", contentToken);
    }

    [Fact]
    public async Task Chat_ViaOpenAiSurface_ReturnsTest()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream = false,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", content);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        string? reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Assert.Contains("Test", reply);
    }

    /// <summary>
    /// Regression test: the proxy rebuilds the request body into a fresh <c>ByteArrayContent</c>
    /// that already carries <c>application/json</c>.  Real clients (the OpenAI SDK used by the VS
    /// Extension) send <c>application/json; charset=utf-8</c>, which cannot be added to the
    /// request headers and is therefore deferred and re-applied to the content afterwards.
    /// <see cref="System.Net.Http.Headers.HttpContentHeaders.TryAddWithoutValidation"/> appends
    /// rather than replaces, which used to yield the malformed two-valued header
    /// <c>application/json, application/json; charset=utf-8</c>.  Lenient upstreams (including this
    /// stub) accept it, but strict gateways reject it with a misleading
    /// <c>400 "Model not exist."</c> — so the header itself must be asserted, not the status.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_ViaOpenAiSurface_EmitsSingleContentTypeUpstream(bool stream)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream,
        };

        // StringContent with an explicit encoding produces "application/json; charset=utf-8",
        // matching what the VS Extension sends.
        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", content);

        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();

        string? upstreamContentType = _upstream.LastContentTypeHeader;

        Assert.NotNull(upstreamContentType);
        Assert.DoesNotContain(',', upstreamContentType);
        Assert.StartsWith("application/json", upstreamContentType);
    }

    [Fact]
    public async Task Chat_ViaOpenAiSurface_Streaming_ReturnsTestToken()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream = true,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", content);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();

        // SSE text/event-stream — each line is "data: {...}" then a blank separator.
        string? contentToken = null;
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ") || line == "data: [DONE]") continue;

            string json = line[6..];  // strip "data: " prefix
            using JsonDocument chunk = JsonDocument.Parse(json);
            if (chunk.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices[0].TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("content", out JsonElement c)
                && c.GetString() is { Length: > 0 } text)
            {
                contentToken = text;
            }
        }

        Assert.Contains("Test", contentToken);
    }

    // ── /v1-less OpenAI surface ─────────────────────────────────────────

    /// <summary>
    /// A client configured with the bare proxy host posts to <c>/chat/completions</c>. That path
    /// used to fall through to the unknown-endpoint branch and answer
    /// <c>404 {"error":"Unknown endpoint: /chat/completions"}</c>, which forced the client's base
    /// URL to be edited to add <c>/v1</c> before the proxy would respond at all. The path is now
    /// normalised, so both base-URL shapes reach the same upstream endpoint.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_WithoutV1Prefix_IsRoutedToV1Upstream(bool stream)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Test" },
            },
            stream,
        };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/chat/completions", content);

        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();

        // The normalised path is what proves the request was really forwarded, not answered locally.
        Assert.Equal("/v1/chat/completions", _upstream.LastPath);
    }

    [Fact]
    public async Task Embeddings_WithoutV1Prefix_IsRoutedToV1Upstream()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        var payload = new { model = "test-model", input = "Test" };

        using StringContent content = new(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/embeddings", content);

        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();

        Assert.Equal("/v1/embeddings", _upstream.LastPath);
    }

    /// <summary>
    /// <c>/models</c> and <c>/models/{id}</c> are answered from the local mapping table exactly as
    /// their <c>/v1</c> twins are, so a client that omits the prefix still discovers models.
    /// </summary>
    [Theory]
    [InlineData("/models")]
    [InlineData("/models/test-model")]
    public async Task Models_WithoutV1Prefix_AnsweredFromLocalMappings(string path)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        using HttpResponseMessage response = await client.GetAsync(
            $"http://localhost:{_proxyPort}{path}");

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        string? id = path == "/models"
            ? doc.RootElement.GetProperty("data")[0].GetProperty("id").GetString()
            : doc.RootElement.GetProperty("id").GetString();

        Assert.Equal("test-model", id);
    }

    [Theory]
    [InlineData("/chat/completions", "/v1/chat/completions")]
    [InlineData("/CHAT/COMPLETIONS", "/v1/chat/completions")]
    [InlineData("/completions", "/v1/completions")]
    [InlineData("/embeddings", "/v1/embeddings")]
    [InlineData("/responses", "/v1/responses")]
    [InlineData("/responses/resp_123", "/v1/responses/resp_123")]
    public void TryNormalizeOpenAiPath_MapsBareOpenAiPaths(string path, string expected)
    {
        Assert.True(OllamaProxyHandler.TryNormalizeOpenAiPath(path, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/models")]
    [InlineData("/chat/completions/compact")]
    [InlineData("/")]
    public void TryNormalizeOpenAiPath_LeavesEveryOtherPathAlone(string path)
    {
        Assert.False(OllamaProxyHandler.TryNormalizeOpenAiPath(path, out string normalized));
        Assert.Empty(normalized);
    }

    /// <summary>
    /// Guards the normalisation against becoming a catch-all: an unrecognised path must still be
    /// rejected rather than silently rewritten onto the upstream.
    /// </summary>
    [Fact]
    public async Task UnknownEndpoint_StillReturns404()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

        using HttpResponseMessage response = await client.GetAsync(
            $"http://localhost:{_proxyPort}/definitely-not-an-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Stub upstream server ────────────────────────────────────────────

    /// <summary>
    /// Minimal HTTP server that answers every <c>/v1/chat/completions</c> POST with a
    /// single‑token "Test" response, mimicking a real llama.cpp upstream.  Non‑streaming
    /// requests receive a complete JSON body; streaming requests receive SSE frames
    /// ("data:{...}\n\ndata: [DONE]\n\n").
    /// </summary>
    private sealed class StubUpstreamServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _listenTask;
        private volatile string? _lastContentTypeHeader;
        private volatile string? _lastPath;

        /// <summary>
        /// Raw <c>Content-Type</c> header seen on the most recent request, or <see langword="null"/>
        /// when it carried none.  Exposed so tests can assert the proxy emits exactly one content
        /// type; <see cref="HttpListener"/> silently accepts a malformed multi-valued header that a
        /// strict real-world gateway would reject.
        /// </summary>
        public string? LastContentTypeHeader => _lastContentTypeHeader;

        /// <summary>
        /// Absolute path of the most recent request the stub received. Exposed so a test can prove
        /// the proxy rebuilt a client's <c>/v1</c>-less path into the canonical <c>/v1/...</c> form
        /// before contacting the upstream, rather than merely answering from local state.
        /// </summary>
        public string? LastPath => _lastPath;

        public StubUpstreamServer(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
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
                _lastContentTypeHeader = ctx.Request.Headers["Content-Type"];
                _lastPath = ctx.Request.Url?.AbsolutePath;

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                using JsonDocument doc = JsonDocument.Parse(body);

                bool stream = doc.RootElement.TryGetProperty("stream", out JsonElement s)
                              && s.GetBoolean();

                ctx.Response.StatusCode = 200;

                if (stream)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    byte[] frame = Encoding.UTF8.GetBytes(
                        "data: {\"id\":\"stub\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Test\"},\"finish_reason\":null}]}\n\n");
                    byte[] done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                    await ctx.Response.OutputStream.WriteAsync(frame);
                    await ctx.Response.OutputStream.WriteAsync(done);
                    await ctx.Response.OutputStream.FlushAsync();
                }
                else
                {
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
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                await ctx.Response.OutputStream.WriteAsync(err);
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        public void Start() { /* started in ctor */ }
        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
            _cts.Dispose();
        }
    }
}