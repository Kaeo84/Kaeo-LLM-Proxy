using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Services.Mcp;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Handles translation between Ollama API requests and llama.cpp OpenAI-compatible API requests.
/// Supports streaming, non-streaming, tool calls, JSON format mode, and batch embeddings.
/// </summary>
internal sealed class OllamaProxyHandler(AppSettings settings, StatisticsService stats, ModuleHost moduleHost, McpServerService mcpServer) : IDisposable
{
    internal const string RedactedBodyText = "[REDACTED BY MODEL LOG REDACTION SETTINGS]";
    private const string RedactedValueText = "[REDACTED]";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private volatile AppSettings _settings = settings;

    // Shared pooled HttpClient — avoids socket exhaustion under load.
    private HttpClient _httpClient = BuildHttpClient();

    // Number of requests currently being processed by HandleAsync. Used to defer disposal of a
    // superseded HttpClient until in-flight requests that may still be using it have completed.
    private int _inFlightRequests;

    private readonly StatisticsService _stats = stats;
    private readonly ModuleHost _moduleHost = moduleHost;
    private readonly McpServerService _mcpServer = mcpServer;
    private readonly ConcurrentDictionary<string, PeriodicHeartbeatState> _periodicHeartbeats = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoCompactionService _autoCompactionService = new(BuildHttpClient());

    /// <summary>Called from the Settings UI after the user saves new settings.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        HttpClient old = _httpClient;
        _httpClient = BuildHttpClient();

        // Dispose the superseded client only once no in-flight requests remain that could still
        // be using it. A fixed delay is unsafe because requests can run up to the per-mapping
        // upstream timeout (e.g. 300 s). We poll the in-flight counter and fall back to a hard
        // 5-minute safety timeout so the old client is never leaked indefinitely.
        _ = Task.Run(async () =>
        {
            try
            {
                DateTime deadline = DateTime.UtcNow.AddMinutes(5);
                while (Volatile.Read(ref _inFlightRequests) > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort wait; always dispose below.
            }
            finally
            {
                old.Dispose();
            }
        });

        SynchronizeHeartbeatMonitors();
    }

    public void StartHeartbeatMonitors() => SynchronizeHeartbeatMonitors();

    public void StopHeartbeatMonitors()
    {
        foreach (PeriodicHeartbeatState state in _periodicHeartbeats.Values)
            state.Dispose();

        _periodicHeartbeats.Clear();
    }

    private void SynchronizeHeartbeatMonitors()
    {
        HashSet<string> activeKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            string modelName = GetHeartbeatModelName(mapping);
            if (string.IsNullOrWhiteSpace(modelName))
                continue;

            _stats.RegisterHeartbeatModel(modelName);

            string key = modelName.Trim();
            activeKeys.Add(key);

            if (!mapping.IsEnabled || !_settings.EnableStreamingHeartbeats || !mapping.EnableHeartbeats)
            {
                if (_periodicHeartbeats.TryRemove(key, out PeriodicHeartbeatState? removed))
                    removed.Dispose();
                continue;
            }

            if (_periodicHeartbeats.TryGetValue(key, out PeriodicHeartbeatState? existing))
            {
                existing.Update(mapping, _settings.StreamingHeartbeatIntervalSeconds);
                continue;
            }

            PeriodicHeartbeatState created = new(
                mapping,
                _settings.StreamingHeartbeatIntervalSeconds,
                SendPeriodicHeartbeatAsync,
                RecordPeriodicHeartbeatFailure);
            if (!_periodicHeartbeats.TryAdd(key, created))
                created.Dispose();
        }

