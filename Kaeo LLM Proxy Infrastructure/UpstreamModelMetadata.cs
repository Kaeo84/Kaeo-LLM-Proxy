using System.Net.Http;
using System.Text.Json;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Reads model metadata from an OpenAI-compatible upstream: the <c>/v1/models</c> discovery list,
/// a single model object, and the context-window figure carried in either.
/// </summary>
/// <remarks>
/// Extracted from the Model Mapping dialog, where this logic lived beside WinForms control
/// construction despite having no UI concern of its own — which is also why the dialog and
/// <c>CapabilityDetector</c> had each grown their own <c>/v1/models</c> fetch and their own
/// <c>context_length</c> reader. Centralising it here gives the proxy's outbound model-metadata
/// calls one HTTP client, one URI builder (see <see cref="UpstreamUriHelper"/>) and one parser.
/// </remarks>
internal static class UpstreamModelMetadata
{
    /// <summary>
    /// Shared client for upstream model discovery. Avoids socket churn when model lists are
    /// fetched repeatedly; the fixed timeout applies to discovery calls only, not proxied traffic.
    /// </summary>
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Outcome of an upstream <c>/v1/models</c> discovery call, for request logging.</summary>
    internal sealed record FetchResult(
        List<string> Models,
        int StatusCode,
        string? ResponseBody,
        string? ErrorMessage);

