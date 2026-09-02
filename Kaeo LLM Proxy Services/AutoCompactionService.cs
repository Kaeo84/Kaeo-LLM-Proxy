using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Handles automatic context compaction when incoming requests exceed the configured
/// proactive overflow threshold. The service intercepts the request, calls the compact
/// endpoint to reduce context size, and returns the compacted message list for forwarding.
/// Includes circuit-breaker logic to prevent infinite compaction loops.
/// </summary>
internal sealed class AutoCompactionService
{
    /// <summary>Maximum compaction attempts per session before the circuit breaker opens.</summary>
    private const int MaxCompactionAttempts = 3;

    /// <summary>
    /// Tracks compaction attempts per conversation key (model + first user message hash).
    /// Used by the circuit breaker to prevent infinite compaction loops.
    /// </summary>
    private readonly ConcurrentDictionary<string, CompactionState> _sessionStates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;

    internal sealed record CompactionState
    {
        public int Attempts;
        public DateTime LastAttemptUtc;
        public bool CircuitOpen;
    }

    public AutoCompactionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Determines whether auto-compaction should be attempted for this request.
    /// Returns false when the feature is disabled for this path, the threshold is not exceeded,
    /// or the circuit breaker is open for this session.
    /// </summary>
    public bool ShouldCompact(
        ModelMapping mapping,
        AutoCompactPaths requestPath,
        string requestBody,
        out string sessionKey)
    {
        sessionKey = string.Empty;

        if (mapping is null)
            return false;

        // Check if auto-compaction is enabled for this request path.
        if ((mapping.AutoCompactPaths & requestPath) == 0)
            return false;

        int threshold = mapping.GetProactiveOverflowThreshold();
        if (threshold <= 0)
            return false;

        // Estimate request size in tokens (rough: 1 token ≈ 4 chars).
        int estimatedTokens = Encoding.UTF8.GetByteCount(requestBody) / 4;
        if (estimatedTokens < threshold)
            return false;

        // Build a session key from the model name and a hash of the first user message
        // so the circuit breaker is per-conversation, not global.
        sessionKey = BuildSessionKey(mapping.ProxyName, requestBody);

        // Circuit breaker check.
        if (_sessionStates.TryGetValue(sessionKey, out CompactionState? state) && state.CircuitOpen)
        {
            Log.Debug("Auto-compaction circuit breaker open for session {SessionKey}", sessionKey);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Performs the compaction by calling the compact endpoint internally.
    /// Returns the compacted request body on success, or null on failure.
    /// On failure, records the attempt and opens the circuit breaker if the max is reached.
    /// </summary>
    public async Task<string?> CompactAsync(
        ModelMapping mapping,
        string requestBody,
        string sessionKey,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // Resolve the effective model for compaction (may redirect to compact model).
        string model = mapping.ProxyName;
        if (mapping.ContextSummarizeModelId.HasValue)
        {
            // Look up the compact model name from settings — caller should have resolved this.
            // We use the original model as fallback.
        }

        // Record the attempt.
        CompactionState state = _sessionStates.GetOrAdd(sessionKey, _ => new());
        state.Attempts++;
        state.LastAttemptUtc = DateTime.UtcNow;

        if (state.Attempts > MaxCompactionAttempts)
        {
            state.CircuitOpen = true;
            Log.Warning(
                "Auto-compaction circuit breaker opened for session {SessionKey} after {Attempts} attempts",
                sessionKey, state.Attempts);
            return null;
        }

        try
        {
            // Build the compact request — same shape as /v1/responses/compact.
            var compactRequest = new
            {
                model,
                input = ExtractMessages(requestBody),
            };

            string compactBody = JsonSerializer.Serialize(compactRequest);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/responses/compact")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(compactBody)),
            };
            httpReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

            if (!string.IsNullOrWhiteSpace(apiKey))
                httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            string absoluteUri = $"{baseUrl.TrimEnd('/')}/v1/responses/compact";
            httpReq.RequestUri = new Uri(absoluteUri);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                httpReq, HttpCompletionOption.ResponseContentRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                Log.Warning(
                    "Auto-compaction failed for session {SessionKey}: HTTP {StatusCode} {Body}",
                    sessionKey, (int)response.StatusCode, errorBody);
                return null;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);

            // Extract the compacted messages from the response.
            // The compact endpoint returns a response with an "output" array containing
            // the compacted conversation. We wrap it back into a chat-compatible format.
            return BuildCompactedRequestBody(requestBody, responseBody);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning(ex, "Auto-compaction request failed for session {SessionKey}", sessionKey);
            return null;
        }
    }

    /// <summary>
    /// Records a successful compaction, resetting the circuit breaker for this session.
    /// </summary>
    public void RecordSuccess(string sessionKey)
    {
        if (_sessionStates.TryGetValue(sessionKey, out CompactionState? state))
        {
            state.Attempts = 0;
            state.CircuitOpen = false;
        }
    }

    private static string BuildSessionKey(string modelName, string requestBody)
    {
        // Extract the first user message content for session identity.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("messages", out JsonElement messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement msg in messages.EnumerateArray())
                {
                    if (msg.TryGetProperty("role", out JsonElement role)
                        && role.GetString()?.Equals("user", StringComparison.OrdinalIgnoreCase) == true
                        && msg.TryGetProperty("content", out JsonElement content))
                    {
                        string text = content.ValueKind == JsonValueKind.String
                            ? content.GetString() ?? string.Empty
                            : content.GetRawText();

                        // Use first 200 chars as session identity.
                        string hashInput = $"{modelName}:{text[..Math.Min(text.Length, 200)]}";
                        return Convert.ToHexString(
                            SHA256.HashData(
                                Encoding.UTF8.GetBytes(hashInput)))[..16];
                    }
                }
            }
        }
        catch
        {
            // Fall through to fallback.
        }

        return $"{modelName}:unknown";
    }

    private static object ExtractMessages(string requestBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("messages", out JsonElement messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                // Return the messages array as-is for the compact endpoint.
                return JsonSerializer.Deserialize<JsonElement>(messages.GetRawText());
            }
        }
        catch
        {
            // Fall through.
        }

        return Array.Empty<object>();
    }

    private static string BuildCompactedRequestBody(string originalRequestBody, string compactResponseBody)
    {
        try
        {
            using JsonDocument originalDoc = JsonDocument.Parse(originalRequestBody);
            using JsonDocument compactDoc = JsonDocument.Parse(compactResponseBody);

            // The compact response has an "output" array with compacted messages.
            // We replace the original "messages" array with the compacted content.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                // Copy all original properties except "messages".
                foreach (JsonProperty prop in originalDoc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("messages"u8))
                        continue;
                    prop.Value.WriteTo(writer);
                }

                // Write the compacted messages.
                if (compactDoc.RootElement.TryGetProperty("output", out JsonElement output)
                    && output.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("messages");
                    // Convert compact output items to chat message format.
                    writer.WriteStartArray();
                    foreach (JsonElement item in output.EnumerateArray())
                    {
                        if (item.TryGetProperty("role", out JsonElement role)
                            && item.TryGetProperty("content", out JsonElement content))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("role", role.GetString() ?? "user");
                            writer.WriteString("content", content.ValueKind == JsonValueKind.String
                                ? content.GetString()
                                : content.GetRawText());
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    // Fallback: keep original messages if compact response is unexpected.
                    originalDoc.RootElement.GetProperty("messages").WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            // On any failure, return the original body unchanged.
            return originalRequestBody;
        }
    }
}
