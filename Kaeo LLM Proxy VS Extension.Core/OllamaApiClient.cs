using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal sealed class OllamaApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public OllamaApiClient(string baseUrl, string? apiKey = null, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _apiKey = apiKey;
        _http = httpClient ?? new HttpClient();
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public Task<bool> HealthAsync(CancellationToken ct = default)
    {
        // Simple ping - attempt GET /health
        return HealthInternalAsync(ct);
    }

    private async Task<bool> HealthInternalAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync(new Uri(new Uri(_baseUrl), "/health"), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        // Ollama-standard GET /api/tags:
        // { "models": [ { "name", "model", "details": { "parameter_size", "context_length", ... }, "capabilities": ["completion","tools","vision"] } ] }
        var resp = await _http.GetAsync(new Uri(new Uri(_baseUrl), "/api/tags"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modelsEl.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var n)) continue;
                var name = n.GetString();
                if (name is null) continue;

                // details.context_length
                long contextLength = 0;
                if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("context_length", out var cl))
                    contextLength = cl.GetInt64();

                // capabilities: Ollama-native tokens. Tool-calling is signalled by "tools".
                var capabilities = new List<string>();
                if (m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in caps.EnumerateArray())
                    {
                        var cv = c.GetString();
                        if (cv is not null) capabilities.Add(cv);
                    }
                }
                var supportsTools = capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase);

                list.Add(new ModelInfo(name, contextLength, capabilities, supportsTools));
            }
        }
        return list;
    }

    public IAsyncEnumerable<ChatChunk> StreamChatAsync(object payload, CancellationToken ct = default)
    {
        return StreamChatCoreAsync(payload, ct);
    }

    private async IAsyncEnumerable<ChatChunk> StreamChatCoreAsync(object payload, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_baseUrl), "/api/chat"));
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var text = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            yield return new ChatChunk(text, done);
        }
    }
}

/// <summary>
/// A model entry from the proxy's Ollama-standard /api/tags endpoint.
/// <see cref="SupportsTools"/> is true when the Ollama "tools" capability token is present,
/// which is the standard signal that the model supports tool/function calling.
/// </summary>
internal sealed record ModelInfo(string Name, long ContextLength, IReadOnlyList<string> Capabilities, bool SupportsTools);

/// <summary>A single streamed chat token chunk from the proxy's /api/chat NDJSON stream.</summary>
internal sealed record ChatChunk(string? Text, bool Done);
