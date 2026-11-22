using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal sealed class OllamaApiClient : IUpstreamClient
{
    /// <summary>
    /// Shared HttpClient cache keyed by base URL. The factory creates a client per refresh,
    /// so per-instance clients burned an ephemeral socket each time and were never disposed;
    /// cached clients live for the process lifetime and reuse pooled connections. Auth rides
    /// on each request (not DefaultRequestHeaders) so two connections to the same host with
    /// different keys cannot corrupt each other.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> SharedClients =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public OllamaApiClient(string baseUrl, string? apiKey = null, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _apiKey = apiKey;
        if (httpClient is not null)
        {
            // Caller-owned client: preserve the historical auth-stamping behavior.
            _http = httpClient;
            HttpHelpers.SetBearerAuth(_http, apiKey);
        }
        else
        {
            _http = SharedClients.GetOrAdd(_baseUrl, _ => new HttpClient());
        }
    }

    /// <summary>Builds a GET request carrying the connection's ****** token, if any.</summary>
    private HttpRequestMessage CreateGetRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
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
            using var request = CreateGetRequest(BuildEndpointUri("/health"));
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            // Cancellation (shutdown/Stop) is not "server down" — let it propagate.
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds an endpoint URI without discarding a path in the base URL (see
    /// <see cref="HttpHelpers.BuildEndpointUri"/>).
    /// </summary>
    private Uri BuildEndpointUri(string endpointPath)
        => HttpHelpers.BuildEndpointUri(_baseUrl, endpointPath);

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        // Ollama-standard GET /api/tags:
        // { "models": [ { "name", "model", "details": { "parameter_size", "context_length", ... }, "capabilities": ["completion","tools","vision"] } ] }
        using var request = CreateGetRequest(BuildEndpointUri("/api/tags"));
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        // net48 exposes only the parameterless ReadAsStreamAsync and lacks
        // JsonDocument.ParseAsync (net7+). Read the (small) body to a string and parse
        // synchronously so both targets compile identically.
        var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using (body)
        {
            // net48: StreamReader has no ReadToEndAsync. ReadToEnd is acceptable because
            // /api/tags responses are small (typically < 100KB).
#pragma warning disable VSTHRD103
            using var reader = new StreamReader(body);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
#pragma warning restore VSTHRD103

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
    }

    }

/// <summary>
/// A model entry from the proxy's Ollama-standard /api/tags endpoint.
/// <see cref="SupportsTools"/> is true when the Ollama "tools" capability token is present,
/// which is the standard signal that the model supports tool/function calling.
/// </summary>
internal sealed record ModelInfo(string Name, long ContextLength, IReadOnlyList<string> Capabilities, bool SupportsTools);
