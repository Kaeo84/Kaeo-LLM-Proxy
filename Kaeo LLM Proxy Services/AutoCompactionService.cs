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
    /// Fraction of the compact model's context window to use as the max tokens per chunk.
    /// Leaves headroom for system prompt, response generation, and token estimation error.
    /// </summary>
    internal const double ContextWindowFraction = 0.75;

    /// <summary>
    /// Safety multiplier applied to estimated token counts to account for estimation error.
    /// Conservative estimate helps prevent overflow on the compact model.
    /// </summary>
    private const double TokenEstimationSafetyFactor = 1.3;

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
    /// <param name="mapping">The original model mapping (for context window and threshold info).</param>
    /// <param name="requestBody">The original request body to compact.</param>
    /// <param name="sessionKey">Session identifier for circuit breaker tracking.</param>
    /// <param name="baseUrl">Upstream base URL for compaction requests.</param>
    /// <param name="apiKey">Optional API key for upstream authentication.</param>
    /// <param name="timeoutSeconds">Timeout for compaction requests.</param>
    /// <param name="maxTokensPerChunk">Maximum tokens per chunk for map-reduce summarization.</param>
    /// <param name="compactModelName">The model name to use for compaction (may differ from mapping.ProxyName if ContextSummarizeModelId is set).</param>
    /// <param name="targetModelContextWindow">The target model's context window in tokens (for post-compaction validation).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> CompactAsync(
        ModelMapping mapping,
        string requestBody,
        string sessionKey,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        int maxTokensPerChunk,
        string compactModelName,
        int targetModelContextWindow,
        int compactModelContextWindow,
        CancellationToken ct)
    {
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
            Log.Information("Auto-compaction starting for session {SessionKey}, attempt {Attempt}/{MaxAttempts}, compact model {CompactModel}, target model context window {TargetContextWindow}, maxTokensPerChunk {MaxTokens}",
                sessionKey, state.Attempts, MaxCompactionAttempts, compactModelName, targetModelContextWindow, maxTokensPerChunk);

            // Extract messages and split into chunks for map-reduce summarization.
            // This prevents the compact model itself from overflowing on very large conversations.
            var messages = ExtractMessagesAsArray(requestBody);

            // Pre-process: truncate any messages that exceed the compact model's capacity.
            // This ensures we can always make progress even with oversized individual messages.
            int maxTokensPerMessage = (int)(maxTokensPerChunk * 0.5); // Leave room for system prompt + response
            for (int i = 0; i < messages.Count; i++)
            {
                int msgTokens = (int)(EstimateMessageTokens(messages[i]) * TokenEstimationSafetyFactor);
                if (msgTokens > maxTokensPerChunk)
                {
                    Log.Warning("Auto-compaction: message {Index} has ~{Tokens} tokens, exceeds compact model capacity ({MaxTokens}). Truncating.",
                        i, msgTokens, maxTokensPerChunk);
                    object truncated = TruncateMessageIfNeeded(messages[i], maxTokensPerMessage);
                    // Convert back to JsonElement
                    string truncatedJson = JsonSerializer.Serialize(truncated);
                    messages[i] = JsonDocument.Parse(truncatedJson).RootElement.Clone();
                }
            }

            // Check if total estimated tokens fit in a single pass (with safety margin).
            int totalEstimatedTokens = messages.Sum(m => (int)(EstimateMessageTokens(m) * TokenEstimationSafetyFactor));
            Log.Information("Auto-compaction: extracted {MessageCount} messages, estimated {TotalTokens} tokens",
                messages.Count, totalEstimatedTokens);

            if (totalEstimatedTokens <= maxTokensPerChunk)
            {
                Log.Information("Auto-compaction: using single-pass compaction (fits in one chunk)");
                // Small enough to compact in a single pass.
                return await CompactSinglePassAsync(compactModelName, requestBody, messages, baseUrl, apiKey, timeoutSeconds, sessionKey, ct);
            }

            // Large conversation: use chunked map-reduce approach.
            Log.Information(
                "Auto-compaction using chunked summarization: {MessageCount} messages, {TotalTokens} estimated tokens",
                messages.Count, totalEstimatedTokens);

            var chunkSummaries = new List<string>();

            // Map phase: summarize each chunk independently.
            // Split purely by token count to avoid overflow on the compact model.
            // Strategy: Fill each chunk greedily - if a message fits, include it entirely.
            // If it doesn't fit, start a new chunk with that message.
            int chunkStart = 0;
            int chunkNumber = 0;

            while (chunkStart < messages.Count)
            {
                chunkNumber++;
                int chunkEnd = chunkStart;
                int estimatedChunkTokens = 0;

                // Build chunk by greedily adding messages that fit.
                while (chunkEnd < messages.Count)
                {
                    int msgTokens = (int)(EstimateMessageTokens(messages[chunkEnd]) * TokenEstimationSafetyFactor);

                    // If this message would exceed the limit and we already have messages in this chunk,
                    // stop here and start a new chunk with this message.
                    if (estimatedChunkTokens + msgTokens > maxTokensPerChunk && chunkEnd > chunkStart)
                    {
                        break;
                    }

                    // Include this message (even if it alone exceeds the limit - we must include it).
                    estimatedChunkTokens += msgTokens;
                    chunkEnd++;
                }

                var chunk = messages.GetRange(chunkStart, chunkEnd - chunkStart);
                Log.Debug("Auto-compaction: summarizing chunk {ChunkNumber} ({MessageCount} messages, ~{Tokens} estimated tokens)",
                    chunkNumber, chunk.Count, estimatedChunkTokens);

                string? chunkSummary = await SummarizeChunkAsync(compactModelName, chunk, baseUrl, apiKey, timeoutSeconds, compactModelContextWindow, ct);
                if (chunkSummary is not null)
                {
                    chunkSummaries.Add(chunkSummary);
                    Log.Debug("Auto-compaction: chunk {ChunkNumber} summarized successfully ({SummaryLength} chars)",
                        chunkNumber, chunkSummary.Length);
                }
                else
                {
                    Log.Warning("Auto-compaction: chunk {ChunkNumber} summarization failed", chunkNumber);
                }

                chunkStart = chunkEnd;
            }

            if (chunkSummaries.Count == 0)
            {
                Log.Warning("Auto-compaction: all chunk summaries failed for session {SessionKey}", sessionKey);
                return null;
            }

            // Reduce phase: combine all chunk summaries into a final coherent summary.
            Log.Debug("Auto-compaction: combining {SummaryCount} chunk summaries", chunkSummaries.Count);
            string finalSummary = await CombineSummariesAsync(compactModelName, chunkSummaries, baseUrl, apiKey, timeoutSeconds, ct);

            // Build the compacted request body with the final summary as a single system message.
            var compactedBody = BuildCompactedBodyWithSummary(requestBody, finalSummary);

            // Extract messages from compacted body for accurate token comparison
            var compactedMessages = ExtractMessagesAsArray(compactedBody);
            int compactedMessageTokens = compactedMessages.Sum(m => (int)(EstimateMessageTokens(m) * TokenEstimationSafetyFactor));

            Log.Information("Auto-compaction completed: original {OriginalTokens} tokens (messages only) → compacted {CompactedTokens} tokens (messages only)",
                totalEstimatedTokens, compactedMessageTokens);

            // Validate that compaction actually reduced the size (comparing message tokens to message tokens)
            if (compactedMessageTokens >= totalEstimatedTokens)
            {
                Log.Warning("Auto-compaction failed to reduce size: {OriginalTokens} → {CompactedTokens} message tokens. Returning null.",
                    totalEstimatedTokens, compactedMessageTokens);
                return null;
            }

            // Post-compaction validation: check if compacted body fits in target model's context window
            if (compactedMessageTokens > targetModelContextWindow)
            {
                Log.Warning("Auto-compaction result still exceeds target model context window: {CompactedTokens} tokens > {TargetContextWindow} context window. Returning null.",
                    compactedMessageTokens, targetModelContextWindow);
                return null;
            }

            Log.Information("Auto-compaction successful: compacted body fits in target model context window ({CompactedTokens}/{TargetContextWindow} tokens)",
                compactedMessageTokens, targetModelContextWindow);

            return compactedBody;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning(ex, "Auto-compaction request failed for session {SessionKey}", sessionKey);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-compaction unexpected error for session {SessionKey}", sessionKey);
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

    private static List<JsonElement> ExtractMessagesAsArray(string requestBody)
    {
        var result = new List<JsonElement>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("messages", out JsonElement messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement msg in messages.EnumerateArray())
                {
                    result.Add(msg.Clone());
                }
            }
        }
        catch
        {
            // Fall through.
        }

        return result;
    }

    private async Task<string?> CompactSinglePassAsync(
        string model,
        string requestBody,
        List<JsonElement> messages,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        string sessionKey,
        CancellationToken ct)
    {
        var compactRequest = new
        {
            model,
            input = messages,
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
        return BuildCompactedRequestBody(requestBody, responseBody);
    }

    private async Task<string?> SummarizeChunkAsync(
        string model,
        List<JsonElement> chunkMessages,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        int compactModelContextWindow,
        CancellationToken ct)
    {
        // Use percentage of compact model's context window instead of hardcoded value
        // Leave headroom for system prompt, response generation, and token estimation error
        int maxTokensPerRequest = (int)(compactModelContextWindow * ContextWindowFraction);
        const int maxTokensPerMessage = 10000; // Truncate individual messages if too long.

        int estimatedTokens = EstimateChunkTokens(chunkMessages);

        // If chunk is too large, split it into smaller sub-chunks using greedy token-based approach.
        if (estimatedTokens > maxTokensPerRequest)
        {
            Log.Debug("Chunk too large ({Tokens} tokens), splitting into sub-chunks", estimatedTokens);
            var subChunkSummaries = new List<string>();

            int subChunkStart = 0;
            while (subChunkStart < chunkMessages.Count)
            {
                int subChunkEnd = subChunkStart;
                int subChunkTokens = 0;

                // Greedily add messages that fit within the limit.
                while (subChunkEnd < chunkMessages.Count)
                {
                    int msgTokens = EstimateMessageTokens(chunkMessages[subChunkEnd]);

                    // If this message would exceed the limit and we already have messages, start new sub-chunk.
                    if (subChunkTokens + msgTokens > maxTokensPerRequest && subChunkEnd > subChunkStart)
                    {
                        break;
                    }

                    subChunkTokens += msgTokens;
                    subChunkEnd++;
                }

                var subChunk = chunkMessages.GetRange(subChunkStart, subChunkEnd - subChunkStart);
                string? subSummary = await SummarizeSubChunkAsync(model, subChunk, baseUrl, apiKey, timeoutSeconds, maxTokensPerMessage, ct);
                if (subSummary is not null)
                {
                    subChunkSummaries.Add(subSummary);
                }

                subChunkStart = subChunkEnd;
            }

            if (subChunkSummaries.Count == 0)
                return null;

            // Combine sub-chunk summaries.
            return string.Join("\n\n", subChunkSummaries);
        }

        // Build a chat completion request to summarize this chunk.
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "You are a conversation summarizer. Summarize the following conversation chunk concisely, preserving key information, decisions, and context. Focus on facts and outcomes rather than pleasantries."
            },
            new
            {
                role = "user",
                content = "Please summarize this conversation chunk:"
            }
        };

        // Add the chunk messages, truncating if necessary.
        foreach (var msg in chunkMessages)
        {
            messages.Add(TruncateMessageIfNeeded(msg, maxTokensPerMessage));
        }

        var request = new
        {
            model,
            messages,
            max_tokens = 1000,
            temperature = 0.3
        };

        string requestBody = JsonSerializer.Serialize(request);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(requestBody)),
        };
        httpReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string absoluteUri = $"{baseUrl.TrimEnd('/')}/v1/chat/completions";
        httpReq.RequestUri = new Uri(absoluteUri);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                httpReq, HttpCompletionOption.ResponseContentRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                Log.Warning("Chunk summarization failed: HTTP {StatusCode} {Body}",
                    (int)response.StatusCode, errorBody);
                return null;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content))
                {
                    return content.GetString();
                }
            }

            Log.Warning("Chunk summarization response missing content");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning(ex, "Chunk summarization request failed");
            return null;
        }
    }

    private async Task<string?> SummarizeSubChunkAsync(
        string model,
        List<JsonElement> subChunkMessages,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        int maxTokensPerMessage,
        CancellationToken ct)
    {
        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = "You are a conversation summarizer. Summarize the following conversation chunk concisely, preserving key information, decisions, and context. Focus on facts and outcomes rather than pleasantries."
            },
            new
            {
                role = "user",
                content = "Please summarize this conversation chunk:"
            }
        };

        foreach (var msg in subChunkMessages)
        {
            messages.Add(TruncateMessageIfNeeded(msg, maxTokensPerMessage));
        }

        var request = new
        {
            model,
            messages,
            max_tokens = 1000,
            temperature = 0.3
        };

        string requestBody = JsonSerializer.Serialize(request);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(requestBody)),
        };
        httpReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string absoluteUri = $"{baseUrl.TrimEnd('/')}/v1/chat/completions";
        httpReq.RequestUri = new Uri(absoluteUri);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                httpReq, HttpCompletionOption.ResponseContentRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                Log.Warning("Sub-chunk summarization failed: HTTP {StatusCode} {Body}",
                    (int)response.StatusCode, errorBody);
                return null;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content))
                {
                    return content.GetString();
                }
            }

            Log.Warning("Sub-chunk summarization response missing content");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning(ex, "Sub-chunk summarization request failed");
            return null;
        }
    }

    private static int EstimateChunkTokens(List<JsonElement> messages)
    {
        int totalChars = 0;
        foreach (var msg in messages)
        {
            if (msg.TryGetProperty("content", out JsonElement content))
            {
                string? text = content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : content.GetRawText();
                if (!string.IsNullOrEmpty(text))
                {
                    totalChars += text.Length;
                }
            }
        }
        // Rough estimate: 1 token ≈ 4 characters
        return totalChars / 4;
    }

    private static int EstimateMessageTokens(JsonElement message)
    {
        int totalChars = 0;

        // Count content
        if (message.TryGetProperty("content", out JsonElement content))
        {
            string? text = content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : content.GetRawText();
            if (!string.IsNullOrEmpty(text))
            {
                totalChars += text.Length;
            }
        }

        // Add overhead for role and other metadata (~20 chars)
        totalChars += 20;

        // Rough estimate: 1 token ≈ 4 characters
        return totalChars / 4;
    }

    private static object TruncateMessageIfNeeded(JsonElement message, int maxTokens)
    {
        int maxChars = maxTokens * 4;

        if (message.TryGetProperty("content", out JsonElement content))
        {
            string? text = content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : content.GetRawText();

            if (!string.IsNullOrEmpty(text) && text.Length > maxChars)
            {
                // Truncate and add ellipsis
                text = text[..maxChars] + "\n... [truncated]";

                // Rebuild the message with truncated content
                var result = new Dictionary<string, object?>();
                foreach (var prop in message.EnumerateObject())
                {
                    if (prop.Name == "content")
                    {
                        result[prop.Name] = text;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result[prop.Name] = prop.Value.GetString();
                    }
                    else
                    {
                        result[prop.Name] = prop.Value.GetRawText();
                    }
                }
                return result;
            }
        }

        // Return message as-is if no truncation needed
        return message;
    }

    private async Task<string> CombineSummariesAsync(
        string model,
        List<string> chunkSummaries,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var combinedInput = string.Join("\n\n---\n\n", chunkSummaries.Select((s, i) => $"Chunk {i + 1} Summary:\n{s}"));

        var messages = new object[]
        {
            new
            {
                role = "system",
                content = "You are a conversation summarizer. Combine multiple conversation chunk summaries into a single coherent summary. Preserve all important information, decisions, and context. Remove duplicates and organize chronologically. Create a concise but comprehensive summary."
            },
            new
            {
                role = "user",
                content = $"Please combine these conversation summaries into one coherent summary:\n\n{combinedInput}"
            }
        };

        var request = new
        {
            model,
            messages,
            max_tokens = 1500,
            temperature = 0.3
        };

        string requestBody = JsonSerializer.Serialize(request);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(requestBody)),
        };
        httpReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string absoluteUri = $"{baseUrl.TrimEnd('/')}/v1/chat/completions";
        httpReq.RequestUri = new Uri(absoluteUri);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                httpReq, HttpCompletionOption.ResponseContentRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                Log.Warning("Summary combination failed: HTTP {StatusCode} {Body}",
                    (int)response.StatusCode, errorBody);
                // Fallback: just concatenate the summaries.
                return combinedInput;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content))
                {
                    return content.GetString() ?? combinedInput;
                }
            }

            Log.Warning("Summary combination response missing content, using fallback");
            return combinedInput;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warning(ex, "Summary combination request failed, using fallback");
            return combinedInput;
        }
    }

    private static string BuildCompactedBodyWithSummary(string originalRequestBody, string summary)
    {
        try
        {
            // Extract model name from original request
            string modelName = "unknown";
            using (JsonDocument originalDoc = JsonDocument.Parse(originalRequestBody))
            {
                if (originalDoc.RootElement.TryGetProperty("model", out JsonElement modelProp))
                {
                    modelName = modelProp.GetString() ?? "unknown";
                }
            }

            // Build a completely new minimal request body
            var compactedRequest = new
            {
                model = modelName,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = $"Previous conversation summary:\n\n{summary}"
                    },
                    new
                    {
                        role = "user",
                        content = "Continuing our conversation based on the summary above."
                    }
                }
            };

            string result = JsonSerializer.Serialize(compactedRequest);
            Log.Debug("Built compacted body: {Length} chars", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build compacted body with summary. Original body length: {Length}, Summary length: {SummaryLength}",
                originalRequestBody.Length, summary.Length);

            // Last resort fallback
            return $"{{\"model\":\"unknown\",\"messages\":[{{\"role\":\"system\",\"content\":\"Previous conversation summary:\\n\\n{summary}\"}},{{\"role\":\"user\",\"content\":\"Continuing our conversation based on the summary above.\"}}]}}";
        }
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