        foreach (string key in _periodicHeartbeats.Keys)
        {
            if (activeKeys.Contains(key))
                continue;

            if (_periodicHeartbeats.TryRemove(key, out PeriodicHeartbeatState? removed))
                removed.Dispose();
        }
    }

    private async Task SendPeriodicHeartbeatAsync(ModelMapping mapping, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
            return;

        string modelName = GetHeartbeatModelName(mapping);
        if (string.IsNullOrWhiteSpace(modelName))
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        ApplyApiKey(request, _settings.ResolveApiKey(mapping));
        int timeout = mapping.UpstreamTimeoutSeconds > 0 ? mapping.UpstreamTimeoutSeconds : 300;
        using HttpResponseMessage response = await SendUpstreamAsync(
            request,
            mapping.UpstreamUrl.TrimEnd('/'),
            timeout,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (response.IsSuccessStatusCode)
        {
            _stats.IncrementHeartbeat(modelName);
            return;
        }

        string error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        _stats.RecordHeartbeatFailure(modelName, error);
        Log.Warning("Heartbeat probe for model {Model} returned {Error}", modelName, error);
    }

    private void RecordPeriodicHeartbeatFailure(ModelMapping mapping, string errorMessage)
    {
        string modelName = GetHeartbeatModelName(mapping);
        if (!string.IsNullOrWhiteSpace(modelName))
            _stats.RecordHeartbeatFailure(modelName, errorMessage);
    }

    private static string GetHeartbeatModelName(ModelMapping mapping)
        => string.IsNullOrWhiteSpace(mapping.ProxyName) ? mapping.ModelName : mapping.ProxyName;

    public void Dispose()
    {
        StopHeartbeatMonitors();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Returns the base URL, timeout, and optional bearer API key to use for a given Ollama model name.
    /// Requires each mapping to have its own upstream URL configured.
    /// If ollamaModel is null or empty and there's at least one mapping configured,
    /// returns the first mapping's upstream settings as a fallback.
    /// </summary>
    private (string BaseUrl, int TimeoutSeconds, string? ApiKey) ResolveUpstream(string ollamaModel)
    {
        ModelMapping? mapping = _settings.FindModelMapping(ollamaModel);
        if (mapping is not null)
        {
            if (string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
                throw new InvalidOperationException(
                    $"Model mapping '{mapping.ProxyName}' has no upstream URL configured. " +
                    "Each mapping must specify its own UpstreamUrl.");

            int timeout = mapping.UpstreamTimeoutSeconds > 0 ? mapping.UpstreamTimeoutSeconds : 300;
            return (mapping.UpstreamUrl.TrimEnd('/'), timeout, _settings.ResolveApiKey(mapping));
        }

        // Fallback: if model name is empty/null and we have at least one mapping,
        // use the first configured mapping's upstream URL (common for single-model setups)
        if (string.IsNullOrWhiteSpace(ollamaModel))
        {
            ModelMapping fallback = _settings.ModelMappings.FirstOrDefault(m => m.IsEnabled)
                ?? throw new InvalidOperationException("No enabled model mappings are configured.");
            if (string.IsNullOrWhiteSpace(fallback.UpstreamUrl))
                throw new InvalidOperationException(
                    $"Model mapping '{fallback.ProxyName}' has no upstream URL configured. " +
                    "Each mapping must specify its own UpstreamUrl.");

            int timeout = fallback.UpstreamTimeoutSeconds > 0 ? fallback.UpstreamTimeoutSeconds : 300;
            return (fallback.UpstreamUrl.TrimEnd('/'), timeout, _settings.ResolveApiKey(fallback));
        }

        throw new InvalidOperationException(
            $"No mapping found for model '{ollamaModel}'. " +
            "Add a mapping in settings with ProxyName, ModelName, and UpstreamUrl.");
    }

    internal static bool ShouldApplyThinkingCompatibility(AppSettings settings, string modelName)
    {
        ModelMapping? mapping = settings.FindModelMapping(modelName);
        return mapping?.EnableThinkingCompatibility ?? true;
    }

    /// <summary>
    /// Detects whether a request is a GitHub Copilot context-summarize (/compact) request by
    /// inspecting only the head of the first message. The Copilot /compact system prompt begins
    /// with a distinctive instruction to produce a session summary; matching a short prefix keeps
    /// the check extremely cheap without scanning the (potentially large) full conversation body.
    /// </summary>
    internal static bool IsContextSummarizeRequest(string? firstMessageContent)
    {
        if (string.IsNullOrEmpty(firstMessageContent))
            return false;

        const int HeadLength = 512;
        int len = Math.Min(firstMessageContent.Length, HeadLength);
        string head = firstMessageContent.AsSpan(0, len).ToString();

        return head.Contains("authoritative, self-contained summary", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<ConversationSummary>", StringComparison.Ordinal)
            || head.Contains("ReasoningScratchpad", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detect whether this incoming HTTP request is originating from GitHub Copilot.
    /// Uses a lightweight heuristic: the User-Agent often contains "copilot" or "github".
    /// Falls back to inspecting the first message content for the /compact signature when
    /// the body is available.
    /// </summary>
    internal static bool IsCopilotRequest(string? userAgent, string? firstMessageContent = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                if (userAgent.IndexOf("copilot", StringComparison.OrdinalIgnoreCase) >= 0
                    || userAgent.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // As a secondary check, if a body-first message is available, check for the compact signature.
            if (!string.IsNullOrEmpty(firstMessageContent) && IsContextSummarizeRequest(firstMessageContent))
                return true;
        }
        catch
        {
            // Best-effort only; do not throw on detection errors.
        }

        return false;
    }

    /// <summary>
    /// Overload that extracts the User-Agent from an HttpListenerRequest.
    /// </summary>
    private static bool IsCopilotRequest(HttpListenerRequest? req, string? firstMessageContent = null)
    {
        string? userAgent = null;
        try
        {
            if (req is not null)
            {
                userAgent = req.Headers["User-Agent"];
            }
        }
        catch
        {
            // Best-effort only; do not throw on detection errors.
        }

        return IsCopilotRequest(userAgent, firstMessageContent);
    }

    /// <summary>
    /// Returns the effective proxy model name for a request, applying the context-summarize
    /// (/compact) redirect when the mapping has a smaller/faster compact model configured
    /// (<see cref="ModelMapping.ContextSummarizeModelId"/>) and the request is detected as a
    /// Copilot /compact summary request. Returns the original model name unchanged when no
    /// redirect applies (not a summarize request, no compact model configured, or the compact
    /// model is not a valid enabled proxy model).
    /// </summary>
    internal static string ResolveEffectiveModel(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return originalModel;

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null || !mapping.ContextSummarizeModelId.HasValue)
            return originalModel;

        ModelMapping? compactMapping = settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
        // Only redirect when the compact model is itself a valid enabled proxy model.
        if (compactMapping is null || !compactMapping.IsEnabled)
            return originalModel;

        return compactMapping.ProxyName;
    }

    /// <summary>
    /// Extracts the first message's text content from an OpenAI-style request body root
    /// (a <c>messages</c> array). Content may be a plain string or an array of typed parts
    /// (e.g. <c>[{"type":"text","text":"..."}]</c>), which OpenAI-compatible clients such as
    /// Copilot commonly emit even for plain text; both shapes are handled so the compact-prompt
    /// signature is detected regardless of wire format. Returns null when the body has no
    /// messages or the first message carries no text content.
    /// </summary>
    private static string? GetFirstMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out JsonElement messagesEl) || messagesEl.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement msg in messagesEl.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
                return null;
            if (!msg.TryGetProperty("content", out JsonElement contentEl))
                return null;

            switch (contentEl.ValueKind)
            {
                case JsonValueKind.String:
                    return contentEl.GetString();
                case JsonValueKind.Array:
                    StringBuilder text = new();
                    foreach (JsonElement part in contentEl.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                            continue;
                        if (part.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String)
                            text.Append(textEl.GetString());
                    }
                    return text.Length > 0 ? text.ToString() : null;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Explains why the context-summarize (/compact) redirect did not apply for a request, for
    /// diagnostic logging. Reports which gate in <see cref="ResolveEffectiveModel"/> stopped the
    /// redirect: signature not detected, no mapping found, no compact model configured, or the
    /// compact model not being a valid enabled proxy. Returns a generic fallback if every gate
    /// passed (which would mean a redirect was expected but did not occur).
    /// </summary>
    private static string DescribeCompactSkipReason(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return "first message did not match a /compact signature";

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null)
            return $"no mapping found for model '{originalModel}'";
        if (!mapping.ContextSummarizeModelId.HasValue)
            return "no ContextSummarizeModelId configured on the mapping";

        ModelMapping? compactMapping = settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
        if (compactMapping is null)
            return $"compact model with ID {mapping.ContextSummarizeModelId.Value} not found";
        if (!compactMapping.IsEnabled)
            return $"compact model '{compactMapping.ProxyName}' (ID {mapping.ContextSummarizeModelId.Value}) is not enabled";

        return "unknown (redirect should have fired)";
    }

    /// <summary>
    /// Maps the Ollama <c>think</c> request field (a boolean or a "low"/"medium"/"high"/"max"
    /// level) to an OpenAI <c>reasoning_effort</c> value. <c>true</c> enables thinking at high
    /// effort; <c>false</c> or a missing field produces null (the field is omitted); the named
    /// levels pass through with "max" clamped to "high", which OpenAI-style providers accept.
    /// </summary>
    internal static string? MapThinkToReasoningEffort(object? think)
    {
        string? level = think switch
        {
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.True => "high",
                JsonValueKind.String => je.GetString(),
                _ => null,
            },
            bool flag => flag ? "high" : null,
            string s => s,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(level))
            return null;

        return level.Trim().ToLowerInvariant() switch
        {
            "low" or "medium" or "high" => level.Trim().ToLowerInvariant(),
            "max" => "high",
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the lowercased reasoning_effort to inject into a translated upstream chat
    /// request. Proxy priority injects the mapping's configured value (override); Client App
    /// priority forwards the Ollama client's <c>think</c> field (already mapped to an effort
    /// level); Provider priority omits the field.
    /// </summary>
    internal static string? ResolveReasoningEffort(ModelMapping? mapping, string? clientEffort) =>
        mapping?.ReasoningEffortPriority switch
        {
            SamplingPriority.Provider => null,
            SamplingPriority.Proxy
                => string.IsNullOrWhiteSpace(mapping.ReasoningEffort)
                    ? null
                    : mapping.ReasoningEffort.Trim().ToLowerInvariant(),
            _ => clientEffort,
        };

    /// <summary>
    /// Applies the resolved reasoning effort — the mapping's configured value under Proxy
    /// priority, the client's <c>think</c> field under Client App priority — to a translated
    /// chat request, emitting every wire shape selected in the mapping's
    /// <see cref="ReasoningEffortFormat"/> flags: legacy top-level field, modern nested
    /// object, the Qwen Cloud <c>extra_body</c> wrapper, and/or <c>chat_template_kwargs</c>.
    /// </summary>
    private static void ApplyReasoningEffort(ModelMapping? mapping, LlamaCppChatRequest request, object? clientThink = null)
    {
        string? effort = ResolveReasoningEffort(mapping, MapThinkToReasoningEffort(clientThink));
        if (effort is null)
            return;

        ReasoningEffortFormat format = mapping?.ReasoningEffortFormat ?? ReasoningEffortFormat.Legacy;

        if (format.HasFlag(ReasoningEffortFormat.Legacy))
            request.ReasoningEffort = effort;
        if (format.HasFlag(ReasoningEffortFormat.Modern))
            request.Reasoning = new LlamaCppReasoning { Enable = true, ThinkingLevel = effort };
        if (format.HasFlag(ReasoningEffortFormat.QwenCloud))
            request.ExtraBody = new LlamaCppExtraBody { EnableThinking = true, ReasoningEffort = effort };
        if (format.HasFlag(ReasoningEffortFormat.ChatTemplateKwargs))
            request.ChatTemplateKwargs = new LlamaCppChatTemplateKwargs { EnableThinking = true, ReasoningEffort = effort };
    }

    /// <summary>
    /// Resolves which sampling value to send upstream per the per-model priority: the client's
    /// value wins (<see cref="SamplingPriority.ClientApp"/>), the proxy's configured value
    /// overrides (<see cref="SamplingPriority.Proxy"/>), or the field is omitted entirely
    /// (<see cref="SamplingPriority.Provider"/>).
    /// </summary>
    private static float? ResolveSamplingValue(SamplingPriority priority, float? clientValue, float proxyValue) =>
        priority switch
        {
            SamplingPriority.Provider => null,
            SamplingPriority.Proxy => proxyValue,
            _ => clientValue,
        };

    /// <summary>
    /// Returns whether heartbeats should be emitted for the given model, combining the
    /// global toggle with the per-mapping <see cref="ModelMapping.EnableHeartbeats"/> flag.
    /// </summary>
    private bool ShouldEmitHeartbeats(string modelName)
    {
        if (!_settings.EnableStreamingHeartbeats) return false;
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableHeartbeats ?? true;
    }

    /// <summary>
    /// Checks if the upstream error response indicates a context size overflow.
    /// Returns a tuple of (isOverflow, body) where body is the response content read once.
    /// </summary>
    private static async Task<(bool IsOverflow, string Body)> IsContextOverflowErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return (false, string.Empty);

        // llama.cpp returns 400 for exceed_context_size_error; other providers may use 413 or 500.
        int status = (int)response.StatusCode;
        if (status != 400 && status != 413 && status != 500)
            return (false, string.Empty);

        string body = await response.Content.ReadAsStringAsync(ct);
        return (IsContextOverflowBody(body), body);
    }

    /// <summary>
    /// Checks an already-read error body string for context overflow indicators.
    /// Use this when the body has already been consumed from the response stream.
    /// </summary>
    private static bool IsContextOverflowBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            // Try to parse as structured error
            LlamaCppErrorResponse? errorResp = JsonSerializer.Deserialize<LlamaCppErrorResponse>(body, _jsonOptions);
            string? errorMessage = errorResp?.Error?.Message;
            string? errorType = errorResp?.Error?.Type;

            // Most reliable: the structured error type from llama.cpp
            if (!string.IsNullOrWhiteSpace(errorType)
                && errorType.Contains("context", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = body;

            // Check for common context overflow patterns across providers.
            // "exceed" matches both "exceeds" (llama.cpp) and "exceeded" (OpenAI/Anthropic).
            return errorMessage.Contains("context", StringComparison.OrdinalIgnoreCase)
                && (errorMessage.Contains("exceed", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too large", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too long", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("max tokens", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("token limit", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Estimates the token count of a serialized request body using a ~4 chars/token heuristic.
    /// Intentionally conservative (overestimates) so compaction thresholds favor compacting
    /// early rather than missing an overflow.
    /// </summary>
    private static int EstimateTokenCount(string body) => body.Length / 4;

    /// <summary>
    /// When the mapping's compaction threshold is exceeded, attempt to produce a compacted
    /// request body and return it. This method no longer short-circuits with a 413; if
    /// compaction is not possible we allow the request to proceed to upstream so the
    /// upstream provider can return an authoritative error (413/400/etc.).
    /// </summary>
    private async Task<(bool overflow, string? compactedBody)> TryProactiveOverflowAsync(
        ModelMapping? mapping,
        string body,
        string model,
        HttpListenerResponse resp,
        RequestLog log,
        AutoCompactPaths requestPath,
        Stream? outputStream,
        CancellationToken ct)
    {
        int threshold = mapping?.GetProactiveOverflowThreshold() ?? 0;
        int estimated = EstimateTokenCount(body);

        if (threshold <= 0)
        {
            // Warn once per mapping when threshold is disabled but context is large
            if (estimated > 50000 && mapping is not null)
            {
                Log.Warning("Auto-compaction disabled for model {Model} (threshold=0), but request has ~{EstimatedTokens} tokens. Consider setting ProactiveOverflowPercent or ProactiveOverflowTokens.",
                    model, estimated);
            }
            return (false, null);
        }

        if (estimated <= threshold)
            return (false, null);

        // Check if auto-compaction should be attempted for this request.
        if (mapping is not null && _autoCompactionService.ShouldCompact(mapping, requestPath, body, out string sessionKey))
        {
            // Stream notification: compaction needed
            if (outputStream is not null)
            {
                string notification = $": <ignorethis>kaeo-compaction-needed: Context size (~{estimated} tokens) exceeds threshold ({threshold} tokens). Starting compaction...</ignorethis>\n\n";
                byte[] notificationBytes = Encoding.UTF8.GetBytes(notification);
                await outputStream.WriteAsync(notificationBytes, ct);
                await outputStream.FlushAsync(ct);
            }

            try
            {
                // Resolve the compact model: global CompactModelProxyName first, then the
                // per-mapping ContextSummarizeModelId. Summarization requests go directly to
                // this model's upstream, so BOTH the base URL and the upstream model name
                // (not the proxy display name) must come from the resolved mapping.
                ModelMapping? compactMapping = null;
                if (!string.IsNullOrWhiteSpace(_settings.CompactModelProxyName))
                    compactMapping = _settings.FindModelMapping(_settings.CompactModelProxyName);
                if (compactMapping is null && mapping.ContextSummarizeModelId.HasValue)
                    compactMapping = _settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
                if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                    compactMapping = null;

                if (compactMapping is null && mapping.ContextSummarizeModelId.HasValue)
                {
                    Log.Warning("Auto-compaction: compact model mapping {CompactModelId} not found or disabled, falling back to original model {Model}",
                        mapping.ContextSummarizeModelId.Value, model);
                }

                var (baseUrl, timeout, apiKey) = ResolveUpstream(compactMapping?.ProxyName ?? model);
                string compactModelName = (compactMapping ?? mapping).ModelName ?? model;
                int compactModelContext = (compactMapping ?? mapping).GetEffectiveContextWindow();
                int maxTokensPerChunk = (int)(compactModelContext * AutoCompactionService.ContextWindowFraction);
                int targetModelContextWindow = mapping.GetEffectiveContextWindow();

                // Stream notification: compaction starting
                if (outputStream is not null)
                {
                    string notification = $": <ignorethis>kaeo-compaction-starting: Compacting context using model '{compactModelName}' (context window: {compactModelContext} tokens)...</ignorethis>\n\n";
                    byte[] notificationBytes = Encoding.UTF8.GetBytes(notification);
                    await outputStream.WriteAsync(notificationBytes, ct);
                    await outputStream.FlushAsync(ct);
                }

                string? compactedBody = await _autoCompactionService.CompactAsync(
                    mapping,
                    body,
                    sessionKey,
                    baseUrl,
                    apiKey,
                    timeout,
                    maxTokensPerChunk,
                    compactModelName,
                    targetModelContextWindow,
                    compactModelContext,
                    ct);

                if (compactedBody is not null)
                {
                    _autoCompactionService.RecordSuccess(sessionKey);

                    // Add headers to signal compaction happened
                    resp.Headers["X-Context-Compacted"] = "true";
                    resp.Headers["X-Context-Original-Tokens"] = estimated.ToString();
                    resp.Headers["X-Context-Compacted-Tokens"] = (compactedBody.Length / 4).ToString();

                    Log.Information("Auto-compaction succeeded: {OriginalTokens} → {CompactedTokens} tokens",
                        estimated, compactedBody.Length / 4);

                    // Stream notification: compaction finished
                    if (outputStream is not null)
                    {
                        int compactedTokens = compactedBody.Length / 4;
                        string notification = $": <ignorethis>kaeo-compaction-complete: Context compacted successfully. {estimated} tokens → {compactedTokens} tokens ({100 - (compactedTokens * 100 / estimated)}% reduction)</ignorethis>\n\n";
                        byte[] notificationBytes = Encoding.UTF8.GetBytes(notification);
                        await outputStream.WriteAsync(notificationBytes, ct);
                        await outputStream.FlushAsync(ct);
                    }

                    // For streaming requests, forward the compacted body so the response streams back
                    bool isStreaming = IsStreamingJsonBody(body);
                    if (isStreaming)
                    {
                        return (false, compactedBody);
                    }

                    // For non-streaming requests, return 413 to signal to Copilot that context
                    // was too large and has been compacted. Copilot will retry with reduced context.
                    log.Status = RequestStatus.Error;
                    log.StatusCode = 413;
                    log.ErrorMessage = $"Context compacted: {estimated} tokens reduced to {compactedBody.Length / 4} tokens";
                    resp.StatusCode = 413;
                    resp.ContentType = "application/json";

                    await WriteJsonAsync(resp, new
                    {
                        error = new
                        {
                            code = 413,
                            message = $"Context size ({estimated} tokens) exceeded threshold. Conversation has been summarized. Please retry with reduced context.",
                            type = "context_compacted",
                            compacted = true,
                            original_tokens = estimated,
                            compacted_tokens = compactedBody.Length / 4
                        }
                    }, ct);

                    return (true, null); // Signal overflow to stop processing
                }
                else
                {
                    Log.Warning("Auto-compaction returned null for session {SessionKey}", sessionKey);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto-compaction failed for model {Model}", model);
                // Fall through — but do not send a proactive 413; allow upstream to decide.
            }
        }

        // No compaction produced. Do not short-circuit with 413 here — let upstream
        // return the authoritative error if it overflows. Return (false, null) so
        // the caller proceeds with the original body.
        return (false, null);
    }

    /// <summary>
    /// Attempts local context compaction after the upstream rejected the prompt with a
    /// context-size overflow error. The caller retries the original request once with the
    /// returned compacted body. Unlike the proactive path this only requires
    /// <see cref="AppSettings.EnableAutoCompaction"/>, so oversized prompts self-heal even
    /// when no threshold was configured for the mapping.
    /// </summary>
    private async Task<string?> TryReactiveCompactionAsync(
        string body,
        string model,
        HttpListenerResponse resp,
        bool streamAlreadyOpen,
        CancellationToken ct)
    {
        ModelMapping? mapping = _settings.FindModelMapping(model);
        if (mapping is null)
            return null;

        try
        {
            if (streamAlreadyOpen)
            {
                byte[] note = Encoding.UTF8.GetBytes(
                    ": <ignorethis>kaeo-compaction-needed: Upstream reported context overflow. Compacting conversation...</ignorethis>\n\n");
                await resp.OutputStream.WriteAsync(note, ct);
                await resp.OutputStream.FlushAsync(ct);
            }

            // Resolve the compact model exactly like the proactive path: global setting first,
            // then the per-mapping id; summarize requests must carry the upstream model name.
            ModelMapping? compactMapping = null;
            if (!string.IsNullOrWhiteSpace(_settings.CompactModelProxyName))
                compactMapping = _settings.FindModelMapping(_settings.CompactModelProxyName);
            if (compactMapping is null && mapping.ContextSummarizeModelId.HasValue)
                compactMapping = _settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
            if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                compactMapping = null;

            var (baseUrl, timeout, apiKey) = ResolveUpstream(compactMapping?.ProxyName ?? model);
            string compactModelName = (compactMapping ?? mapping).ModelName ?? model;
            int compactModelContext = (compactMapping ?? mapping).GetEffectiveContextWindow();
            int maxTokensPerChunk = (int)(compactModelContext * AutoCompactionService.ContextWindowFraction);

            Log.Information("Reactive auto-compaction triggered for model {Model} after upstream context overflow", model);

            string? compacted = await _autoCompactionService.CompactAsync(
                mapping,
                body,
                $"reactive:{model}:{body.GetHashCode():X8}",
                baseUrl,
                apiKey,
                timeout,
                maxTokensPerChunk,
                compactModelName,
                mapping.GetEffectiveContextWindow(),
                compactModelContext,
                ct);

            if (compacted is not null && streamAlreadyOpen)
            {
                byte[] done = Encoding.UTF8.GetBytes(
                    $": <ignorethis>kaeo-compaction-complete: Conversation compacted ({body.Length / 4} → {compacted.Length / 4} est. tokens). Retrying upstream...</ignorethis>\n\n");
                await resp.OutputStream.WriteAsync(done, ct);
                await resp.OutputStream.FlushAsync(ct);
            }
            else if (compacted is null)
            {
                Log.Warning("Reactive auto-compaction failed for model {Model}; surfacing upstream overflow error", model);
            }

            return compacted;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reactive auto-compaction threw for model {Model}", model);
            return null;
        }
    }

    /// <summary>
    /// Sends <paramref name="req"/> to the resolved upstream URL, enforcing the per-mapping timeout
    /// via a linked <see cref="CancellationTokenSource"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendUpstreamAsync(
        HttpRequestMessage req,
        string baseUrl,
        int timeoutSeconds,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        // Build absolute URI from base + relative path already set on req.
        // See UpstreamUriHelper for why this can't be done via HttpClient.BaseAddress
        // or naive string concatenation without risking a 404 from the upstream.
        req.RequestUri = UpstreamUriHelper.BuildRequestUri(baseUrl, req.RequestUri!.ToString());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        return await _httpClient.SendAsync(req, completionOption, cts.Token);
    }

    private static void ApplyApiKey(HttpRequestMessage request, string? apiKey)
    {
        // A mapping-level API key always wins over whatever the client sent, so stale or
        // mismatched client credentials never shadow a correctly configured upstream key.
        // When no mapping key is configured, leave the client's own Authorization header
        // (if any) untouched instead of clearing it - callers such as Visual Studio's
        // OpenAI-compatible model connections rely on their own key passing straight
        // through to the upstream for mappings that don't set ApiKey.
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static HttpClient BuildHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Single-user proxy: 8 upstream sockets per host is ample and keeps pooled native
            // socket buffers bounded. A local upstream rarely serves more than a handful of
            // parallel requests.
            MaxConnectionsPerServer = 8,
        })
        {
            // Timeout is managed per-request via a linked CancellationTokenSource
            // so that individual model mappings can have different timeouts.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        // Track in-flight requests so a superseded HttpClient (see UpdateSettings) is not disposed
        // while a request that may still be using it is running.
        Interlocked.Increment(ref _inFlightRequests);

        HttpListenerRequest req = context.Request;
        HttpListenerResponse resp = context.Response;

        string path = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        // Short correlation ID for this request. Pushed into Serilog's LogContext so every log
        // emitted while handling the request carries it, and echoed back in error responses so a
        // client-reported failure can be matched to the exact server-side request.
        string requestId = Guid.NewGuid().ToString("N")[..12];

        var log = new RequestLog
        {
            RequestId = requestId,
            Method = method,
            OllamaPath = path,
        };

        var sw = Stopwatch.StartNew();

        try
        {
            using (Serilog.Context.LogContext.PushProperty("RequestId", requestId))
            {
                await HandleCoreAsync(req, resp, log, path, method, requestId, sw, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    private async Task HandleCoreAsync(
        HttpListenerRequest req,
        HttpListenerResponse resp,
        RequestLog log,
        string path,
        string method,
        string requestId,
        Stopwatch sw,
        CancellationToken ct)
    {
        // CORS headers and OPTIONS preflight are only emitted when explicitly enabled. In a
        // backend-to-backend topology (behind a load balancer/WAF) browsers never call the proxy
        // directly, so a wildcard CORS policy is unnecessary and would let any webpage drive it.
        if (_settings.EnableCors)
        {
            resp.AddHeader("Access-Control-Allow-Origin", "*");
            resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
            resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (method == "OPTIONS")
            {
                resp.StatusCode = 204;
                resp.Close();

                if (_settings.CollectAllTraffic)
                {
                    log.Status = RequestStatus.Success;
                    log.StatusCode = 204;
                    sw.Stop();
                    log.DurationMs = sw.Elapsed.TotalMilliseconds;
                    _stats.AddLog(log);
                }
                return;
            }
        }

        // Load balancer / uptime health checks commonly probe "/" with GET or HEAD. Answer
        // directly without logging. HEAD must never write body bytes — HttpListener treats the
        // response as having a 0-byte entity body for HEAD requests, and writing anything to the
        // output stream (even via WriteJsonAsync's normal JSON payload) throws
        // ProtocolViolationException ("Bytes to be written to the stream exceed the Content-Length
        // bytes size specified").
        if (path == "/" && (method == "GET" || method == "HEAD"))
        {
            resp.ContentType = "text/plain";
            if (method == "HEAD")
            {
                resp.ContentLength64 = 0;
                resp.Close();
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes("OK");
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, ct);
                resp.Close();
            }

            if (_settings.CollectAllTraffic)
            {
                log.Status = RequestStatus.Success;
                log.StatusCode = 200;
                sw.Stop();
                log.DurationMs = sw.Elapsed.TotalMilliseconds;
                _stats.AddLog(log);
            }
            return;
        }

        // Static version probe answered without logging — infrastructure noise that would inflate
        // the request log on every client connection.
        if (method == "GET" && path == "/api/version")
        {
            await WriteJsonAsync(resp, new { version = "0.1.0" }, ct);

            if (_settings.CollectAllTraffic)
            {
                log.Status = RequestStatus.Success;
                log.StatusCode = 200;
                sw.Stop();
                log.DurationMs = sw.Elapsed.TotalMilliseconds;
                _stats.AddLog(log);
            }
            return;
        }

        // Scalar API explorer — served only when explicitly enabled in settings.
        if (_settings.EnableApiExplorer && method == "GET")
        {
            if (path is "/scalar" or "/scalar/")
            {
                await WriteHtmlAsync(resp, await BuildApiExplorerHtmlAsync(ct).ConfigureAwait(false), ct);

                if (_settings.CollectAllTraffic)
                {
                    log.Status = RequestStatus.Success;
                    log.StatusCode = 200;
                    sw.Stop();
                    log.DurationMs = sw.Elapsed.TotalMilliseconds;
                    _stats.AddLog(log);
                }
                return;
            }

            if (path == "/openapi/v1/openapi.json")
            {
                await WriteJsonRawAsync(resp, OpenApiSpec, ct);

                if (_settings.CollectAllTraffic)
                {
                    log.Status = RequestStatus.Success;
                    log.StatusCode = 200;
                    sw.Stop();
                    log.DurationMs = sw.Elapsed.TotalMilliseconds;
                    _stats.AddLog(log);
                }
                return;
            }
        }

        bool exceptionLogged = false;
        try
        {
            if (method == "GET" && path == "/api/tags")
            {
                log.UpstreamPath = "/v1/models";
                await HandleTagsAsync(resp, log, ct);
            }
            else if (method == "GET" && path == "/api/ps")
            {
                await HandlePsAsync(resp, log, ct);
            }
            else if (method == "POST" && path == "/api/show")
            {
                log.UpstreamPath = "(local mapping — no upstream call)";
                await HandleShowAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path == "/api/generate")
            {
                log.UpstreamPath = "/v1/completions";
                await HandleGenerateAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path == "/api/chat")
            {
                log.UpstreamPath = "/v1/chat/completions";
                await HandleChatAsync(req, resp, log, ct);
            }
            else if (method == "POST" && (path == "/api/embeddings" || path == "/api/embed"))
            {
                log.UpstreamPath = "/v1/embeddings";
                await HandleEmbeddingsAsync(req, resp, log, ct);
            }
            else if (path is "/api/pull" or "/api/push" or "/api/create" or "/api/copy" or "/api/delete")
            {
                log.Status = RequestStatus.Error;
                resp.StatusCode = 501;
                await WriteJsonAsync(resp,
                    new { error = $"'{path}' is not supported. llama.cpp has no model-management API." }, ct);
            }
            else if (method == "GET" && path == "/v1/models")
            {
                log.UpstreamPath = "(local mapping — no upstream call)";
                await HandleV1ModelsAsync(resp, log, ct);
            }
            else if (method == "GET" && path.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "(local mapping — no upstream call)";
                await HandleV1ModelAsync(path, resp, log, ct);
            }
            else if (method == "POST" && path.Equals("/v1/responses/compact", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "/v1/responses/compact";
                await HandleCompactAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path.Equals("/v1/chat/completions/compact", StringComparison.OrdinalIgnoreCase))
            {
                if (!_settings.EnableManualCompactionEndpoint)
                {
                    resp.StatusCode = 404;
                    await WriteJsonAsync(resp, new { error = "Manual compaction endpoint is disabled. Enable EnableManualCompactionEndpoint in settings." }, ct);
                    return;
                }
                log.UpstreamPath = "/v1/chat/completions/compact";
                await HandleManualCompactAsync(req, resp, log, ct);
            }
            else if (path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                  || path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            {
                // Transparent passthrough — forward OpenAI-native requests (e.g. from VS Copilot,
                // OpenAI SDKs) directly to the upstream llama.cpp /v1/* surface unchanged.
                log.UpstreamPath = path;
                await PassthroughAsync(req, resp, log, ct);
            }
            else
            {
                resp.StatusCode = 404;
                await WriteJsonAsync(resp, new { error = $"Unknown endpoint: {path}" }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            log.Status = RequestStatus.Cancelled;
            try { resp.StatusCode = 499; resp.Close(); } catch { }
        }
        catch (RequestBodyTooLargeException ex)
        {
            // Oversized request body — reject before buffering to protect against memory exhaustion.
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;
            Log.Warning("Rejected oversized request body on {Path}: {Message}", path, ex.Message);

            try
            {
                resp.StatusCode = 413;
                await WriteJsonAsync(resp, new { error = "Request body too large.", requestId }, ct);
            }
            catch { }

            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        catch (Exception ex)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;

            // Log the full exception detail server-side only. The client receives a generic
            // message so internal details (paths, hostnames, connection strings) are never leaked.
            Log.Error(ex, "Unhandled error processing {Method} {Path}", method, path);

            // Persist the full exception detail (stack trace, inner exceptions) separately.
            _stats.AddLog(log, ex);
            exceptionLogged = true;

            try
            {
                resp.StatusCode = 500;
                await WriteJsonAsync(resp, new { error = "Internal proxy error.", requestId }, ct);
            }
            catch { }

            // Skip the finally AddLog — we already logged above with the exception.
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        finally
        {
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            if (!exceptionLogged)
                _stats.AddLog(log);
        }
    }

    /// <summary>
    /// Returns true for request headers that must NOT be forwarded to the upstream. This includes
    /// hop-by-hop headers managed by <see cref="HttpClient"/> itself, and proxy/forwarding headers
    /// (X-Forwarded-*, X-Real-IP, Forwarded) that a client reaching the proxy directly could spoof
    /// to impersonate a trusted edge. Because the proxy sits behind a load balancer/WAF, any such
    /// header arriving from a client is untrusted and is dropped rather than relayed.
    /// </summary>
    private static bool ShouldSkipRequestHeader(string name)
    {
        return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);
    }

    // ── /v1/* → transparent passthrough ────────────────────────────────────

    /// <summary>
    /// Forwards any OpenAI-native /v1/* request verbatim to the upstream llama.cpp server
    /// and streams the response back. Handles both streaming (SSE) and non-streaming responses.
    /// For POST requests the "model" field in the JSON body is rewritten through the mapping
    /// table so that clients sending e.g. "gpt-4o" are transparently mapped to the loaded model.
    /// </summary>
    private async Task PassthroughAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        bool contextCompacted = false;
        bool headersPreCommitted = false;
        using var upstreamReq = new HttpRequestMessage
        {
            Method = new HttpMethod(req.HttpMethod),
            // RequestUri is set to relative path; SendUpstreamAsync will make it absolute.
            RequestUri = new Uri(req.Url!.PathAndQuery, UriKind.Relative),
        };

        // Copy request headers, skipping hop-by-hop headers the HttpClient manages itself and
        // proxy/forwarding headers that a direct-access client could spoof (see ShouldSkipRequestHeader).
        foreach (string? name in req.Headers.AllKeys)
        {
            if (name is null) continue;
            if (ShouldSkipRequestHeader(name))
                continue;

            // Authorization is copied here so a client's own bearer token (e.g. Visual
            // Studio's OpenAI-compatible model connection) reaches the upstream for
            // mappings without their own configured credential. ApplyApiKey below overrides
            // this with the mapping's resolved credential secret when one is configured.

            string value = req.Headers[name] ?? string.Empty;
            if (!upstreamReq.Headers.TryAddWithoutValidation(name, value))
                upstreamReq.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        // Track which original model was requested so we can resolve the upstream URL.
        string originalModel = string.Empty;
        bool isCompletionPath = IsChatCompletionsPath(req.Url?.AbsolutePath)
            || req.Url?.AbsolutePath.Equals("/v1/completions", StringComparison.OrdinalIgnoreCase) == true;
        bool isStreamingRequest = false;
        // Captured upstream-bound JSON body so an overflow rejection can be compacted and retried.
        string? passthroughBody = null;
        string? consumedErrorBody = null;

        if (req.HasEntityBody)
        {
            bool isJsonPost = req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                           && (req.ContentType?.StartsWith("application/json",
                               StringComparison.OrdinalIgnoreCase) ?? false);

            if (isJsonPost)
            {
                string bodyText = await ReadBodyAsync(req, ct);
                log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);
                string rewritten = NormalizeRequestBody(
                    bodyText, _settings, log, modelName => ShouldApplyThinkingCompatibility(_settings, modelName));
                originalModel = log.Model; // set by NormalizeRequestBody
                // Capture both the client's original body and the upstream-bound (rewritten)
                // body so proxy-injected values such as reasoning_effort can be compared
                // side-by-side in the request log. Debug mode captures bodies independently of
                // the Collect flags, mirroring the /api/chat path.
                if (_settings.CollectRequestDetails || _settings.DebugMode)
                {
                    log.RequestBody = RedactRequestBodyForLog(_settings, bodyText, originalModel);
                    log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, rewritten, originalModel);
                }
                isStreamingRequest = isCompletionPath && IsStreamingJsonBody(bodyText);
                log.Streaming = isStreamingRequest;

                // Proactive context-overflow check for OpenAI-native passthrough requests.
                // Skip proactive auto-compaction for recognized Copilot requests when
                // EnableCopilotNativeCompaction is enabled, so Copilot's native /compact
                // flow manages session state.
                bool shouldSkipAutoCompaction = false;
                if (_settings.EnableCopilotNativeCompaction)
                {
                    string? firstMsgForDetection = null;
                    try
                    {
                        using JsonDocument _tmpDoc = JsonDocument.Parse(rewritten);
                        firstMsgForDetection = GetFirstMessageContent(_tmpDoc.RootElement);
                    }
                    catch { }

                    if (IsCopilotRequest(req, firstMsgForDetection))
                    {
                        shouldSkipAutoCompaction = true;
                        Log.Debug("Skipping proactive auto-compaction for Copilot request (OpenAI passthrough)");
                    }
                }

                if (!shouldSkipAutoCompaction && _settings.EnableAutoCompaction)
                {
                    // For streaming requests, pre-commit SSE headers before compaction so we can write progress comments
                    Stream? compactionOutputStream = null;
                    if (isStreamingRequest)
                    {
                        resp.StatusCode = 200;
                        resp.ContentType = "text/event-stream";
                        resp.SendChunked = true;
                        resp.KeepAlive = true;
                        headersPreCommitted = true;
                        compactionOutputStream = resp.OutputStream;
                    }

                    (bool overflow, string? compacted) = await TryProactiveOverflowAsync(_settings.FindModelMapping(originalModel), rewritten, originalModel, resp, log, AutoCompactPaths.OpenAI, compactionOutputStream, ct);
                    if (overflow)
                        return;
                    if (compacted is not null)
                    {
                        rewritten = compacted;
                        contextCompacted = true;
                    }
                }

                byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                passthroughBody = rewritten;
                upstreamReq.Content = new ByteArrayContent(bodyBytes);
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            }
            else
            {
                upstreamReq.Content = new StreamContent(req.InputStream);
                if (!string.IsNullOrEmpty(req.ContentType))
                    upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
            }
        }

        var (baseUrl, timeout, apiKey) = ResolveUpstream(originalModel);
        ApplyApiKey(upstreamReq, apiKey);

        // Append upstream routing info to the debug summary so it's visible which server
        // the request is actually being sent to, including after compact redirects.
        if (_settings.DebugMode && log.DebugSummary is not null)
        {
            ModelMapping? resolvedMapping = _settings.FindModelMapping(originalModel);
            string mappingName = resolvedMapping?.ProxyName ?? originalModel;
            log.DebugSummary += "\n" + DebugNotes.UpstreamRouting(
                mappingName, baseUrl, !string.IsNullOrWhiteSpace(apiKey), timeout);
        }

        // For streaming requests, pre-commit SSE headers to the client immediately and pump
        // heartbeat comments while waiting for the upstream to send its first response header.
        // llama.cpp does not send any HTTP headers until the first token is ready, so clients
        // with a short NetworkTimeout (e.g. the OpenAI .NET SDK default of 100 s) would
        // otherwise time out silently during long prompt-processing / thinking phases.
        using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task preResponseHeartbeatTask = Task.CompletedTask;

        if (isStreamingRequest && !headersPreCommitted && ShouldEmitHeartbeats(originalModel))
        {
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream";
            resp.SendChunked = true;
            resp.KeepAlive = true;
            headersPreCommitted = true;

            // Flush a single comment frame so the HTTP headers are actually sent on the wire.
            byte[] initial = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
            await resp.OutputStream.WriteAsync(initial, ct);
            await resp.OutputStream.FlushAsync(ct);

            preResponseHeartbeatTask = PumpPreResponseHeartbeatsAsync(
                resp.OutputStream,
                _settings.StreamingHeartbeatIntervalSeconds,
                preResponseCts.Token);
        }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await SendUpstreamAsync(
                upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        finally
        {
            // Stop the pre-response heartbeat pump as soon as upstream headers arrive.
            await preResponseCts.CancelAsync();
            await preResponseHeartbeatTask;
        }

        // Reactive compaction: llama.cpp rejects prompts that exceed the loaded model's
        // context with a 400 "exceed_context_size_error". When that happens and the request
        // was not already compacted, run the chunked map-reduce summarizer locally and retry
        // the upstream call once. This covers mappings where the proactive threshold is not
        // configured as well as cases where the proxy's token estimate undershot.
        if (!upstreamResp.IsSuccessStatusCode
            && passthroughBody is not null
            && !contextCompacted
            && _settings.EnableAutoCompaction)
        {
            string probe = await upstreamResp.Content.ReadAsStringAsync(ct);
            if (IsContextOverflowBody(probe))
            {
                string? reactive = await TryReactiveCompactionAsync(
                    passthroughBody, originalModel, resp, headersPreCommitted, ct);
                if (reactive is not null)
                {
                    contextCompacted = true;
                    passthroughBody = reactive;
                    upstreamResp.Dispose();

                    using var retryReq = new HttpRequestMessage(HttpMethod.Post, req.Url!.PathAndQuery)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(reactive)),
                    };
                    retryReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                    ApplyApiKey(retryReq, apiKey);

                    upstreamResp = await SendUpstreamAsync(
                        retryReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                else
                {
                    consumedErrorBody = probe;
                }
            }
            else
            {
                consumedErrorBody = probe;
            }
        }

        // Ensure the response (and its pooled connection) is released on every exit path,
        // including the early error return and any exception thrown mid-stream.
        using HttpResponseMessage ownedUpstreamResponse = upstreamResp;

        log.StatusCode = (int)upstreamResp.StatusCode;

        // Only set status/headers if we haven't already pre-committed them to the client.
        if (!headersPreCommitted)
        {
            resp.StatusCode = (int)upstreamResp.StatusCode;

            // Copy response headers
            foreach (var header in upstreamResp.Headers)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                resp.Headers[header.Key] = string.Join(",", header.Value);
            }
            foreach (var header in upstreamResp.Content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                resp.Headers[header.Key] = string.Join(",", header.Value);
            }

            resp.SendChunked = true;
            resp.KeepAlive = true;
        }

        if (!upstreamResp.IsSuccessStatusCode)
        {
            // Read error body so it can be logged before forwarding. If the reactive
            // compaction probe already consumed it, reuse the captured text.
            string errorBody = consumedErrorBody ?? await upstreamResp.Content.ReadAsStringAsync(ct);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
            if (_settings.CollectResponseDetails && log.ResponseBody is null)
                log.ResponseBody = errorBody;
            // Debug mode captures the raw upstream error body independently of the Collect
            // flags, mirroring the /api/chat path.
            if (_settings.DebugMode)
                log.UpstreamResponseBody = RedactResponseBodyForLog(errorBody, originalModel);

            // Detect context overflow so we can rewrite the status to 413 for clients
            // (e.g. Copilot) that trigger compaction on that code.
            int clientStatusCode = IsContextOverflowBody(errorBody) ? 413 : (int)upstreamResp.StatusCode;

            if (headersPreCommitted)
            {
                // Headers already sent as 200/SSE — emit the error as a data frame so the
                // client sees it rather than getting a silent stream close.
                string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(errorBody)},\"code\":{clientStatusCode}}}}}\n\n";
                byte[] errorFrameBytes = Encoding.UTF8.GetBytes(errorFrame);
                await resp.OutputStream.WriteAsync(errorFrameBytes, ct);
            }
            else
            {
                resp.StatusCode = clientStatusCode;
                byte[] errorBytes = System.Text.Encoding.UTF8.GetBytes(errorBody);
                await resp.OutputStream.WriteAsync(errorBytes, ct);
            }
            resp.OutputStream.Close();
            return;
        }

        await using Stream upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);

        bool isServerSentEvents = IsServerSentEventsResponse(upstreamResp);

        using CountingStream countingStream = new(resp.OutputStream);
        bool shouldMirrorReasoningContent = IsChatCompletionsPath(req.Url?.AbsolutePath);

        // Per-model thinking extraction (e.g. Qwen Cloud's older inline <think> format) is only
        // meaningful on the OpenAI chat-completions surface where reasoning_content is understood.
        ModelMapping? activeMapping = _settings.FindModelMapping(originalModel);
        ThinkingMode thinkingMode = shouldMirrorReasoningContent && activeMapping is not null
            ? activeMapping.ThinkingMode
            : ThinkingMode.LeaveInline;

        // Capture the terminal usage chunk (prompt/completion/cached/reasoning tokens + draft timings)
        // on every passthrough path without buffering the forwarded body.
        Action<LlamaCppStreamChunk> onUsage = chunk => FillTokenStats(log, chunk);

        bool collectResponse = _settings.CollectResponseDetails;
        // Debug mode captures the raw upstream response (the "before" of any transformation)
        // independently of the Collect flags, mirroring the /api/chat path.
        bool debugCapture = _settings.DebugMode;

        if (isServerSentEvents)
        {
            if (shouldMirrorReasoningContent)
            {
                // The chat-completions stream is rewritten (thinking mode, XML tool calls), so
                // capture the raw upstream frames separately: ResponseBody keeps stripped/moved
                // thinking reviewable and UpstreamResponseBody records the DebugMode "before".
                using ResponseCaptureStream? rawCapture = collectResponse || debugCapture
                    ? new ResponseCaptureStream(Stream.Null)
                    : null;
                await CopyOpenAiChatCompletionSseStreamAsync(
                    upstreamStream,
                    countingStream,
                    thinkingMode,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel),
                    onUsage,
                    rawCapture);

                if (rawCapture is not null)
                {
                    string rawText = rawCapture.GetCapturedText();
                    if (collectResponse)
                        log.ResponseBody = RedactResponseBodyForLog(rawText, originalModel);
                    if (debugCapture)
                        log.UpstreamResponseBody = RedactResponseBodyForLog(rawText, originalModel);
                }
            }
            else if (collectResponse || debugCapture)
            {
                // No rewriting happens on this path; capture what is forwarded as-is.
                using ResponseCaptureStream captureStream = new(countingStream);
                await CopyStreamWithSseHeartbeatsAsync(
                    upstreamStream,
                    captureStream,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel),
                    onUsage);

                string forwardedText = captureStream.GetCapturedText();
                if (collectResponse)
                    log.ResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
                if (debugCapture)
                    log.UpstreamResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
            }
            else
            {
                await CopyStreamWithSseHeartbeatsAsync(
                    upstreamStream,
                    countingStream,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel),
                    onUsage);
            }
        }
        else
        {
            // Non-streaming: buffer the body once so usage stats and the optional captures all
            // read from the raw upstream body while the transformed body streams to the client.
            Action<string>? onBody = null;
            if (isCompletionPath)
                onBody += body => FillTokenStats(log, TryParseChunk(body));
            if (collectResponse)
                onBody += body => log.ResponseBody = RedactResponseBodyForLog(body, originalModel);
            if (debugCapture)
                onBody += body => log.UpstreamResponseBody = RedactResponseBodyForLog(body, originalModel);

            await CopyNonStreamingChatResponseAsync(
                upstreamStream,
                countingStream,
                thinkingMode,
                ct,
                onBody,
                extractToolCalls: shouldMirrorReasoningContent);
        }

        log.ResponseBytes = countingStream.BytesWritten;

        resp.OutputStream.Close();

        log.Status = RequestStatus.Success;
    }

    private static bool IsServerSentEventsResponse(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return response.Headers.TryGetValues("X-Accel-Buffering", out IEnumerable<string>? values)
            && values.Any(value => value.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsChatCompletionsPath(string? path) =>
        path?.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Copies a non-streaming (complete JSON) chat-completion response, transforming
    /// <c>&lt;think&gt;</c> blocks according to the model's <see cref="ThinkingMode"/>: moved into
    /// <c>reasoning_content</c> (<see cref="ThinkingMode.MoveToReasoningContent"/>), dropped
    /// entirely (<see cref="ThinkingMode.StripFromOutput"/>), or left unchanged
    /// (<see cref="ThinkingMode.LeaveInline"/>). When <paramref name="extractToolCalls"/> is
    /// set (chat-completions passthrough), inline XML tool-call blocks are additionally
    /// converted into structured OpenAI <c>tool_calls</c>, mirroring what the streaming
    /// <see cref="OpenAiSseRewriter"/> and the <c>/api/chat</c> path already do. The
    /// <paramref name="onBody"/> callback always receives the unmodified upstream body so
    /// captured logs retain any stripped thinking.
    /// </summary>
    private static async Task CopyNonStreamingChatResponseAsync(
        Stream source,
        Stream destination,
        ThinkingMode thinkingMode,
        CancellationToken ct,
        Action<string>? onBody = null,
        bool extractToolCalls = false)
    {
        if (thinkingMode == ThinkingMode.LeaveInline && onBody is null && !extractToolCalls)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }

        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string body = await reader.ReadToEndAsync(ct);
        onBody?.Invoke(body);
        string outgoing = thinkingMode == ThinkingMode.LeaveInline && !extractToolCalls
            ? body
            : TransformNonStreamingChatBody(body, thinkingMode, extractToolCalls);
        byte[] bytes = Encoding.UTF8.GetBytes(outgoing);
        await destination.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Transforms a complete (non-streaming) OpenAI chat-completion JSON body by extracting
    /// <c>&lt;think&gt;...&lt;/think&gt;</c> blocks from each choice's <c>message.content</c>.
    /// In <see cref="ThinkingMode.MoveToReasoningContent"/> the extracted text is re-emitted as
    /// <c>message.reasoning_content</c>; in <see cref="ThinkingMode.StripFromOutput"/> it is
    /// discarded along with any native <c>reasoning_content</c>. When
    /// <paramref name="extractToolCalls"/> is set, inline XML tool-call blocks left in the
    /// content are converted into structured OpenAI <c>message.tool_calls</c> (skipping choices
    /// the upstream already answered with structured tool calls) and <c>finish_reason</c> is
    /// forced to <c>"tool_calls"</c> — the non-streaming parity of the streaming
    /// <see cref="OpenAiSseRewriter"/>. Returns the original text unchanged if parsing fails.
    /// </summary>
    internal static string TransformNonStreamingChatBody(string json, ThinkingMode thinkingMode, bool extractToolCalls = false)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(json) as JsonObject;
            if (root is null || root["choices"] is not JsonArray choices)
                return json;

            bool strip = thinkingMode == ThinkingMode.StripFromOutput;
            bool extractThinking = thinkingMode != ThinkingMode.LeaveInline;
            (string openTag, string closeTag) = ThinkTagExtractor.TagsFor(thinkingMode);

            foreach (JsonNode? choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice) continue;
                if (choice["message"] is not JsonObject message) continue;

                if (strip)
                    message.Remove("reasoning_content");

                string content = message["content"] is JsonValue cv
                    && cv.TryGetValue(out string? contentStr) ? contentStr ?? string.Empty : string.Empty;

                if (extractThinking && !string.IsNullOrEmpty(content))
                {
                    (string reasoning, string answer) = ThinkTagExtractor.ExtractAll(content, openTag, closeTag);
                    if (!strip && reasoning.Length > 0)
                    {
                        string existing = message["reasoning_content"] is JsonValue erv
                            && erv.TryGetValue(out string? ervStr) ? ervStr ?? string.Empty : string.Empty;
                        message["reasoning_content"] = JsonValue.Create(existing + reasoning);
                    }

                    content = answer;
                    message["content"] = JsonValue.Create(answer);
                }

                // XML tool-call extraction — only for choices the upstream did not already
                // answer with structured tool_calls (same guard as the /api/chat path).
                if (extractToolCalls
                    && !string.IsNullOrEmpty(content)
                    && message["tool_calls"] is not JsonArray { Count: > 0 })
                {
                    ToolCallExtraction extraction = ExtractXmlToolCalls(content);
                    if (extraction.ToolCalls is { Count: > 0 })
                    {
                        message["tool_calls"] = BuildOpenAiToolCallsArray(extraction.ToolCalls);
                        message["content"] = JsonValue.Create(extraction.Content ?? string.Empty);
                        choice["finish_reason"] = "tool_calls";
                    }
                }
            }

            return root.ToJsonString(_jsonOptions);
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    /// Converts tool calls parsed from an inline XML block into the OpenAI wire shape
    /// (<c>id</c> + <c>type: "function"</c> + serialized <c>arguments</c>), mirroring the
    /// frames the streaming <see cref="OpenAiSseRewriter"/> synthesises.
    /// </summary>
    private static JsonArray BuildOpenAiToolCallsArray(List<OllamaToolCall> toolCalls)
    {
        JsonArray array = [];
        foreach (OllamaToolCall toolCall in toolCalls)
        {
            string argumentsJson = toolCall.Function?.Arguments is null
                ? "{}"
                : JsonSerializer.Serialize(toolCall.Function.Arguments, _jsonOptions);

            array.Add(new JsonObject
            {
                ["id"] = "call_" + Guid.NewGuid().ToString("N")[..16],
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolCall.Function?.Name ?? string.Empty,
                    ["arguments"] = argumentsJson,
                },
            });
        }

        return array;
    }

    /// <summary>
    /// Returns true when a JSON POST body has <c>"stream": true</c>, indicating the client
    /// expects an SSE response and we should pre-commit headers before the upstream responds.
    /// Uses a lightweight regex scan instead of parsing the full JSON DOM.
    /// </summary>
    private static bool IsStreamingJsonBody(string json)
    {
        try
        {
            // Match "stream": true with optional whitespace, case-insensitive
            return Regex.IsMatch(json, "\"stream\"\\s*:\\s*true", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pumps SSE comment heartbeat frames into <paramref name="output"/> at
    /// <paramref name="intervalSeconds"/> intervals until <paramref name="ct"/> is cancelled.
    /// Used to keep the client connection alive while waiting for the upstream to send its
    /// first response header (i.e. before the first token is generated).
    /// </summary>
    private static async Task PumpPreResponseHeartbeatsAsync(
        Stream output,
        int intervalSeconds,
        CancellationToken ct)
    {
        byte[] heartbeat = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 5, 300));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await output.WriteAsync(heartbeat, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on cancel */ }
    }

    private static async Task CopyStreamWithSseHeartbeatsAsync(
        Stream source,
        Stream destination,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        CancellationToken ct,
        Action? onHeartbeatSent = null,
        Action<LlamaCppStreamChunk>? onUsage = null)
    {
        byte[] buffer = new byte[81920];
        byte[] heartbeatBytes = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));
        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        while (!ct.IsCancellationRequested)
        {
            ValueTask<int> readValueTask = source.ReadAsync(buffer, ct);
            Task<int> readTask = readValueTask.AsTask();

            while (enableHeartbeats && !readTask.IsCompleted)
            {
                Task delayTask = Task.Delay(heartbeatInterval, ct);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == readTask)
                    break;

                await destination.WriteAsync(heartbeatBytes, ct);
                await destination.FlushAsync(ct);
                onHeartbeatSent?.Invoke();
            }

            int bytesRead = await readTask;
            if (bytesRead == 0)
                break;

            usageSniffer?.Feed(Encoding.UTF8.GetString(buffer, 0, bytesRead));

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await destination.FlushAsync(ct);
        }

        usageSniffer?.Flush();
    }

    private static async Task CopyOpenAiChatCompletionSseStreamAsync(
        Stream source,
        Stream destination,
        ThinkingMode thinkingMode,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        CancellationToken ct,
        Action? onHeartbeatSent = null,
        Action<LlamaCppStreamChunk>? onUsage = null,
        Stream? rawCapture = null)
    {
        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        byte[] heartbeatBytes = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));
        OpenAiSseRewriter rewriter = new(thinkingMode);
        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        while (!ct.IsCancellationRequested)
        {
            Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

            while (enableHeartbeats && !readTask.IsCompleted)
            {
                Task delayTask = Task.Delay(heartbeatInterval, ct);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == readTask)
                    break;

                await destination.WriteAsync(heartbeatBytes, ct);
                await destination.FlushAsync(ct);
                onHeartbeatSent?.Invoke();
            }

            string? line = await readTask;
            if (line is null)
                break;

            usageSniffer?.Feed(line + "\n");
            if (rawCapture is not null)
                await rawCapture.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

            // Upstream framing uses "data: {...}\n\n". ReadLineAsync strips the line
            // terminator and surfaces the blank terminator line as an empty string.
            // Because a single inbound "data:" line may expand into multiple outbound
            // "data:" frames (original + synthesised tool_call deltas), we must emit
            // each outbound line as its OWN complete SSE event ("\n\n") rather than
            // relying on the upstream blank line — otherwise SSE parsers will join
            // consecutive data: lines into one event payload and JSON parsing fails.
            if (line.Length == 0)
                continue;

            foreach (string outgoingLine in rewriter.Process(line))
            {
                byte[] lineBytes = Encoding.UTF8.GetBytes(outgoingLine + "\n\n");
                await destination.WriteAsync(lineBytes, ct);
            }
            await destination.FlushAsync(ct);
        }

        usageSniffer?.Flush();
    }

    /// <summary>
    /// Incrementally separates <c>&lt;think&gt;...&lt;/think&gt;</c> reasoning blocks from normal
    /// answer text as an upstream stream arrives. Providers such as Qwen Cloud (older response
    /// format) emit reasoning inline inside the <c>content</c> field; this extractor splits it out
    /// so callers can re-emit the reasoning as <c>reasoning_content</c>.
    ///
    /// The extractor is stateful across calls because a <c>&lt;think&gt;</c> / <c>&lt;/think&gt;</c>
    /// tag may be split across SSE chunks. Any trailing text that could be the prefix of a tag is
    /// held in <see cref="_pending"/> until the next call (or <see cref="Flush"/> at end of stream)
    /// so a partial tag is never emitted as literal content.
    /// </summary>
    private sealed class ThinkTagExtractor
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";

        private readonly string _openTag;
        private readonly string _closeTag;

        private bool _inThink;
        private string _pending = string.Empty;

        // Qwen thinking compatibility markers: the model emits a literal [Thinking] marker, the
        // reasoning, then a literal [Answer] marker and the final answer.
        public const string QwenOpenTag = "[Thinking]";
        public const string QwenCloseTag = "[Answer]";

        public ThinkTagExtractor(string openTag = OpenTag, string closeTag = CloseTag)
        {
            _openTag = openTag;
            _closeTag = closeTag;
        }

        /// <summary>
        /// Returns the open/close tag pair for a given <see cref="ThinkingMode"/>: the Qwen
        /// <c>[Thinking]</c>/<c>[Answer]</c> markers for <see cref="ThinkingMode.QwenThinkingCompatible"/>,
        /// or the default <c>&lt;think&gt;</c>/<c>&lt;/think&gt;</c> blocks for every other mode.
        /// </summary>
        public static (string OpenTag, string CloseTag) TagsFor(ThinkingMode mode)
            => mode == ThinkingMode.QwenThinkingCompatible
                ? (QwenOpenTag, QwenCloseTag)
                : (OpenTag, CloseTag);

        /// <summary>
        /// Feeds the next incremental fragment of <c>content</c> text. Returns the separated
        /// reasoning and answer text that is safe to emit now (either may be empty). Call
        /// <see cref="Flush"/> once at end of stream to drain any buffered trailing text.
        /// </summary>
        public (string Reasoning, string Content) Process(string fragment)
        {
            if (fragment.Length == 0)
                return (string.Empty, string.Empty);

            string work = _pending + fragment;
            _pending = string.Empty;

            var reasoning = new StringBuilder();
            var content = new StringBuilder();

            int pos = 0;
            while (pos < work.Length)
            {
                string activeTag = _inThink ? _closeTag : _openTag;
                int tagIndex = work.IndexOf(activeTag, pos, StringComparison.Ordinal);

                if (tagIndex < 0)
                {
                    // No complete tag found. Hold back a possible partial tag at the tail so it
                    // can be completed by the next fragment instead of being emitted literally.
                    int hold = PartialTagSuffixLength(work, pos, activeTag);
                    int emitEnd = work.Length - hold;

                    if (emitEnd > pos)
                        Append(_inThink ? reasoning : content, work[pos..emitEnd]);

                    if (hold > 0)
                        _pending = work[emitEnd..];

                    pos = work.Length;
                    break;
                }

                // Emit text before the tag, then flip state and continue after the tag.
                if (tagIndex > pos)
                    Append(_inThink ? reasoning : content, work[pos..tagIndex]);

                _inThink = !_inThink;
                pos = tagIndex + activeTag.Length;
            }

            return (reasoning.ToString(), content.ToString());
        }

        /// <summary>
        /// Drains any buffered trailing text at end of stream. If the stream ended while still
        /// inside an unterminated <c>&lt;think&gt;</c> block, the remainder is treated as reasoning;
        /// otherwise it is treated as answer content.
        /// </summary>
        public (string Reasoning, string Content) Flush()
        {
            string remaining = _pending;
            _pending = string.Empty;

            if (remaining.Length == 0)
                return (string.Empty, string.Empty);

            return _inThink ? (remaining, string.Empty) : (string.Empty, remaining);
        }

        /// <summary>
        /// One-shot extraction for a complete (non-streaming) <c>content</c> string. Returns the
        /// separated reasoning and answer text with all <c>&lt;think&gt;...&lt;/think&gt;</c> blocks
        /// removed from the answer.
        /// </summary>
        public static (string Reasoning, string Content) ExtractAll(string content)
            => ExtractAll(content, OpenTag, CloseTag);

        public static (string Reasoning, string Content) ExtractAll(string content, string openTag, string closeTag)
        {
            if (string.IsNullOrEmpty(content))
                return (string.Empty, string.Empty);

            ThinkTagExtractor extractor = new(openTag, closeTag);
            (string reasoning, string answer) = extractor.Process(content);
            (string tailReasoning, string tailAnswer) = extractor.Flush();
            return (reasoning + tailReasoning, answer + tailAnswer);
        }

        private static void Append(StringBuilder target, string text)
        {
            if (text.Length > 0)
                target.Append(text);
        }

        /// <summary>
        /// Returns the number of trailing characters (from <paramref name="start"/> onward) that
        /// form a proper prefix of <paramref name="tag"/>. This is the amount to buffer rather than
        /// emit, so a tag split across fragments is still recognised on the next call.
        /// </summary>
        private static int PartialTagSuffixLength(string work, int start, string tag)
        {
            int max = Math.Min(tag.Length - 1, work.Length - start);
            for (int len = max; len > 0; len--)
            {
                if (string.CompareOrdinal(work, work.Length - len, tag, 0, len) == 0)
                    return len;
            }

            return 0;
        }
    }

    /// <summary>
    /// Incrementally scans a forwarded SSE byte/text stream for the terminal
    /// <c>usage</c> chunk without buffering the whole body. Only lines that start with
    /// <c>data:</c> and contain the literal <c>"usage"</c> are JSON-parsed; everything else
    /// passes through untouched. A trailing partial line is held until the next
    /// <see cref="Feed"/> call or <see cref="Flush"/> at end of stream.
    /// </summary>
    private sealed class SseUsageSniffer(Action<LlamaCppStreamChunk> onUsage)
    {
        private readonly StringBuilder _pending = new();

        public void Feed(string text)
        {
            if (text.Length == 0)
                return;

            _pending.Append(text);

            while (true)
            {
                string buffered = _pending.ToString();
                int newline = buffered.IndexOf('\n');
                if (newline < 0)
                    break;

                _pending.Remove(0, newline + 1);
                ProcessLine(buffered[..newline]);
            }
        }

        public void Flush()
        {
            if (_pending.Length > 0)
                ProcessLine(_pending.ToString());

            _pending.Clear();
        }

        private void ProcessLine(string line)
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
                return;

            string data = trimmed["data:".Length..].Trim();
            if (data.Length == 0 || !data.Contains("\"usage\"", StringComparison.Ordinal))
                return;

            try
            {
                LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(data, _jsonOptions);
                if (chunk?.Usage is not null)
                    onUsage(chunk);
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "Skipping unparseable SSE data frame while sniffing usage");
            }
        }
    }

    /// <summary>
    /// Rewrites an OpenAI-compatible SSE chat-completion stream on the fly:
    ///   • mirrors <c>reasoning_content</c> into <c>content</c> when <c>content</c> is empty,
    ///   • detects inline XML tool-call blocks (<c>&lt;tool_call&gt;&lt;function=NAME&gt;&lt;parameter=K&gt;V&lt;/parameter&gt;…&lt;/function&gt;&lt;/tool_call&gt;</c>)
    ///     emitted by some llama.cpp templates and converts them into proper OpenAI streaming
    ///     <c>tool_calls</c> deltas so that downstream OpenAI SDK clients (e.g. VS Copilot agent mode)
    ///     execute the tool instead of receiving raw XML text,
    ///   • forces <c>finish_reason</c> to <c>"tool_calls"</c> on the terminal chunk when tool calls were emitted.
    /// </summary>
    private sealed class OpenAiSseRewriter(ThinkingMode thinkingMode)
    {
        // Per-choice buffer of streamed delta.content prior to/within a tool_call block.
        private readonly Dictionary<int, ChoiceState> _state = [];

        // Per-choice incremental <think> tag extractor, used in every mode except LeaveInline.
        private readonly Dictionary<int, ThinkTagExtractor> _thinkExtractors = [];
        private readonly ThinkingMode _thinkingMode = thinkingMode;
        private readonly (string OpenTag, string CloseTag) _thinkTags = ThinkTagExtractor.TagsFor(thinkingMode);

        public IEnumerable<string> Process(string rawLine)
        {
            const string dataPrefix = "data:";

            if (!rawLine.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                yield return rawLine;
                yield break;
            }

            string data = rawLine[dataPrefix.Length..];
            if (data.StartsWith(' ')) data = data[1..];

            if (data.Length == 0 || data == "[DONE]")
            {
                yield return rawLine;
                yield break;
            }

            JsonObject? root;
            try { root = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException ex)
            {
                Log.Debug(ex, "Skipping unparseable SSE data frame in OpenAI stream rewriter");
                root = null;
            }

            if (root is null || root["choices"] is not JsonArray choices)
            {
                yield return rawLine;
                yield break;
            }

            // Collect extra synthesised frames (tool_call deltas) to emit AFTER the rewritten one.
            List<JsonObject> extraFrames = [];

            foreach (JsonNode? choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice) continue;

                int index = choice["index"]?.GetValue<int>() ?? 0;
                if (!_state.TryGetValue(index, out ChoiceState? cs))
                {
                    cs = new ChoiceState();
                    _state[index] = cs;
                }

                JsonObject? delta = choice["delta"] as JsonObject;

                // Handle inline <think>...</think> blocks per the configured thinking mode:
                // move them into reasoning_content (MoveToReasoningContent/ExtractThinkTags),
                // drop them entirely (StripFromOutput), or leave them untouched (LeaveInline/Off).
                if (_thinkingMode != ThinkingMode.LeaveInline && delta is not null)
                {
                    if (!_thinkExtractors.TryGetValue(index, out ThinkTagExtractor? extractor))
                    {
                        extractor = new ThinkTagExtractor(_thinkTags.OpenTag, _thinkTags.CloseTag);
                        _thinkExtractors[index] = extractor;
                    }

                    string incoming = delta["content"] is JsonValue contentValue
                        && contentValue.TryGetValue(out string? contentStr)
                            ? contentStr ?? string.Empty
                            : string.Empty;

                    (string reasoning, string content) = extractor.Process(incoming);

                    if (_thinkingMode == ThinkingMode.StripFromOutput)
                    {
                        // Thinking must not reach the client at all; drop any native
                        // reasoning_content the upstream may have sent as well.
                        delta.Remove("reasoning_content");
                    }
                    else if (reasoning.Length > 0)
                    {
                        string existingReasoning = delta["reasoning_content"] is JsonValue erv
                            && erv.TryGetValue(out string? ervStr) ? ervStr ?? string.Empty : string.Empty;
                        delta["reasoning_content"] = JsonValue.Create(existingReasoning + reasoning);
                    }

                    // Rewrite content based on what the extractor produced:
                    // - non-empty remainder → set it (will be post-processed by tool-call ingestion below)
                    // - empty remainder but incoming was present (thinking consumed it or partial-tag
                    //   buffering) → remove the key so reasoning-only / role-only deltas are clean
                    // - no incoming content at all → leave delta untouched (don't fabricate "")
                    if (content.Length > 0)
                        delta["content"] = JsonValue.Create(content);
                    else if (incoming.Length > 0)
                        delta.Remove("content");
                }

                // Mirror reasoning_content → content (when content is empty/null). Only in
                // LeaveInline mode; in Move/Strip modes we deliberately keep reasoning separate
                // from (or absent from) the visible answer.
                if (_thinkingMode == ThinkingMode.LeaveInline
                    && delta is not null
                    && delta["reasoning_content"] is JsonValue rv
                    && rv.TryGetValue(out string? rcStr)
                    && !string.IsNullOrEmpty(rcStr)
                    && (delta["content"] is not JsonValue cv
                        || !cv.TryGetValue(out string? cStr)
                        || string.IsNullOrEmpty(cStr)))
                {
                    delta["content"] = rcStr;
                }

                // Strip / capture XML tool_call from delta.content.
                if (delta?["content"] is JsonValue contentVal
                    && contentVal.TryGetValue(out string? token)
                    && !string.IsNullOrEmpty(token))
                {
                    string visible = cs.IngestToken(token, root, choice, index, extraFrames);
                    if (string.IsNullOrEmpty(visible))
                        delta.Remove("content");
                    else
                        delta["content"] = visible;
                }

                // If the model finished and we emitted tool calls, override finish_reason.
                if (choice["finish_reason"] is JsonValue fr
                    && fr.TryGetValue(out string? frStr)
                    && !string.IsNullOrEmpty(frStr)
                    && cs.EmittedToolCallCount > 0)
                {
                    choice["finish_reason"] = "tool_calls";
                }
            }

            yield return $"data: {root.ToJsonString(_jsonOptions)}";

            foreach (JsonObject extra in extraFrames)
                yield return $"data: {extra.ToJsonString(_jsonOptions)}";
        }

        private sealed class ChoiceState
        {
            private readonly StringBuilder _toolBuffer = new();
            private bool _inToolCall;
            public int EmittedToolCallCount { get; private set; }

            /// <summary>
            /// Consumes the next content token, returns the substring that should remain
            /// visible to the client (anything outside of a <c>&lt;tool_call&gt;</c> block),
            /// and appends synthesised <c>tool_calls</c> delta frames to <paramref name="extraFrames"/>
            /// once a complete block has been seen.
            /// </summary>
            public string IngestToken(string token, JsonObject root, JsonObject choice, int choiceIndex, List<JsonObject> extraFrames)
            {
                StringBuilder visible = new();
                int cursor = 0;

                while (cursor < token.Length)
                {
                    if (_inToolCall)
                    {
                        int end = token.IndexOf("</tool_call>", cursor, StringComparison.OrdinalIgnoreCase);
                        if (end < 0)
                        {
                            _toolBuffer.Append(token, cursor, token.Length - cursor);
                            cursor = token.Length;
                        }
                        else
                        {
                            int closeEnd = end + "</tool_call>".Length;
                            _toolBuffer.Append(token, cursor, closeEnd - cursor);
                            cursor = closeEnd;
                            _inToolCall = false;

                            EmitToolCallFromBuffer(root, choice, choiceIndex, extraFrames);
                            _toolBuffer.Clear();
                        }
                    }
                    else
                    {
                        int start = token.IndexOf("<tool_call>", cursor, StringComparison.OrdinalIgnoreCase);
                        if (start < 0)
                        {
                            visible.Append(token, cursor, token.Length - cursor);
                            cursor = token.Length;
                        }
                        else
                        {
                            visible.Append(token, cursor, start - cursor);
                            cursor = start;
                            _inToolCall = true;
                        }
                    }
                }

                return visible.ToString();
            }

            private void EmitToolCallFromBuffer(JsonObject root, JsonObject choice, int choiceIndex, List<JsonObject> extraFrames)
            {
                string xml = _toolBuffer.ToString();
                Match m = Regex.Match(
                    xml,
                    @"<tool_call>\s*<function=(?<name>[^>\s]+)>\s*(?<body>.*?)\s*</function>\s*</tool_call>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) return;

                string name = m.Groups["name"].Value.Trim();
                Dictionary<string, object?> args = new(StringComparer.OrdinalIgnoreCase);
                foreach (Match pm in Regex.Matches(
                    m.Groups["body"].Value,
                    @"<parameter=(?<n>[^>\s]+)>\s*(?<v>.*?)\s*</parameter>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    args[pm.Groups["n"].Value.Trim()] = ParseXmlToolParameterValue(pm.Groups["v"].Value.Trim());
                }

                string argsJson = JsonSerializer.Serialize(args, _jsonOptions);
                string callId = "call_" + Guid.NewGuid().ToString("N")[..16];
                int toolIndex = EmittedToolCallCount++;

                // Build a synthesised SSE frame mirroring the original chunk envelope.
                JsonObject toolDelta = new()
                {
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = toolIndex,
                            ["id"] = callId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = name,
                                ["arguments"] = argsJson,
                            },
                        },
                    },
                };

                JsonObject newChoice = new()
                {
                    ["index"] = choiceIndex,
                    ["delta"] = toolDelta,
                    ["finish_reason"] = null,
                };

                JsonObject frame = new()
                {
                    ["id"] = root["id"]?.DeepClone(),
                    ["object"] = root["object"]?.DeepClone(),
                    ["created"] = root["created"]?.DeepClone(),
                    ["model"] = root["model"]?.DeepClone(),
                    ["choices"] = new JsonArray(newChoice),
                };

                extraFrames.Add(frame);
            }
        }
    }

    /// <summary>
    /// Normalises a JSON request body before forwarding to the upstream:
    /// <list type="bullet">
    ///   <item>Rewrites the "model" field through the mapping table.</item>
    ///   <item>Merges multiple consecutive leading system messages into a single one,
    ///         separated by a blank line, so that strict Jinja templates (e.g. Qwen3)
    ///         that only allow one system message do not raise an exception.</item>
    ///   <item>Removes a trailing assistant response-prefill message, because some upstreams reject it when thinking mode is enabled.</item>
    ///   <item>Applies the per-model sampling priorities: <c>Provider</c> drops client-supplied
    ///         <c>temperature</c>/<c>repeat_penalty</c> so hosted providers keep their platform
    ///         values; <c>Proxy</c> overwrites (or injects) the configured proxy values.</item>
    ///   <item>Under <c>Proxy</c> reasoning priority, injects the configured reasoning effort in
    ///         every wire shape selected by the mapping's <see cref="ReasoningEffortFormat"/>
    ///         flags (legacy <c>reasoning_effort</c>, modern <c>reasoning.effort</c>, the Qwen
    ///         Cloud <c>extra_body</c> wrapper, and/or <c>chat_template_kwargs</c>), lowercasing
    ///         the value.</item>
    /// </list>
    /// Returns the original text unchanged if the body isn't valid JSON.
    /// </summary>
    internal static string NormalizeRequestBody(string json, AppSettings settings, RequestLog log, Func<string, bool>? shouldApplyThinkingCompatibility = null)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Read model name for logging and rewriting.
            string original = root.TryGetProperty("model", out JsonElement modelEl)
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;

            // Context-summarize (/compact) redirect: when this mapping has a smaller/faster
            // compact model configured and the request is a Copilot /compact summary request,
            // route the whole request to the compact model — its upstream, sampling, and
            // instruction-set settings all apply.
            string? firstContent = GetFirstMessageContent(root);
            string effectiveModel = ResolveEffectiveModel(settings, original, firstContent);
            bool compactRedirected = !string.Equals(effectiveModel, original, StringComparison.OrdinalIgnoreCase);

            string resolved = settings.ResolveModelName(effectiveModel);
            log.Model = effectiveModel;
            if (compactRedirected)
            {
                log.OriginalModel = original;
                Log.Debug(
                    "Context-summarize (/compact) request for {OriginalModel} redirected to compact model {CompactModel}",
                    original, effectiveModel);
            }
            else
            {
                Log.Debug(
                    "Compact redirect not applied for {OriginalModel}: {Reason}",
                    original, DescribeCompactSkipReason(settings, original, firstContent));
            }
            bool applyThinkingCompatibility = shouldApplyThinkingCompatibility?.Invoke(effectiveModel) ?? true;
            string? injectedInstructions = GetInstructionTextForModel(settings, effectiveModel);
            ModelMapping? normalizeMapping = settings.FindModelMapping(effectiveModel);
            SamplingPriority tempPriority = normalizeMapping?.TemperaturePriority ?? SamplingPriority.ClientApp;
            SamplingPriority repeatPriority = normalizeMapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp;
            double proxyTemperature = normalizeMapping?.Temperature ?? 0.7;
            double proxyRepeatPenalty = normalizeMapping?.RepeatPenalty ?? 1.0;
            SamplingPriority reasoningPriority = normalizeMapping?.ReasoningEffortPriority ?? SamplingPriority.ClientApp;
            // Providers expect lowercase effort levels (e.g. OpenAI rejects "High").
            string proxyReasoningEffort = normalizeMapping?.ReasoningEffort?.Trim().ToLowerInvariant() ?? string.Empty;
            bool proxyHasReasoningEffort = reasoningPriority == SamplingPriority.Proxy && proxyReasoningEffort.Length > 0;
            // The configured formats only matter when the proxy injects: each selected flag adds
            // its wire shape (top-level reasoning_effort, nested reasoning object, the extra_body
            // wrapper, or chat_template_kwargs for local inference servers).
            ReasoningEffortFormat reasoningFormat = normalizeMapping?.ReasoningEffortFormat ?? ReasoningEffortFormat.Legacy;
            bool injectLegacyEffort = proxyHasReasoningEffort && reasoningFormat.HasFlag(ReasoningEffortFormat.Legacy);
            bool injectModernEffort = proxyHasReasoningEffort && reasoningFormat.HasFlag(ReasoningEffortFormat.Modern);
            bool injectExtraBody = proxyHasReasoningEffort && reasoningFormat.HasFlag(ReasoningEffortFormat.QwenCloud);
            bool injectChatTemplateKwargs = proxyHasReasoningEffort && reasoningFormat.HasFlag(ReasoningEffortFormat.ChatTemplateKwargs);
            // Provider drops the field; Proxy overrides/injects the configured value. Client App
            // (and Proxy without a configured value) leave the client's field untouched.
            bool rewriteReasoningEffort = reasoningPriority == SamplingPriority.Provider || proxyHasReasoningEffort;

            // Check whether the messages array has consecutive leading system messages.
            bool hasConsecutiveSystemMessages = false;
            bool hasTrailingAssistantPrefill = false;
            bool shouldInjectInstructions = false;
            if (root.TryGetProperty("messages", out JsonElement messagesEl)
                && messagesEl.ValueKind == JsonValueKind.Array)
            {
                List<JsonElement> messages = [.. messagesEl.EnumerateArray()];
                shouldInjectInstructions = !string.IsNullOrWhiteSpace(injectedInstructions);

                int leadingSystem = 0;
                foreach (JsonElement msg in messages)
                {
                    if (msg.TryGetProperty("role", out JsonElement role)
                        && role.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true)
                        leadingSystem++;
                    else
                        break;
                }

                hasConsecutiveSystemMessages = leadingSystem > 1;
                hasTrailingAssistantPrefill = applyThinkingCompatibility
                    && messages.Count > 0
                    && IsAssistantResponsePrefill(messages[^1]);
            }

            // Nothing to rewrite — return original text unchanged.
            if (string.Equals(original, resolved, StringComparison.Ordinal)
                && !hasConsecutiveSystemMessages
                && !hasTrailingAssistantPrefill
                && !shouldInjectInstructions
                && tempPriority == SamplingPriority.ClientApp
                && repeatPriority == SamplingPriority.ClientApp
                && !rewriteReasoningEffort)
                return json;

            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();

            bool clientHadTemperature = false;
            bool clientHadRepeatPenalty = false;
            bool wroteReasoningEffort = false;
            bool wroteReasoning = false;
            bool wroteExtraBody = false;
            bool wroteChatTemplateKwargs = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.Name.Equals("temperature", StringComparison.OrdinalIgnoreCase))
                {
                    clientHadTemperature = true;
                    if (tempPriority == SamplingPriority.Provider)
                        continue;

                    if (tempPriority == SamplingPriority.Proxy)
                        writer.WriteNumber("temperature", proxyTemperature);
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("repeat_penalty", StringComparison.OrdinalIgnoreCase))
                {
                    clientHadRepeatPenalty = true;
                    if (repeatPriority == SamplingPriority.Provider)
                        continue;

                    if (repeatPriority == SamplingPriority.Proxy)
                        writer.WriteNumber("repeat_penalty", proxyRepeatPenalty);
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                {
                    if (reasoningPriority == SamplingPriority.Provider)
                        continue;

                    if (proxyHasReasoningEffort)
                    {
                        // The proxy takes over: override when the configured format carries a
                        // legacy field, otherwise drop the client's value.
                        if (injectLegacyEffort)
                        {
                            writer.WriteString("reasoning_effort", proxyReasoningEffort);
                            wroteReasoningEffort = true;
                        }
                    }
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (proxyHasReasoningEffort)
                    {
                        // The proxy takes over: override when the configured format carries a
                        // modern object, otherwise drop the client's.
                        if (injectModernEffort)
                        {
                            WriteReasoningObject(writer, proxyReasoningEffort);
                            wroteReasoning = true;
                        }
                    }
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("extra_body", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (proxyHasReasoningEffort)
                    {
                        // The proxy takes over: override when the configured formats carry an
                        // extra_body wrapper, otherwise drop the client's.
                        if (injectExtraBody)
                        {
                            WriteExtraBodyObject(writer, proxyReasoningEffort);
                            wroteExtraBody = true;
                        }
                    }
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("chat_template_kwargs", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (proxyHasReasoningEffort)
                    {
                        // The proxy takes over: override when the configured format carries
                        // chat_template_kwargs, otherwise drop the client's.
                        if (injectChatTemplateKwargs)
                        {
                            WriteChatTemplateKwargsObject(writer, proxyReasoningEffort);
                            wroteChatTemplateKwargs = true;
                        }
                    }
                    else
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("model", resolved);
                }
                else if (prop.Name.Equals("messages", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Array
                      && (hasConsecutiveSystemMessages || hasTrailingAssistantPrefill || shouldInjectInstructions))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();

                    List<JsonElement> messages = [.. prop.Value.EnumerateArray()];

                    // Collect and merge consecutive leading system message contents.
                    var systemParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(injectedInstructions))
                        systemParts.Add(injectedInstructions);

                    bool merging = true;

                    for (int i = 0; i < messages.Count; i++)
                    {
                        JsonElement msg = messages[i];

                        if (hasTrailingAssistantPrefill && i == messages.Count - 1 && IsAssistantResponsePrefill(msg))
                            continue;

                        bool isSystem = merging
                            && msg.TryGetProperty("role", out JsonElement r)
                            && r.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true;

                        if (isSystem)
                        {
                            string content = msg.TryGetProperty("content", out JsonElement c)
                                ? c.GetString() ?? string.Empty
                                : string.Empty;
                            systemParts.Add(content);
                        }
                        else
                        {
                            // Emit the merged system message once when we leave the system block.
                            if (merging && systemParts.Count > 0)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("role", "system");
                                writer.WriteString("content", string.Join("\n\n", systemParts));
                                writer.WriteEndObject();
                                merging = false;
                            }

                            msg.WriteTo(writer);
                        }
                    }

                    // Edge case: all messages were system messages.
                    if (merging && systemParts.Count > 0)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", string.Join("\n\n", systemParts));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            // Proxy priority injects the configured value when the client did not send one.
            if (tempPriority == SamplingPriority.Proxy && !clientHadTemperature)
                writer.WriteNumber("temperature", proxyTemperature);
            if (repeatPriority == SamplingPriority.Proxy && !clientHadRepeatPenalty)
                writer.WriteNumber("repeat_penalty", proxyRepeatPenalty);
            if (proxyHasReasoningEffort)
            {
                if (injectLegacyEffort && !wroteReasoningEffort)
                    writer.WriteString("reasoning_effort", proxyReasoningEffort);
                if (injectModernEffort && !wroteReasoning)
                    WriteReasoningObject(writer, proxyReasoningEffort);
                if (injectExtraBody && !wroteExtraBody)
                    WriteExtraBodyObject(writer, proxyReasoningEffort);
                if (injectChatTemplateKwargs && !wroteChatTemplateKwargs)
                    WriteChatTemplateKwargsObject(writer, proxyReasoningEffort);
            }

            writer.WriteEndObject();
            writer.Flush();

            string output = System.Text.Encoding.UTF8.GetString(ms.ToArray());

            // Debug mode: record every settings-driven override/transformation applied on the
            // passthrough path (model rewrite, sampling, reasoning effort, system merging).
            if (settings.DebugMode)
                log.DebugSummary = BuildNormalizeDebugNotes(
                    root,
                    original,
                    effectiveModel,
                    resolved,
                    compactRedirected,
                    tempPriority,
                    repeatPriority,
                    proxyTemperature,
                    proxyRepeatPenalty,
                    normalizeMapping,
                    reasoningPriority,
                    proxyReasoningEffort,
                    reasoningFormat,
                    injectedInstructions,
                    hasConsecutiveSystemMessages,
                    hasTrailingAssistantPrefill);

            return output;
        }
        catch
        {
            // Non-JSON or malformed body — forward as-is.
            return json;
        }
    }

    /// <summary>
    /// Builds the multi-line debug audit trail for a <c>/v1/*</c> passthrough body from the
    /// same per-model settings decisions applied by <see cref="NormalizeRequestBody"/>. Returns
    /// null when no transformation was applied (the caller only reaches this when the body was
    /// actually rewritten, so a summary is always produced here).
    /// </summary>
    private static string BuildNormalizeDebugNotes(
        JsonElement root,
        string original,
        string effectiveModel,
        string resolved,
        bool compactRedirected,
        SamplingPriority tempPriority,
        SamplingPriority repeatPriority,
        double proxyTemperature,
        double proxyRepeatPenalty,
        ModelMapping? mapping,
        SamplingPriority reasoningPriority,
        string proxyReasoningEffort,
        ReasoningEffortFormat reasoningFormat,
        string? injectedInstructions,
        bool hasConsecutiveSystemMessages,
        bool hasTrailingAssistantPrefill)
    {
        // Extract the client's original values for before/after comparison.
        float? clientTemperature = ReadJsonNumber(root, "temperature");
        float? clientRepeatPenalty = ReadJsonNumber(root, "repeat_penalty");
        string? clientReasoningEffort = root.TryGetProperty("reasoning_effort", out JsonElement re)
            && re.ValueKind == JsonValueKind.String
                ? re.GetString()
                : null;

        StringBuilder sb = new();

        // Compact redirect: show when the request was rerouted to a different model.
        if (compactRedirected)
            sb.AppendLine(DebugNotes.ContextSummarizeRedirectPassthrough(original, effectiveModel));

        sb.AppendLine(DebugNotes.ModelResolution(effectiveModel, resolved, !string.Equals(effectiveModel, resolved, StringComparison.OrdinalIgnoreCase)));
        sb.AppendLine(DebugNotes.SamplingDecision("temperature", tempPriority, clientTemperature, (float)proxyTemperature));
        sb.AppendLine(DebugNotes.SamplingDecision("repeat_penalty", repeatPriority, clientRepeatPenalty, (float)proxyRepeatPenalty));
        sb.AppendLine(DebugNotes.ReasoningEffortDecision(reasoningPriority, clientReasoningEffort, proxyReasoningEffort, reasoningFormat));

        if (!string.IsNullOrWhiteSpace(injectedInstructions))
            sb.AppendLine(DebugNotes.InstructionInjection(mapping?.InstructionSetName ?? string.Empty));
        if (hasConsecutiveSystemMessages)
            sb.AppendLine("messages: merged consecutive leading system messages into a single system message");
        if (hasTrailingAssistantPrefill)
            sb.AppendLine("messages: removed trailing assistant prefill (thinking compatibility)");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads a numeric property (number or numeric string) from a JSON element as a float, or
    /// null when the property is absent or not numeric.
    /// </summary>
    private static float? ReadJsonNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement prop))
            return null;

        return prop.ValueKind == JsonValueKind.Number
            ? (float)prop.GetDouble()
            : prop.ValueKind == JsonValueKind.String && prop.TryGetSingle(out float value)
                ? value
                : null;
    }

    /// <summary>Writes the modern nested <c>"reasoning": { "enable": true, "thinking_level": "..." }</c> object.</summary>
    private static void WriteReasoningObject(Utf8JsonWriter writer, string effort)
    {
        writer.WritePropertyName("reasoning");
        writer.WriteStartObject();
        writer.WriteBoolean("enable", true);
        writer.WriteString("thinking_level", effort);
        writer.WriteEndObject();
    }

    /// <summary>Writes the Qwen Cloud <c>"extra_body": { "enable_thinking": true,
    /// "reasoning_effort": "..." }</c> wrapper.</summary>
    private static void WriteExtraBodyObject(Utf8JsonWriter writer, string effort)
    {
        writer.WritePropertyName("extra_body");
        writer.WriteStartObject();
        writer.WriteBoolean("enable_thinking", true);
        writer.WriteString("reasoning_effort", effort);
        writer.WriteEndObject();
    }

    /// <summary>Writes the <c>"chat_template_kwargs": { "enable_thinking": true,
    /// "reasoning_effort": "..." }</c> object used by local inference servers such as
    /// llama.cpp and vLLM. The <c>enable_thinking</c> flag is required by Qwen3 chat
    /// templates to activate thinking mode before <c>reasoning_effort</c> takes effect.</summary>
    private static void WriteChatTemplateKwargsObject(Utf8JsonWriter writer, string effort)
    {
        writer.WritePropertyName("chat_template_kwargs");
        writer.WriteStartObject();
        writer.WriteBoolean("enable_thinking", true);
        writer.WriteString("reasoning_effort", effort);
        writer.WriteEndObject();
    }

    private static bool IsAssistantResponsePrefill(JsonElement message)
    {
        if (!message.TryGetProperty("role", out JsonElement role)
            || !string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array
            && toolCalls.GetArrayLength() > 0)
        {
            return false;
        }

        if (message.TryGetProperty("tool_call_id", out JsonElement toolCallId)
            && toolCallId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(toolCallId.GetString()))
        {
            return false;
        }

        return true;
    }

    internal static string? GetInstructionTextForModel(AppSettings settings, string modelName)
    {
        ModelMapping? mapping = settings.FindModelMapping(modelName);
        InstructionSet? instructionSet = settings.FindInstructionSet(mapping?.InstructionSetName);
        return string.IsNullOrWhiteSpace(instructionSet?.Instructions)
            ? null
            : instructionSet.Instructions;
    }

    internal static string RedactRequestBodyForLog(AppSettings settings, string body, string modelName)
    {
        ModelMapping? mapping = settings.FindModelMapping(modelName);
        if (mapping?.RedactRequestBodies ?? true)
            return RedactedBodyText;

        return mapping?.RedactSensitiveJsonFields ?? true
            ? RedactSensitiveJsonFields(body)
            : body;
    }

    private string RedactResponseBodyForLog(string body, string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        if (mapping?.RedactResponseBodies ?? true)
            return RedactedBodyText;

        return mapping?.RedactSensitiveJsonFields ?? true
            ? RedactSensitiveJsonFields(body)
            : body;
    }

    /// <summary>
    /// Replaces the values of sensitive JSON properties with a redaction marker without
    /// re-serializing the document. Everything that is not a sensitive value — whitespace,
    /// key order, string escaping — is preserved byte-for-byte, so a clean body is returned
    /// as the exact same string. This keeps logged request bodies identical to what the
    /// client actually sent. Returns the body unchanged when it is not valid JSON.
    /// </summary>
    internal static string RedactSensitiveJsonFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        // Verify the body is valid JSON; if not, return it unchanged.
        try
        {
            using (JsonDocument.Parse(body)) { }
        }
        catch (JsonException)
        {
            return body;
        }

        // Walk the raw text, preserving everything byte-for-byte except the values of
        // properties whose names match IsSensitiveJsonProperty (recursing into nested
        // objects/arrays so deeply-nested credentials are still redacted).
        var output = new StringBuilder(body.Length + RedactedValueText.Length);
        AppendValue(body, 0, output);
        return output.ToString();
    }

    /// <summary>
    /// Appends the value starting at <paramref name="index"/> to <paramref name="output"/>.
    /// For objects and arrays the interior is walked so nested sensitive values are redacted;
    /// all other text (whitespace, commas, key order, string escapes, scalars) is copied
    /// verbatim.
    /// </summary>
    private static void AppendValue(string body, int index, StringBuilder output)
    {
        int i = SkipValueStart(body, index);

        if (i >= body.Length)
            return;

        // Copy any leading whitespace before the value verbatim.
        output.Append(body.AsSpan(index, i - index));

        char c = body[i];

        if (c == '{')
        {
            output.Append('{');
            int afterOpen = i + 1;

            while (true)
            {
                int tokenStart = afterOpen;
                afterOpen = SkipValueStart(body, tokenStart);
                if (afterOpen >= body.Length || body[afterOpen] == '}')
                {
                    // Copy trailing whitespace and the closing brace verbatim.
                    output.Append(body.AsSpan(tokenStart, (afterOpen < body.Length ? afterOpen + 1 : body.Length) - tokenStart));
                    break;
                }

                // A property name must be a string; anything else is copied verbatim.
                if (body[afterOpen] != '"')
                {
                    output.Append(body.AsSpan(tokenStart));
                    break;
                }

                int nameEnd = FindStringEnd(body, afterOpen);
                string name = body[(afterOpen + 1)..nameEnd];

                // A property requires a colon after the name; otherwise copy verbatim.
                int colonIdx = SkipValueStart(body, nameEnd + 1);
                if (colonIdx >= body.Length || body[colonIdx] != ':')
                {
                    output.Append(body.AsSpan(tokenStart));
                    break;
                }

                // Copy name + colon + whitespace verbatim so the body stays byte-identical.
                int valueStart = SkipValueStart(body, colonIdx + 1);
                output.Append(body.AsSpan(tokenStart, valueStart - tokenStart));

                if (IsSensitiveJsonProperty(name))
                {
                    // Replace only the value with a quoted marker; name + colon stay verbatim.
                    output.Append('"');
                    output.Append(RedactedValueText);
                    output.Append('"');
                    afterOpen = FindValueEnd(body, valueStart);
                }
                else if (valueStart < body.Length && (body[valueStart] == '{' || body[valueStart] == '['))
                {
                    // Recurse so nested sensitive properties are still redacted.
                    AppendValue(body, valueStart, output);
                    afterOpen = FindValueEnd(body, valueStart);
                }
                else
                {
                    int valueEnd = FindValueEnd(body, valueStart);
                    output.Append(body.AsSpan(valueStart, valueEnd - valueStart));
                    afterOpen = valueEnd;
                }

                int commaIdx = SkipValueStart(body, afterOpen);
                if (commaIdx < body.Length && body[commaIdx] == ',')
                {
                    // Copy whitespace up to and including the comma verbatim.
                    output.Append(body.AsSpan(afterOpen, commaIdx + 1 - afterOpen));
                    afterOpen = commaIdx + 1;
                    continue;
                }

                // No comma after this value: loop back so the header copies the closing
                // brace (and any preceding whitespace) verbatim.
                continue;
            }
        }
        else if (c == '[')
        {
            output.Append('[');
            int afterOpen = i + 1;

            while (true)
            {
                int tokenStart = afterOpen;
                afterOpen = SkipValueStart(body, tokenStart);
                if (afterOpen >= body.Length || body[afterOpen] == ']')
                {
                    // Copy trailing whitespace and the closing bracket verbatim.
                    output.Append(body.AsSpan(tokenStart, (afterOpen < body.Length ? afterOpen + 1 : body.Length) - tokenStart));
                    break;
                }

                int valueEnd = FindValueEnd(body, afterOpen);
                output.Append(body.AsSpan(tokenStart, valueEnd - tokenStart));
                afterOpen = valueEnd;

                int commaIdx = SkipValueStart(body, afterOpen);
                if (commaIdx < body.Length && body[commaIdx] == ',')
                {
                    // Copy whitespace up to and including the comma verbatim.
                    output.Append(body.AsSpan(afterOpen, commaIdx + 1 - afterOpen));
                    afterOpen = commaIdx + 1;
                    continue;
                }

                // No comma after this value: loop back so the header copies the closing
                // bracket (and any preceding whitespace) verbatim.
                continue;
            }
        }
        else
        {
            int valueEnd = FindValueEnd(body, i);
            output.Append(body.AsSpan(i, valueEnd - i));
        }
    }

    /// <summary>Advances past leading whitespace before a JSON value.</summary>
    private static int SkipValueStart(string body, int index)
    {
        int i = index;
        while (i < body.Length && char.IsWhiteSpace(body[i]))
            i++;
        return i;
    }

    /// <summary>
    /// Returns the index of the closing quote of a JSON string whose opening quote is at
    /// <paramref name="index"/>.
    /// </summary>
    private static int FindStringEnd(string body, int index)
    {
        int i = index + 1;
        while (i < body.Length)
        {
            if (body[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (body[i] == '"')
                return i;
            i++;
        }

        return body.Length;
    }

    /// <summary>
    /// Given the index of the first non-whitespace character of a JSON value, returns the
    /// index just past the end of that value. Handles strings (with escapes), numbers,
    /// literals, objects, and arrays.
    /// </summary>
    private static int FindValueEnd(string body, int start)
    {
        int i = SkipValueStart(body, start);
        if (i >= body.Length)
            return i;

        char c = body[i];

        if (c == '"')
            return FindStringEnd(body, i) + 1;

        if (c == '{' || c == '[')
        {
            char open = c;
            char close = c == '{' ? '}' : ']';
            int depth = 0;
            while (i < body.Length)
            {
                char ch = body[i];
                if (ch == '"')
                    i = FindStringEnd(body, i) + 1;
                else
                {
                    if (ch == open)
                        depth++;
                    else if (ch == close)
                    {
                        depth--;
                        if (depth == 0)
                            return i + 1;
                    }
                    i++;
                }
            }
            return body.Length;
        }

        // Scalar: number, true, false, null — read until whitespace, comma, close, or end.
        while (i < body.Length)
        {
            char ch = body[i];
            if (char.IsWhiteSpace(ch) || ch == ',' || ch == '}' || ch == ']')
                break;
            i++;
        }
        return i;
    }

    private static bool IsSensitiveJsonProperty(string propertyName)
    {
        // Credentials and secrets only. Prompt/message content fields are intentionally
        // left intact — when body capture is enabled the content is exactly what the
        // user opted to inspect, and redacting it would make the logs useless.
        return propertyName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apikey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("password", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssistantResponsePrefill(LlamaCppMessage message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        && (message.ToolCalls is null || message.ToolCalls.Count == 0)
        && string.IsNullOrWhiteSpace(message.ToolCallId);

    // ── /api/tags → configured proxy model names ───────────────────────────

    private async Task HandleTagsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var tags = new OllamaTagsResponse
        {
            Models = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !string.IsNullOrWhiteSpace(m.ProxyName))
                .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)
                .Select(CreateOllamaModelEntry)],
        };

        string tagsJson = JsonSerializer.Serialize(tags, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = tagsJson;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, tagsJson, ct);
    }

    // ── /v1/models → OpenAI-format model list with context_length ───────────

    private async Task HandleV1ModelsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var response = new LlamaCppModelsResponse
        {
            Data = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !string.IsNullOrWhiteSpace(m.ProxyName))
                .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)
                .Select(m =>
                {
                    string name = string.IsNullOrWhiteSpace(m.ProxyName) ? m.ModelName : m.ProxyName;
                    return new LlamaCppModel
                    {
                        Id = name,
                        OwnedBy = "kaeo-proxy",
                        ContextLength = m.GetEffectiveContextWindow(),
                        Capabilities = BuildCapabilities(m),
                    };
                })],
        };

        string json = JsonSerializer.Serialize(response, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = json;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, json, ct);
    }

    // ── GET /v1/models/{model} → single-model lookup from local mappings ─────

    /// <summary>
    /// Answers <c>GET /v1/models/{model}</c> entirely from the local mapping table, mirroring
    /// <c>/api/show</c>. Upstreams vary wildly in whether/how they support a single-model lookup
    /// (some return 404, some 400, some nothing at all), and only the proxy knows its exposed
    /// names — building the response locally keeps model availability consistent with what
    /// <c>/v1/models</c> reports.
    /// </summary>
    private async Task HandleV1ModelAsync(string path, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string requestedModel = Uri.UnescapeDataString(path["/v1/models/".Length..]);
        ModelMapping? mapping = _settings.FindModelMapping(requestedModel);
        log.Model = requestedModel;

        if (mapping is null)
        {
            log.StatusCode = 404;
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Model '{requestedModel}' not found in configured mappings.";

            resp.StatusCode = 404;
            await WriteJsonAsync(resp, new
            {
                error = new
                {
                    message = $"The model '{requestedModel}' does not exist or is not enabled in the proxy configuration.",
                    type = "invalid_request_error",
                    param = "model",
                    code = "model_not_found",
                },
            }, ct);
            return;
        }

        string name = string.IsNullOrWhiteSpace(mapping.ProxyName) ? mapping.ModelName : mapping.ProxyName;
        var model = new LlamaCppModel
        {
            Id = name,
            OwnedBy = "kaeo-proxy",
            ContextLength = mapping.GetEffectiveContextWindow(),
            Capabilities = BuildCapabilities(mapping),
        };

        string modelJson = JsonSerializer.Serialize(model, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = modelJson;

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, modelJson, ct);
    }

    // ── POST /v1/responses/compact → OpenAI-compatible conversation compaction ──

    /// <summary>
    /// Handles <c>POST /v1/responses/compact</c> — forwards the compaction request to the
    /// upstream OpenAI-compatible endpoint, applying the compact model redirect when the
    /// mapping has a smaller/faster model configured for context summarization.
    /// </summary>
    private async Task HandleCompactAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string bodyText = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);

        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.RequestBody = bodyText;

        // Extract the model name from the request body and apply the compact model redirect.
        string originalModel = string.Empty;
        string effectiveModel = string.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("model", out JsonElement modelEl)
                && modelEl.ValueKind == JsonValueKind.String)
            {
                originalModel = modelEl.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "Invalid JSON in request body." }, ct);
            return;
        }

        log.Model = originalModel;
        Log.Debug("Compact request received for model {OriginalModel}, request size: {RequestBytes} bytes",
            originalModel, log.RequestBytes);

        // Apply compact model redirect: first check global CompactModelProxyName, then per-mapping ContextSummarizeModelId
        if (!string.IsNullOrWhiteSpace(_settings.CompactModelProxyName))
        {
            // Use global compact model if configured
            ModelMapping? globalCompactMapping = _settings.FindModelMapping(_settings.CompactModelProxyName);
            if (globalCompactMapping is not null && globalCompactMapping.IsEnabled)
            {
                effectiveModel = globalCompactMapping.ProxyName;
                Log.Debug("Using global compact model {CompactModel} for request model {OriginalModel}",
                    effectiveModel, originalModel);
                if (_settings.DebugMode && log.DebugSummary is not null)
                    log.DebugSummary += "\n" + DebugNotes.ContextSummarizeRedirect(
                        originalModel, effectiveModel);
            }
        }

        // Fall back to per-mapping ContextSummarizeModelId if global not set or not found
        if (string.IsNullOrEmpty(effectiveModel))
        {
            ModelMapping? mapping = _settings.FindModelMapping(originalModel);
            if (mapping is not null && mapping.ContextSummarizeModelId.HasValue)
            {
                ModelMapping? compactMapping = _settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
                if (compactMapping is not null && compactMapping.IsEnabled)
                {
                    effectiveModel = compactMapping.ProxyName;
                    Log.Debug("Using per-mapping compact model {CompactModel} for request model {OriginalModel}",
                        effectiveModel, originalModel);
                    if (_settings.DebugMode && log.DebugSummary is not null)
                        log.DebugSummary += "\n" + DebugNotes.ContextSummarizeRedirect(
                            originalModel, effectiveModel);
                }
            }
        }

        if (string.IsNullOrEmpty(effectiveModel))
            effectiveModel = originalModel;

        // Rewrite the model name in the request body if redirecting.
        string upstreamBody = bodyText;
        if (!string.Equals(effectiveModel, originalModel, StringComparison.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(bodyText);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                doc.RootElement.WriteTo(writer);
            }
            // Simple string replacement for the model field — safe because model names are
            // always quoted strings and we control the replacement value.
            upstreamBody = bodyText.Replace(
                $"\"model\":\"{originalModel}\"",
                $"\"model\":\"{effectiveModel}\"");
        }

        var (baseUrl, timeout, apiKey) = ResolveUpstream(effectiveModel);

        if (_settings.DebugMode && log.DebugSummary is not null)
        {
            log.DebugSummary += "\n" + DebugNotes.UpstreamRouting(
                effectiveModel, baseUrl, !string.IsNullOrWhiteSpace(apiKey), timeout);
        }

        // Use the proxy's own AutoCompactionService to perform compaction locally
        // instead of forwarding to upstream (which doesn't implement /v1/responses/compact)
        try
        {
            ModelMapping? mapping = _settings.FindModelMapping(originalModel);
            if (mapping is null)
            {
                resp.StatusCode = 404;
                await WriteJsonAsync(resp, new { error = $"Model '{originalModel}' not found." }, ct);
                return;
            }

            // Build a session key for circuit breaker tracking
            string sessionKey = $"compact:{originalModel}:{bodyText.GetHashCode():X8}";

            // Get the compact model's context window for chunk sizing
            ModelMapping? compactMapping = null;
            if (!string.IsNullOrEmpty(effectiveModel) && !string.Equals(effectiveModel, originalModel, StringComparison.Ordinal))
            {
                compactMapping = _settings.FindModelMapping(effectiveModel);
            }
            int compactModelContext = (compactMapping ?? mapping).GetEffectiveContextWindow();
            int maxTokensPerChunk = (int)(compactModelContext * AutoCompactionService.ContextWindowFraction);
            int targetModelContextWindow = mapping.GetEffectiveContextWindow();

            // Summarization requests are sent straight to the upstream, which knows the model
            // by its ModelName (e.g. the .gguf path) — not the proxy display name.
            string compactUpstreamModel = (compactMapping ?? mapping).ModelName ?? effectiveModel;

            // Detect if this is a Copilot request to determine the appropriate format
            CompactionFormat format = IsCopilotRequest(req) ? CompactionFormat.Ollama : CompactionFormat.Proxy;

            string? compactedBody = await _autoCompactionService.CompactAsync(
                mapping,
                bodyText,
                sessionKey,
                baseUrl,
                apiKey,
                timeout,
                maxTokensPerChunk,
                compactUpstreamModel,
                targetModelContextWindow,
                compactModelContext,
                ct,
                format);

            if (compactedBody is null)
            {
                Log.Warning("Compact request failed for model {Model}", originalModel);
                resp.StatusCode = 500;
                await WriteJsonAsync(resp, new
                {
                    error = "Compaction failed. The conversation may be too large or the compact model may be unavailable.",
                }, ct);
                return;
            }

            _autoCompactionService.RecordSuccess(sessionKey);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(compactedBody);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = compactedBody;

            resp.StatusCode = 200;
            resp.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(compactedBody);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, ct);
            resp.Close();

            log.StatusCode = 200;
            log.Status = RequestStatus.Success;
            Log.Information("Compact request completed successfully for model {Model}", originalModel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Compact request failed for model {Model}", originalModel);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;
            resp.StatusCode = 500;
            await WriteJsonAsync(resp, new
            {
                error = "Internal error during compaction. Please retry.",
            }, ct);
        }
    }

    // ── POST /v1/chat/completions/compact ─────────────────────────────────────

    /// <summary>
    /// Handles <c>POST /v1/chat/completions/compact</c> — manual context compaction endpoint.
    /// Accepts a chat completion request body, compacts the conversation history using the
    /// configured compact model, and returns the compacted messages. This endpoint is disabled
    /// by default and must be enabled via <c>EnableManualCompactionEndpoint</c> in settings.
    /// </summary>
    private async Task HandleManualCompactAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string bodyText = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);

        string originalModel = string.Empty;
        string effectiveModel = string.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("model", out JsonElement modelEl)
                && modelEl.ValueKind == JsonValueKind.String)
            {
                originalModel = modelEl.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "Invalid JSON in request body." }, ct);
            return;
        }

        log.Model = originalModel;

        // Apply compact model redirect: first check global CompactModelProxyName, then per-mapping ContextSummarizeModelId
        if (!string.IsNullOrWhiteSpace(_settings.CompactModelProxyName))
        {
            // Use global compact model if configured
            ModelMapping? globalCompactMapping = _settings.FindModelMapping(_settings.CompactModelProxyName);
            if (globalCompactMapping is not null && globalCompactMapping.IsEnabled)
            {
                effectiveModel = globalCompactMapping.ProxyName;
                Log.Debug("Using global compact model {CompactModel} for manual compact request model {OriginalModel}",
                    effectiveModel, originalModel);
                if (_settings.DebugMode && log.DebugSummary is not null)
                    log.DebugSummary += "\n" + DebugNotes.ContextSummarizeRedirect(
                        originalModel, effectiveModel);
            }
        }

        // Fall back to per-mapping ContextSummarizeModelId if global not set or not found
        if (string.IsNullOrEmpty(effectiveModel))
        {
            ModelMapping? mapping = _settings.FindModelMapping(originalModel);
            if (mapping is not null && mapping.ContextSummarizeModelId.HasValue)
            {
                ModelMapping? compactMapping = _settings.FindModelMappingById(mapping.ContextSummarizeModelId.Value);
                if (compactMapping is not null && compactMapping.IsEnabled)
                {
                    effectiveModel = compactMapping.ProxyName;
                    Log.Debug("Using per-mapping compact model {CompactModel} for manual compact request model {OriginalModel}",
                        effectiveModel, originalModel);
                    if (_settings.DebugMode && log.DebugSummary is not null)
                        log.DebugSummary += "\n" + DebugNotes.ContextSummarizeRedirect(
                            originalModel, effectiveModel);
                }
            }
        }

        if (string.IsNullOrEmpty(effectiveModel))
            effectiveModel = originalModel;

        // Rewrite the model name in the request body if redirecting.
        string upstreamBody = bodyText;
        if (!string.Equals(effectiveModel, originalModel, StringComparison.Ordinal))
        {
            upstreamBody = bodyText.Replace(
                $"\"model\":\"{originalModel}\"",
                $"\"model\":\"{effectiveModel}\"");
        }

        var (baseUrl, timeout, apiKey) = ResolveUpstream(effectiveModel);

        if (_settings.DebugMode && log.DebugSummary is not null)
        {
            log.DebugSummary += "\n" + DebugNotes.UpstreamRouting(
                effectiveModel, baseUrl, !string.IsNullOrWhiteSpace(apiKey), timeout);
        }

        Log.Information("Manual compaction requested for model {Model}, redirecting to {CompactModel}",
            originalModel, effectiveModel);

        // Use the proxy's own AutoCompactionService to perform compaction locally
        // instead of forwarding to upstream (which doesn't implement /v1/responses/compact)
        try
        {
            ModelMapping? mapping = _settings.FindModelMapping(originalModel);
            if (mapping is null)
            {
                resp.StatusCode = 404;
                await WriteJsonAsync(resp, new { error = $"Model '{originalModel}' not found." }, ct);
                return;
            }

            // Build a session key for circuit breaker tracking
            string sessionKey = $"manual-compact:{originalModel}:{bodyText.GetHashCode():X8}";

            // Get the compact model's context window for chunk sizing
            ModelMapping? compactMapping = null;
            if (!string.IsNullOrEmpty(effectiveModel) && !string.Equals(effectiveModel, originalModel, StringComparison.Ordinal))
            {
                compactMapping = _settings.FindModelMapping(effectiveModel);
            }
            int compactModelContext = (compactMapping ?? mapping).GetEffectiveContextWindow();
            int maxTokensPerChunk = (int)(compactModelContext * AutoCompactionService.ContextWindowFraction);
            int targetModelContextWindow = mapping.GetEffectiveContextWindow();

            // Summarization requests are sent straight to the upstream, which knows the model
            // by its ModelName (e.g. the .gguf path) — not the proxy display name.
            string compactUpstreamModel = (compactMapping ?? mapping).ModelName ?? effectiveModel;

            // Detect if this is a Copilot request to determine the appropriate format
            CompactionFormat format = IsCopilotRequest(req) ? CompactionFormat.Ollama : CompactionFormat.Proxy;

            string? compactedBody = await _autoCompactionService.CompactAsync(
                mapping,
                bodyText,
                sessionKey,
                baseUrl,
                apiKey,
                timeout,
                maxTokensPerChunk,
                compactUpstreamModel,
                targetModelContextWindow,
                compactModelContext,
                ct,
                format);

            if (compactedBody is null)
            {
                Log.Warning("Manual compact request failed for model {Model}", originalModel);
                resp.StatusCode = 500;
                await WriteJsonAsync(resp, new
                {
                    error = "Manual compaction failed. The conversation may be too large or the compact model may be unavailable.",
                }, ct);
                return;
            }

            _autoCompactionService.RecordSuccess(sessionKey);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(compactedBody);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = compactedBody;

            resp.StatusCode = 200;
            resp.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(compactedBody);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, ct);
            resp.Close();

            log.StatusCode = 200;
            log.Status = RequestStatus.Success;
            Log.Information("Manual compaction completed successfully for model {Model}", originalModel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual compact request failed for model {Model}", originalModel);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;
            resp.StatusCode = 500;
            await WriteJsonAsync(resp, new
            {
                error = "Internal error during manual compaction. Please retry.",
            }, ct);
        }
    }

    // ── /api/ps → running model stub ──────────────────────────────────────

    private async Task HandlePsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        // Report configured enabled mappings as "running" so clients see the proxy-facing names
        // rather than whatever ID the upstream happens to advertise. The expires_at field is a
        // stub — llama.cpp keeps the model permanently loaded.
        var running = _settings.ModelMappings
            .Where(m => m.IsEnabled && !string.IsNullOrWhiteSpace(m.ProxyName))
            .Select(m =>
            {
                string name = string.IsNullOrWhiteSpace(m.ProxyName) ? m.ModelName : m.ProxyName;
                return new
                {
                    name,
                    model = name,
                    size = 0L,
                    digest = string.Empty,
                    details = CreateOllamaModelDetails(new LlamaCppModel { Id = m.ModelName }, m),
                    expires_at = DateTime.UtcNow.AddHours(1).ToString("o"),
                    size_vram = 0L,
                    context_length = m.GetEffectiveContextWindow(),
                    capabilities = BuildOllamaCapabilities(m),
                };
            })
            .ToList();

        string psJson = JsonSerializer.Serialize(new { models = running }, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = psJson;

        log.Status = RequestStatus.Success;
        await WriteJsonRawAsync(resp, psJson, ct);
    }

    // /api/show → answered entirely from local mapping config, no upstream call

    private async Task HandleShowAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        OllamaShowRequest? showReq = JsonSerializer.Deserialize<OllamaShowRequest>(body, _jsonOptions);
        string requestedModel = showReq?.Model ?? showReq?.Name ?? string.Empty;
        ModelMapping? mapping = _settings.FindModelMapping(requestedModel);
        if (mapping is null && !string.IsNullOrWhiteSpace(requestedModel))
        {
            Log.Warning(
                "/api/show could not find a configured mapping for requested model {RequestedModel}. " +
                "Capabilities (including vision) will not reflect any per-mapping override.",
                requestedModel);
        }
        string modelName = mapping?.ModelName ?? _settings.ResolveModelName(requestedModel);
        log.Model = modelName;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, requestedModel);

        // /api/show asks the proxy what it has configured for a model — it isn't a
        // request the upstream needs to answer, and upstreams vary wildly in whether/how
        // they support a single-model lookup (some return 404, some 400, some nothing at
        // all). Building the response purely from the mapping avoids depending on any of
        // that and keeps model availability consistent with what /api/tags reports.
        var placeholderModel = new LlamaCppModel { Id = modelName };

        resp.StatusCode = mapping is not null ? 200 : 404;
        log.StatusCode = resp.StatusCode;

        var showResp = new OllamaShowResponse
        {
            Model = mapping?.ProxyName ?? modelName,
            Details = CreateOllamaModelDetails(placeholderModel, mapping),
            ModelInfo = CreateOllamaModelInfo(mapping, placeholderModel),
            Capabilities = BuildOllamaCapabilities(mapping),
        };

        string showJson = JsonSerializer.Serialize(showResp, _jsonOptions);
        if (_settings.CollectResponseDetails)
            log.ResponseBody = showJson;

        log.Status = mapping is not null ? RequestStatus.Success : RequestStatus.Error;
        await WriteJsonRawAsync(resp, showJson, ct);
    }

    private async Task<LlamaCppModel?> TryFindModelFromListAsync(
        string modelName,
        string baseUrl,
        int timeoutSeconds,
        string? apiKey,
        CancellationToken ct)
    {
        using var listReqMsg = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        ApplyApiKey(listReqMsg, apiKey);
        using HttpResponseMessage listResp = await SendUpstreamAsync(
            listReqMsg,
            baseUrl,
            timeoutSeconds,
            HttpCompletionOption.ResponseContentRead,
            ct);

        if (!listResp.IsSuccessStatusCode)
            return null;

        string body = await listResp.Content.ReadAsStringAsync(ct);
        LlamaCppModelsResponse? models = JsonSerializer.Deserialize<LlamaCppModelsResponse>(body, _jsonOptions);
        LlamaCppModel? model = models?.Data.FirstOrDefault(m =>
            string.Equals(m.Id, modelName, StringComparison.OrdinalIgnoreCase));

        return model ?? new LlamaCppModel { Id = modelName };
    }

    private static OllamaModelEntry CreateOllamaModelEntry(ModelMapping mapping)
    {
        string modelName = string.IsNullOrWhiteSpace(mapping.ProxyName) ? mapping.ModelName : mapping.ProxyName;

        return new OllamaModelEntry
        {
            Name = modelName,
            Model = modelName,
            ModifiedAt = DateTime.UtcNow.ToString("o"),
            Details = CreateOllamaModelDetails(new LlamaCppModel { Id = mapping.ModelName }, mapping),
            Capabilities = BuildOllamaCapabilities(mapping),
        };
    }

    private static OllamaModelDetails CreateOllamaModelDetails(LlamaCppModel model, ModelMapping? mapping = null)
    {
        string family = GetModelFamily(model.Id);

        return new OllamaModelDetails
        {
            Format = "openai-compatible",
            Family = family,
            Families = [family],
            ParameterSize = GetParameterSize(model.Id),
            QuantizationLevel = GetQuantizationLevel(model.Id),
            ContextLength = mapping?.GetEffectiveContextWindow() ?? model.ContextLength ?? 0,
        };
    }

    /// <summary>
    /// Returns the OpenAI-style capability tokens advertised for this model on the
    /// <c>/v1/models</c> discovery endpoint: exactly the tokens the operator checked in the
    /// Model Mapping dialog, returned in canonical order (deduped, known tokens only).
    /// Empty when no capabilities are configured.
    /// </summary>
    private static List<string>? BuildCapabilities(ModelMapping? mapping)
    {
        List<string> normalized = ModelCapabilities.Normalize(mapping?.Capabilities);
        return normalized.Count > 0 ? normalized : null; // Omit when empty — matches omitempty.
    }

    /// <summary>
    /// Maps the operator-configured capability tokens to the Ollama-native capability tokens
    /// that real Ollama emits on <c>/api/show</c> (e.g. <c>"completion"</c>, <c>"tools"</c>,
    /// <c>"vision"</c>). The proxy's internal <see cref="ModelCapabilities"/> tokens use
    /// OpenAI-style names (<c>"text"</c>, <c>"function_calling"</c>) which do not match the
    /// Ollama specification and cause Ollama clients (including Visual Studio) to misinterpret
    /// the model's capabilities.
    /// </summary>
    private static List<string>? BuildOllamaCapabilities(ModelMapping? mapping)
    {
        List<string> normalized = ModelCapabilities.Normalize(mapping?.Capabilities);
        if (normalized.Count == 0)
            return null; // Omit from JSON entirely — matches Ollama's Go omitempty behavior

        HashSet<string> ollamaTokens = new(StringComparer.OrdinalIgnoreCase);

        foreach (string token in normalized)
        {
            switch (token)
            {
                case "text":
                case "chat":
                case "code":
                    ollamaTokens.Add("completion");
                    break;
                case "function_calling":
                    ollamaTokens.Add("tools");
                    break;
                case "vision":
                    ollamaTokens.Add("vision");
                    break;
                case "embeddings":
                    ollamaTokens.Add("embedding");
                    break;
                case "reasoning":
                    ollamaTokens.Add("thinking");
                    break;
                case "image_generation":
                    ollamaTokens.Add("image");
                    break;
                case "audio":
                    ollamaTokens.Add("audio");
                    break;
            }
        }

        // Return in canonical Ollama order (matches model.Capability const order in Ollama source).
        List<string> ordered = [];
        foreach (string t in new[] { "completion", "tools", "insert", "vision", "embedding", "thinking", "image", "audio" })
        {
            if (ollamaTokens.Contains(t))
                ordered.Add(t);
        }

        return ordered.Count > 0 ? ordered : null;
    }

    private static Dictionary<string, object> CreateOllamaModelInfo(ModelMapping? mapping, LlamaCppModel? model)
    {
        string id = model?.Id ?? mapping?.ModelName ?? string.Empty;
        string family = GetModelFamily(id);
        int contextWindow = mapping?.GetEffectiveContextWindow() ?? ModelMapping.DefaultContextWindowTokens;

        Dictionary<string, object> modelInfo = new(StringComparer.OrdinalIgnoreCase)
        {
            ["general.architecture"] = family,
            ["general.basename"] = id,
            ["general.context_length"] = contextWindow,
            [$"{family}.context_length"] = contextWindow,
            ["proxy.upstream_type"] = mapping?.UpstreamType.ToDisplayName() ?? UpstreamType.OpenAI.ToDisplayName(),
        };

        if (!string.IsNullOrWhiteSpace(mapping?.ProxyName))
            modelInfo["proxy.name"] = mapping.ProxyName;

        if (!string.IsNullOrWhiteSpace(mapping?.UpstreamUrl))
            modelInfo["proxy.upstream_url"] = mapping.UpstreamUrl;

        if (!string.IsNullOrWhiteSpace(model?.OwnedBy))
            modelInfo["openai.owned_by"] = model.OwnedBy;

        if (model?.Created > 0)
            modelInfo["openai.created"] = model.Created;

        return modelInfo;
    }

    private static string GetModelFamily(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return "openai-compatible";

        string lowered = modelId.ToLowerInvariant();

        return lowered switch
        {
            string value when value.Contains("llama", StringComparison.Ordinal) => "llama",
            string value when value.Contains("mistral", StringComparison.Ordinal) => "mistral",
            string value when value.Contains("qwen", StringComparison.Ordinal) => "qwen",
            string value when value.Contains("phi", StringComparison.Ordinal) => "phi",
            string value when value.Contains("gemma", StringComparison.Ordinal) => "gemma",
            string value when value.Contains("deepseek", StringComparison.Ordinal) => "deepseek",
            string value when value.Contains("gpt", StringComparison.Ordinal) => "gpt",
            _ => modelId,
        };
    }

    private static string GetParameterSize(string modelId)
    {
        Match match = Regex.Match(modelId, @"(?<size>\d+(?:\.\d+)?)[bB](?![A-Za-z])");
        return match.Success ? $"{match.Groups["size"].Value}B" : string.Empty;
    }

    private static string GetQuantizationLevel(string modelId)
    {
        Match match = Regex.Match(modelId, @"(?<quant>q\d(?:_[a-z0-9]+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : string.Empty;
    }

    // ── /api/generate → POST /v1/completions ───────────────────────────────

    private async Task HandleGenerateAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaGenerateRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaGenerateRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        // Context-summarize (/compact) redirect: route the request to the mapping's configured
        // compact model when the system/prompt is a Copilot session-summary prompt.
        string? firstContent = !string.IsNullOrEmpty(ollamaReq.System) ? ollamaReq.System : ollamaReq.Prompt;
        string effectiveModel = ResolveEffectiveModel(_settings, ollamaReq.Model, firstContent);
        if (!string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase))
        {
            log.OriginalModel = ollamaReq.Model;
            Log.Debug(
                "Context-summarize (/compact) generate request for {OriginalModel} redirected to compact model {CompactModel}",
                ollamaReq.Model, effectiveModel);
        }

        string resolvedModel = _settings.ResolveModelName(effectiveModel);
        log.Model = resolvedModel;
        bool genDebug = _settings.DebugMode;
        StringBuilder? genDebugNotes = genDebug ? new StringBuilder() : null;
        if (genDebugNotes is not null && !string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase))
            genDebugNotes.AppendLine(DebugNotes.ContextSummarizeRedirect(ollamaReq.Model, effectiveModel));
        bool genMapped = !string.Equals(effectiveModel, resolvedModel, StringComparison.OrdinalIgnoreCase);
        if (genDebugNotes is not null)
            genDebugNotes.AppendLine(DebugNotes.ModelResolution(effectiveModel, resolvedModel, genMapped));
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (genBase, genTimeout, genApiKey) = ResolveUpstream(effectiveModel);

        // Build the prompt, optionally injecting custom instructions
        string prompt = ollamaReq.Prompt;
        string? systemPrefix = ollamaReq.System;

        // Inject custom instructions if configured for this model mapping
        ModelMapping? mapping = _settings.FindModelMapping(effectiveModel);
        if (mapping?.InstructionSetName is not null)
        {
            InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);
            if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            {
                // Prepend custom instructions to the system prompt
                systemPrefix = string.IsNullOrEmpty(systemPrefix)
                    ? instructionSet.Instructions
                    : $"{instructionSet.Instructions}\n\n{systemPrefix}";
            }
        }

        // Combine system and user prompt
        if (!string.IsNullOrEmpty(systemPrefix))
            prompt = $"{systemPrefix}\n\n{prompt}";

        var llamaReq = new LlamaCppCompletionRequest
        {
            Model = resolvedModel,
            Prompt = prompt,
            Stream = ollamaReq.Stream,
            ResponseFormat = ResolveResponseFormat(ollamaReq.Format),
            Temperature = ResolveSamplingValue(
                    mapping?.TemperaturePriority ?? SamplingPriority.ClientApp,
                    ollamaReq.Options?.Temperature,
                    (float)(mapping?.Temperature ?? 0.7)),
            TopP = ollamaReq.Options?.TopP,
            TopK = ollamaReq.Options?.TopK,
            MinP = ollamaReq.Options?.MinP,
            MaxTokens = ollamaReq.Options?.NumPredict,
            Stop = ollamaReq.Options?.Stop,
            Seed = ollamaReq.Options?.Seed,
            PresencePenalty = ollamaReq.Options?.PresencePenalty,
            FrequencyPenalty = ollamaReq.Options?.FrequencyPenalty,
            RepeatPenalty = ResolveSamplingValue(
                    mapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp,
                    ollamaReq.Options?.RepeatPenalty,
                    (float)(mapping?.RepeatPenalty ?? 1.0)),
        };

        string upstreamBody = JsonSerializer.Serialize(llamaReq, _jsonOptions);
        // Capture the upstream-bound (translated) body so proxy-injected values can be
        // compared against the client body in the request log.
        if (_settings.CollectRequestDetails)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, upstreamBody, effectiveModel);

        if (genDebugNotes is not null)
        {
            genDebugNotes.AppendLine(DebugNotes.UpstreamRouting(
                mapping?.ProxyName ?? effectiveModel, genBase, !string.IsNullOrWhiteSpace(genApiKey), genTimeout));
            log.DebugSummary = genDebugNotes.ToString().TrimEnd();
        }

        using StringContent genContent = new(upstreamBody, Encoding.UTF8, "application/json");
        using var genReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/completions") { Content = genContent };
        ApplyApiKey(genReqMsg, genApiKey);
        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            genReqMsg,
            genBase, genTimeout, HttpCompletionOption.ResponseHeadersRead, ct);

        log.StatusCode = (int)upstreamResp.StatusCode;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
            if (_settings.CollectResponseDetails && log.ResponseBody is null)
                log.ResponseBody = errorBody;
            resp.StatusCode = (int)upstreamResp.StatusCode;
            resp.Close();
            return;
        }

        if (ollamaReq.Stream)
        {
            resp.ContentType = "application/x-ndjson";
            resp.SendChunked = true;
            resp.KeepAlive = true; // Keep connection alive during long thinking periods
            await StreamCompletionToOllamaAsync(
                upstreamResp,
                resp,
                ollamaReq.Model,
                log,
                _settings.CollectResponseDetails,
                responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                sw,
                ct);
        }
        else
        {
            string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);
            string text = chunk?.Choices?.FirstOrDefault()?.Text ?? string.Empty;
            LlamaCppUsage? usage = chunk?.Usage;

            FillTokenStats(log, usage);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(text, ollamaReq.Model);

            long elapsedNs = ElapsedNanos(sw);
            var ollamaResp = new OllamaGenerateResponse
            {
                Model = ollamaReq.Model,
                Response = text,
                Done = true,
                DoneReason = "stop",
                TotalDuration = elapsedNs,
                LoadDuration = 0L,
                PromptEvalCount = usage?.PromptTokens,
                EvalCount = usage?.CompletionTokens,
                EvalDuration = elapsedNs,
            };

            await WriteJsonAsync(resp, ollamaResp, ct);
            log.Status = RequestStatus.Success;
        }
    }

    // ── /api/chat → POST /v1/chat/completions ──────────────────────────────

    private async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaChatRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaChatRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        // Context-summarize (/compact) redirect: route the request to the mapping's configured
        // compact model when the first message is a Copilot session-summary prompt.
        string effectiveModel = ResolveEffectiveModel(
            _settings, ollamaReq.Model,
            ollamaReq.Messages.Count > 0 ? ollamaReq.Messages[0].Content : null);

        if (!string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase))
            log.OriginalModel = ollamaReq.Model;

        string resolvedModel = _settings.ResolveModelName(effectiveModel);
        log.Model = resolvedModel;
        bool debug = _settings.DebugMode;
        StringBuilder? debugNotes = debug ? new StringBuilder() : null;
        if (debugNotes is not null && !string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase))
            debugNotes.Append(DebugNotes.ContextSummarizeRedirect(ollamaReq.Model, effectiveModel));
        bool mapped = !string.Equals(effectiveModel, resolvedModel, StringComparison.OrdinalIgnoreCase);
        if (debugNotes is not null)
            debugNotes.Append(DebugNotes.ModelResolution(effectiveModel, resolvedModel, mapped));
        // Capture the client (before) body when either the Collect flag or DebugMode is on.
        if (_settings.CollectRequestDetails || debug)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (chatBase, chatTimeout, chatApiKey) = ResolveUpstream(effectiveModel);

        ModelMapping? mapping = _settings.FindModelMapping(effectiveModel);

        // Map messages, preserving / synthesising tool_call IDs so OpenAI-compatible
        // upstreams can correlate assistant tool_calls with the following role:"tool" replies.
        List<LlamaCppMessage> messages = MapMessagesWithToolCorrelation(ollamaReq.Messages);
        bool removedAssistantPrefill = messages.Count > 0
            && ShouldApplyThinkingCompatibility(_settings, effectiveModel)
            && IsAssistantResponsePrefill(messages[^1]);
        if (removedAssistantPrefill)
        {
            messages.RemoveAt(messages.Count - 1);
            if (debugNotes is not null)
                debugNotes.AppendLine("messages: removed trailing assistant prefill (thinking compatibility)");
        }

        // Inject custom instructions if configured for this model mapping
        if (mapping?.InstructionSetName is not null)
        {
            InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);
            if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            {
                // Prepend system message with custom instructions
                messages.Insert(0, new LlamaCppMessage("system", instructionSet.Instructions));
                if (debugNotes is not null)
                    debugNotes.AppendLine(DebugNotes.InstructionInjection(instructionSet.Name));
            }
        }

        var llamaReq = new LlamaCppChatRequest
        {
            Model = resolvedModel,
            Messages = messages,
            Stream = ollamaReq.Stream,
            Tools = MapTools(ollamaReq.Tools),
            ResponseFormat = ResolveResponseFormat(ollamaReq.Format),
            Temperature = ResolveSamplingValue(
                mapping?.TemperaturePriority ?? SamplingPriority.ClientApp,
                ollamaReq.Options?.Temperature,
                (float)(mapping?.Temperature ?? 0.7)),
            TopP = ollamaReq.Options?.TopP,
            TopK = ollamaReq.Options?.TopK,
            MinP = ollamaReq.Options?.MinP,
            MaxTokens = ollamaReq.Options?.NumPredict,
            Stop = ollamaReq.Options?.Stop,
            Seed = ollamaReq.Options?.Seed,
            PresencePenalty = ollamaReq.Options?.PresencePenalty,
            FrequencyPenalty = ollamaReq.Options?.FrequencyPenalty,
            RepeatPenalty = ResolveSamplingValue(
                mapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp,
                ollamaReq.Options?.RepeatPenalty,
                (float)(mapping?.RepeatPenalty ?? 1.0)),
            Mirostat = ollamaReq.Options?.Mirostat,
            MirostatTau = ollamaReq.Options?.MirostatTau,
            MirostatEta = ollamaReq.Options?.MirostatEta,
            NCtx = ollamaReq.Options?.NumCtx,
        };

        // Apply the reasoning effort in the mapping's wire format (legacy, modern, both, or
        // Qwen Cloud): the client's `think` field under Client App priority, or the
        // mapping's configured value under Proxy priority.
        ApplyReasoningEffort(mapping, llamaReq, ollamaReq.Think);

        // Record every settings-driven override/transformation for the debug audit trail.
        if (debugNotes is not null)
        {
            debugNotes.AppendLine(DebugNotes.SamplingDecision(
                "temperature",
                mapping?.TemperaturePriority ?? SamplingPriority.ClientApp,
                ollamaReq.Options?.Temperature,
                (float)(mapping?.Temperature ?? 0.7)));
            debugNotes.AppendLine(DebugNotes.SamplingDecision(
                "repeat_penalty",
                mapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp,
                ollamaReq.Options?.RepeatPenalty,
                (float)(mapping?.RepeatPenalty ?? 1.0)));
            debugNotes.AppendLine(DebugNotes.ReasoningEffortDecision(
                mapping?.ReasoningEffortPriority ?? SamplingPriority.ClientApp,
                MapThinkToReasoningEffort(ollamaReq.Think),
                mapping?.ReasoningEffort?.Trim().ToLowerInvariant(),
                mapping?.ReasoningEffortFormat ?? ReasoningEffortFormat.Legacy));
            if (ollamaReq.Tools is not null && ollamaReq.Tools.Count > 0)
                debugNotes.AppendLine($"tools: mapped {ollamaReq.Tools.Count} tool definition(s)");
            if (ollamaReq.Format is not null)
                debugNotes.AppendLine($"response_format: resolved from client \"format\" ({FormatDescriptor(ollamaReq.Format)})");
            debugNotes.AppendLine(DebugNotes.UpstreamRouting(
                mapping?.ProxyName ?? effectiveModel, chatBase, !string.IsNullOrWhiteSpace(chatApiKey), chatTimeout));
            log.DebugSummary = debugNotes.ToString().TrimEnd();
        }

        string upstreamBody = JsonSerializer.Serialize(llamaReq, _jsonOptions);
        // Capture the upstream-bound (translated) body so proxy-injected values such as
        // reasoning_effort can be compared against the client body in the request log.
        if (_settings.CollectRequestDetails || debug)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, upstreamBody, effectiveModel);

        // Proactive context-overflow check: if the estimated token count exceeds the mapping's
        // configured threshold, return 413 immediately so clients compact before we pay for an
        // upstream round-trip that is guaranteed to overflow. Skip proactive auto-compaction for
        // Copilot requests when EnableCopilotNativeCompaction is enabled, so Copilot's native
        // /compact flow manages session state.
        string? firstMsgContent = ollamaReq.Messages.Count > 0 ? ollamaReq.Messages[0].Content : null;
        bool shouldSkipAutoCompaction = false;
        if (_settings.EnableCopilotNativeCompaction && IsCopilotRequest(req, firstMsgContent))
        {
            shouldSkipAutoCompaction = true;
            Log.Debug("Skipping proactive auto-compaction for Copilot request (Ollama path)");
        }

        if (!shouldSkipAutoCompaction && _settings.EnableAutoCompaction)
        {
            // For Ollama path, pass null for output stream (streaming notifications not yet implemented for this path)
            var (overflow, compactedBody) = await TryProactiveOverflowAsync(mapping, upstreamBody, effectiveModel, resp, log, AutoCompactPaths.Ollama, null, ct);
            if (overflow)
                return;
            if (compactedBody is not null)
                upstreamBody = compactedBody;
        }

        using StringContent chatContent = new(upstreamBody, Encoding.UTF8, "application/json");
        using var chatReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = chatContent };
        ApplyApiKey(chatReqMsg, chatApiKey);
        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            chatReqMsg,
            chatBase, chatTimeout, HttpCompletionOption.ResponseHeadersRead, ct);

        log.StatusCode = (int)upstreamResp.StatusCode;

        // Check for context overflow error
        (bool isContextOverflow, string errorBody) = await IsContextOverflowErrorAsync(upstreamResp, ct);

        if (!upstreamResp.IsSuccessStatusCode)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = isContextOverflow
                ? $"Upstream {(int)upstreamResp.StatusCode}: Context overflow"
                : $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
            if (_settings.CollectResponseDetails)
                log.ResponseBody = errorBody;
            // Debug mode captures the raw upstream response body independently of the Collect flags.
            if (_settings.DebugMode)
                log.UpstreamResponseBody = RedactResponseBodyForLog(errorBody, ollamaReq.Model);

            // For context overflow, return 413 so clients like Copilot recognize this as a
            // context limit error and can trigger their own compaction (ContextLimitRetry).
            resp.StatusCode = isContextOverflow ? 413 : (int)upstreamResp.StatusCode;
            resp.Close();
            return;
        }

        if (ollamaReq.Stream)
        {
            resp.ContentType = "application/x-ndjson";
            resp.SendChunked = true;
            resp.KeepAlive = true; // Keep connection alive during long thinking periods

            await StreamChatToOllamaAsync(
                upstreamResp,
                resp,
                ollamaReq.Model,
                log,
                _settings.CollectResponseDetails,
                responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                ShouldEmitHeartbeats(ollamaReq.Model),
                _settings.StreamingHeartbeatIntervalSeconds,
                mapping?.ThinkingMode ?? ThinkingMode.LeaveInline,
                sw,
                ct,
                () => _stats.IncrementHeartbeat(ollamaReq.Model),
                collectRawUpstream: _settings.DebugMode);
        }
        else
        {
            string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            // Debug mode captures the raw OpenAI upstream response body (the "before" of the
            // response translation) independently of the Collect flags.
            if (_settings.DebugMode)
                log.UpstreamResponseBody = RedactResponseBodyForLog(respBody, ollamaReq.Model);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);

            // Non-streaming: prefer .message over .delta (OpenAI non-streaming uses message)
            LlamaCppChoice? firstChoice = chunk?.Choices?.FirstOrDefault();
            LlamaCppDelta? delta = firstChoice?.Message ?? firstChoice?.Delta;
            string? upstreamFinishReason = firstChoice?.FinishReason;
            FillTokenStats(log, chunk);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

            List<OllamaToolCall>? toolCalls = MapToolCallsToOllama(delta?.ToolCalls);

            // The upstream's thinking/reasoning trace is emitted in the Ollama-native
            // message.thinking field rather than inlined into content. The native
            // reasoning_content field is always surfaced (unless the mapping strips thinking);
            // in addition, inline think blocks left inside content are extracted per the
            // mapping's ThinkingMode so Ollama clients can render a dedicated thinking section
            // even when the upstream inlines its reasoning instead of using reasoning_content.
            string? content = delta?.Content;
            string? nativeThinking = mapping?.ThinkingMode == ThinkingMode.StripFromOutput
                ? null
                : delta?.ReasoningContent;
            ThinkingMode thinkingMode = mapping?.ThinkingMode ?? ThinkingMode.LeaveInline;

            string? extractedThinking = null;
            if (thinkingMode != ThinkingMode.StripFromOutput
                && thinkingMode != ThinkingMode.LeaveInline
                && !string.IsNullOrEmpty(content))
            {
                (string openTag, string closeTag) = ThinkTagExtractor.TagsFor(thinkingMode);
                (string reasoning, string answer) = ThinkTagExtractor.ExtractAll(content, openTag, closeTag);
                content = answer;
                extractedThinking = reasoning.Length > 0 ? reasoning : null;
            }

            string? thinking = !string.IsNullOrEmpty(extractedThinking)
                ? (nativeThinking is null ? extractedThinking : nativeThinking + extractedThinking)
                : nativeThinking;

            ToolCallExtraction toolCallExtraction = ExtractXmlToolCalls(content);
            if (toolCalls is null)
                toolCalls = toolCallExtraction.ToolCalls;

            content = toolCallExtraction.Content;

            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(content ?? string.Empty, ollamaReq.Model);

            long elapsedNs = ElapsedNanos(sw);
            var ollamaResp = new OllamaChatResponse
            {
                Model = ollamaReq.Model,
                Message = new OllamaMessage("assistant", content) { ToolCalls = toolCalls, Thinking = thinking },
                Done = true,
                DoneReason = toolCalls?.Count > 0 ? "tool_calls" : upstreamFinishReason ?? "stop",
                TotalDuration = elapsedNs,
                LoadDuration = 0L,
                PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                EvalDuration = elapsedNs,
            };

            await WriteJsonAsync(resp, ollamaResp, ct);
            log.Status = RequestStatus.Success;
        }

        return;
    }

    // ── /api/embeddings → POST /v1/embeddings ──────────────────────────────

    private async Task HandleEmbeddingsAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        OllamaEmbeddingsRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaEmbeddingsRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, ollamaReq.Model);
        var (embedBase, embedTimeout, embedApiKey) = ResolveUpstream(ollamaReq.Model);

        // Resolve input: prefer new `input` (string or string[]), fall back to legacy `prompt`.
        object resolvedInput = ResolveEmbeddingInput(ollamaReq);
        bool isBatch = resolvedInput is string[] batch && batch.Length > 1;

        var llamaReq = new LlamaCppEmbeddingsRequest { Model = resolvedModel, Input = resolvedInput };

        string upstreamBody = JsonSerializer.Serialize(llamaReq, _jsonOptions);
        // Capture the upstream-bound (translated) body for the request log.
        if (_settings.CollectRequestDetails)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, upstreamBody, ollamaReq.Model);

        using StringContent embedContent = new(upstreamBody, Encoding.UTF8, "application/json");
        using var embedReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings") { Content = embedContent };
        ApplyApiKey(embedReqMsg, embedApiKey);
        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(embedReqMsg, embedBase, embedTimeout, HttpCompletionOption.ResponseContentRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
        LlamaCppEmbeddingsResponse? llamaResp = JsonSerializer.Deserialize<LlamaCppEmbeddingsResponse>(respBody, _jsonOptions);

        OllamaEmbeddingsResponse ollamaResp = isBatch
            ? new OllamaEmbeddingsResponse
            {
                Model = ollamaReq.Model,
                Embeddings = [.. (llamaResp?.Data ?? []).Select(d => d.Embedding)],
                PromptEvalCount = llamaResp?.Usage?.PromptTokens,
            }
            : new OllamaEmbeddingsResponse
            {
                Model = ollamaReq.Model,
                Embedding = llamaResp?.Data?.FirstOrDefault()?.Embedding ?? [],
                PromptEvalCount = llamaResp?.Usage?.PromptTokens,
            };

        FillTokenStats(log, llamaResp?.Usage);

        log.Status = upstreamResp.IsSuccessStatusCode ? RequestStatus.Success : RequestStatus.Error;
        if (_settings.CollectResponseDetails)
        {
            // Embedding vectors are huge; the per-mapping redaction (marker by default) keeps
            // them out of the database unless the user explicitly opts in per model.
            log.ResponseBody = upstreamResp.IsSuccessStatusCode
                ? RedactResponseBodyForLog(JsonSerializer.Serialize(ollamaResp, _jsonOptions), ollamaReq.Model)
                : respBody;
        }

        await WriteJsonAsync(resp, ollamaResp, ct);
    }

    // ── Streaming helpers ───────────────────────────────────────────────────

    /// <summary>Elapsed stopwatch time in nanoseconds — Ollama's duration unit.</summary>
    private static long ElapsedNanos(Stopwatch sw) => (long)(sw.Elapsed.TotalSeconds * 1_000_000_000);

    private static async Task StreamCompletionToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        Func<string, string> redactResponse,
        Stopwatch sw,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        using PooledCharBuffer? responseAccumulator = collectResponse ? new PooledCharBuffer() : null;
        bool reachedDone = false;
        bool terminalChunkSent = false;
        long responseBytes = 0;

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            // SSE format: "data: {...}" or "data: [DONE]"
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                line = line[6..];

            if (line == "[DONE]")
            {
                reachedDone = true;
                if (terminalChunkSent) break;

                long elapsedNs = ElapsedNanos(sw);
                var doneChunk = new OllamaGenerateResponse
                {
                    Model = modelName,
                    Response = string.Empty,
                    Done = true,
                    DoneReason = "stop",
                    TotalDuration = elapsedNs,
                    LoadDuration = 0L,
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                    EvalDuration = elapsedNs,
                };
                string doneJson = JsonSerializer.Serialize(doneChunk, _jsonOptions);
                responseBytes += Encoding.UTF8.GetByteCount(doneJson);
                await writer.WriteLineAsync(doneJson);
                await writer.FlushAsync(ct);
                break;
            }

            LlamaCppStreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(line, _jsonOptions); }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipping unparseable streaming completion chunk");
                continue;
            }

            if (chunk is null) continue;

            FillTokenStats(log, chunk);

            LlamaCppChoice? choice = chunk.Choices?.FirstOrDefault();
            string token = choice?.Text ?? string.Empty;
            bool done = choice?.FinishReason != null;

            responseAccumulator?.Append(token);

            var ollamaChunk = new OllamaGenerateResponse
            {
                Model = modelName,
                Response = token,
                Done = done,
                DoneReason = done ? choice?.FinishReason ?? "stop" : null,
                PromptEvalCount = done && log.PromptTokens > 0 ? log.PromptTokens : null,
                EvalCount = done && log.CompletionTokens > 0 ? log.CompletionTokens : null,
            };

            if (done) terminalChunkSent = true;

            string chunkJson = JsonSerializer.Serialize(ollamaChunk, _jsonOptions);
            responseBytes += Encoding.UTF8.GetByteCount(chunkJson);
            await writer.WriteLineAsync(chunkJson);
            await writer.FlushAsync(ct);
        }

        if (responseAccumulator is not null)
            log.ResponseBody = redactResponse(responseAccumulator.ToString());

        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = ct.IsCancellationRequested && !reachedDone
            ? RequestStatus.Cancelled
            : RequestStatus.Success;
    }

    private static async Task StreamChatToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        Func<string, string> redactResponse,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        ThinkingMode thinkingMode,
        Stopwatch sw,
        CancellationToken ct,
        Action? onHeartbeatSent = null,
        bool collectRawUpstream = false)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        using PooledCharBuffer? responseAccumulator = collectResponse ? new PooledCharBuffer() : null;
        // When debug capture is on, accumulate the raw upstream (OpenAI) SSE data lines so the
        // "before" of the response translation is visible alongside the Ollama "after".
        using PooledCharBuffer? rawUpstreamAccumulator = collectRawUpstream ? new PooledCharBuffer() : null;
        bool reachedDone = false;
        bool terminalChunkSent = false;
        long responseBytes = 0;
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));
        Dictionary<int, StreamingToolCallBuilder> toolCallBuilders = [];
        StringBuilder xmlToolCallBuilder = new();
        bool capturingXmlToolCall = false;

        // When the mapping moves or strips inline think blocks (anything other than LeaveInline
        // / StripFromOutput), run a stateful incremental extractor so a  tag split across
        // upstream chunks is still recognised. StripFromOutput drops the text instead of
        // re-emitting it as message.thinking.
        ThinkTagExtractor? inlineExtractor = null;
        if (thinkingMode != ThinkingMode.LeaveInline && thinkingMode != ThinkingMode.StripFromOutput)
        {
            (string open, string close) = ThinkTagExtractor.TagsFor(thinkingMode);
            inlineExtractor = new ThinkTagExtractor(open, close);
        }
        bool emitThinking = thinkingMode != ThinkingMode.StripFromOutput;

        while (!ct.IsCancellationRequested)
        {
            string? line = await ReadLineWithOllamaChatHeartbeatsAsync(
                reader,
                writer,
                modelName,
                enableHeartbeats,
                heartbeatInterval,
                ct,
                onHeartbeatSent);
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
                line = line[6..];

            // Capture the raw OpenAI upstream chunk (the "before" of the response translation).
            rawUpstreamAccumulator?.Append(line);
            rawUpstreamAccumulator?.Append('\n');

            if (line == "[DONE]")
            {
                reachedDone = true;
                if (terminalChunkSent)
                    break;

                long elapsedNs = ElapsedNanos(sw);
                var doneChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", string.Empty),
                    Done = true,
                    DoneReason = "stop",
                    TotalDuration = elapsedNs,
                    LoadDuration = 0L,
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                    EvalDuration = elapsedNs,
                };
                string doneJson = JsonSerializer.Serialize(doneChunk, _jsonOptions);
                responseBytes += Encoding.UTF8.GetByteCount(doneJson);
                await writer.WriteLineAsync(doneJson);
                await writer.FlushAsync(ct);
                break;
            }

            LlamaCppStreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(line, _jsonOptions); }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipping unparseable streaming chat chunk");
                continue;
            }

            if (chunk is null) continue;

            FillTokenStats(log, chunk);

            LlamaCppChoice? choice = chunk.Choices?.FirstOrDefault();
            LlamaCppDelta? delta = choice?.Delta;
            // The upstream's thinking/reasoning trace streams into the Ollama-native
            // message.thinking field, kept separate from message.content, unless the mapping
            // strips thinking from the client-facing output (StripFromOutput mode). The native
            // reasoning_content field is always surfaced when present; additionally, inline
            // think blocks are separated per the mapping's ThinkingMode so Ollama clients can
            // render a dedicated thinking section even when the upstream inlines its reasoning.
            string? rawContent = delta?.Content;
            string? nativeThinking = emitThinking && !string.IsNullOrEmpty(delta?.ReasoningContent)
                ? delta.ReasoningContent
                : null;

            string? extractedThinking = null;
            string token = rawContent ?? string.Empty;
            if (inlineExtractor is not null)
            {
                (string reasoning, string content) = inlineExtractor.Process(token);
                token = content;
                extractedThinking = reasoning.Length > 0 ? reasoning : null;
            }

            string? thinking = !string.IsNullOrEmpty(extractedThinking)
                ? (nativeThinking is null ? extractedThinking : nativeThinking + extractedThinking)
                : nativeThinking;
            AppendStreamingToolCalls(toolCallBuilders, delta?.ToolCalls);
            token = CaptureXmlToolCallToken(token, xmlToolCallBuilder, ref capturingXmlToolCall);
            bool done = choice?.FinishReason != null;
            // Some OpenAI-compatible upstreams (notably llama.cpp) emit finish_reason="stop"
            // even when tool_calls are present in the delta. Flush any accumulated tool_calls
            // on the terminal chunk regardless of the reported finish_reason.
            List<OllamaToolCall>? toolCalls = done && toolCallBuilders.Count > 0
                ? BuildOllamaToolCalls(toolCallBuilders)
                : null;

            if (done && toolCalls is null)
                toolCalls = ExtractXmlToolCalls(xmlToolCallBuilder.ToString()).ToolCalls;

            if (done && toolCalls is not null)
                token = string.Empty;

            responseAccumulator?.Append(token);

            long? terminalNs = done ? ElapsedNanos(sw) : null;
            var ollamaChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", token) { ToolCalls = toolCalls, Thinking = thinking },
                Done = done,
                DoneReason = done ? (toolCalls is not null ? "tool_calls" : choice?.FinishReason ?? "stop") : null,
                TotalDuration = terminalNs,
                LoadDuration = done ? 0L : null,
                PromptEvalCount = done && log.PromptTokens > 0 ? log.PromptTokens : null,
                EvalCount = done && log.CompletionTokens > 0 ? log.CompletionTokens : null,
                EvalDuration = terminalNs,
            };

            if (done)
                terminalChunkSent = true;

            string chunkJson = JsonSerializer.Serialize(ollamaChunk, _jsonOptions);
            responseBytes += Encoding.UTF8.GetByteCount(chunkJson);
            await writer.WriteLineAsync(chunkJson);
            await writer.FlushAsync(ct);
        }

        // Drain any text the extractor held back because the stream ended inside an unterminated
        // think block. Emit a final chunk carrying it (as message.thinking, or as content when the
        // stream closed in answer mode) so the tail of the reasoning trace is not lost. Only when
        // no terminal (finish_reason) chunk was already sent, to avoid a duplicate done marker.
        if (inlineExtractor is not null && !terminalChunkSent)
        {
            (string tailReasoning, string tailAnswer) = inlineExtractor.Flush();
            string? tailThinking = tailReasoning.Length > 0 ? tailReasoning : null;
            string tailToken = tailAnswer.Length > 0 ? tailAnswer : string.Empty;
            if (tailThinking is not null || tailToken.Length > 0)
            {
                responseAccumulator?.Append(tailToken);
                long tailNs = ElapsedNanos(sw);
                var tailChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", tailToken) { Thinking = tailThinking },
                    Done = true,
                    DoneReason = "stop",
                    TotalDuration = tailNs,
                    LoadDuration = 0L,
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                    EvalDuration = tailNs,
                };
                string tailJson = JsonSerializer.Serialize(tailChunk, _jsonOptions);
                responseBytes += Encoding.UTF8.GetByteCount(tailJson);
                await writer.WriteLineAsync(tailJson);
                await writer.FlushAsync(ct);
            }
        }

        if (responseAccumulator is not null)
            log.ResponseBody = redactResponse(responseAccumulator.ToString());

        if (rawUpstreamAccumulator is not null && rawUpstreamAccumulator.Length > 0)
            log.UpstreamResponseBody = redactResponse(rawUpstreamAccumulator.ToString());

        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = ct.IsCancellationRequested && !reachedDone
            ? RequestStatus.Cancelled
            : RequestStatus.Success;
    }

    private static async Task<string?> ReadLineWithOllamaChatHeartbeatsAsync(
        StreamReader reader,
        StreamWriter writer,
        string modelName,
        bool enableHeartbeats,
        TimeSpan heartbeatInterval,
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

        while (enableHeartbeats && !readTask.IsCompleted)
        {
            Task delayTask = Task.Delay(heartbeatInterval, ct);
            Task completed = await Task.WhenAny(readTask, delayTask);
            if (completed == readTask)
                break;

            var heartbeatChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", string.Empty),
                Done = false,
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(heartbeatChunk, _jsonOptions));
            await writer.FlushAsync(ct);
            onHeartbeatSent?.Invoke();
        }

        return await readTask;
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Converts Ollama's <c>format</c> field to an OpenAI <c>response_format</c> object.
    /// Ollama accepts:
    ///   • the literal string "json"  → OpenAI {"type":"json_object"}
    ///   • a full JSON Schema object  → OpenAI {"type":"json_schema","json_schema":{...}}
    ///   • an OpenAI-style object     → forwarded as-is
    /// </summary>
    private static LlamaCppResponseFormat? ResolveResponseFormat(object? format)
    {
        if (format is null) return null;
        if (format is not JsonElement je) return null;

        if (je.ValueKind == JsonValueKind.String)
        {
            string? s = je.GetString();
            return string.Equals(s, "json", StringComparison.OrdinalIgnoreCase)
                ? new LlamaCppResponseFormat { Type = "json_object" }
                : null;
        }
        if (je.ValueKind != JsonValueKind.Object)
            return null;

        // OpenAI-style passthrough.
        if (je.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
        {
            string type = t.GetString() ?? "text";
            object? schema = je.TryGetProperty("json_schema", out JsonElement js)
                ? JsonSerializer.Deserialize<object>(js.GetRawText(), _jsonOptions)
                : null;
            return new LlamaCppResponseFormat { Type = type, JsonSchema = schema };
        }

        // Ollama JSON-Schema (object with no "type"): wrap as OpenAI json_schema.
        object? raw = JsonSerializer.Deserialize<object>(je.GetRawText(), _jsonOptions);
        return new LlamaCppResponseFormat
        {
            Type = "json_schema",
            JsonSchema = new Dictionary<string, object?>
            {
                ["name"] = "ollama_format",
                ["strict"] = true,
                ["schema"] = raw,
            },
        };
    }

    /// <summary>
    /// Returns a short human-readable description of an Ollama <c>format</c> value for the
    /// debug audit trail. Accepts the Ollama <c>"json"</c> string, an OpenAI-style object with
    /// a <c>type</c>, or a bare JSON schema object.
    /// </summary>
    private static string FormatDescriptor(object? format)
    {
        if (format is not JsonElement je)
            return format is null ? "none" : format.GetType().Name;

        if (je.ValueKind == JsonValueKind.String)
            return $"\"{je.GetString()}\"";

        if (je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "object";
            return "json_schema (bare)";
        }

        return je.ValueKind.ToString();
    }

    private static LlamaCppMessage MapMessage(OllamaMessage m) =>
        new(m.Role, m.Content)
        {
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls is not null
                ? [.. m.ToolCalls.Select(tc => new LlamaCppToolCall
                    {
                        Id = string.IsNullOrWhiteSpace(tc.Id) ? Guid.NewGuid().ToString("N")[..8] : tc.Id!,
                        Function = tc.Function is null ? null : new LlamaCppToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments switch
                            {
                                null => null,
                                string s => s,
                                _ => JsonSerializer.Serialize(tc.Function.Arguments, _jsonOptions),
                            },
                        },
                    })]
                : null,
        };

    /// <summary>
    /// Maps Ollama messages to OpenAI/llama.cpp messages and rewrites tool_call IDs so that:
    ///   • each assistant tool_call gets a stable id (preserved if supplied, generated otherwise),
    ///   • each following role:"tool" reply that lacks an id is correlated to the most recent
    ///     unfulfilled assistant tool_call (by order, or by function name when available).
    /// OpenAI-compatible upstreams reject tool replies whose tool_call_id doesn't match.
    /// </summary>
    private static List<LlamaCppMessage> MapMessagesWithToolCorrelation(List<OllamaMessage> source)
    {
        List<LlamaCppMessage> mapped = [.. source.Select(MapMessage)];
        // Queue of (id, function name) per pending assistant tool_call.
        Queue<(string Id, string? Name)> pending = new();

        for (int i = 0; i < mapped.Count; i++)
        {
            LlamaCppMessage msg = mapped[i];

            if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && msg.ToolCalls is { Count: > 0 })
            {
                foreach (LlamaCppToolCall tc in msg.ToolCalls)
                    pending.Enqueue((tc.Id, tc.Function?.Name));
            }
            else if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(msg.ToolCallId)
                && pending.Count > 0)
            {
                msg.ToolCallId = pending.Dequeue().Id;
            }
        }

        return mapped;
    }

    private static List<LlamaCppTool>? MapTools(List<OllamaTool>? tools) =>
        tools is null ? null
        : [.. tools.Select(t => new LlamaCppTool
            {
                Type = t.Type,
                Function = t.Function is null ? null : new LlamaCppToolFunction
                {
                    Name = t.Function.Name,
                    Description = t.Function.Description,
                    Parameters = t.Function.Parameters,
                },
            })];

    private static string CaptureXmlToolCallToken(string token, StringBuilder toolCallBuilder, ref bool isCapturing)
    {
        if (string.IsNullOrEmpty(token))
            return token;

        if (isCapturing)
        {
            toolCallBuilder.Append(token);
            if (toolCallBuilder.ToString().Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
                isCapturing = false;

            return string.Empty;
        }

        int startIndex = token.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return token;

        string visibleContent = token[..startIndex];
        toolCallBuilder.Append(token[startIndex..]);
        if (!toolCallBuilder.ToString().Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
            isCapturing = true;

        return visibleContent;
    }

    private static ToolCallExtraction ExtractXmlToolCalls(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new(content, null);

        MatchCollection matches = Regex.Matches(
            content,
            @"<tool_call>\s*<function=(?<name>[^>\s]+)>\s*(?<body>.*?)\s*</function>\s*</tool_call>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (matches.Count == 0)
            return new(content, null);

        List<OllamaToolCall> toolCalls = [];
        foreach (Match match in matches)
        {
            string name = match.Groups["name"].Value.Trim();
            string body = match.Groups["body"].Value;
            Dictionary<string, object?> arguments = new(StringComparer.OrdinalIgnoreCase);

            foreach (Match parameterMatch in Regex.Matches(
                body,
                @"<parameter=(?<name>[^>\s]+)>\s*(?<value>.*?)\s*</parameter>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string parameterName = parameterMatch.Groups["name"].Value.Trim();
                string parameterValue = parameterMatch.Groups["value"].Value.Trim();
                arguments[parameterName] = ParseXmlToolParameterValue(parameterValue);
            }

            toolCalls.Add(new OllamaToolCall
            {
                Function = new OllamaToolCallFunction
                {
                    Name = name,
                    Arguments = arguments,
                },
            });
        }

        string strippedContent = matches.Aggregate(content, static (current, match) => current.Replace(match.Value, string.Empty)).Trim();
        return new(strippedContent, toolCalls);
    }

    private static object? ParseXmlToolParameterValue(string value)
    {
        if (bool.TryParse(value, out bool booleanValue))
            return booleanValue;

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int integerValue))
            return integerValue;

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
            return doubleValue;

        return value;
    }

    private static List<OllamaToolCall>? MapToolCallsToOllama(List<LlamaCppToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return null;
        return [.. toolCalls.Select(tc =>
        {
            object? args = null;
            if (tc.Function?.Arguments is not null)
            {
                try { args = JsonSerializer.Deserialize<object>(tc.Function.Arguments, _jsonOptions); }
                catch { args = tc.Function.Arguments; }
            }
            return new OllamaToolCall
            {
                Id = string.IsNullOrWhiteSpace(tc.Id) ? null : tc.Id,
                Function = new OllamaToolCallFunction { Name = tc.Function?.Name ?? string.Empty, Arguments = args },
            };
        })];
    }

    private static void AppendStreamingToolCalls(
        Dictionary<int, StreamingToolCallBuilder> builders,
        List<LlamaCppToolCall>? toolCalls)
    {
        if (toolCalls is null)
            return;

        for (int i = 0; i < toolCalls.Count; i++)
        {
            LlamaCppToolCall toolCall = toolCalls[i];
            int index = toolCall.Index ?? i;
            if (!builders.TryGetValue(index, out StreamingToolCallBuilder? builder))
            {
                builder = new StreamingToolCallBuilder();
                builders[index] = builder;
            }

            if (!string.IsNullOrWhiteSpace(toolCall.Id))
                builder.Id = toolCall.Id;

            if (!string.IsNullOrWhiteSpace(toolCall.Function?.Name))
                builder.Name = toolCall.Function.Name;

            if (toolCall.Function?.Arguments is not null)
                builder.Arguments.Append(toolCall.Function.Arguments);
        }
    }

    private static List<OllamaToolCall>? BuildOllamaToolCalls(Dictionary<int, StreamingToolCallBuilder> builders)
    {
        if (builders.Count == 0)
            return null;

        return [.. builders
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                string arguments = pair.Value.Arguments.ToString();
                object? parsedArguments = null;
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    try { parsedArguments = JsonSerializer.Deserialize<object>(arguments, _jsonOptions); }
                    catch { parsedArguments = arguments; }
                }

                return new OllamaToolCall
                {
                    Id = string.IsNullOrWhiteSpace(pair.Value.Id) ? null : pair.Value.Id,
                    Function = new OllamaToolCallFunction
                    {
                        Name = pair.Value.Name,
                        Arguments = parsedArguments,
                    },
                };
            })];
    }

    private sealed class StreamingToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }

    private sealed record ToolCallExtraction(string? Content, List<OllamaToolCall>? ToolCalls);

    private static object ResolveEmbeddingInput(OllamaEmbeddingsRequest req)
    {
        if (req.Input is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            if (je.ValueKind == JsonValueKind.String)
                return je.GetString() ?? string.Empty;
        }
        if (req.Input is string s && !string.IsNullOrEmpty(s))
            return s;
        return req.Prompt ?? string.Empty;
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the request body as a string, enforcing the configured
    /// <see cref="AppSettings.MaxRequestBodyBytes"/> limit. Rejecting oversized bodies before they
    /// are fully buffered protects the proxy from memory-exhaustion (DoS) attacks.
    /// </summary>
    /// <exception cref="RequestBodyTooLargeException">Thrown when the body exceeds the limit.</exception>
    private async Task<string> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        long limit = _settings.MaxRequestBodyBytes;

        // Fast path: a declared Content-Length over the limit is rejected without reading the body.
        if (req.ContentLength64 > limit)
            throw new RequestBodyTooLargeException(limit, req.ContentLength64);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await req.InputStream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
                throw new RequestBodyTooLargeException(limit, total);

            buffer.Write(chunk, 0, read);
        }

        return req.ContentEncoding.GetString(buffer.ToArray());
    }

    /// <summary>Indicates a request body exceeded <see cref="AppSettings.MaxRequestBodyBytes"/>.</summary>
    private sealed class RequestBodyTooLargeException(long limit, long actual)
        : Exception($"Request body of {actual} bytes exceeds the configured maximum of {limit} bytes.")
    {
        public long Limit { get; } = limit;
        public long Actual { get; } = actual;
    }

    /// <summary>
    /// Deserializes a JSON request body into <typeparamref name="T"/>. When the body is missing,
    /// malformed, or does not match the expected shape, writes a 400 Bad Request response, marks
    /// the log as an error, and returns null so the caller can simply return.
    /// </summary>
    private async Task<T?> TryDeserializeRequestAsync<T>(string body, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
        where T : class
    {
        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(body, _jsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Rejected malformed JSON request body");
            result = null;
        }

        if (result is null)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = "Invalid or malformed request body.";
            resp.StatusCode = 400;
            await WriteJsonAsync(resp, new { error = "Invalid or malformed request body." }, ct);
        }

        return result;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object value, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(value, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    private static async Task WriteJsonRawAsync(HttpListenerResponse resp, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, string html, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    // ── API Explorer (Scalar) / OpenAPI ─────────────────────────────────────

    // Short-lived client used only to fetch OpenAPI documents reported by loaded modules when
    // rendering the explorer page. Only module-reported URLs are ever fetched (never user input).
    private static readonly HttpClient _explorerSpecClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string ApiExplorerHtmlTemplate = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Kaeo LLM Proxy — API Explorer</title>
            <style>
                body { margin: 0; padding: 0; }
                #kaeo-doc-selector {
                    position: fixed;
                    top: 10px;
                    right: 14px;
                    z-index: 10000;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                }
                #kaeo-doc-select { padding: 4px 8px; }
            </style>
        </head>
        <body>
            <div id="kaeo-doc-selector" hidden>
                <select id="kaeo-doc-select" aria-label="API document"></select>
            </div>
            <div id="kaeo-api-reference"></div>
            <script>
                var kaeoDocuments = /*DOCUMENTS*/[];
            </script>
            <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference@1"></script>
            <script>
                (function () {
                    var mount = document.getElementById('kaeo-api-reference');
                    var select = document.getElementById('kaeo-doc-select');
                    var selector = document.getElementById('kaeo-doc-selector');
                    var instance = null;

                    function configurationFor(doc) {
                        return { spec: { content: doc.spec } };
                    }

                    function loadDocument(index) {
                        var config = configurationFor(kaeoDocuments[index]);
                        if (instance && typeof instance.updateConfig === 'function') {
                            instance.updateConfig(config);
                            return;
                        }
                        mount.innerHTML = '';
                        instance = Scalar.createApiReference(mount, config);
                    }

                    kaeoDocuments.forEach(function (doc, i) {
                        var option = document.createElement('option');
                        option.value = String(i);
                        option.textContent = doc.label;
                        select.appendChild(option);
                    });

                    if (kaeoDocuments.length > 1) {
                        selector.hidden = false;
                        select.addEventListener('change', function () {
                            loadDocument(Number(select.value));
                        });
                    }

                    if (kaeoDocuments.length > 0) loadDocument(0);
                })();
            </script>
        </body>
        </html>
        """;

    /// <summary>
    /// Builds the Scalar explorer page at render time. The proxy's own OpenAPI document is
    /// embedded inline; documents reported by loaded modules (<see cref="IApiExplorerDocumentsProvider"/>)
    /// are fetched server-side and embedded too, so the browser never needs cross-origin access.
    /// Unreachable module documents are omitted gracefully.
    /// </summary>
    private async Task<string> BuildApiExplorerHtmlAsync(CancellationToken ct)
    {
        List<(string Label, string SpecJson)> documents = [("Kaeo LLM Proxy", OpenApiSpec)];

        // The built-in MCP server's document is embedded directly (same process, no fetch needed).
        if (_mcpServer.IsRunning && _mcpServer.ApiExplorer is { } mcpExplorer)
            documents.Add(("Kaeo LLM Proxy MCP", mcpExplorer.BuildSpecJson()));

        foreach (LoadedModule loaded in _moduleHost.LoadedModules)
        {
            if (loaded.Module is not IApiExplorerDocumentsProvider provider)
                continue;

            IReadOnlyList<ExplorerDocument> moduleDocuments;
            try
            {
                moduleDocuments = provider.GetExplorerDocuments();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Module {Name} failed to report explorer documents", loaded.Entry.Name);
                continue;
            }

            foreach (ExplorerDocument document in moduleDocuments)
            {
                string? specJson = await TryFetchExplorerSpecAsync(document.SpecUrl, ct).ConfigureAwait(false);
                if (specJson is null)
                {
                    Log.Information(
                        "API explorer: skipping '{Label}' ({Url}); the document is not reachable",
                        document.Label, document.SpecUrl);
                    continue;
                }

                documents.Add((document.Label, specJson));
            }
        }

        var documentsJson = new StringBuilder("[");
        for (int i = 0; i < documents.Count; i++)
        {
            if (i > 0)
                documentsJson.Append(',');

            // Spec content is embedded as validated raw JSON; "</" is escaped so an embedded
            // string value can never terminate the enclosing <script> block early.
            documentsJson
                .Append("{\"label\":")
                .Append(JsonSerializer.Serialize(documents[i].Label))
                .Append(",\"spec\":")
                .Append(documents[i].SpecJson.Replace("</", "<\\/"))
                .Append('}');
        }
        documentsJson.Append(']');

        return ApiExplorerHtmlTemplate.Replace("/*DOCUMENTS*/[]", documentsJson.ToString());
    }

    /// <summary>
    /// Fetches a module-reported OpenAPI document and validates that it is JSON. Returns null
    /// when the document is unreachable, empty, or not valid JSON.
    /// </summary>
    private static async Task<string?> TryFetchExplorerSpecAsync(string specUrl, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _explorerSpecClient.GetAsync(specUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            // Validate and normalize to compact JSON before embedding into the explorer page.
            using JsonDocument parsed = JsonDocument.Parse(content);
            return parsed.RootElement.GetRawText();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    private const string OpenApiSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Kaeo LLM Proxy",
            "description": "Ollama-compatible proxy that translates requests to OpenAI-compatible upstreams (llama.cpp, Qwen Cloud, etc.).",
            "version": "0.1.0"
          },
          "servers": [
            { "url": "/", "description": "This proxy" }
          ],
          "tags": [
            { "name": "Ollama Discovery", "description": "Ollama-compatible endpoints for model and version discovery. Answered locally from the mapping table — no upstream call." },
            { "name": "Ollama Generation", "description": "Ollama-compatible generation endpoints. The proxy translates these to OpenAI-compatible upstream calls." },
            { "name": "OpenAI Passthrough", "description": "Transparent passthrough to the upstream OpenAI-compatible /v1/* surface. No translation is performed." },
            { "name": "OpenAI Discovery", "description": "OpenAI-compatible endpoints for model discovery. Answered locally from the mapping table — no upstream call." }
          ],
          "paths": {
            "/api/version": {
              "get": {
                "summary": "Proxy version probe",
                "operationId": "getVersion",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Version information",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": { "version": { "type": "string" } }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/tags": {
              "get": {
                "summary": "List available models",
                "description": "Returns all enabled model mappings with their capabilities (text, chat, reasoning, vision, audio, function_calling, embeddings, code, image_generation).",
                "operationId": "listModels",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Model list",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "models": {
                              "type": "array",
                              "items": { "$ref": "#/components/schemas/ModelEntry" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/ps": {
              "get": {
                "summary": "List running models",
                "operationId": "listRunningModels",
                "tags": ["Ollama Discovery"],
                "responses": {
                  "200": {
                    "description": "Running model list",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "models": { "type": "array", "items": { "type": "object" } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/api/show": {
              "post": {
                "summary": "Show model information",
                "description": "Returns detailed information about a model including its capabilities and configuration.",
                "operationId": "showModel",
                "tags": ["Ollama Discovery"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model"],
                        "properties": {
                          "model": { "type": "string", "description": "Model name (e.g. 'myqwen' or 'myqwen:latest')" },
                          "name": { "type": "string", "description": "Alias for model" }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": {
                    "description": "Model details",
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/ShowResponse" }
                      }
                    }
                  }
                }
              }
            },
            "/api/chat": {
              "post": {
                "summary": "Chat completion",
                "description": "Sends a chat conversation to the upstream model. Supports streaming (NDJSON) and non-streaming responses, tool calls, and vision (image) inputs.",
                "operationId": "chat",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/ChatRequest" }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Chat response (streaming NDJSON or single JSON)" }
                }
              }
            },
            "/api/generate": {
              "post": {
                "summary": "Text generation",
                "description": "Sends a prompt to the upstream model for text completion. Supports streaming and non-streaming.",
                "operationId": "generate",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": { "$ref": "#/components/schemas/GenerateRequest" }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Generation response" }
                }
              }
            },
            "/api/embeddings": {
              "post": {
                "summary": "Generate embeddings",
                "operationId": "embeddings",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "prompt"],
                        "properties": {
                          "model": { "type": "string" },
                          "prompt": { "type": "string" }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Embedding vector" }
                }
              }
            },
            "/api/embed": {
              "post": {
                "summary": "Generate embeddings (alias)",
                "operationId": "embed",
                "tags": ["Ollama Generation"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "input"],
                        "properties": {
                          "model": { "type": "string" },
                          "input": {
                            "oneOf": [
                              { "type": "string" },
                              { "type": "array", "items": { "type": "string" } }
                            ]
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Embedding vectors" }
                }
              }
            },
            "/v1/chat/completions": {
              "post": {
                "summary": "OpenAI-compatible chat completions (passthrough)",
                "description": "Transparent passthrough to the upstream OpenAI-compatible /v1/chat/completions endpoint.",
                "operationId": "openAiChat",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI chat completion response" } }
              }
            },
            "/v1/completions": {
              "post": {
                "summary": "OpenAI-compatible completions (passthrough)",
                "operationId": "openAiCompletions",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI completion response" } }
              }
            },
            "/v1/models": {
              "get": {
                "summary": "OpenAI-compatible model list",
                "description": "Returns all enabled model mappings in OpenAI format, including the effective context_length (tokens) for each model.",
                "operationId": "openAiModels",
                "tags": ["OpenAI Discovery"],
                "responses": {
                  "200": {
                    "description": "OpenAI model list with context_length per model",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "object": { "type": "string" },
                            "data": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "id": { "type": "string" },
                                  "object": { "type": "string" },
                                  "created": { "type": "integer", "format": "int64" },
                                  "owned_by": { "type": "string" },
                                  "context_length": { "type": "integer", "description": "Effective context window in tokens." }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/v1/models/{model}": {
              "get": {
                "summary": "OpenAI-compatible single model lookup",
                "description": "Returns a single configured model mapping in OpenAI format. Answered locally from the mapping table (no upstream call), mirroring /api/show.",
                "operationId": "openAiModel",
                "tags": ["OpenAI Discovery"],
                "parameters": [
                  {
                    "name": "model",
                    "in": "path",
                    "required": true,
                    "schema": { "type": "string" },
                    "description": "Exposed proxy name (or upstream model name) of the mapping to look up."
                  }
                ],
                "responses": {
                  "200": {
                    "description": "OpenAI model object",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "id": { "type": "string" },
                            "object": { "type": "string" },
                            "created": { "type": "integer", "format": "int64" },
                            "owned_by": { "type": "string" },
                            "context_length": { "type": "integer", "description": "Effective context window in tokens." }
                          }
                        }
                      }
                    }
                  },
                  "404": { "description": "Model not found in configured mappings" }
                }
              }
            },
            "/v1/embeddings": {
              "post": {
                "summary": "OpenAI-compatible embeddings (passthrough)",
                "operationId": "openAiEmbeddings",
                "tags": ["OpenAI Passthrough"],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "200": { "description": "OpenAI embedding response" } }
              }
            },
            "/v1/responses/compact": {
              "post": {
                "summary": "Compact conversation context",
                "description": "Compacts the conversation history to reduce context size. Supports model redirect to a smaller/faster compact model when configured in the mapping.",
                "operationId": "compactConversation",
                "tags": ["OpenAI Passthrough"],
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "required": ["model", "input"],
                        "properties": {
                          "model": { "type": "string", "description": "Model name to use for compaction" },
                          "input": {
                            "type": "array",
                            "description": "Conversation messages to compact",
                            "items": {
                              "type": "object",
                              "properties": {
                                "role": { "type": "string" },
                                "content": { "type": "string" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                },
                "responses": {
                  "200": { "description": "Compacted conversation response" },
                  "502": { "description": "Upstream server error during compaction" }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "ModelEntry": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "model": { "type": "string" },
                  "modified_at": { "type": "string", "format": "date-time" },
                  "size": { "type": "integer", "format": "int64" },
                  "digest": { "type": "string" },
                  "details": {
                    "type": "object",
                    "properties": {
                      "parent_model": { "type": "string" },
                      "format": { "type": "string" },
                      "family": { "type": "string" },
                      "families": { "type": "array", "items": { "type": "string" } },
                      "parameter_size": { "type": "string" },
                      "quantization_level": { "type": "string" }
                    }
                  },
                  "capabilities": {
                    "type": "array",
                    "items": { "type": "string", "enum": ["text", "chat", "reasoning", "vision", "audio", "function_calling", "embeddings", "code", "image_generation"] },
                    "description": "Advertised model capabilities. Contains exactly the capabilities enabled per-mapping."
                  },
                  "context_length": { "type": "integer", "description": "Effective context window in tokens, used by clients for compaction thresholds." }
                }
              },
              "ShowResponse": {
                "type": "object",
                "properties": {
                  "modelfile": { "type": "string" },
                  "parameters": { "type": "string" },
                  "template": { "type": "string" },
                  "details": { "$ref": "#/components/schemas/ModelEntry/properties/details" },
                  "model_info": { "type": "object" },
                  "capabilities": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                }
              },
              "ChatRequest": {
                "type": "object",
                "required": ["model", "messages"],
                "properties": {
                  "model": { "type": "string" },
                  "messages": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "role": { "type": "string", "enum": ["system", "user", "assistant", "tool"] },
                        "content": { "type": "string" },
                        "images": { "type": "array", "items": { "type": "string" }, "description": "Base64-encoded images (vision models)" },
                        "tool_calls": { "type": "array", "items": { "type": "object" } }
                      }
                    }
                  },
                  "stream": { "type": "boolean", "default": true },
                  "format": { "type": "string" },
                  "options": {
                    "type": "object",
                    "properties": {
                      "temperature": { "type": "number" },
                      "repeat_penalty": { "type": "number" },
                      "num_predict": { "type": "integer" }
                    }
                  },
                  "tools": { "type": "array", "items": { "type": "object" } }
                }
              },
              "GenerateRequest": {
                "type": "object",
                "required": ["model", "prompt"],
                "properties": {
                  "model": { "type": "string" },
                  "prompt": { "type": "string" },
                  "system": { "type": "string" },
                  "stream": { "type": "boolean", "default": true },
                  "format": { "type": "string" },
                  "options": {
                    "type": "object",
                    "properties": {
                      "temperature": { "type": "number" },
                      "repeat_penalty": { "type": "number" }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static void FillTokenStats(RequestLog log, LlamaCppStreamChunk? chunk)
    {
        FillTokenStats(log, chunk?.Usage);

        LlamaCppTimings? timings = chunk?.Timings;
        if (timings is not null)
        {
            log.DraftN = timings.DraftN;
            log.DraftNAccepted = timings.DraftNAccepted;
        }
    }

    private static void FillTokenStats(RequestLog log, LlamaCppUsage? usage)
    {
        if (usage is null) return;
        log.PromptTokens = usage.PromptTokens;
        log.CompletionTokens = usage.CompletionTokens;
        log.TotalTokens = usage.TotalTokens;
        log.CachedPromptTokens = usage.PromptTokensDetails?.CachedTokens ?? 0;
        log.ReasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens ?? 0;
    }

    /// <summary>Parses a complete (non-streaming) chat/completion JSON body for its chunk (usage + timings); null when absent or unparseable.</summary>
    private static LlamaCppStreamChunk? TryParseChunk(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<LlamaCppStreamChunk>(body, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Wraps a write-only stream and counts the bytes written through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            BytesWritten += buffer.Length;
        }
    }

    /// <summary>Captures bytes written through it while forwarding them immediately to the inner stream.</summary>
    private sealed class ResponseCaptureStream(Stream inner) : Stream
    {
        private readonly MemoryStream _capture = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public string GetCapturedText() => Encoding.UTF8.GetString(_capture.ToArray());

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _capture.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            await _capture.WriteAsync(buffer.AsMemory(offset, count), ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await _capture.WriteAsync(buffer, ct);
        }
    }

    private sealed class PeriodicHeartbeatState : IDisposable
    {
        private readonly Func<ModelMapping, CancellationToken, Task> _sendHeartbeatAsync;
        private readonly Action<ModelMapping, string> _recordFailure;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private System.Threading.Timer _timer;
        private ModelMapping _mapping;

        public PeriodicHeartbeatState(
            ModelMapping mapping,
            int intervalSeconds,
            Func<ModelMapping, CancellationToken, Task> sendHeartbeatAsync,
            Action<ModelMapping, string> recordFailure)
        {
            _mapping = CloneMapping(mapping);
            _sendHeartbeatAsync = sendHeartbeatAsync;
            _recordFailure = recordFailure;
            TimeSpan interval = GetInterval(intervalSeconds);
            _timer = new System.Threading.Timer(OnTimer, null, TimeSpan.Zero, interval);
        }

        public void Update(ModelMapping mapping, int intervalSeconds)
        {
            _mapping = CloneMapping(mapping);
            TimeSpan interval = GetInterval(intervalSeconds);
            _timer.Change(TimeSpan.Zero, interval);
        }

        private void OnTimer(object? state)
        {
            if (_cts.IsCancellationRequested)
                return;

            _ = SendAsync();
        }

        private async Task SendAsync()
        {
            try
            {
                if (!await _sendLock.WaitAsync(0, _cts.Token).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _sendHeartbeatAsync(_mapping, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _recordFailure(_mapping, ex.Message);
                Log.Warning(ex, "Periodic heartbeat failed for model {Model}", _mapping.ProxyName);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _timer.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }

        private static TimeSpan GetInterval(int intervalSeconds)
            => TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 5, 300));

        private static ModelMapping CloneMapping(ModelMapping mapping) => new()
        {
            IsEnabled = mapping.IsEnabled,
            ProxyName = mapping.ProxyName,
            ModelName = mapping.ModelName,
            EnableThinkingCompatibility = mapping.EnableThinkingCompatibility,
            EnableHeartbeats = mapping.EnableHeartbeats,
            CredentialName = mapping.CredentialName,
            UpstreamType = mapping.UpstreamType,
            UpstreamUrl = mapping.UpstreamUrl,
            UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds,
            RepeatPenalty = mapping.RepeatPenalty,
            TemperaturePriority = mapping.TemperaturePriority,
            RepeatPenaltyPriority = mapping.RepeatPenaltyPriority,
            Temperature = mapping.Temperature,
            InstructionSetName = mapping.InstructionSetName,
            RedactRequestBodies = mapping.RedactRequestBodies,
            RedactResponseBodies = mapping.RedactResponseBodies,
            RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields,
        };
    }
}
