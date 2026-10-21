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
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _stats);
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

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
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