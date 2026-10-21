using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

internal sealed class OllamaApiClient : IUpstreamClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaApiClient(string baseUrl, string? apiKey = null, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _http = httpClient ?? new HttpClient();
        HttpHelpers.SetBearerAuth(_http, apiKey);
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
            var resp = await _http.GetAsync(BuildEndpointUri("/health"), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
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
        var resp = await _http.GetAsync(BuildEndpointUri("/api/tags"), ct).ConfigureAwait(false);
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
