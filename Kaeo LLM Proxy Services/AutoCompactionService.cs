using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Defines the output format for context compaction.
/// </summary>
internal enum CompactionFormat
{
    /// <summary>
    /// Proxy's internal format: simple summary message prepended to conversation.
    /// </summary>
    Proxy,

    /// <summary>
    /// Ollama-compatible format: tool-based summary with CompactionToolName and prefix markers.
    /// Matches the format Ollama uses for native compaction.
    /// </summary>
    Ollama
}

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

    /// <summary>Maximum number of recursive combine passes when reducing chunk summaries.</summary>
    private const int MaxCombinePasses = 6;

    /// <summary>Token budget for the last user message kept verbatim in a compacted request.</summary>
    private const int MaxKeptSuffixTokens = 8000;

    /// <summary>
    /// Shared system prompt for chunk/sub-chunk summarization. Directs the model to keep tool
    /// activity explicit so compacted requests still record which tools succeeded.
    /// </summary>
    private const string SummarizerInstructions =
        "You are a conversation summarizer. Summarize the following conversation chunk concisely, preserving key information, decisions, and context. " +
        "Focus on facts and outcomes rather than pleasantries. " +
        "If the transcript includes a <toolcalls> section, those are tool invocations and their results. You MUST end your summary with a " +
        "'## Tool activity' section listing each tool called, whether it succeeded or failed, and any important outputs " +
        "(file paths, command results, errors, and decisions made from them). Never omit tool activity.";

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
    /// <param name="compactModelContextWindow">The compact model's context window in tokens (for chunk sizing).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="format">The output format for the compacted body (Proxy or Ollama).</param>
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
        CancellationToken ct,
        CompactionFormat format = CompactionFormat.Proxy)
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

            if (messages.Count == 0)
            {
                Log.Warning("Auto-compaction: no chat messages found in request body for session {SessionKey}; cannot compact", sessionKey);
                return null;
            }

            // Every request goes through the chunked map-reduce path. (The former single-pass
            // shortcut posted to /v1/responses/compact, which llama.cpp does not implement —
            // a conversation that fits in one budget simply produces one summarization request.)
            Log.Information(
                "Auto-compaction: chunked summarization for {MessageCount} messages, {TotalTokens} estimated tokens",
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

            // Reduce phase: combine chunk summaries into a single coherent summary.
            // A lone chunk summary is already coherent; skip the extra LLM call.
            string finalSummary;
            if (chunkSummaries.Count == 1)
            {
                finalSummary = chunkSummaries[0];
            }
            else
            {
                Log.Debug("Auto-compaction: combining {SummaryCount} chunk summaries", chunkSummaries.Count);
                finalSummary = await CombineSummariesAsync(compactModelName, chunkSummaries, baseUrl, apiKey, timeoutSeconds, compactModelContextWindow, ct);
            }

            // Build the compacted request body with the final summary
            var compactedBody = format == CompactionFormat.Ollama
                ? BuildOllamaCompactedBodyWithSummary(requestBody, finalSummary)
                : BuildCompactedBodyWithSummary(requestBody, finalSummary);

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

    /// <summary>
    /// Prepares chunk messages for the summarization request. Tool interactions
    /// (assistant tool_calls and role:"tool" results) are flattened into text lines inside a
    /// single &lt;toolcalls&gt; section appended after the transcript. This keeps the request
    /// valid for strict chat templates (no orphaned tool_call_id correlations when chunks
    /// split a call from its result) while giving the summarizer an explicit block to turn
    /// into a '## Tool activity' section.
    /// </summary>
    private static List<object> BuildTranscriptMessages(List<JsonElement> chunkMessages, int maxTokensPerMessage)
    {
        var transcript = new List<object>();
        var toolLines = new List<string>();

        foreach (JsonElement msg in chunkMessages)
        {
            string role = msg.TryGetProperty("role", out JsonElement roleEl)
                ? roleEl.GetString() ?? "user"
                : "user";

            if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                string toolName = msg.TryGetProperty("tool_name", out JsonElement tn)
                    ? tn.GetString() ?? "tool"
                    : "tool";
                toolLines.Add($"- Result of tool '{toolName}': {ShrinkForSummary(GetContentText(msg), maxTokensPerMessage)}");
                continue;
            }

            bool hasToolCalls = msg.TryGetProperty("tool_calls", out JsonElement toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array
                && toolCalls.GetArrayLength() > 0;

            if (hasToolCalls)
            {
                foreach (JsonElement call in msg.GetProperty("tool_calls").EnumerateArray())
                {
                    string name = "?";
                    string args = "";
                    if (call.TryGetProperty("function", out JsonElement fn))
                    {
                        if (fn.TryGetProperty("name", out JsonElement ne))
                            name = ne.GetString() ?? "?";
                        if (fn.TryGetProperty("arguments", out JsonElement ae))
                            args = ae.ValueKind == JsonValueKind.String ? ae.GetString() ?? "" : ae.GetRawText();
                    }

                    toolLines.Add($"- Assistant called tool '{name}' with arguments: {ShrinkForSummary(args, maxTokensPerMessage)}");
                }
            }

            string content = GetContentText(msg);
            if (string.IsNullOrEmpty(content))
                continue;

            if (hasToolCalls)
            {
                // Keep the assistant text but strip the raw tool_calls array: their matching
                // role:"tool" replies were flattened away, and an unpaired tool_calls field can
                // be rejected by strict upstream chat templates.
                JsonObject stripped = [];
                foreach (JsonProperty prop in msg.EnumerateObject())
                {
                    if (prop.NameEquals("tool_calls"u8))
                        continue;
                    stripped[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }

                if (stripped["content"] is JsonValue contentValue && contentValue.TryGetValue(out string? contentText))
                {
                    int maxChars = maxTokensPerMessage * 4;
                    if (contentText?.Length > maxChars)
                        stripped["content"] = contentText[..maxChars] + "\n... [truncated]";
                }

                transcript.Add(stripped);
            }
            else
            {
                transcript.Add(TruncateMessageIfNeeded(msg, maxTokensPerMessage));
            }
        }

        if (toolLines.Count > 0)
        {
            transcript.Add(new
            {
                role = "user",
                content = "<toolcalls>\n" + string.Join("\n", toolLines) + "\n</toolcalls>",
            });
        }

        return transcript;
    }

    private static string GetContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
            return "";

        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.GetRawText();
    }

    private static string ShrinkForSummary(string text, int maxTokens)
    {
        int maxChars = Math.Max(200, maxTokens * 4);
        return text.Length <= maxChars ? text : text[..maxChars] + "... [truncated]";
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
                content = SummarizerInstructions
            },
            new
            {
                role = "user",
                content = "Please summarize this conversation chunk:"
            }
        };

        // Add the chunk messages (tool calls flattened into a <toolcalls> section).
        messages.AddRange(BuildTranscriptMessages(chunkMessages, maxTokensPerMessage));

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
                content = SummarizerInstructions
            },
            new
            {
                role = "user",
                content = "Please summarize this conversation chunk:"
            }
        };

        // Add the sub-chunk messages (tool calls flattened into a <toolcalls> section).
        messages.AddRange(BuildTranscriptMessages(subChunkMessages, maxTokensPerMessage));

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

        // Include tool_calls payloads so chunk budgets account for them.
        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            totalChars += toolCalls.GetRawText().Length;
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

                // Rebuild the message with truncated content, preserving the JSON type of every
                // other property (tool_calls arrays must stay arrays, not become strings).
                JsonObject result = [];
                foreach (var prop in message.EnumerateObject())
                {
                    if (prop.Name == "content")
                    {
                        result[prop.Name] = text;
                    }
                    else
                    {
                        result[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                    }
                }
                return result;
            }
        }

        // Return message as-is if no truncation needed
        return message;
    }

    /// <summary>
    /// Reduce phase: combines chunk summaries into one coherent summary. When the combined
    /// summaries exceed the compact model's own context budget, they are summarized in
    /// batches first and the process repeats until everything fits in a single request
    /// (summarize the summaries, then summarize those, and so on).
    /// </summary>
    private async Task<string> CombineSummariesAsync(
        string model,
        List<string> chunkSummaries,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        int compactModelContextWindow,
        CancellationToken ct)
    {
        int maxTokensPerRequest = (int)(compactModelContextWindow * ContextWindowFraction);
        List<string> current = chunkSummaries;
        int pass = 0;

        while (true)
        {
            pass++;
            string combinedInput = string.Join("\n\n---\n\n",
                current.Select((s, i) => $"Chunk {i + 1} Summary:\n{s}"));
            int combinedTokens = (int)(Encoding.UTF8.GetByteCount(combinedInput) / 4 * TokenEstimationSafetyFactor);

            if (combinedTokens <= maxTokensPerRequest)
                return await CombineOnceAsync(model, combinedInput, baseUrl, apiKey, timeoutSeconds, ct);

            if (pass >= MaxCombinePasses)
            {
                Log.Warning(
                    "Auto-compaction: combine exceeded {Passes} passes ({CombinedTokens} tokens > {MaxTokens}); truncating before final combine",
                    MaxCombinePasses, combinedTokens, maxTokensPerRequest);
                int maxChars = maxTokensPerRequest * 4;
                return await CombineOnceAsync(model,
                    combinedInput[..Math.Min(combinedInput.Length, maxChars)] + "\n... [truncated]",
                    baseUrl, apiKey, timeoutSeconds, ct);
            }

            // Split the summaries into batches that fit under the budget, summarize each
            // batch, and loop with the (fewer) batch summaries.
            Log.Information("Auto-compaction: combine pass {Pass} over budget ({CombinedTokens} tokens > {MaxTokens}); batching {SummaryCount} summaries",
                pass, combinedTokens, maxTokensPerRequest, current.Count);

            var batches = new List<List<string>>();
            var batch = new List<string>();
            int batchTokens = 0;
            foreach (string s in current)
            {
                int sTokens = (int)(Encoding.UTF8.GetByteCount(s) / 4 * TokenEstimationSafetyFactor);
                if (batch.Count > 0 && batchTokens + sTokens > maxTokensPerRequest)
                {
                    batches.Add(batch);
                    batch = [];
                    batchTokens = 0;
                }
                batch.Add(s);
                batchTokens += sTokens;
            }
            if (batch.Count > 0)
                batches.Add(batch);

            var next = new List<string>(batches.Count);
            foreach (List<string> b in batches)
            {
                string joined = string.Join("\n\n---\n\n", b.Select((s, i) => $"Part {i + 1}:\n{s}"));
                next.Add(await CombineOnceAsync(model, joined, baseUrl, apiKey, timeoutSeconds, ct));
            }
            current = next;
        }
    }

    /// <summary>
    /// Single LLM call that merges the given pre-joined summary text into one coherent summary.
    /// Falls back to the input text when the request fails.
    /// </summary>
    private async Task<string> CombineOnceAsync(
        string model,
        string combinedInput,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var messages = new object[]
        {
            new
            {
                role = "system",
                content = "You are a conversation summarizer. Combine multiple conversation chunk summaries into a single coherent summary. Preserve all important information, decisions, and context. Remove duplicates and organize chronologically. If the summaries contain '## Tool activity' sections, merge them into a single final '## Tool activity' section that keeps every distinct tool invocation and its outcome."
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
                // Fallback: just return the concatenated summaries.
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

    /// <summary>
    /// Builds a compacted request body using the proxy's internal format: the pinned system
    /// instructions merged with a summary block, plus the latest user message so the live
    /// question survives compaction.
    /// </summary>
    private static string BuildCompactedBodyWithSummary(string originalRequestBody, string summary)
    {
        try
        {
            string modelName = ExtractModelName(originalRequestBody);
            var source = ExtractMessagesAsArray(originalRequestBody);
            string summaryBlock = $"Previous conversation summary:\n\n{summary}";

            var messages = new List<object>
            {
                new { role = "system", content = MergeSummaryIntoSystemMessage(source, summaryBlock) },
            };

            var trailing = ExtractTrailingMessages(source);
            if (trailing.Count > 0)
                messages.AddRange(trailing);
            else
                messages.Add(new { role = "user", content = "Continuing our conversation based on the summary above." });

            string result = JsonSerializer.Serialize(new { model = modelName, messages });
            Log.Debug("Built compacted body (Proxy format): {Length} chars", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build compacted body with summary. Original body length: {Length}, Summary length: {SummaryLength}",
                originalRequestBody.Length, summary.Length);

            return JsonSerializer.Serialize(new
            {
                model = "unknown",
                messages = new[] { new { role = "system", content = $"Previous conversation summary:\n\n{summary}" } },
            });
        }
    }

    /// <summary>
    /// Builds a compacted request body using Ollama's native compaction format:
    /// tool-based summary with CompactionToolName and prefix markers.
    /// Matches the format Ollama's SimpleCompactor produces.
    /// </summary>
    /// <remarks>
    /// Ollama's compaction format uses a tool call/response pair:
    /// 1. An assistant message with a tool_call to the compaction tool
    /// 2. A tool response message with the summary prefixed by the compaction marker
    /// This allows Ollama-compatible clients (like Copilot in Ollama mode) to recognize
    /// and properly handle the compacted context.
    /// </remarks>
    private static string BuildOllamaCompactedBodyWithSummary(string originalRequestBody, string summary)
    {
        try
        {
            string modelName = ExtractModelName(originalRequestBody);
            var source = ExtractMessagesAsArray(originalRequestBody);

            // Ollama compaction format constants (from Ollama's agent/compactor.go)
            const string compactionToolName = "session_summary";
            const string compactionSummaryPrefix = "<conversation_summary>";
            const string compactionContinueInstruction = "\n\nContinue the task from where it left off.";

            string summaryContent = $"{compactionSummaryPrefix}{summary}{compactionContinueInstruction}";

            var messages = new List<object>();

            // Ollama's compactor pins the leading system message; keep it before the summary pair.
            string? pinnedSystem = ExtractPinnedSystemMessage(source);
            if (pinnedSystem is not null)
                messages.Add(new { role = "system", content = pinnedSystem });

            // Assistant message with tool call to compaction tool
            messages.Add(new
            {
                role = "assistant",
                tool_calls = new[]
                {
                    new
                    {
                        id = "compaction_summary",
                        type = "function",
                        function = new
                        {
                            name = compactionToolName,
                            arguments = "{}"
                        }
                    }
                }
            });

            // Tool response with summary
            messages.Add(new
            {
                role = "tool",
                tool_name = compactionToolName,
                tool_call_id = "compaction_summary",
                content = summaryContent
            });

            // Keep the trailing turn (last user message + any pending tool results) so the model
            // still has the live question and can continue after tool calls.
            var trailing = ExtractTrailingMessages(source);
            if (trailing.Count > 0)
                messages.AddRange(trailing);

            string result = JsonSerializer.Serialize(new { model = modelName, messages });
            Log.Debug("Built compacted body (Ollama format): {Length} chars", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to build Ollama compacted body. Original body length: {Length}, Summary length: {SummaryLength}",
                originalRequestBody.Length, summary.Length);

            // Fallback to proxy format
            return BuildCompactedBodyWithSummary(originalRequestBody, summary);
        }
    }

    /// <summary>
    /// Returns the content of the leading system message (pinned instructions), if any.
    /// </summary>
    private static string? ExtractPinnedSystemMessage(List<JsonElement> messages)
    {
        if (messages.Count > 0
            && messages[0].TryGetProperty("role", out JsonElement role)
            && role.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true
            && messages[0].TryGetProperty("content", out JsonElement content))
        {
            string? text = content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    /// <summary>
    /// Merges the summary block into the pinned system message so the compacted request keeps
    /// its original behavioral instructions.
    /// </summary>
    private static string MergeSummaryIntoSystemMessage(List<JsonElement> messages, string summaryBlock)
    {
        string? pinned = ExtractPinnedSystemMessage(messages);
        return pinned is null ? summaryBlock : $"{pinned}\n\n{summaryBlock}";
    }

    /// <summary>
    /// Returns the trailing turn of the conversation (last user message + any assistant tool_calls
    /// + tool results after it), truncated to keep the compacted request small. This preserves
    /// pending tool state so the model can continue after tool calls.
    /// </summary>
    private static List<object> ExtractTrailingMessages(List<JsonElement> messages)
    {
        var result = new List<object>();

        // Find the last user message
        int lastUserIndex = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].TryGetProperty("role", out JsonElement role)
                && role.GetString()?.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
            {
                lastUserIndex = i;
                break;
            }
        }

        if (lastUserIndex < 0)
            return result;

        // Include the last user message and everything after it (assistant tool_calls, tool results)
        int count = messages.Count - lastUserIndex;
        int perMessageTokens = Math.Max(250, MaxKeptSuffixTokens / count);

        for (int i = lastUserIndex; i < messages.Count; i++)
        {
            result.Add(TruncateMessageIfNeeded(messages[i], perMessageTokens));
        }

        return result;
    }

    private static string ExtractModelName(string requestBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("model", out JsonElement modelProp))
            {
                return modelProp.GetString() ?? "unknown";
            }
        }
        catch
        {
            // Fall through to default
        }
        return "unknown";
    }
}
