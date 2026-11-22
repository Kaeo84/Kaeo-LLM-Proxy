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
/// End-to-end coverage for cross-format API translation. A path-aware stub upstream answers in the
/// native wire format of whichever endpoint the proxy contacts (Ollama <c>/api/chat</c>, Anthropic
/// <c>/v1/messages</c>, OpenAI <c>/v1/chat/completions</c>), so these tests exercise the real routing,
/// per-model upstream resolution, credential shaping and streaming pumps added for translation —
/// not just the pure converters (which have their own unit suites).
/// </summary>
[Collection("AppLogger")]
public sealed class ApiTranslationE2ETests : IAsyncDisposable
{
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
    private readonly FormatStubUpstream _upstream;

    public ApiTranslationE2ETests()
    {
        (_upstream, _stubPort) = StartStubOnFreePort();
        _proxyPort = GetFreePort();
        while (_proxyPort == _stubPort)
            _proxyPort = GetFreePort();

        _dbPath = Path.Combine(Path.GetTempPath(), $"kaeo-xlat-{Guid.NewGuid():N}.db");
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
                    ProxyName = "ollama-target",
                    ModelName = "ollama-target",
                    UpstreamUrl = $"http://localhost:{_stubPort}",
                    UpstreamType = UpstreamType.Ollama,
                    IsEnabled = true,
                    ContextWindowTokens = 32_768,
                },
                new ModelMapping
                {
                    ProxyName = "anthropic-target",
                    ModelName = "anthropic-target",
                    UpstreamUrl = $"http://localhost:{_stubPort}",
                    UpstreamType = UpstreamType.Anthropic,
                    IsEnabled = true,
                    ContextWindowTokens = 32_768,
                },
                new ModelMapping
                {
                    ProxyName = "openai-target",
                    ModelName = "openai-target",
                    UpstreamUrl = $"http://localhost:{_stubPort}/v1",
                    UpstreamType = UpstreamType.OpenAI,
                    IsEnabled = true,
                    ContextWindowTokens = 32_768,
                },
            ],
        };
        _settings.Logging.ApplicationDatabasePath = _dbPath;

        _database = new AppDatabase(_settings.Logging);
        _moduleHost = new ModuleHost(_database, _settings);
        _stats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours);
        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _stats);
        _handler = new OllamaProxyHandler(_settings, _stats, _moduleHost, _mcpServer);
        _proxy = new ProxyServer(_handler);

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

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"E2E cleanup: could not delete '{path}': {ex.Message}");
        }
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static (FormatStubUpstream Server, int Port) StartStubOnFreePort()
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = GetFreePort();
            try { return (new FormatStubUpstream(port), port); }
            catch (HttpListenerException) when (attempt < 4) { }
        }
    }

    private HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object OpenAiBody(string model, bool stream) => new
    {
        model,
        messages = new[] { new { role = "user", content = "Test" } },
        stream,
    };

    private static object AnthropicBody(string model, bool stream) => new
    {
        model,
        max_tokens = 64,
        messages = new[] { new { role = "user", content = "Test" } },
        stream,
    };

    private static string OpenAiContent(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string AnthropicContent(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        foreach (JsonElement block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.GetProperty("type").GetString() == "text")
                return block.GetProperty("text").GetString() ?? "";
        return "";
    }

    private static string ConcatOpenAiSseContent(string body)
    {
        StringBuilder sb = new();
        foreach (string line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") continue;
            using JsonDocument doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("content", out JsonElement c))
                sb.Append(c.GetString());
        }
        return sb.ToString();
    }

    private static string ConcatAnthropicSseText(string body)
    {
        StringBuilder sb = new();
        foreach (string line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            using JsonDocument doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("type", out JsonElement t)
                && t.GetString() == "content_block_delta"
                && doc.RootElement.TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("text", out JsonElement text))
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    // ── OpenAI inbound → Ollama upstream ──────────────────────────────────

    [Fact]
    public async Task OpenAiInbound_OllamaUpstream_NonStream_TranslatesReply()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", Json(OpenAiBody("ollama-target", false)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", OpenAiContent(body));
        Assert.EndsWith("/api/chat", _upstream.LastPath);
    }

    [Fact]
    public async Task OpenAiInbound_OllamaUpstream_Stream_TranslatesToSse()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", Json(OpenAiBody("ollama-target", true)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", ConcatOpenAiSseContent(body));
        Assert.Contains("data: [DONE]", body);
    }

    // ── OpenAI inbound → Anthropic upstream ───────────────────────────────

    [Fact]
    public async Task OpenAiInbound_AnthropicUpstream_NonStream_TranslatesReply()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", Json(OpenAiBody("anthropic-target", false)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", OpenAiContent(body));
        Assert.EndsWith("/v1/messages", _upstream.LastPath);
        Assert.Equal("2023-06-01", _upstream.LastAnthropicVersion);
    }

    [Fact]
    public async Task OpenAiInbound_AnthropicUpstream_Stream_TranslatesToSse()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", Json(OpenAiBody("anthropic-target", true)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", ConcatOpenAiSseContent(body));
        Assert.Contains("data: [DONE]", body);
    }

    // ── Anthropic inbound → OpenAI upstream ───────────────────────────────

    [Fact]
    public async Task AnthropicInbound_OpenAiUpstream_NonStream_TranslatesReply()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/messages", Json(AnthropicBody("openai-target", false)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", AnthropicContent(body));
    }

    [Fact]
    public async Task AnthropicInbound_OpenAiUpstream_Stream_TranslatesToAnthropicSse()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/messages", Json(AnthropicBody("openai-target", true)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", ConcatAnthropicSseText(body));
        Assert.Contains("event: message_stop", body);
    }

    // ── Anthropic inbound → Anthropic upstream (passthrough) ──────────────

    [Fact]
    public async Task AnthropicInbound_AnthropicUpstream_NonStream_Passthrough()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/messages", Json(AnthropicBody("anthropic-target", false)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test", AnthropicContent(body));
        Assert.EndsWith("/v1/messages", _upstream.LastPath);
    }

    // ── Translation toggle ────────────────────────────────────────────────

    [Fact]
    public async Task OpenAiInbound_OllamaUpstream_TranslationDisabled_RawPassthrough()
    {
        _settings.ModelMappings[0].EnableApiTranslation = false;

        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.PostAsync(
            $"http://localhost:{_proxyPort}/v1/chat/completions", Json(OpenAiBody("ollama-target", false)));
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        // With translation off the OpenAI body is forwarded verbatim to the OpenAI surface, so the
        // stub answers in OpenAI format and the client receives an OpenAI reply unchanged.
        Assert.Contains("Test", OpenAiContent(body));
        Assert.EndsWith("/v1/chat/completions", _upstream.LastPath);
    }

    // ── Path-aware stub upstream ──────────────────────────────────────────

    private sealed class FormatStubUpstream : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private volatile string? _lastPath;
        private volatile string? _lastAnthropicVersion;

        public string? LastPath => _lastPath;
        public string? LastAnthropicVersion => _lastAnthropicVersion;

        public FormatStubUpstream(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
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
                string path = ctx.Request.Url?.AbsolutePath ?? "";
                _lastPath = path;
                _lastAnthropicVersion = ctx.Request.Headers["anthropic-version"];

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                using JsonDocument doc = JsonDocument.Parse(body);
                bool stream = doc.RootElement.TryGetProperty("stream", out JsonElement s) && s.GetBoolean();

                ctx.Response.StatusCode = 200;

                if (path.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
                    await WriteOllamaAsync(ctx, stream);
                else if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                    await WriteAnthropicAsync(ctx, stream);
                else
                    await WriteOpenAiAsync(ctx, stream);
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

        private static async Task WriteOpenAiAsync(HttpListenerContext ctx, bool stream)
        {
            if (stream)
            {
                ctx.Response.ContentType = "text/event-stream";
                await WriteAsync(ctx, "data: {\"id\":\"stub\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Test\"},\"finish_reason\":null}]}\n\n");
                await WriteAsync(ctx, "data: {\"id\":\"stub\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                await WriteAsync(ctx, "data: [DONE]\n\n");
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await WriteAsync(ctx, """
                {"id":"stub","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Test"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
                """);
            }
        }

        private static async Task WriteOllamaAsync(HttpListenerContext ctx, bool stream)
        {
            if (stream)
            {
                ctx.Response.ContentType = "application/x-ndjson";
                await WriteAsync(ctx, "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"Test\"},\"done\":false}\n");
                await WriteAsync(ctx, "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":2,\"eval_count\":1}\n");
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await WriteAsync(ctx, """
                {"model":"m","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"Test"},"done":true,"done_reason":"stop","prompt_eval_count":2,"eval_count":1}
                """);
            }
        }

        private static async Task WriteAnthropicAsync(HttpListenerContext ctx, bool stream)
        {
            if (stream)
            {
                ctx.Response.ContentType = "text/event-stream";
                await WriteAsync(ctx, "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_stub\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"m\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":2,\"output_tokens\":0}}}\n\n");
                await WriteAsync(ctx, "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
                await WriteAsync(ctx, "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Test\"}}\n\n");
                await WriteAsync(ctx, "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                await WriteAsync(ctx, "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");
                await WriteAsync(ctx, "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await WriteAsync(ctx, """
                {"id":"msg_stub","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"Test"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}
                """);
            }
        }

        private static async Task WriteAsync(HttpListenerContext ctx, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            await ctx.Response.OutputStream.FlushAsync();
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