    /// <summary>
    /// Fetches the model list from the specified upstream URL. Returns the parsed model ids along
    /// with the HTTP status code, raw response body, and any error text so the caller can record
    /// the discovery call in the request log.
    /// </summary>
    public static async Task<FetchResult> FetchModelsAsync(
        string upstreamUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Uri requestUri = UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using HttpResponseMessage resp = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return new FetchResult([], (int)resp.StatusCode, body, $"Upstream {(int)resp.StatusCode}: {body}");

            List<string> models = [];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data))
                    return new FetchResult([], (int)resp.StatusCode, body, "Upstream response has no 'data' model array.");

                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement id))
                    {
                        string? name = id.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            models.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
                return new FetchResult([], (int)resp.StatusCode, body, "Upstream returned an unparseable model list.");
            }

            return new FetchResult(models, (int)resp.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FetchResult([], 0, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Raw body of a single model metadata response plus the status it came back with.</summary>
    internal sealed record ModelJsonResult(string Body, int StatusCode);

    /// <summary>
    /// Reads a single model's metadata body, trying <c>/v1/models/{id}</c> first and falling back to
    /// the list endpoint (Ollama and others do not support the single-model route).
    /// </summary>
    /// <remarks>
    /// Returns the body the caller should display together with the status code to report, which is
    /// not always the body's own status: when the single-model route fails but the list route answers
    /// with a matching element, the matched element is returned under the failed route's status. That
    /// asymmetry is deliberate — it is the behaviour the Model Mapping dialog has always shown, and a
    /// caller reporting "HTTP 404" while displaying a model it did find is a known quirk rather than
    /// something to silently redefine here.
    /// </remarks>
    public static async Task<ModelJsonResult> FetchModelJsonAsync(
        string upstreamUrl, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        (string body, int status, bool ok) = await SendAsync(
            UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models/" + Uri.EscapeDataString(modelId)),
            apiKey, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            (string listBody, int listStatus, bool listOk) = await SendAsync(
                UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models"),
                apiKey, cancellationToken).ConfigureAwait(false);

            if (listOk)
            {
                // Keep the original (failed) status; only the body is upgraded to the matched element.
                body = TryFindModelElement(listBody, modelId) ?? body;
            }
            else
            {
                body = listBody;
                status = listStatus;
            }
        }

        return new ModelJsonResult(body, status);
    }

    /// <summary>
    /// Returns the JSON of <paramref name="modelId"/> from the upstream, or null when neither the
    /// single-model route nor the list route yields a valid JSON object for it.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="FetchModelJsonAsync"/>: a body is only accepted when it parses as JSON,
    /// so an upstream error page cannot be mistaken for model metadata. Callers that treat a missing
    /// model as a hard failure (capability detection) want this overload; callers that display
    /// whatever came back (the mapping dialog) want the other one.
    /// </remarks>
    public static async Task<string?> TryFetchModelJsonAsync(
        string upstreamUrl, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        (string body, int _, bool ok) = await SendAsync(
            UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models/" + Uri.EscapeDataString(modelId)),
            apiKey, cancellationToken).ConfigureAwait(false);
        if (ok && IsJson(body))
            return body;

        (string listBody, int _, bool listOk) = await SendAsync(
            UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models"),
            apiKey, cancellationToken).ConfigureAwait(false);
        if (listOk && IsJson(listBody))
            return TryFindModelElement(listBody, modelId);

        return null;
    }

    /// <summary>
    /// Picks the <c>data[]</c> element whose <c>id</c> matches <paramref name="modelId"/>, returning
    /// its raw JSON, or null when the list has no such element.
    /// </summary>
    private static string? TryFindModelElement(string listBody, string modelId)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(listBody);
            if (doc.RootElement.TryGetProperty("data", out JsonElement arr))
            {
                foreach (JsonElement item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement id)
                        && string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        return item.GetRawText();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable list; the caller decides what to do with the bodies it already has.
        }

        return null;
    }

    private static bool IsJson(string body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>GETs a model-metadata URI, returning the body, status code and success flag.</summary>
    private static async Task<(string Body, int StatusCode, bool IsSuccess)> SendAsync(
        Uri uri, string? apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (body, (int)response.StatusCode, response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (string.Empty, 0, false);
        }
    }

    /// <summary>
    /// Best-effort extraction of a model's context window from an upstream metadata payload.
    /// Handles the shapes published by llama.cpp, Ollama and several hosted OpenAI-compatible
    /// providers. Returns false when no usable figure is present.
    /// </summary>
    /// <remarks>
    /// The candidate field names and the nesting strategies are retained exactly as they were in the
    /// Model Mapping dialog, where they were derived against live upstreams. A figure is only
    /// accepted when it is a positive integer, so a provider that reports <c>0</c> or a string
    /// number is treated as "not stated" rather than silently clamping the proxy's budget to zero.
    /// </remarks>
    public static bool TryExtractContextWindow(string json, out int? contextWindow)
    {
        contextWindow = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Search common field names for context window size.
            string[] candidateNames = [
                "context_window", "context_length", "max_context_length", "max_context",
                "context", "num_ctx", "max_tokens", "max_position_embeddings",
                "sliding_window", "n_ctx_train"
            ];

            // 1. Root-level candidates first.
            foreach (string name in candidateNames)
            {
                if (root.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Number
                    && val.TryGetInt32(out int v) && v > 0)
                {
                    contextWindow = v;
                    return true;
                }
            }

            // 2. Nested "details", "parameters", "config", or "architecture" objects.
            foreach (string nested in new[] { "details", "parameters", "config", "architecture" })
            {
                if (root.TryGetProperty(nested, out var nestedObj) && nestedObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (string name in candidateNames)
                    {
                        if (nestedObj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Number
                            && val.TryGetInt32(out int v) && v > 0)
                        {
                            contextWindow = v;
                            return true;
                        }
                    }
                }
            }

            // 3. Ollama /api/show style: "model_info" object with dotted keys like "llama.context_length".
            if (root.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in modelInfo.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number
                        && prop.Value.TryGetInt32(out int v) && v > 0
                        && prop.Name.Contains("context_length", StringComparison.OrdinalIgnoreCase))
                    {
                        contextWindow = v;
                        return true;
                    }
                }
            }

            // 4. "llama" object with "context_length" (some providers nest under model family).
            if (root.TryGetProperty("llama", out var llamaObj) && llamaObj.ValueKind == JsonValueKind.Object)
            {
                if (llamaObj.TryGetProperty("context_length", out var llamaCtx)
                    && llamaCtx.ValueKind == JsonValueKind.Number
                    && llamaCtx.TryGetInt32(out int llamaV) && llamaV > 0)
                {
                    contextWindow = llamaV;
                    return true;
                }
            }
        }
        catch (JsonException) { }

        return false;
    }
}