using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;

namespace Kaeo.LlmProxy;

/// <summary>
/// Best-effort, data-only capability detection for an upstream model. It queries upstream
/// metadata endpoints (GET /v1/models/{id}, falling back to GET /v1/models) and infers
/// capability tokens from explicit metadata fields plus conservative name heuristics. It never
/// invokes the model (no chat/embedding/generation requests). Results are advisory: the user
/// reviews and adjusts them in the Model Mapping dialog.
/// </summary>
internal static class CapabilityDetector
{
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Detects the capabilities of <paramref name="modelId"/> on the upstream at
    /// <paramref name="upstreamUrl"/>. Uses data-only endpoints and name heuristics; it never
    /// sends a chat/embedding/generation request.
    /// </summary>
    public static async Task<CapabilityDetectionResult> DetectAsync(
        string upstreamUrl, string modelId, string? apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // 1. Fetch model metadata (data calls only). Best-effort: a failure just means we fall
        //    back to name heuristics.
        string? modelJson = null;
        string? fetchNote = null;
        try
        {
            modelJson = await FetchModelJsonAsync(upstreamUrl, modelId, apiKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fetchNote = $"Could not reach upstream ({ex.GetType().Name}); using name heuristics only.";
        }

        // 2. Parse explicit capability metadata if the provider advertises it.
        List<string> detected = [];
        bool fromMetadata = false;
        if (modelJson is not null)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(modelJson);
                int beforeMetadata = detected.Count;
                ExtractExplicitCapabilities(doc.RootElement, detected);
                if (detected.Count > beforeMetadata)
                    fromMetadata = true;
            }
            catch (JsonException)
            {
                // Unparseable metadata; ignore and rely on heuristics.
            }
        }

        // 3. Conservative name heuristics (always applied, additive).
        int beforeHeuristics = detected.Count;
        ApplyNameHeuristics(modelId, detected);
        bool fromHeuristics = detected.Count > beforeHeuristics;

        List<string> normalized = ModelCapabilities.Normalize(detected);

        // No fallback defaults: when nothing is detected the table is left blank so the user can
        // use "Set Defaults" or add capabilities manually.
        string summary;
        if (normalized.Count == 0)
        {
            summary = fetchNote is not null
                ? $"{fetchNote} No capabilities detected — the table is left blank. Use Set Defaults or add them manually."
                : "No capabilities detected — the table is left blank. Use Set Defaults or add them manually.";
        }
        else
        {
            List<string> sources = [];
            if (fromMetadata) sources.Add("upstream metadata");
            if (fromHeuristics) sources.Add("name heuristics");
            string sourceText = string.Join(" + ", sources);
            string detectedText = string.Join(", ", normalized);
            summary = fetchNote is not null
                ? $"{fetchNote} Detected: {detectedText} ({sourceText}). Review and adjust."
                : $"Detected: {detectedText} ({sourceText}). Best-effort — review and adjust.";
        }

        return new CapabilityDetectionResult(normalized, summary);
    }

    /// <summary>
    /// Fetches the raw JSON of the model object from the upstream. Tries the single-model
    /// endpoint first, then falls back to the list endpoint (Ollama and others do not support
    /// GET /v1/models/{id}). Throws when neither yields the model.
    /// </summary>
    private static async Task<string> FetchModelJsonAsync(
        string upstreamUrl, string modelId, string? apiKey, CancellationToken cancellationToken)
    {
        // 1. Single-model endpoint (GET /v1/models/{id}).
        string? body = await FetchBodyAsync(
            UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models/" + Uri.EscapeDataString(modelId)),
            apiKey, cancellationToken);
        if (body is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(body);
                return body;
            }
            catch (JsonException)
            {
                // Not valid JSON; fall through to the list endpoint.
            }
        }

        // 2. List endpoint fallback (GET /v1/models).
        string? listBody = await FetchBodyAsync(
            UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models"),
            apiKey, cancellationToken);
        if (listBody is not null)
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

        throw new HttpRequestException("Upstream model metadata was not found at /v1/models or /v1/models/{id}.");
    }

    private static async Task<string?> FetchBodyAsync(
        Uri uri, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode ? responseBody : null;
    }

    /// <summary>
    /// Extracts an explicit <c>capabilities</c> field from the model metadata, if present.
    /// Supports both an array of strings and a boolean flag map (keys whose value is true).
    /// </summary>
    private static void ExtractExplicitCapabilities(JsonElement model, List<string> detected)
    {
        if (!model.TryGetProperty("capabilities", out JsonElement caps))
            return;

        if (caps.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in caps.EnumerateArray())
            {
                string? value = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    detected.Add(value!);
            }
        }
        else if (caps.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in caps.EnumerateObject())
            {
                bool isTrue = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.String => string.Equals(prop.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                if (isTrue)
                    detected.Add(prop.Name);
            }
        }
    }

    /// <summary>
    /// Applies conservative, word-boundary name heuristics to infer capabilities from the model
    /// id. These are best-effort signals; the user reviews the result.
    /// </summary>
    private static void ApplyNameHeuristics(string modelId, List<string> detected)
    {
        string m = modelId.ToLowerInvariant();

        if (ContainsAnyToken(m, ["vision", "llava", "vl", "omni"]))
            AddIfMissing(detected, "vision");
        if (ContainsAnyToken(m, ["reason", "think", "qwq", "r1", "o1", "o3", "o4", "deepthink"]))
            AddIfMissing(detected, "reasoning");
        if (ContainsAnyToken(m, ["whisper", "asr", "tts", "speech", "audio", "voice", "talk"]))
            AddIfMissing(detected, "audio");
        if (ContainsAnyToken(m, ["embed", "bge", "e5", "sentence", "text-embed"]))
            AddIfMissing(detected, "embeddings");
        if (ContainsAnyToken(m, ["flux", "dall", "imagen", "stable-diffusion", "sdxl", "sd3", "image-gen", "img-gen"]))
            AddIfMissing(detected, "image_generation");
        if (ContainsAnyToken(m, ["code", "coder", "codellama", "starcoder"]))
            AddIfMissing(detected, "code");
    }

    private static bool ContainsAnyToken(string text, string[] tokens)
        => tokens.Any(t => ContainsToken(text, t));

    /// <summary>Matches <paramref name="token"/> as a standalone word (not part of a larger alphanumeric run).</summary>
    private static bool ContainsToken(string text, string token)
    {
        int idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            int start = idx;
            int end = idx + token.Length;
            bool leftOk = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk)
                return true;
            idx += 1;
        }
        return false;
    }

    private static void AddIfMissing(List<string> list, string token)
    {
        if (!list.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
            list.Add(token);
    }
}

/// <summary>Outcome of a capability detection run.</summary>
internal sealed record CapabilityDetectionResult(List<string> Capabilities, string Summary);
