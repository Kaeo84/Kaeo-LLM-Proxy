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

using Kaeo.LlmProxy.Services.Translation;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Handles translation between Ollama API requests and llama.cpp OpenAI-compatible API requests.
/// Supports streaming, non-streaming, tool calls, JSON format mode, and batch embeddings.
/// </summary>
internal sealed partial class OllamaProxyHandler(AppSettings settings, StatisticsService stats, ModuleHost moduleHost, McpServerService mcpServer, StatisticsService? nonProxiedStats = null) : IDisposable
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

        /// <summary>
        /// Destination for requests the proxy answered without calling a model. Kept separate so the
        /// frequent automated probes cannot bury real traffic in the main log. Falls back to
        /// <see cref="_stats"/> when no dedicated store is supplied, which keeps the split purely a
        /// presentation concern in hosts that do not wire one.
        /// </summary>
        private readonly StatisticsService _nonProxiedStats = nonProxiedStats ?? stats;
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

            // Gated only on the mapping's own heartbeat flag. The SSE keep-alive settings must not
            // appear here: they control holding a streaming chat session open for a client, which is
            // unrelated to whether this model's upstream is reachable. Coupling them meant turning
            // keep-alive off also silenced health monitoring.
            if (!mapping.IsEnabled || !mapping.EnableHeartbeats)
            {
                if (_periodicHeartbeats.TryRemove(key, out PeriodicHeartbeatState? removed))
                    removed.Dispose();
                continue;
            }

            if (_periodicHeartbeats.TryGetValue(key, out PeriodicHeartbeatState? existing))
            {
                existing.Update(mapping, _settings.HeartbeatIntervalSeconds);
                continue;
            }

            PeriodicHeartbeatState created = new(
                mapping,
                _settings.HeartbeatIntervalSeconds,
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
            // This is the periodic upstream liveness probe, not a client-facing SSE keep-alive,
            // so it records probe state rather than bumping the keep-alive frame counter.
            _stats.RecordHeartbeatProbeSuccess(modelName);
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
                if (userAgent.Contains("copilot", StringComparison.OrdinalIgnoreCase)
                    || userAgent.Contains("github", StringComparison.OrdinalIgnoreCase))
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
    /// Returns the effective proxy model name for a request, applying the context-summarize
    /// (/compact) redirect when the request is detected as a Copilot /compact summary request
    /// and the mapping has opted into redirection via
    /// <see cref="ModelMapping.RedirectManualCompaction"/> with a usable compaction target.
    /// Returns the original model name unchanged when no redirect applies, so the request is
    /// handled by the model the client asked for.
    /// </summary>
    /// <remarks>
    /// Target resolution is delegated to <see cref="ResolveManualCompactTarget(AppSettings, ModelMapping)"/>
    /// so the signature-based redirect on the chat paths and the explicit <c>/compact</c>
    /// endpoints always agree on where a compaction request goes.
    /// </remarks>
    internal static string ResolveEffectiveModel(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return originalModel;

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null)
            return originalModel;

        (ModelMapping target, bool redirected) = ResolveManualCompactTarget(settings, mapping);
        return redirected ? target.ProxyName : originalModel;
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
    /// Reports whether an OpenAI-style request body carries a top-level <c>stream_options</c> member and
    /// whether it sets <c>include_usage</c>. Matching is case-insensitive to mirror the property loop in
    /// <see cref="NormalizeRequestBody"/>, since <c>JsonElement.TryGetProperty</c> is case-sensitive and a
    /// client capitalizing the member would otherwise slip through unstripped.
    /// </summary>
    private static (bool Present, bool IncludeUsage) ReadStreamOptions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (false, false);

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals("stream_options", StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Object)
                return (true, false);

            foreach (JsonProperty option in prop.Value.EnumerateObject())
            {
                if (option.Name.Equals("include_usage", StringComparison.OrdinalIgnoreCase)
                    && option.Value.ValueKind == JsonValueKind.True)
                    return (true, true);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    /// Explains why the context-summarize (/compact) redirect did not apply for a request, for
    /// diagnostic logging. Reports which gate in <see cref="ResolveEffectiveModel"/> stopped the
    /// redirect: signature not detected, no mapping found, redirection not enabled, no compaction
    /// target configured, or the target not being usable. Returns a generic fallback if every gate
    /// passed (which would mean a redirect was expected but did not occur).
    /// </summary>
    private static string DescribeCompactSkipReason(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return "first message did not match a /compact signature";

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null)
            return $"no mapping found for model '{originalModel}'";
        if (!mapping.RedirectManualCompaction)
            return "'Redirect manual compaction' is not enabled on the mapping, so the model handles its own compaction";

        // Resolve through FindContextSummarizeTarget rather than reading the stored ID, so the
        // reason reported here matches the target the redirect actually uses.
        ModelMapping? compactMapping = settings.FindContextSummarizeTarget(mapping);
        if (compactMapping is null)
            return "no compaction model is selected on the mapping";
        if (!compactMapping.IsEnabled)
            return $"compaction model '{compactMapping.ProxyName}' is not enabled";
        if (string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl))
            return $"compaction model '{compactMapping.ProxyName}' has no upstream URL";
        if (compactMapping.Id == mapping.Id)
            return "the compaction model is the same mapping, so no redirect applies";

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
    /// The SSE comment frame used as a client-side keep-alive. Leading colon makes it an SSE
    /// comment line, so conformant clients ignore it and it never reaches the model's output.
    /// </summary>
    private const string SseKeepAliveFrame = ": kaeo-keep-alive\n\n";

    private static readonly byte[] SseKeepAliveFrameBytes = Encoding.UTF8.GetBytes(SseKeepAliveFrame);

    /// <summary>
    /// Returns whether SSE keep-alive frames should be emitted to the client for the given model,
    /// combining the global toggle with the per-mapping
    /// <see cref="ModelMapping.EnableSseKeepAlive"/> flag.
    /// </summary>
    /// <remarks>
    /// This is the client-facing keep-alive that prevents streaming clients from timing out while
    /// the upstream is still processing the prompt. It is unrelated to the periodic upstream
    /// liveness probe (<see cref="PeriodicHeartbeatState"/>), which is reported separately.
    /// </remarks>
    private bool ShouldEmitSseKeepAlive(string modelName)
    {
        if (!_settings.EnableSseKeepAlive) return false;
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableSseKeepAlive ?? true;
    }

    /// <summary>
    /// Returns whether Copilot-compatible stream handling applies for the given model, from the
    /// per-mapping <see cref="ModelMapping.EnableCopilotCompatibility"/> flag. Defaults to true for
    /// an unmapped model, so a request the proxy cannot match to a mapping is still given a
    /// well-formed stream rather than being allowed to hang the client.
    /// </summary>
    /// <remarks>
    /// When enabled the proxy strips <c>stream_options</c> from the upstream-bound body, guarantees a
    /// <c>data: [DONE]</c> terminator, synthesizes the terminal <c>usage</c> chunk the client asked
    /// for, and reports post-header failures as an SSE error frame. Clients built on
    /// Microsoft.Extensions.AI (e.g. Visual Studio Copilot) await that terminal event and block
    /// indefinitely without it. Unrelated to <see cref="ShouldEmitSseKeepAlive"/>, which only holds
    /// the connection open while the upstream is still processing the prompt.
    /// </remarks>
    private bool ShouldApplyCopilotCompatibility(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableCopilotCompatibility ?? true;
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
    /// early rather than missing an overflow. Delegates to the shared estimator so the gate in
    /// <see cref="AutoCompactionService.ShouldCompact"/> and the callers here always measure a
    /// body identically — a divergence made the two disagree on non-ASCII bodies.
    /// </summary>
    private static int EstimateTokenCount(string body) => AutoCompactionService.EstimateTokenCount(body);

    /// <summary>
    /// When the mapping's compaction threshold is exceeded, summarizes the conversation with the
    /// mapping's compaction target and returns the compacted request body so the caller can forward
    /// it upstream. Returns null when compaction is disabled for the path, no threshold or compaction
    /// target is configured, the request is already under the threshold, or compaction failed — in
    /// every one of those cases the caller proceeds with the original body and the upstream returns
    /// its own authoritative error if it really does overflow.
    /// </summary>
    /// <remarks>
    /// This deliberately never short-circuits the request with a 413. The previous behavior
    /// summarized the conversation (paying for a compaction model call), discarded the summary, and
    /// told the client to retry with reduced context — so the client had to send the whole
    /// conversation again. The compacted body is now forwarded, which is the only way the
    /// summarization work is useful.
    /// </remarks>
    private async Task<string?> TryProactiveOverflowAsync(
        ModelMapping? mapping,
        string body,
        string model,
        HttpListenerResponse resp,
        AutoCompactPaths requestPath,
        Stream? outputStream,
        CancellationToken ct)
    {
        // Fast path: skip token estimation entirely if this path isn't enabled or no threshold is set.
        if (mapping is null || !mapping.IsAutoCompactActiveFor(requestPath))
            return null;

        int threshold = mapping.GetProactiveOverflowThreshold();

        if (threshold <= 0)
        {
            int estTokens = EstimateTokenCount(body);
            if (estTokens > 50000)
            {
                Log.Warning(
                    "Auto-compaction is not configured for model {Model} (no threshold set) but the request has ~{EstimatedTokens} tokens. Set a Compaction threshold (% of context or tokens) and select a Compaction Model to enable it, or leave both empty to let the model handle its own context.",
                    model, estTokens);
            }

            return null;
        }

        int estimated = EstimateTokenCount(body);

        if (estimated <= threshold)
            return null;

        // Check if auto-compaction should be attempted for this request.
        if (_autoCompactionService.ShouldCompact(mapping, requestPath, body, out string sessionKey))
        {
            // Resolve the compaction target through FindContextSummarizeTarget, which prefers the
            // stored proxy name and falls back to the stored ID.
            // Auto-compaction requires a resolved target — with none configured (dropdown =
            // None) it does nothing and lets upstream decide (no fallback to the original model).
            ModelMapping? compactMapping = _settings.FindContextSummarizeTarget(mapping);
            if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                compactMapping = null;

            if (compactMapping is null)
            {
                Log.Debug("Auto-compaction for model {Model} skipped: no compaction model selected", model);
                return null;
            }

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
                // Summarization requests go directly to the target's upstream, so BOTH the
                // base URL and the upstream model name (not the proxy display name) must come
                // from the resolved target mapping.
                var (baseUrl, timeout, apiKey) = ResolveUpstream(compactMapping.ProxyName);
                string compactModelName = compactMapping.ModelName ?? model;
                // Size chunks from the compact model's window, falling back to the global
                // conservative cap rather than the 131072 advertised default when the window was
                // never set. Overestimating here is what made small compact models overflow on
                // every attempt; the advertised value stays authoritative for /v1/models.
                int compactModelContext = compactMapping.GetCompactionContextWindow(
                    _settings.CompactionFallbackContextTokens);
                int maxTokensPerChunk = AutoCompactionService.GetSummaryPromptBudget(compactModelContext);
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

                    int compactedTokens = EstimateTokenCount(compactedBody);

                    // Add headers to signal compaction happened. These survive on the eventual
                    // upstream response, so the client learns the proxy compacted its context.
                    resp.Headers["X-Context-Compacted"] = "true";
                    resp.Headers["X-Context-Original-Tokens"] = estimated.ToString();
                    resp.Headers["X-Context-Compacted-Tokens"] = compactedTokens.ToString();

                    Log.Information(
                        "Auto-compaction succeeded for model {Model} using {CompactModel}: {OriginalTokens} -> {CompactedTokens} est. tokens; forwarding the compacted request upstream",
                        model, compactMapping.ProxyName, estimated, compactedTokens);

                    // Stream notification: compaction finished
                    if (outputStream is not null)
                    {
                        string notification = $": <ignorethis>kaeo-compaction-complete: Context compacted successfully. {estimated} tokens -> {compactedTokens} tokens ({100 - (compactedTokens * 100 / estimated)}% reduction)</ignorethis>\n\n";
                        byte[] notificationBytes = Encoding.UTF8.GetBytes(notification);
                        await outputStream.WriteAsync(notificationBytes, ct);
                        await outputStream.FlushAsync(ct);
                    }

                    // Return the compacted body for both streaming and non-streaming requests so
                    // the caller forwards it upstream and streams the answer back to the client.
                    return compactedBody;
                }

                Log.Warning("Auto-compaction for model {Model} produced no compacted body (session {SessionKey}); forwarding the original request",
                    model, sessionKey);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto-compaction failed for model {Model}; forwarding the original request so upstream can decide", model);
                // Fall through — do not send a proactive 413; allow upstream to decide.
            }
        }

        // No compaction produced. Do not short-circuit with 413 here — let upstream
        // return the authoritative error if it overflows.
        return null;
    }

    /// <summary>
    /// Attempts local context compaction after the upstream rejected the prompt with a
    /// context-size overflow error. The caller retries the original request once with the
    /// returned compacted body. This self-heals oversized prompts even when no proactive
    /// threshold was configured for the mapping, but it is still governed by the same
    /// per-mapping <see cref="ModelMapping.AutoCompactPaths"/> setting and the same compaction
    /// target requirement as the proactive path, so a mapping left on "Disabled" never
    /// compacts and simply surfaces the upstream error.
    /// </summary>
    /// <param name="requestPath">
    /// The path the request arrived on, used to consult
    /// <see cref="ModelMapping.IsAutoCompactActiveFor"/>.
    /// </param>
    private async Task<string?> TryReactiveCompactionAsync(
        string body,
        string model,
        AutoCompactPaths requestPath,
        HttpListenerResponse resp,
        bool streamAlreadyOpen,
        CancellationToken ct)
    {
        ModelMapping? mapping = _settings.FindModelMapping(model);
        if (mapping is null)
            return null;

        if (!mapping.IsAutoCompactActiveFor(requestPath))
        {
            Log.Debug(
                "Reactive auto-compaction for model {Model} skipped: auto-compaction is disabled for this path (AutoCompactPaths={Paths})",
                model, mapping.AutoCompactPaths);
            return null;
        }

        try
        {
            // Resolve the compaction target through FindContextSummarizeTarget, which prefers the
            // stored proxy name and falls back to the stored ID.
            // Reactive compaction requires a resolved target — with none configured it does
            // nothing and surfaces the upstream overflow error.
            ModelMapping? compactMapping = _settings.FindContextSummarizeTarget(mapping);
            if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                compactMapping = null;

            if (compactMapping is null)
            {
                Log.Debug("Reactive auto-compaction for model {Model} skipped: no compaction model selected", model);
                return null;
            }

            if (streamAlreadyOpen)
            {
                byte[] note = Encoding.UTF8.GetBytes(
                    ": <ignorethis>kaeo-compaction-needed: Upstream reported context overflow. Compacting conversation...</ignorethis>\n\n");
                await resp.OutputStream.WriteAsync(note, ct);
                await resp.OutputStream.FlushAsync(ct);
            }

            var (baseUrl, timeout, apiKey) = ResolveUpstream(compactMapping.ProxyName);
            string compactModelName = compactMapping.ModelName ?? model;
            int compactModelContext = compactMapping.GetCompactionContextWindow(
                _settings.CompactionFallbackContextTokens);
            int maxTokensPerChunk = AutoCompactionService.GetSummaryPromptBudget(compactModelContext);

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
                    $": <ignorethis>kaeo-compaction-complete: Conversation compacted ({EstimateTokenCount(body)} → {EstimateTokenCount(compacted)} est. tokens). Retrying upstream...</ignorethis>\n\n");
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
            // Captured for every request so a row that carries no model or meaningful path (a
            // health probe, an unknown endpoint, a malformed body) can still be traced to who sent
            // it. User-Agent names the calling tool; the address identifies the socket.
            ClientAddress = GetClientAddress(req.RemoteEndPoint),
            UserAgent = string.IsNullOrWhiteSpace(req.UserAgent) ? null : req.UserAgent,
            // Captured here rather than in each handler so every path — including the ones that
            // answer without a body — carries the caller's headers. Credential values are always
            // masked by the formatter.
            RequestHeaders = _settings.CollectRequestDetails || _settings.DebugMode
                ? FormatHeadersForLog(EnumerateRequestHeaders(req))
                : null,
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

    /// <summary>
    /// Records a request that <see cref="ProxyServer"/> rejected before it ever reached
    /// <see cref="HandleAsync"/> — currently only the 503 shed when no concurrency slot frees within
    /// the acquire timeout. Such a request has no <see cref="RequestLog"/> and never runs the
    /// logging <c>finally</c>, so without this it would leave no trace at all despite having been
    /// answered on the proxy's port.
    /// </summary>
    /// <remarks>
    /// Deliberately infallible: this runs on a fire-and-forget path already handling an overload,
    /// so a logging failure here must not turn a shed request into an unobserved exception.
    /// </remarks>
    internal void RecordRejectedRequest(HttpListenerRequest req, int statusCode, string reason)
    {
        try
        {
            RequestLog log = new()
            {
                RequestId = Guid.NewGuid().ToString("N")[..12],
                Method = req.HttpMethod,
                OllamaPath = req.Url?.AbsolutePath ?? "/",
                UpstreamPath = "(rejected before routing — no upstream call)",
                StatusCode = statusCode,
                Status = RequestLog.DeriveStatus(statusCode),
                ErrorMessage = reason,
                RequestBytes = Math.Max(0, req.ContentLength64),
                ClientAddress = GetClientAddress(req.RemoteEndPoint),
                UserAgent = string.IsNullOrWhiteSpace(req.UserAgent) ? null : req.UserAgent,
                RequestHeaders = _settings.CollectRequestDetails || _settings.DebugMode
                    ? FormatHeadersForLog(EnumerateRequestHeaders(req))
                    : null,
            };

            // A shed request never reached a model, so it belongs with the other rejected calls.
            // Written directly to the Non-proxied store rather than routed through the classifier,
            // because no handler ran and there is no request lifecycle to classify after the fact.
            _nonProxiedStats.AddLog(log);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to record a rejected request in the request log");
        }
    }

    /// <summary>
    /// Flattens <see cref="HttpListenerRequest.Headers"/> into name/value pairs for logging. Some
    /// header names legitimately repeat (e.g. <c>Accept</c>, <c>Via</c>), and the indexed accessor
    /// returns them comma-joined, which is what the wire format allows, so no information is lost.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> EnumerateRequestHeaders(HttpListenerRequest req)
    {
        System.Collections.Specialized.NameValueCollection headers = req.Headers;
        foreach (string? key in headers.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            yield return new KeyValuePair<string, string>(
                key, headers[key] ?? string.Empty);
        }
    }

    /// <summary>
    /// Renders the caller's IP as a display string for request-log attribution, preferring IPv4:
    /// an IPv4-mapped IPv6 address (what a loopback connection often presents on a dual-stack
    /// listener) is reported as plain IPv4 so the log stays readable. Mirrors the MCP host's
    /// equivalent so both log views show addresses the same way.
    /// </summary>
    private static string? GetClientAddress(IPEndPoint? remoteEndPoint)
    {
        if (remoteEndPoint is null)
            return null;

        IPAddress address = remoteEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.ToString();
    }

    /// <summary>
    /// Classifies a request as a category of non-proxied traffic, or returns null when the request
    /// is proxied — meaning it reached, or was meant to reach, an upstream model. The boundary is
    /// "the proxy answered this without calling a model". Pure and listener-free so it can be
    /// unit tested directly.
    /// </summary>
    /// <remarks>
    /// Classification is deliberately based on method and path only, never on the response status
    /// or <c>UpstreamPath</c>. Both of those are unreliable here: the routing table assigns
    /// <c>UpstreamPath</c> optimistically as soon as a route matches, before the body is even
    /// parsed, and a status code cannot distinguish a proxy-produced 400 from an upstream one that
    /// was passed through. The question "was a model meant to be called?" is answerable from the
    /// route alone, so that is what this uses.
    /// <para>
    /// Consequence worth knowing: a malformed body on <c>/api/chat</c> is a <em>proxied</em> route,
    /// so it stays in the Proxy log even though no model was reached. That is the deliberate
    /// reading of the boundary — the request was addressed to a model — and it keeps a client's
    /// broken chat call with the traffic it belongs to rather than in the noise log.
    /// </para>
    /// </remarks>
    internal static NonProxiedCategory? ClassifyNonProxied(string method, string path, int statusCode)
    {
        _ = statusCode; // Retained for call-site clarity; routing alone decides the category.

        bool isGet = method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        bool isHead = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return NonProxiedCategory.CorsPreflight;

        if (path == "/" && (isGet || isHead))
            return NonProxiedCategory.HealthProbes;

        if (isGet && path == "/api/version")
            return NonProxiedCategory.VersionAndExplorer;

        if (path is "/scalar" or "/scalar/" or "/openapi/v1/openapi.json")
            return NonProxiedCategory.VersionAndExplorer;

        // Answered entirely from local mapping configuration, with no upstream call.
        if (path is "/api/ps" or "/api/show")
            return NonProxiedCategory.LocalStubs;

        if (isGet && (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase)))
            return NonProxiedCategory.LocalStubs;

        // Routes addressed to a model stay in the Proxy log — including when they fail, because the
        // client asked for a model and the failure belongs with that traffic. Note /api/tags maps
        // onto the upstream's /v1/models, so it is a real call, unlike the local /v1/models above.
        if (IsModelRoute(method, path))
            return null;

        // Everything else was answered without reaching a model: unknown endpoints, unsupported
        // model-management calls, and the overload shed.
        return NonProxiedCategory.RejectedRequests;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a route whose purpose is to reach an upstream model.
    /// </summary>
    /// <remarks>
    /// The method verb is accepted but unused: every model route here is identified by path alone.
    /// It is kept on the signature because it is the natural discriminator if a path ever needs
    /// different handling per verb, and because callers already have it to hand.
    /// </remarks>
    private static bool IsModelRoute(string _, string path)
    {
        if (path is "/api/chat" or "/api/generate" or "/api/embeddings" or "/api/embed"
            || path.Equals("/api/tags", StringComparison.OrdinalIgnoreCase))
            return true;

        // OpenAI-native passthrough, including the compact endpoints. The local /v1/models lookups
        // were already returned above, so any remaining /v1* path is a model call.
        return path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase);
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
        // exception detail. Suppresses the second AddLog in the finally.
        bool exceptionLogged = false;

        // One try/finally wraps the whole method, including the early-return branches above the
        // routing table. Previously those returned before the try, so a health probe produced no
        // log entry at all — even with CollectAllTraffic on.
        try
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
                    RecordResponseStatus(resp, log, 204);
                    resp.Close();
                    return;
                }
            }

            // Load balancer / uptime health checks commonly probe "/" with GET or HEAD. HEAD must
            // never write body bytes — HttpListener treats the response as having a 0-byte entity
            // body for HEAD requests, and writing anything to the output stream (even via
            // WriteJsonAsync's normal JSON payload) throws ProtocolViolationException ("Bytes to be
            // written to the stream exceed the Content-Length bytes size specified").
            if (path == "/" && (method == "GET" || method == "HEAD"))
            {
                RecordResponseStatus(resp, log, 200);
                resp.ContentType = "text/plain";
                if (method == "HEAD")
                {
                    resp.ContentLength64 = 0;
                    resp.Close();
                }
                else
                {
                    byte[] bytes = Encoding.UTF8.GetBytes("OK");
                    if (_settings.CollectResponseDetails)
                        log.ResponseBody = "OK";
                    resp.ContentLength64 = bytes.Length;
                    await resp.OutputStream.WriteAsync(bytes, ct);
                    resp.Close();
                }

                return;
            }

            // Static version probe — infrastructure noise that would inflate the request log on
            // every client connection.
            if (method == "GET" && path == "/api/version")
            {
                RecordResponseStatus(resp, log, 200);
                const string versionJson = "{\"version\":\"0.1.0\"}";
                if (_settings.CollectResponseDetails)
                    log.ResponseBody = versionJson;
                await WriteJsonAsync(resp, new { version = "0.1.0" }, ct);
                return;
            }

            // Scalar API explorer — served only when explicitly enabled in settings.
            if (_settings.EnableApiExplorer && method == "GET")
            {
                if (path is "/scalar" or "/scalar/")
                {
                    RecordResponseStatus(resp, log, 200);
                    await WriteHtmlAsync(resp, await BuildApiExplorerHtmlAsync(ct).ConfigureAwait(false), ct);
                    return;
                }

                if (path == "/openapi/v1/openapi.json")
                {
                    RecordResponseStatus(resp, log, 200);
                    await WriteJsonRawAsync(resp, OpenApiSpec, ct);
                    return;
                }
            }

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
                RecordResponseStatus(resp, log, 501);
                string errorJson = $"{{\"error\":\"'{path}' is not supported. llama.cpp has no model-management API.\"}}";
                await CaptureRejectedBodyAsync(req, log, errorJson, ct);
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
                // Both /compact endpoints are always live and behave identically: they forward to
                // the resolved target's upstream and relay the model's summary. There is no global
                // endpoint toggle — per-mapping RedirectManualCompaction + the compaction target
                // decide whether the request is redirected or goes to the client's own model.
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
                // Unknown endpoint. Recorded explicitly so an unrecognised call — a scanner, a
                // misconfigured client, or an endpoint this proxy does not implement — is visible
                // in the log as a 404 rather than defaulting to a success with no status code.
                RecordResponseStatus(resp, log, 404);
                log.ErrorMessage = $"Unknown endpoint: {path}";
                string errorJson = $"{{\"error\":\"Unknown endpoint: {path}\"}}";
                await CaptureRejectedBodyAsync(req, log, errorJson, ct);
                await WriteJsonAsync(resp, new { error = $"Unknown endpoint: {path}" }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The response may already have started (a streaming request commits its SSE headers
            // before the upstream answers), in which case the 499 cannot be delivered. Record that
            // rather than silently swallowing it, then always release the connection.
            if (!await TryWriteErrorResponseAsync(resp, log, 499, new { error = "Request cancelled.", requestId }, CancellationToken.None))
            {
                RecordUndeliverableError(log, 499);
                CloseResponseQuietly(resp);
            }
        }
        catch (RequestBodyTooLargeException ex)
        {
            // Oversized request body — reject before buffering to protect against memory exhaustion.
            log.ErrorMessage = ex.Message;
            Log.Warning("Rejected oversized request body on {Path}: {Message}", path, ex.Message);

            // The body is rejected before anything is streamed, so this should always be deliverable.
            if (!await TryWriteErrorResponseAsync(resp, log, 413, new { error = "Request body too large.", requestId }, CancellationToken.None))
                RecordUndeliverableError(log, 413);

            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        catch (Exception ex)
        {
            log.ErrorMessage = ex.Message;

            // Log the full exception detail server-side only. The client receives a generic
            // message so internal details (paths, hostnames, connection strings) are never leaked.
            Log.Error(ex, "Unhandled error processing {Method} {Path}", method, path);

            // A streaming request commits its SSE headers before the upstream answers, so this 500 is
            // often undeliverable and the client is left with a truncated response. Say so in the log
            // instead of swallowing the failure, which is what previously made these hangs invisible.
            if (!await TryWriteErrorResponseAsync(resp, log, 500, new { error = "Internal proxy error.", requestId }, CancellationToken.None))
            {
                RecordUndeliverableError(log, 500);
                CloseResponseQuietly(resp);
            }

            // Persist the full exception detail (stack trace, inner exceptions) separately.
            _stats.AddLog(log, ex);
            exceptionLogged = true;

            // Skip the finally AddLog — we already logged above with the exception.
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        finally
        {
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;

            // The unhandled-exception handler already persisted this entry with its exception
            // detail. AddLog passes the same instance to the background writer, so mutating it
            // here would race that write — and its status is already correct.
            if (!exceptionLogged)
            {
                // Response headers are captured before anything else, while the response object is
                // most likely still readable. HttpListenerResponse.Headers throws once the response
                // is closed or its headers are committed, so this is best-effort: a failure just
                // leaves the field null rather than losing the whole log entry.
                if (_settings.CollectResponseDetails)
                {
                    try
                    {
                        List<KeyValuePair<string, string>> responseHeaders = [];
                        foreach (string? key in resp.Headers.AllKeys)
                        {
                            if (string.IsNullOrWhiteSpace(key))
                                continue;

                            responseHeaders.Add(new KeyValuePair<string, string>(key, resp.Headers[key] ?? string.Empty));
                        }

                        log.ResponseHeaders = FormatHeadersForLog(responseHeaders);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                        or ObjectDisposedException
                        or HttpListenerException)
                    {
                        Log.Debug(ex, "Could not read the response headers for the request log");
                    }
                }

                // Safety net for any branch that answered the client without recording what it sent.
                // Only fills a zero: PassthroughCoreAsync stores the *upstream* status, and
                // HandleChatAsync deliberately records the upstream 400 while rewriting the
                // client-facing code to 413, so an unconditional overwrite would corrupt both.
                // Reading after Close() can throw; a failure leaves the code at 0, which
                // DeriveStatus treats as an error — the safe direction for a missing status.
                if (log.StatusCode == 0)
                {
                    try
                    {
                        log.StatusCode = resp.StatusCode;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                        or HttpListenerException
                        or ObjectDisposedException)
                    {
                        Log.Debug(ex, "Could not read the response status to backfill the request log");
                    }
                }

                // Derive the status from the code, but only ever escalate. A branch that already
                // recorded Error or Cancelled knows something the code cannot express — the
                // passthrough failure at "headers already committed" leaves StatusCode at the
                // upstream's 200 because that status line is already on the wire, yet the request
                // genuinely failed. Deriving unconditionally would downgrade it back to Success.
                RequestStatus derived = RequestLog.DeriveStatus(log.StatusCode);
                if (log.Status == RequestStatus.Success && derived != RequestStatus.Success)
                    log.Status = derived;

                // Classify where this request belongs. A request the proxy answered without calling a model
                // is filed as Non-proxied so the frequent automated probes cannot bury real traffic;
                // everything else stays in the main Proxy log, unconditionally, because an
                // unrecognised caller is exactly what needs to stay findable.
                NonProxiedCategory? category = ClassifyNonProxied(method, path, log.StatusCode);

                if (category is { } classified)
                {
                    // Capture is per category: routine noise can be silenced while the rejected
                    // requests worth investigating stay on.
                    if (_settings.CollectNonProxiedCategories.Contains(classified))
                        _nonProxiedStats.AddLog(log);
                }
                else
                {
                    _stats.AddLog(log);
                }
            }
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
    /// <summary>
    /// Tracks whether the SSE response headers have already been written to the client during a
    /// passthrough request. <see cref="HttpListenerResponse"/> exposes no "headers sent" flag, so the
    /// handler owns this state and uses it to decide how a failure can still be reported: with a real
    /// status code while the headers are unwritten, or as a frame inside the already-open stream once
    /// they are not.
    /// </summary>
    private sealed class PassthroughResponseState
    {
        /// <summary>True once <c>200 text/event-stream</c> has been flushed to the client.</summary>
        public bool HeadersCommitted { get; set; }
    }

    /// <summary>
    /// Forwards any OpenAI-native <c>/v1/*</c> request to the resolved upstream, containing every
    /// failure so the client always receives a complete response. Once the SSE headers are committed
    /// a status code can no longer be changed, so a late failure is reported as an error frame
    /// followed by the <c>data: [DONE]</c> terminator instead of a bare connection close — without
    /// which a client awaiting the terminal stream event blocks indefinitely.
    /// </summary>
    private async Task PassthroughAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        PassthroughResponseState state = new();

        try
        {
            await PassthroughCoreAsync(req, resp, log, state, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away or the server is stopping. Rethrow so HandleCoreAsync records the
            // cancellation and closes the response; there is no client left to notify.
            throw;
        }
        catch (Exception ex) when (state.HeadersCommitted)
        {
            // The status line is already on the wire as 200, so the only way to tell the client
            // anything is inside the stream. Emit both frames and close deliberately.
            string description = DescribePassthroughFailure(ex);

            // StatusCode is left as-is: it already carries the upstream's status when the failure came
            // after the upstream answered, matching the pre-committed error branch below. It stays 0
            // when the failure preceded any upstream response.
            log.Status = RequestStatus.Error;
            log.ErrorMessage = description;
            Log.Warning(ex, "Passthrough failed after the SSE headers were committed; terminating the stream with an error frame");

            await WritePostCommitErrorFramesAsync(resp, description, 502, ct).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Writes an SSE error frame followed by the <c>data: [DONE]</c> terminator to a response whose
    /// headers are already committed, then closes it. Every write is independently guarded because the
    /// client may have disconnected at any point; a failure to deliver the frame is not itself an error
    /// worth propagating.
    /// </summary>
    /// <param name="statusCode">Code reported inside the frame, since the real HTTP status is fixed at 200.</param>
    private static async Task WritePostCommitErrorFramesAsync(
        HttpListenerResponse resp, string message, int statusCode, CancellationToken ct)
    {
        string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(message ?? string.Empty)},\"code\":{statusCode}}}}}\n\ndata: [DONE]\n\n";

        try
        {
            await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorFrame), ct).ConfigureAwait(false);
            await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
            Log.Debug(ex, "Could not deliver the terminal error frame; the client has likely disconnected");
        }

        try { resp.OutputStream.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
        {
            Log.Debug(ex, "Response stream was already closed");
        }
    }

    /// <summary>
    /// Produces the client-facing description of a passthrough failure. Timeouts and upstream
    /// unavailability are named explicitly because those are the two cases an operator can act on;
    /// everything else falls back to the exception message.
    /// </summary>
    private static string DescribePassthroughFailure(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "Upstream request timed out.",
        HttpRequestException httpEx => $"Upstream request failed: {httpEx.Message}",
        _ => ex.Message,
    };

    private async Task PassthroughCoreAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, PassthroughResponseState state, CancellationToken ct)
    {
        bool contextCompacted = false;

        // HttpListener only puts the response headers on the wire once at least one byte has been
        // written. Setting StatusCode/ContentType alone therefore leaves the client staring at an
        // empty socket, so every path that needs SSE must go through this single idempotent commit
        // point, which also flushes one comment frame to force the headers out immediately.
        // Without that flush a streaming client with a short network timeout gives up during the
        // upstream's prompt-processing phase even though the request is progressing normally.
        bool sseCommitted = false;
        async Task CommitSseHeadersAsync()
        {
            if (sseCommitted)
                return;

            sseCommitted = true;
            resp.StatusCode = 200;
            resp.ContentType = "text/event-stream";
            resp.SendChunked = true;
            resp.KeepAlive = true;
            state.HeadersCommitted = true;

            // Written unconditionally, even when periodic keep-alive is disabled, because this flush
            // is what commits the headers. It is a single SSE comment line, which conformant clients
            // discard, so it never reaches the model's output.
            await resp.OutputStream.WriteAsync(SseKeepAliveFrameBytes, ct);
            await resp.OutputStream.FlushAsync(ct);
        }

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
        // Set when the signature-based /compact redirect already retargeted this request, which
        // means the request IS a compaction request. Neither the proactive nor the reactive
        // auto-compaction may run on top of it (that would summarize a summary), so both gates
        // read this single flag.
        bool alreadyRedirectedForCompaction = false;
        // Function names the client declared in its "tools" array. Null = unknown/unrestricted;
        // an empty set means the client (e.g. the Copilot Help surface) cannot execute ANY tool
        // call, so the response paths must strip or inline every tool call.
        IReadOnlySet<string>? passthroughDeclaredTools = null;

        // What the client asked for in its stream_options block. NormalizeRequestBody strips that
        // block from the upstream-bound body, so the response path has to produce what it requested.
        StreamOptionsInfo passthroughStreamOptions = StreamOptionsInfo.None;

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
                    bodyText,
                    _settings,
                    log,
                    modelName => ShouldApplyThinkingCompatibility(_settings, modelName),
                    out passthroughStreamOptions);
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
                passthroughDeclaredTools = ExtractDeclaredToolNames(bodyText);

                // Proactive context-overflow check for OpenAI-native passthrough requests.
                //
                // Only ONE compaction may act on a request. NormalizeRequestBody already applies
                // the signature-based /compact redirect, and it records that in log.OriginalModel,
                // so a non-empty OriginalModel means this request is itself a compaction request.
                // Running threshold-based auto-compaction on top of it would summarize a summary.
                alreadyRedirectedForCompaction = !string.IsNullOrEmpty(log.OriginalModel);
                if (alreadyRedirectedForCompaction)
                {
                    Log.Debug(
                        "Skipping proactive auto-compaction for {Model}: the request is already a compaction request redirected from {OriginalModel} (OpenAI passthrough)",
                        originalModel, log.OriginalModel);
                }
                else
                {
                    // For streaming requests, commit SSE headers before compaction so progress
                    // comments can be written. CommitSseHeadersAsync also flushes the initial
                    // keep-alive frame, which is what actually puts the headers on the wire.
                    Stream? compactionOutputStream = null;
                    if (isStreamingRequest)
                    {
                        await CommitSseHeadersAsync();
                        compactionOutputStream = resp.OutputStream;
                    }

                    // Per-mapping AutoCompactPaths is the only gate: TryProactiveOverflowAsync
                    // consults IsAutoCompactActiveFor, the threshold, and the compaction target.
                    // There is deliberately no global toggle, so a mapping left on "Disabled"
                    // never compacts and the model handles its own context.
                    string? compacted = await TryProactiveOverflowAsync(_settings.FindModelMapping(originalModel), rewritten, originalModel, resp, AutoCompactPaths.OpenAI, compactionOutputStream, ct);
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

        // Copilot compatibility: guarantee the stream reaches its terminal event. Clients built on
        // Microsoft.Extensions.AI await `data: [DONE]` — and, when they asked for it via
        // stream_options.include_usage, the terminal usage chunk the proxy stripped — and block
        // indefinitely when either never arrives.
        //
        // Constructed before ResolveUpstream because that call can throw after the proactive-compaction
        // path has already committed the SSE headers, and the error handling below needs the terminator
        // to close that stream cleanly. It is only ever handed to SSE paths, so a non-streaming
        // response can never receive these frames. Null when the mapping opted out, which leaves the
        // forwarded stream byte-identical to what the upstream sent.
        OpenAiStreamTerminator? streamTerminator = ShouldApplyCopilotCompatibility(originalModel)
            ? new OpenAiStreamTerminator(originalModel, passthroughStreamOptions.MustSynthesizeUsage)
            : null;

        // Token totals for a synthesized usage chunk, read only after the usage sniffer has flushed so
        // any counts the upstream did report are included.
        (int PromptTokens, int CompletionTokens) tokenCounts() => (log.PromptTokens, log.CompletionTokens);

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
        // keep-alive comments while waiting for the upstream to send its first response header.
        // llama.cpp does not send any HTTP headers until the first token is ready, so clients
        // with a short NetworkTimeout (e.g. the OpenAI .NET SDK default of 100 s) would
        // otherwise time out silently during long prompt-processing / thinking phases.
        //
        // This deliberately does NOT test state.HeadersCommitted. The proactive-compaction path above
        // already commits SSE headers for streaming requests without necessarily writing anything,
        // so gating on that flag skipped the keep-alive pump for almost every streaming request and
        // left the client with no bytes at all until the upstream answered.
        using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task preResponseKeepAliveTask = Task.CompletedTask;

        if (isStreamingRequest && ShouldEmitSseKeepAlive(originalModel))
        {
            await CommitSseHeadersAsync();

            preResponseKeepAliveTask = PumpPreResponseSseKeepAliveAsync(
                resp.OutputStream,
                _settings.SseKeepAliveIntervalSeconds,
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
            // Stop the pre-response keep-alive pump as soon as upstream headers arrive.
            await preResponseCts.CancelAsync();
            await preResponseKeepAliveTask;
        }

        // Reactive compaction: llama.cpp rejects prompts that exceed the loaded model's
        // context with a 400 "exceed_context_size_error". When that happens and the request
        // was not already compacted, run the chunked map-reduce summarizer locally and retry
        // the upstream call once. This covers mappings where the proactive threshold is not
        // configured as well as cases where the proxy's token estimate undershot.
        //
        // Gated on the same per-mapping AutoCompactPaths setting as the proactive path (inside
        // TryReactiveCompactionAsync) and skipped when the request is itself a compaction request.
        if (!upstreamResp.IsSuccessStatusCode
            && passthroughBody is not null
            && !contextCompacted
            && !alreadyRedirectedForCompaction)
        {
            string probe = await upstreamResp.Content.ReadAsStringAsync(ct);
            if (IsContextOverflowBody(probe))
            {
                // The retry path is the longest silent window in the whole request: the proxy
                // re-summarizes the conversation locally and then waits again for upstream headers,
                // all while the client sees nothing. Headers may already be committed by this point
                // (streaming requests commit early for compaction progress), so the client is
                // sitting on an open SSE stream with no frames arriving. Pump keep-alives across
                // both phases for the same reason the pre-response phase does.
                using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task retryKeepAliveTask = Task.CompletedTask;

                if (isStreamingRequest && ShouldEmitSseKeepAlive(originalModel))
                {
                    await CommitSseHeadersAsync();
                    retryKeepAliveTask = PumpPreResponseSseKeepAliveAsync(
                        resp.OutputStream,
                        _settings.SseKeepAliveIntervalSeconds,
                        retryCts.Token);
                }

                string? reactive;
                try
                {
                    reactive = await TryReactiveCompactionAsync(
                        passthroughBody, originalModel, AutoCompactPaths.OpenAI, resp, state.HeadersCommitted, ct);
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
                finally
                {
                    // Stop pumping before any body is forwarded below; two writers on the same
                    // OutputStream would interleave frames and corrupt the SSE stream.
                    await retryCts.CancelAsync();
                    await retryKeepAliveTask;
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
        if (!state.HeadersCommitted)
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

            if (state.HeadersCommitted)
            {
                // Headers already sent as 200/SSE — emit the error as a data frame so the
                // client sees it rather than getting a silent stream close. The terminator frame
                // is required as well: a client awaiting `data: [DONE]` blocks indefinitely
                // without it. Reaching this branch means the request was streaming, since
                // CommitSseHeadersAsync is only ever called for streaming requests.
                string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(errorBody)},\"code\":{clientStatusCode}}}}}\n\ndata: [DONE]\n\n";
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
        void onUsage(LlamaCppStreamChunk chunk) => FillTokenStats(log, chunk);

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
                    ShouldEmitSseKeepAlive(originalModel),
                    _settings.SseKeepAliveIntervalSeconds,
                    ct,
                    () => _stats.IncrementSseKeepAlive(originalModel),
                    onUsage,
                    rawCapture,
                    declaredToolNames: passthroughDeclaredTools,
                    terminator: streamTerminator,
                    tokenCounts: tokenCounts,
                    useIrTranslation: _settings.UseIrTranslation);

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
                await CopyStreamWithSseKeepAliveAsync(
                    upstreamStream,
                    captureStream,
                    ShouldEmitSseKeepAlive(originalModel),
                    _settings.SseKeepAliveIntervalSeconds,
                    ct,
                    () => _stats.IncrementSseKeepAlive(originalModel),
                    onUsage,
                    streamTerminator,
                    tokenCounts);

                string forwardedText = captureStream.GetCapturedText();
                if (collectResponse)
                    log.ResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
                if (debugCapture)
                    log.UpstreamResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
            }
            else
            {
                await CopyStreamWithSseKeepAliveAsync(
                    upstreamStream,
                    countingStream,
                    ShouldEmitSseKeepAlive(originalModel),
                    _settings.SseKeepAliveIntervalSeconds,
                    ct,
                    () => _stats.IncrementSseKeepAlive(originalModel),
                    onUsage,
                    streamTerminator,
                    tokenCounts);
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
                extractToolCalls: shouldMirrorReasoningContent,
                declaredToolNames: passthroughDeclaredTools);
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
        bool extractToolCalls = false,
        IReadOnlySet<string>? declaredToolNames = null)
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
            : TransformNonStreamingChatBody(body, thinkingMode, extractToolCalls, declaredToolNames);
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
    /// <see cref="OpenAiSseRewriter"/>. When <paramref name="declaredToolNames"/> is supplied,
    /// only calls naming a client-declared function are kept; the rest are stripped (structured)
    /// or left visible as text (inline XML). Returns the original text unchanged if parsing fails.
    /// </summary>
    internal static string TransformNonStreamingChatBody(
        string json,
        ThinkingMode thinkingMode,
        bool extractToolCalls = false,
        IReadOnlySet<string>? declaredToolNames = null)
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

                // Structured tool calls from the upstream may name functions the client never
                // declared — a function part the client cannot bind (Copilot UI crash). Strip
                // those; when nothing survives, the turn ends as a plain answer.
                if (extractToolCalls && message["tool_calls"] is JsonArray structured && structured.Count > 0)
                {
                    int keptCalls = 0;
                    List<JsonNode?> dropped = [];
                    foreach (JsonNode? callNode in structured)
                    {
                        string? callName = (callNode as JsonObject)?["function"] is JsonObject fnNode
                            && fnNode["name"] is JsonValue nameVal
                            && nameVal.TryGetValue(out string? nameStr) ? nameStr : null;

                        if (IsToolNameAllowed(declaredToolNames, callName))
                            keptCalls++;
                        else
                            dropped.Add(callNode);
                    }

                    foreach (JsonNode? droppedNode in dropped)
                        structured.Remove(droppedNode);

                    if (keptCalls == 0)
                    {
                        message.Remove("tool_calls");
                        message["content"] ??= JsonValue.Create(string.Empty);
                        choice["finish_reason"] = "stop";
                    }
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
                        List<OllamaToolCall> allowedCalls = [.. extraction.ToolCalls
                            .Where(tc => IsToolNameAllowed(declaredToolNames, tc.Function?.Name))];

                        if (allowedCalls.Count > 0)
                        {
                            message["tool_calls"] = BuildOpenAiToolCallsArray(allowedCalls);
                            // Strip only the accepted blocks; rejected XML stays visible as text.
                            message["content"] = JsonValue.Create(XmlToolCallRegex().Replace(
                                content,
                                match => IsToolNameAllowed(declaredToolNames, match.Groups["name"].Value.Trim())
                                    ? string.Empty
                                    : match.Value).Trim());
                            choice["finish_reason"] = "tool_calls";
                        }
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
    /// Collects the function names a request declares in its "tools" array, or null when the
    /// body cannot be parsed. An empty set means the client declared no tools at all (e.g. the
    /// Copilot Help surface) so responses must carry no tool calls; null means unknown, so
    /// filtering is skipped and legacy behaviour is preserved.
    /// </summary>
    internal static IReadOnlySet<string>? ExtractDeclaredToolNames(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tools", out JsonElement tools)
                || tools.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("function", out JsonElement fn)
                    && fn.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    names.Add(name.GetString()!);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides whether a tool call for <paramref name="name"/> may reach the client: a null
    /// declared set is unrestricted (legacy), an empty set means no tool was declared (drop
    /// everything), otherwise the name must match a declared function exactly. Shared with the
    /// Phase-B IR frame translator so the declared-tool policy has one definition.
    /// </summary>
    internal static bool IsToolNameAllowed(IReadOnlySet<string>? declaredToolNames, string? name)
        => declaredToolNames is null
            || (!string.IsNullOrEmpty(name) && declaredToolNames.Contains(name));

    // Inline XML tool-call format emitted by some llama.cpp templates; shared by the
    // streaming rewriter and the non-streaming transform.
    [GeneratedRegex(@"<tool_call>\s*<function=(?<name>[^>\s]+)>\s*(?<body>.*?)\s*</function>\s*</tool_call>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlToolCallRegex();

    [GeneratedRegex(@"<parameter=(?<n>[^>\s]+)>\s*(?<v>.*?)\s*</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlParameterRegex();

    /// <summary>
    /// The same parameter element as <see cref="XmlParameterRegex"/>, but capturing under the
    /// <c>name</c>/<c>value</c> group names the non-streaming extractor reads. Kept separate rather
    /// than renaming one set of call sites: the two extractors, for the streaming rewriter and the
    /// non-streaming transform, are independent translation paths and each names its captures to
    /// match its own surrounding code.
    /// </summary>
    [GeneratedRegex(@"<parameter=(?<name>[^>\s]+)>\s*(?<value>.*?)\s*</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlToolCallParameterRegex();

    /// <summary>
    /// Returns true when a JSON POST body has <c>"stream": true</c>, indicating the client
    /// expects an SSE response and we should pre-commit headers before the upstream responds.
    /// Uses a lightweight regex scan instead of parsing the full JSON DOM.
    /// </summary>
    [GeneratedRegex("\"stream\"\\s*:\\s*true", RegexOptions.IgnoreCase, 100)]
    private static partial Regex StreamingJsonRegex();

    private static bool IsStreamingJsonBody(string json)
    {
        try
        {
            // Match "stream": true with optional whitespace, case-insensitive
            return StreamingJsonRegex().IsMatch(json);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pumps SSE comment keep-alive frames into <paramref name="output"/> at
    /// <paramref name="intervalSeconds"/> intervals until <paramref name="ct"/> is cancelled.
    /// Used to keep the client connection alive while waiting for the upstream to send its
    /// first response header (i.e. before the first token is generated).
    /// </summary>
    private static async Task PumpPreResponseSseKeepAliveAsync(
        Stream output,
        int intervalSeconds,
        CancellationToken ct)
    {
        TimeSpan interval = AppSettings.SseKeepAliveInterval(intervalSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await output.WriteAsync(SseKeepAliveFrameBytes, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on cancel */ }
    }

    /// <summary>
    /// Copies an SSE response to the client verbatim, interleaving keep-alive comment frames while the
    /// upstream is quiet. Used on passthrough paths that need no rewriting.
    /// </summary>
    /// <param name="terminator">
    /// When supplied (Copilot-compatible stream), observes forwarded bytes and appends the terminal
    /// frames the upstream omitted once the stream ends. Null leaves the stream exactly as the
    /// upstream sent it.
    /// </param>
    /// <param name="tokenCounts">
    /// Supplies the prompt/completion token totals for a synthesized usage chunk. Read only after the
    /// usage sniffer has flushed, so counts captured from the upstream's own frames are included.
    /// </param>
    private static async Task CopyStreamWithSseKeepAliveAsync(
        Stream source,
        Stream destination,
        bool enableKeepAlive,
        int keepAliveIntervalSeconds,
        CancellationToken ct,
        Action? onKeepAliveSent = null,
        Action<LlamaCppStreamChunk>? onUsage = null,
        OpenAiStreamTerminator? terminator = null,
        Func<(int PromptTokens, int CompletionTokens)>? tokenCounts = null)
    {
        byte[] buffer = new byte[81920];
        TimeSpan keepAliveInterval = AppSettings.SseKeepAliveInterval(keepAliveIntervalSeconds);
        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        while (!ct.IsCancellationRequested)
        {
            ValueTask<int> readValueTask = source.ReadAsync(buffer, ct);
            Task<int> readTask = readValueTask.AsTask();

            while (enableKeepAlive && !readTask.IsCompleted)
            {
                Task delayTask = Task.Delay(keepAliveInterval, ct);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == readTask)
                    break;

                await destination.WriteAsync(SseKeepAliveFrameBytes, ct);
                await destination.FlushAsync(ct);
                onKeepAliveSent?.Invoke();
            }

            int bytesRead = await readTask;
            if (bytesRead == 0)
                break;

            // Decoded once and shared: this path forwards raw bytes rather than lines, so both the
            // usage sniffer and the terminator need the text form of the same chunk.
            if (usageSniffer is not null || terminator is not null)
            {
                string chunkText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                usageSniffer?.Feed(chunkText);
                terminator?.Feed(chunkText);
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await destination.FlushAsync(ct);
        }

        // Flush the sniffer first so the token totals it captured from the upstream's own usage frame
        // are available to a synthesized chunk, then emit whatever terminator the upstream omitted.
        usageSniffer?.Flush();
        await EmitTerminalFramesAsync(destination, terminator, tokenCounts, ct);
    }

    /// <summary>
    /// Copies an OpenAI chat-completion SSE stream while rewriting it: thinking blocks are relocated or
    /// stripped per <paramref name="thinkingMode"/> and inline XML tool calls become structured
    /// <c>tool_calls</c> deltas. Keep-alive comment frames are interleaved while the upstream is quiet.
    /// </summary>
    /// <param name="terminator">
    /// When supplied (Copilot-compatible stream), observes every forwarded line and appends the
    /// terminal frames the upstream omitted once the stream ends. Null leaves the stream exactly as
    /// the upstream sent it.
    /// </param>
    /// <param name="tokenCounts">
    /// Supplies the prompt/completion token totals for a synthesized usage chunk. Read only after the
    /// usage sniffer has flushed, so counts captured from the upstream's own frames are included.
    /// </param>
    /// <param name="useIrTranslation">
    /// Routes frame translation through the Phase-B IR translator instead of the legacy string-surgery
    /// rewriter. Defaults to false so the legacy path stays the coding default until the IR route has
    /// held under live traffic.
    /// </param>
    private static async Task CopyOpenAiChatCompletionSseStreamAsync(
        Stream source,
        Stream destination,
        ThinkingMode thinkingMode,
        bool enableKeepAlive,
        int keepAliveIntervalSeconds,
        CancellationToken ct,
        Action? onKeepAliveSent = null,
        Action<LlamaCppStreamChunk>? onUsage = null,
        Stream? rawCapture = null,
        IReadOnlySet<string>? declaredToolNames = null,
        OpenAiStreamTerminator? terminator = null,
        Func<(int PromptTokens, int CompletionTokens)>? tokenCounts = null,
        bool useIrTranslation = false)
    {
        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        TimeSpan keepAliveInterval = AppSettings.SseKeepAliveInterval(keepAliveIntervalSeconds);

        // Phase B: the IR-routed frame translator (Plans/20260914-proxy-meai-phase-b-design.md)
        // behind a default-off flag; golden byte-parity tests pin it to the legacy rewriter.
        Func<string, IEnumerable<string>> rewriter = useIrTranslation
            ? new OpenAiSseFrameTranslation(thinkingMode, declaredToolNames).Process
            : new OpenAiSseRewriter(thinkingMode, declaredToolNames).Process;

        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        while (!ct.IsCancellationRequested)
        {
            Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

            while (enableKeepAlive && !readTask.IsCompleted)
            {
                Task delayTask = Task.Delay(keepAliveInterval, ct);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == readTask)
                    break;

                await destination.WriteAsync(SseKeepAliveFrameBytes, ct);
                await destination.FlushAsync(ct);
                onKeepAliveSent?.Invoke();
            }

            string? line = await readTask;
            if (line is null)
                break;

            usageSniffer?.Feed(line + "\n");
            // The rewriter forwards [DONE] and usage frames verbatim, so observing the inbound line is
            // equivalent to observing what the client received.
            terminator?.ObserveLine(line);
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

            foreach (string outgoingLine in rewriter(line))
            {
                byte[] lineBytes = Encoding.UTF8.GetBytes(outgoingLine + "\n\n");
                await destination.WriteAsync(lineBytes, ct);
            }
            await destination.FlushAsync(ct);
        }

        // Flush the sniffer first so the token totals it captured from the upstream's own usage frame
        // are available to a synthesized chunk, then emit whatever terminator the upstream omitted.
        usageSniffer?.Flush();
        await EmitTerminalFramesAsync(destination, terminator, tokenCounts, ct);
    }

    /// <summary>
    /// Writes the terminal SSE frames a Copilot-compatible stream owes the client (an optional
    /// synthesized <c>usage</c> chunk, then <c>data: [DONE]</c>) when the upstream did not send them.
    /// No-op when there is no terminator, nothing is missing, or the client has already gone away.
    /// </summary>
    private static async Task EmitTerminalFramesAsync(
        Stream destination,
        OpenAiStreamTerminator? terminator,
        Func<(int PromptTokens, int CompletionTokens)>? tokenCounts,
        CancellationToken ct)
    {
        if (terminator is null || ct.IsCancellationRequested)
            return;

        terminator.Flush();

        (int promptTokens, int completionTokens) = tokenCounts?.Invoke() ?? (0, 0);

        foreach (string frame in terminator.BuildTerminalFrames(promptTokens, completionTokens))
        {
            byte[] frameBytes = Encoding.UTF8.GetBytes(frame);
            await destination.WriteAsync(frameBytes, ct);
        }

        await destination.FlushAsync(ct);
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
    ///   • forces <c>finish_reason</c> to <c>"tool_calls"</c> on the terminal chunk when tool calls were emitted,
    ///   • keeps only tool calls (structured or synthesised) whose function name appears in
    ///     <paramref name="declaredToolNames"/>; calls the client cannot bind are stripped or
    ///     returned as plain text.
    /// </summary>
    /// <summary>
        /// Exposes the legacy string-surgery SSE rewriter as a line transform, so the Phase-B IR frame
        /// translator can be pinned to it by golden parity tests. The delegate is stateful for the
        /// lifetime of the returned instance, matching how the streaming path uses it: the same
        /// instance must be fed the whole stream in order.
        /// </summary>
        internal static Func<string, IEnumerable<string>> CreateLegacySseRewriter(
            ThinkingMode thinkingMode, IReadOnlySet<string>? declaredToolNames)
        {
            OpenAiSseRewriter rewriter = new(thinkingMode, declaredToolNames);
            return rewriter.Process;
        }

        /// <summary>
        /// The OpenAI SSE passthrough rewriter: rewrites each <c>data:</c> frame's choice deltas in
        /// place, moving or stripping inline thought, filtering undeclared tool calls, and promoting
        /// inline markup calls into structured ones. Superseded by the IR-routed frame translator
        /// behind <c>AppSettings.UseIrTranslation</c>; retained as the default coding path.
        /// </summary>
        private sealed class OpenAiSseRewriter(ThinkingMode thinkingMode, IReadOnlySet<string>? declaredToolNames)
    {
        // Per-choice buffer of streamed delta.content prior to/within a tool_call block.
        private readonly Dictionary<int, ChoiceState> _state = [];

        private readonly IReadOnlySet<string>? _declaredToolNames = declaredToolNames;

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
                    cs = new ChoiceState(_declaredToolNames);
                    _state[index] = cs;
                }

                JsonObject? delta = choice["delta"] as JsonObject;

                // Handle inline <think>...</think> blocks per the configured thinking mode:
                // move them into reasoning_content (MoveToReasoningContent/ExtractThinkTags),
                // drop them entirely (StripFromOutput), or leave them untouched (LeaveInline/Off).
                if (delta is JsonObject inlineDelta && _thinkingMode != ThinkingMode.LeaveInline)
                {
                    if (!_thinkExtractors.TryGetValue(index, out ThinkTagExtractor? extractor))
                    {
                        extractor = new ThinkTagExtractor(_thinkTags.OpenTag, _thinkTags.CloseTag);
                        _thinkExtractors[index] = extractor;
                    }

                    string incoming = inlineDelta["content"] is JsonValue contentValue
                        && contentValue.TryGetValue(out string? contentStr)
                            ? contentStr ?? string.Empty
                            : string.Empty;

                    (string reasoning, string content) = extractor.Process(incoming);

                    if (_thinkingMode == ThinkingMode.StripFromOutput)
                    {
                        // Thinking must not reach the client at all; drop any native
                        // reasoning_content the upstream may have sent as well.
                        inlineDelta.Remove("reasoning_content");
                    }
                    else if (reasoning.Length > 0)
                    {
                        string existingReasoning = inlineDelta["reasoning_content"] is JsonValue erv
                            && erv.TryGetValue(out string? ervStr) ? ervStr ?? string.Empty : string.Empty;
                        inlineDelta["reasoning_content"] = JsonValue.Create(existingReasoning + reasoning);
                    }

                    // Rewrite content based on what the extractor produced:
                    // - non-empty remainder → set it (will be post-processed by tool-call ingestion below)
                    // - empty remainder but incoming was present (thinking consumed it or partial-tag
                    //   buffering) → remove the key so reasoning-only / role-only deltas are clean
                    // - no incoming content at all → leave delta untouched (don't fabricate "")
                    if (content.Length > 0)
                        inlineDelta["content"] = JsonValue.Create(content);
                    else if (incoming.Length > 0)
                        inlineDelta.Remove("content");
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

                // Structured tool calls naming functions the client did not declare must be
                // dropped: OpenAI clients such as Copilot render them as function parts they
                // cannot bind (the VS Copilot UI crashes on those).
                if (delta is not null && delta["tool_calls"] is JsonArray nativeCalls)
                {
                    List<JsonNode?> drop = [];
                    foreach (JsonNode? callNode in nativeCalls)
                    {
                        if (callNode is not JsonObject call)
                            continue;

                        int callIndex = call["index"] is JsonValue cix && cix.TryGetValue(out int idx) ? idx : 0;
                        bool valid;
                        if (call["function"] is JsonObject fnNode
                            && fnNode["name"] is JsonValue nameVal
                            && nameVal.TryGetValue(out string? callName)
                            && !string.IsNullOrEmpty(callName))
                        {
                            valid = IsToolNameAllowed(_declaredToolNames, callName);
                        }
                        else
                        {
                            // Argument fragment of an already-evaluated call — inherit its verdict.
                            valid = !cs.NativeCallValidity.TryGetValue(callIndex, out bool previous) || previous;
                        }

                        cs.NativeCallValidity[callIndex] = valid;
                        if (valid)
                            cs.HadValidToolCall = true;
                        else
                            drop.Add(callNode);
                    }

                    foreach (JsonNode? dropped in drop)
                        nativeCalls.Remove(dropped);

                    if (nativeCalls.Count == 0)
                        delta.Remove("tool_calls");
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

                // Finish-reason bookkeeping: keep "tool_calls" while calls were emitted; when
                // every call was filtered out, report "stop" so the client ends the turn
                // instead of waiting for tool results that will never arrive.
                if (choice["finish_reason"] is JsonValue fr
                    && fr.TryGetValue(out string? frStr)
                    && !string.IsNullOrEmpty(frStr))
                {
                    if (cs.EmittedToolCallCount > 0)
                        choice["finish_reason"] = "tool_calls";
                    else if (frStr == "tool_calls" && !cs.HadValidToolCall)
                        choice["finish_reason"] = "stop";
                }
            }

            yield return $"data: {root.ToJsonString(_jsonOptions)}";

            foreach (JsonObject extra in extraFrames)
                yield return $"data: {extra.ToJsonString(_jsonOptions)}";
        }

        private sealed class ChoiceState(IReadOnlySet<string>? declaredToolNames)
        {
            private readonly IReadOnlySet<string>? _declaredToolNames = declaredToolNames;
            private readonly StringBuilder _toolBuffer = new();
            private bool _inToolCall;
            public int EmittedToolCallCount { get; private set; }

            /// <summary>Filter verdict per native (upstream-sent) tool-call stream index.</summary>
            public Dictionary<int, bool> NativeCallValidity { get; } = [];

            /// <summary>True when at least one upstream tool-call delta survived filtering.</summary>
            public bool HadValidToolCall { get; set; }

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

            // The incoming choice object is not read: the frame is rebuilt from the parsed XML and the
            // buffered call state, so only the index and the outgoing frame list are needed.
            private void EmitToolCallFromBuffer(JsonObject root, JsonObject _, int choiceIndex, List<JsonObject> extraFrames)
            {
                string xml = _toolBuffer.ToString();
                Match m = XmlToolCallRegex().Match(xml);
                if (!m.Success) return;

                string name = m.Groups["name"].Value.Trim();

                // Tool-less or differently-declared clients cannot execute this call: hand the
                // raw block back as visible content instead of a tool-call delta.
                if (!IsToolNameAllowed(_declaredToolNames, name))
                {
                    extraFrames.Add(BuildChoiceFrame(root, choiceIndex, new JsonObject { ["content"] = xml.Trim() }));
                    return;
                }

                Dictionary<string, object?> args = new(StringComparer.OrdinalIgnoreCase);
                foreach (Match pm in XmlParameterRegex().Matches(m.Groups["body"].Value))
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

                extraFrames.Add(BuildChoiceFrame(root, choiceIndex, toolDelta));
            }

            /// <summary>
            /// Builds a synthesised chunk envelope (mirroring the original frame's metadata)
            /// carrying one choice delta.
            /// </summary>
            private static JsonObject BuildChoiceFrame(JsonObject root, int choiceIndex, JsonObject deltaObject) => new()
            {
                ["id"] = root["id"]?.DeepClone(),
                ["object"] = root["object"]?.DeepClone(),
                ["created"] = root["created"]?.DeepClone(),
                ["model"] = root["model"]?.DeepClone(),
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = choiceIndex,
                    ["delta"] = deltaObject,
                    ["finish_reason"] = null,
                }),
            };
        }
    }

    /// <summary>
    /// Normalises a JSON request body before forwarding to the upstream, discarding the
    /// <c>stream_options</c> details captured along the way. Prefer the overload taking an
    /// <see cref="StreamOptionsInfo"/> out-parameter on streaming paths, where the response side needs
    /// to know what the client asked for.
    /// </summary>
    internal static string NormalizeRequestBody(string json, AppSettings settings, RequestLog log, Func<string, bool>? shouldApplyThinkingCompatibility = null)
        => NormalizeRequestBody(json, settings, log, shouldApplyThinkingCompatibility, out _);

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
    ///   <item>Drops the <c>stream_options</c> block when
    ///         <see cref="ModelMapping.EnableCopilotCompatibility"/> applies to the effective model,
    ///         reporting it through <paramref name="streamOptions"/> so the response path can
    ///         synthesize what the client asked for.</item>
    /// </list>
    /// Returns the original text unchanged if the body isn't valid JSON.
    /// </summary>
    /// <param name="streamOptions">
    /// Receives whether the body carried <c>stream_options</c>, whether it set <c>include_usage</c>,
    /// and whether the proxy stripped the block. <see cref="StreamOptionsInfo.None"/> when the body
    /// was not valid JSON, since nothing could be inspected.
    /// </param>
    internal static string NormalizeRequestBody(
        string json,
        AppSettings settings,
        RequestLog log,
        Func<string, bool>? shouldApplyThinkingCompatibility,
        out StreamOptionsInfo streamOptions)
    {
        streamOptions = StreamOptionsInfo.None;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Read model name for logging and rewriting.
            string original = root.TryGetProperty("model", out JsonElement modelEl)
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;

            // Context-summarize (/compact) redirect: when the request is detected as a Copilot
            // /compact summary request and the mapping opted in via RedirectManualCompaction with
            // a usable compaction target, route the whole request to that target — its upstream,
            // sampling, and instruction-set settings all apply.
            string? firstContent = GetFirstMessageContent(root);
            string effectiveModel = ResolveEffectiveModel(settings, original, firstContent);
            bool compactRedirected = !string.Equals(effectiveModel, original, StringComparison.OrdinalIgnoreCase);

            string resolved = settings.ResolveModelName(effectiveModel);
            log.Model = effectiveModel;
            if (compactRedirected)
            {
                log.OriginalModel = original;
                Log.Debug(
                    "Context-summarize (/compact) request for {OriginalModel} redirected to compaction model {CompactModel}",
                    original, effectiveModel);
            }
            else if (IsContextSummarizeRequest(firstContent))
            {
                // Only explain a non-redirect when the request actually WAS a /compact request.
                // Logging this for every ordinary chat request claimed a redirect had been
                // "not applied" to requests that never asked for compaction.
                Log.Debug(
                    "Context-summarize (/compact) request for {OriginalModel} is not redirected: {Reason}",
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

            // Copilot compatibility: many OpenAI-compatible local servers reject or silently ignore
            // the stream_options block, and some open the stream and then never deliver the terminal
            // usage chunk it asks for. Drop the block and take over producing what the client asked
            // for. The response path reads the captured info to synthesize the missing frames.
            // Unmapped models default to compatible, matching ShouldApplyCopilotCompatibility.
            bool copilotCompatible = normalizeMapping?.EnableCopilotCompatibility ?? true;
            (bool hadStreamOptions, bool includeUsage) = ReadStreamOptions(root);
            bool stripStreamOptions = copilotCompatible && hadStreamOptions;

            streamOptions = new StreamOptionsInfo(hadStreamOptions, includeUsage, stripStreamOptions);

            if (stripStreamOptions)
                Log.Debug(
                    "Stripping stream_options from the upstream request for {Model} (include_usage={IncludeUsage}); the proxy will synthesize the terminal usage chunk",
                    effectiveModel, includeUsage);

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
                && !rewriteReasoningEffort
                && !stripStreamOptions)
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
                else if (prop.Name.Equals("stream_options", StringComparison.OrdinalIgnoreCase))
                {
                    // Copilot compatibility strips the block (see stripStreamOptions); the proxy then
                    // synthesizes the terminal usage chunk the client asked for. When the mapping opted
                    // out, forward it unchanged so the upstream stays responsible.
                    if (!stripStreamOptions)
                        prop.WriteTo(writer);
                }
                else if (prop.Name.Equals("messages", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Array
                      && (hasConsecutiveSystemMessages || hasTrailingAssistantPrefill || shouldInjectInstructions))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();

                    List<JsonElement> messages = [.. prop.Value.EnumerateArray()];

                    // System-prompt composition is shared with /api/chat and /api/generate: the
                    // instruction-set text and every leading system message are folded into exactly
                    // one system message, because strict chat templates reject a second one.
                    int leadingSystemCount = SystemPromptComposer.LeadingSystemCount(messages);
                    bool recomposeSystem = SystemPromptComposer.ShouldRecompose(injectedInstructions, leadingSystemCount);

                    if (recomposeSystem)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", SystemPromptComposer.Merge(
                            injectedInstructions,
                            SystemPromptComposer.LeadingSystemContents(messages, leadingSystemCount)));
                        writer.WriteEndObject();
                    }

                    // When nothing is being recomposed the client's lone system message is forwarded
                    // verbatim so any extra properties it carries survive.
                    for (int i = recomposeSystem ? leadingSystemCount : 0; i < messages.Count; i++)
                    {
                        JsonElement msg = messages[i];

                        if (hasTrailingAssistantPrefill && i == messages.Count - 1 && IsAssistantResponsePrefill(msg))
                            continue;

                        msg.WriteTo(writer);
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

    /// <summary>
    /// Redacts a request body from a caller with no resolved model mapping — an unknown endpoint, an
    /// unsupported call, or a health probe. Such a request is by definition not carrying a model
    /// prompt, and its payload is the main thing that makes the call diagnosable ("what is this app
    /// actually sending me?"), so whole-body masking would defeat the purpose of capturing it.
    /// </summary>
    /// <remarks>
    /// Falls back to field-level redaction, which still masks credentials and known sensitive JSON
    /// fields (<see cref="AppSettings.RedactSensitiveJsonFields"/> defaults to on) while leaving the
    /// payload structure readable. Whole-body masking remains the behaviour for a request that does
    /// resolve to a mapping, where the body is prompt content.
    /// </remarks>
    internal static string RedactUnmappedRequestBodyForLog(string body)
    {
        // Whole-body masking is the default for a mapped request because there the body is prompt
        // content. Here there is no mapping to consult, and field-level redaction already masks
        // credentials and known sensitive fields, so the payload stays readable.
        return RedactSensitiveJsonFields(body);
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
    /// Formats a header collection as a <c>Name: value</c> block for the log, masking the value of
    /// every credential-bearing header.
    /// </summary>
    /// <remarks>
    /// Credential headers are ALWAYS masked, independently of the redaction settings: a log that
    /// stores a bearer token is a credential leak, and unlike a body there is no diagnostic value
    /// in keeping it. The block is truncated at <see cref="RequestLog.MaxHeaderBlockChars"/> so a
    /// pathological set (a proxy chain appending hundreds of forwarding entries) cannot write an
    /// unbounded blob into every row.
    /// </remarks>
    internal static string? FormatHeadersForLog(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        StringBuilder sb = new();
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (sb.Length >= RequestLog.MaxHeaderBlockChars)
            {
                sb.Append(RedactedValueText)
                    .Append(" (truncated at ")
                    .Append(RequestLog.MaxHeaderBlockChars)
                    .Append(" characters)");
                break;
            }

            sb.Append(header.Key)
                .Append(": ")
                .Append(IsSensitiveHeaderName(header.Key) ? RedactedValueText : header.Value)
                .Append('\n');
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Reports whether a header carries a credential, by name. Matched case-insensitively and by
    /// substring so vendor-prefixed variants (e.g. <c>x-goog-api-key</c>) are covered rather than
    /// only the exact names.
    /// </summary>
    private static bool IsSensitiveHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Exact matches first: short names that must not be matched loosely.
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Api-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Suffix/substring matches cover the long tail of vendor-specific credential headers
        // (x-auth-token, x-access-token, x-goog-api-key, x-amz-security-token, ...).
        return name.Contains("-api-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-auth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the value of every credential-bearing header in a pre-formatted block.</summary>
    /// <remarks>
    /// Used for response headers, which are read back off the response as a name/value collection
    /// and formatted by the caller. Kept separate from <see cref="FormatHeadersForLog"/> so a caller
    /// holding an already-formatted string can still be scrubbed.
    /// </remarks>
    internal static string? RedactHeaderBlock(string? block)
    {
        if (string.IsNullOrWhiteSpace(block))
            return block;

        StringBuilder sb = new();
        foreach (string line in block.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            string name = line[..colon].Trim();
            sb.Append(name)
                .Append(": ")
                .Append(IsSensitiveHeaderName(name) ? RedactedValueText : line[(colon + 1)..].TrimStart())
                .Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
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
        // The Hidden flag is honored only by the two list endpoints (/v1/models and /api/tags)
        // so clients that auto-discover models do not pick up embeddings-only or otherwise
        // restricted mappings. Routing, heartbeats, keep-alive, and eligibility as a compaction
        // target are unaffected — a hidden mapping remains fully usable when named explicitly.
        var tags = new OllamaTagsResponse
        {
            Models = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !m.Hidden && !string.IsNullOrWhiteSpace(m.ProxyName))
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
        // The Hidden flag is honored only by the two list endpoints (/v1/models and /api/tags)
        // so clients that auto-discover models do not pick up embeddings-only or otherwise
        // restricted mappings. Routing, heartbeats, keep-alive, and eligibility as a compaction
        // target are unaffected — a hidden mapping remains fully usable when named explicitly.
        var response = new LlamaCppModelsResponse
        {
            Data = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !m.Hidden && !string.IsNullOrWhiteSpace(m.ProxyName))
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

    // ── Manual /compact forwarding ──

    /// <summary>
    /// Rewrites the top-level <c>model</c> property of a manual compaction request body so it
    /// names the model the target's upstream actually knows. Clients address the proxy by its
    /// display name (e.g. <c>claude-fast</c>) while upstreams expect their own identifier
    /// (e.g. a <c>.gguf</c> path), so the field must be swapped before forwarding. The rest of
    /// the body is passed through untouched — compaction requests are already valid
    /// chat-completion payloads and must not be reshaped. Returns the original text unchanged
    /// when it is not valid JSON so the caller can surface the upstream's own error.
    /// </summary>
    /// <param name="bodyText">The original request body.</param>
    /// <param name="upstreamModelName">The target mapping's upstream model identifier.</param>
    internal static string RewriteCompactTargetModel(string bodyText, string upstreamModelName)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return bodyText;

        try
        {
            if (JsonNode.Parse(bodyText) is not JsonObject root)
                return bodyText;

            root["model"] = upstreamModelName;
            return root.ToJsonString(_jsonOptions);
        }
        catch (JsonException)
        {
            return bodyText;
        }
    }

    /// <summary>
    /// Removes the top-level <c>stream_options</c> member from an OpenAI-style request body, reporting
    /// what the client had asked for. Used on the manual compaction endpoints, whose bodies bypass
    /// <see cref="NormalizeRequestBody"/> so that a compaction request is otherwise never reshaped.
    /// Returns the body unchanged when it carries no such member or is not valid JSON.
    /// </summary>
    /// <param name="bodyText">The request body about to be forwarded upstream.</param>
    /// <returns>The body to forward, plus what was stripped.</returns>
    internal static (string Body, StreamOptionsInfo StreamOptions) StripStreamOptions(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return (bodyText, StreamOptionsInfo.None);

        try
        {
            if (JsonNode.Parse(bodyText) is not JsonObject root)
                return (bodyText, StreamOptionsInfo.None);

            string? matchedKey = root
                .Select(property => property.Key)
                .FirstOrDefault(key => key.Equals("stream_options", StringComparison.OrdinalIgnoreCase));

            if (matchedKey is null)
                return (bodyText, StreamOptionsInfo.None);

            bool includeUsage = root[matchedKey] is JsonObject options
                && options.Any(property =>
                    property.Key.Equals("include_usage", StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue value
                    && value.TryGetValue(out bool flag)
                    && flag);

            root.Remove(matchedKey);
            return (root.ToJsonString(_jsonOptions), new StreamOptionsInfo(true, includeUsage, true));
        }
        catch (JsonException)
        {
            return (bodyText, StreamOptionsInfo.None);
        }
    }

    /// <summary>
    /// Resolves which model a manual compaction request should be sent to. This is the single
    /// authority for the manual-compaction routing decision, shared by both <c>/compact</c>
    /// endpoints and by the signature-based redirect on the chat paths so they cannot disagree.
    /// </summary>
    /// <remarks>
    /// Redirection requires <b>both</b> <see cref="ModelMapping.RedirectManualCompaction"/> and a
    /// compaction target that is itself a usable mapping (found, enabled, and has an upstream
    /// URL). When either is missing the request goes to the model the client asked for, so the
    /// model handles its own compaction — there is no global fallback target and the proxy never
    /// invents one. A configured-but-unusable target is reported as a warning rather than being
    /// silently ignored, because it means the user's redirect setting is not taking effect.
    /// </remarks>
    /// <param name="mapping">The mapping that matches the request's model.</param>
    /// <returns>
    /// The mapping to forward to, and whether it differs from the request's own mapping.
    /// </returns>
    private (ModelMapping Target, bool Redirected) ResolveManualCompactTarget(ModelMapping mapping) =>
        ResolveManualCompactTarget(_settings, mapping);

    /// <summary>
    /// Static form of the manual-compaction target resolver so both the instance handlers and
    /// the static signature-based redirect (<see cref="ResolveEffectiveModel"/>) share one set of
    /// gates and cannot drift apart.
    /// </summary>
    /// <remarks>
    /// The target is resolved through <see cref="AppSettings.FindContextSummarizeTarget"/>, never
    /// by reading <see cref="ModelMapping.ContextSummarizeModelId"/> directly. The ID is a
    /// surrogate key that can end up belonging to a different mapping, after which it still
    /// resolves — just to a model the user never chose. The finder prefers the stored proxy name
    /// and only falls back to the ID.
    /// </remarks>
    internal static (ModelMapping Target, bool Redirected) ResolveManualCompactTarget(
        AppSettings settings, ModelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mapping);

        if (!mapping.RedirectManualCompaction)
            return (mapping, false);

        // No target configured, or the configured target no longer exists. Either way the request
        // goes to the model the client asked for, so that model produces its own summary.
        ModelMapping? candidate = settings.FindContextSummarizeTarget(mapping);
        if (candidate is null)
            return (mapping, false);

        if (!candidate.IsEnabled)
        {
            Log.Warning(
                "Manual compaction for {Model} is configured to redirect to {Target}, but that mapping is disabled. Forwarding to {Model} instead.",
                mapping.ProxyName, candidate.ProxyName, mapping.ProxyName);
            return (mapping, false);
        }

        if (string.IsNullOrWhiteSpace(candidate.UpstreamUrl))
        {
            Log.Warning(
                "Manual compaction for {Model} is configured to redirect to {Target}, but that mapping has no upstream URL. Forwarding to {Model} instead.",
                mapping.ProxyName, candidate.ProxyName, mapping.ProxyName);
            return (mapping, false);
        }

        // A mapping that points its compaction target at itself would loop back to the same
        // upstream; treat it as no redirect so the logging stays truthful.
        if (candidate.Id == mapping.Id)
            return (mapping, false);

        Log.Debug(
            "Manual compaction for {Model} redirected to compaction model {Target}",
            mapping.ProxyName, candidate.ProxyName);

        return (candidate, true);
    }

    /// <summary>
    /// Forwards a manual compaction request to <paramref name="targetMapping"/>'s upstream
    /// <c>/v1/chat/completions</c> and returns that model's response to the client unchanged.
    /// The model produces the summary itself, which is what "proxy the compaction" means: the
    /// proxy resolves the target and relays the conversation, it does not synthesize a summary.
    /// Handles both streaming (SSE with keep-alive frames) and non-streaming responses, and mirrors
    /// the passthrough path's error handling so a failed compaction surfaces as a real error
    /// rather than a silently closed stream.
    /// </summary>
    /// <param name="targetMapping">
    /// The mapping to forward to — the compaction target when the request is redirected,
    /// otherwise the request's own mapping.
    /// </param>
    /// <param name="bodyText">The original request body.</param>
    /// <param name="originalModel">The model name the client asked for, for logging.</param>
    /// <param name="redirected">Whether the request was redirected to a separate compaction model.</param>
    /// <remarks>
    /// Handles its own failures. Whether a 500 can still be written depends on whether the SSE
    /// headers were pre-committed for a streaming request, and that state only exists inside this
    /// method — <see cref="HttpListenerResponse"/> exposes no "headers sent" flag — so the error
    /// response is produced here rather than by the callers.
    /// </remarks>
    private async Task ForwardCompactToUpstreamAsync(
        ModelMapping targetMapping,
        string bodyText,
        string originalModel,
        bool redirected,
        HttpListenerResponse resp,
        RequestLog log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetMapping);

        bool headersPreCommitted = false;

        try
        {
            // Upstreams identify models by ModelName, not by the proxy's display name.
            string upstreamModel = string.IsNullOrWhiteSpace(targetMapping.ModelName)
                ? targetMapping.ProxyName
                : targetMapping.ModelName;

            string forwardedBody = RewriteCompactTargetModel(bodyText, upstreamModel);

            // Compaction bodies bypass NormalizeRequestBody so they are never otherwise reshaped, so
            // the stream_options block is stripped here instead. The client is still owed the terminal
            // usage chunk it asked for, which the terminator below synthesizes.
            StreamOptionsInfo compactStreamOptions = StreamOptionsInfo.None;
            if (ShouldApplyCopilotCompatibility(targetMapping.ProxyName))
                (forwardedBody, compactStreamOptions) = StripStreamOptions(forwardedBody);

            // Pre-flight: a manual /compact body is forwarded verbatim, so unlike the proactive and
            // reactive paths it has never been measured against the target model's window. An
            // embeddings-only or otherwise small compaction target receives Copilot's entire
            // conversation and the upstream rejects it with a raw overflow error the client cannot
            // act on. Run it through the same map-reduce summarizer when the target allows it, and
            // otherwise reject here with an actionable message instead of paying for a round-trip
            // that is guaranteed to fail.
            //
            // This runs before any SSE headers are committed, so a rejection can still be delivered
            // as a real HTTP status. A null result means the response has already been written.
            string? preparedBody = await PrepareManualCompactBodyAsync(
                targetMapping, forwardedBody, resp, log, ct);
            if (preparedBody is null)
                return;

            forwardedBody = preparedBody;

            var (baseUrl, timeout, apiKey) = ResolveUpstream(targetMapping.ProxyName);

            Log.Information(
                redirected
                    ? "Manual compaction for {OriginalModel} redirected to compaction model {TargetModel} at {BaseUrl}"
                    : "Manual compaction for {OriginalModel} forwarded to its own upstream {TargetModel} at {BaseUrl} (no redirect configured)",
                originalModel, upstreamModel, baseUrl);

            if (_settings.DebugMode && log.DebugSummary is not null)
            {
                log.DebugSummary += "\n" + DebugNotes.ManualCompactionTarget(originalModel, upstreamModel, redirected);
                log.DebugSummary += "\n" + DebugNotes.UpstreamRouting(
                    targetMapping.ProxyName, baseUrl, !string.IsNullOrWhiteSpace(apiKey), timeout);
            }

            using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(forwardedBody)),
            };
            upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            ApplyApiKey(upstreamReq, apiKey);

            // For streaming requests, pre-commit the SSE headers and pump keep-alive frames while
            // the upstream summarizes. Compaction can take a long time and clients with a short
            // network timeout would otherwise give up silently.
            bool isStreamingRequest = IsStreamingJsonBody(forwardedBody);

            using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task preResponseKeepAliveTask = Task.CompletedTask;

            if (isStreamingRequest && ShouldEmitSseKeepAlive(targetMapping.ProxyName))
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/event-stream";
                resp.SendChunked = true;
                resp.KeepAlive = true;
                headersPreCommitted = true;

                // Writing and flushing this frame is what actually puts the headers on the wire;
                // setting StatusCode/ContentType alone leaves the client with nothing to read.
                await resp.OutputStream.WriteAsync(SseKeepAliveFrameBytes, ct);
                await resp.OutputStream.FlushAsync(ct);

                preResponseKeepAliveTask = PumpPreResponseSseKeepAliveAsync(
                    resp.OutputStream,
                    _settings.SseKeepAliveIntervalSeconds,
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
                await preResponseCts.CancelAsync();
                await preResponseKeepAliveTask;
            }

            using HttpResponseMessage ownedUpstreamResponse = upstreamResp;

            log.StatusCode = (int)upstreamResp.StatusCode;

            if (!upstreamResp.IsSuccessStatusCode)
            {
                string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);

                log.Status = RequestStatus.Error;
                log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
                if (_settings.CollectResponseDetails)
                    log.ResponseBody = errorBody;
                if (_settings.DebugMode)
                    log.UpstreamResponseBody = RedactResponseBodyForLog(errorBody, originalModel);

                Log.Warning(
                    "Manual compaction failed for model {OriginalModel} at upstream {TargetModel}: {StatusCode}",
                    originalModel, upstreamModel, (int)upstreamResp.StatusCode);

                if (headersPreCommitted)
                {
                    // Headers already sent as 200/SSE — emit the error as a data frame so the
                    // client sees it rather than getting a silent stream close, then terminate the
                    // stream: a client awaiting the terminal event would otherwise block forever.
                    string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(errorBody)},\"code\":{(int)upstreamResp.StatusCode}}}}}\n\ndata: [DONE]\n\n";
                    await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorFrame), ct);
                }
                else
                {
                    resp.StatusCode = (int)upstreamResp.StatusCode;
                    await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody), ct);
                }

                resp.OutputStream.Close();
                return;
            }

            if (!headersPreCommitted)
            {
                resp.StatusCode = (int)upstreamResp.StatusCode;

                string? mediaType = upstreamResp.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(mediaType))
                    resp.ContentType = mediaType;

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

            await using Stream upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);

            bool collectResponse = _settings.CollectResponseDetails;
            bool debugCapture = _settings.DebugMode;

            using CountingStream countingStream = new(resp.OutputStream);

            if (IsServerSentEventsResponse(upstreamResp))
            {
                void onUsage(LlamaCppStreamChunk chunk) => FillTokenStats(log, chunk);

                // Guarantee the compaction stream reaches its terminal event, for the same reason the
                // passthrough path does: the client that asked for a summary awaits `data: [DONE]`.
                OpenAiStreamTerminator? streamTerminator = ShouldApplyCopilotCompatibility(targetMapping.ProxyName)
                    ? new OpenAiStreamTerminator(originalModel, compactStreamOptions.MustSynthesizeUsage)
                    : null;
                (int PromptTokens, int CompletionTokens) tokenCounts() => (log.PromptTokens, log.CompletionTokens);

                if (collectResponse || debugCapture)
                {
                    using ResponseCaptureStream captureStream = new(countingStream);
                    await CopyStreamWithSseKeepAliveAsync(
                        upstreamStream,
                        captureStream,
                        ShouldEmitSseKeepAlive(targetMapping.ProxyName),
                        _settings.SseKeepAliveIntervalSeconds,
                        ct,
                        () => _stats.IncrementSseKeepAlive(targetMapping.ProxyName),
                        onUsage,
                        streamTerminator,
                        tokenCounts);

                    string forwardedText = captureStream.GetCapturedText();
                    if (collectResponse)
                        log.ResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
                    if (debugCapture)
                        log.UpstreamResponseBody = RedactResponseBodyForLog(forwardedText, originalModel);
                }
                else
                {
                    await CopyStreamWithSseKeepAliveAsync(
                        upstreamStream,
                        countingStream,
                        ShouldEmitSseKeepAlive(targetMapping.ProxyName),
                        _settings.SseKeepAliveIntervalSeconds,
                        ct,
                        () => _stats.IncrementSseKeepAlive(targetMapping.ProxyName),
                        onUsage,
                        streamTerminator,
                        tokenCounts);
                }
            }
            else
            {
                // Buffer once so token usage and the optional captures read from the same body
                // that is forwarded to the client.
                void onBody(string body)
                {
                    FillTokenStats(log, TryParseChunk(body));
                    if (collectResponse)
                        log.ResponseBody = RedactResponseBodyForLog(body, originalModel);
                    if (debugCapture)
                        log.UpstreamResponseBody = RedactResponseBodyForLog(body, originalModel);
                }

                // ThinkingMode.LeaveInline with no tool extraction forwards the body byte-for-byte.
                await CopyNonStreamingChatResponseAsync(
                    upstreamStream,
                    countingStream,
                    ThinkingMode.LeaveInline,
                    ct,
                    onBody,
                    extractToolCalls: false);
            }

            log.ResponseBytes = countingStream.BytesWritten;
            resp.OutputStream.Close();
            log.Status = RequestStatus.Success;

            Log.Information(
                "Manual compaction completed for {OriginalModel} using {TargetModel} ({ResponseBytes} bytes)",
                originalModel, upstreamModel, log.ResponseBytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual compaction for model {Model} threw", originalModel);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;

            // Once the SSE headers are committed the status code is fixed at 200; report the
            // failure inside the stream instead so the client is not left with a silent close.
            // The terminator frame is required as well: a client awaiting `data: [DONE]` blocks
            // indefinitely without it.
            if (headersPreCommitted)
            {
                try
                {
                    string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(ex.Message)},\"code\":500}}}}\n\ndata: [DONE]\n\n";
                    await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorFrame), CancellationToken.None);
                    resp.OutputStream.Close();
                }
                catch (Exception writeEx)
                {
                    Log.Debug(writeEx, "Could not write a compaction error frame for model {Model}", originalModel);
                }

                return;
            }

            resp.StatusCode = 500;
            await WriteJsonAsync(resp, new
            {
                error = "Internal error during manual compaction. Please retry.",
            }, ct);
        }
    }

    /// <summary>
    /// Ensures a manual compaction body will actually fit in the target model's context before it is
    /// forwarded. The manual <c>/compact</c> endpoints relay the body verbatim, so — unlike the
    /// proactive and reactive paths — nothing has measured it against the target's window, and an
    /// oversized conversation aimed at a small compaction target (an embeddings model, for example)
    /// fails upstream with an error the client cannot act on.
    /// </summary>
    /// <remarks>
    /// Map-reduce summarization runs only when the <b>target</b> mapping has automatic compaction
    /// enabled for the OpenAI path; the proxy never compacts behind a mapping's back. Redirection
    /// itself is decided earlier by <see cref="ResolveManualCompactTarget"/>, so by the time this
    /// runs the target is already the model that will do the summarizing.
    /// <para>
    /// The rejection happens before any SSE headers are committed, so it can still be delivered as a
    /// real 413 status rather than a frame buried in an open stream.
    /// </para>
    /// </remarks>
    /// <param name="targetMapping">The mapping whose upstream will receive the compaction request.</param>
    /// <param name="bodyText">The upstream-bound compaction body.</param>
    /// <param name="resp">Client response, written to when the request is rejected.</param>
    /// <param name="log">Request log, updated when the request is rejected.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The body to forward (unchanged, or compacted), or null when a rejection response has already
    /// been written and the caller must stop.
    /// </returns>
    private async Task<string?> PrepareManualCompactBodyAsync(
        ModelMapping targetMapping,
        string bodyText,
        HttpListenerResponse resp,
        RequestLog log,
        CancellationToken ct)
    {
        // Chunk sizing uses the compaction window (explicit value or the global conservative cap),
        // never the advertised default: overestimating a compact model's capacity is precisely what
        // made every attempt overflow.
        int compactionWindow = targetMapping.GetCompactionContextWindow(_settings.CompactionFallbackContextTokens);
        int estimated = EstimateTokenCount(bodyText);

        // Same budget the map-reduce path chunks against, so a body measured as fitting here is a
        // body the summarizer can actually accept.
        int budget = AutoCompactionService.GetSummaryPromptBudget(compactionWindow);
        if (estimated <= budget)
            return bodyText;

        if (!targetMapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI))
        {
            string message =
                $"Manual compaction for '{targetMapping.ProxyName}' needs ~{estimated} tokens but that model's "
                + $"compaction budget is {budget} tokens (context window {compactionWindow}, less the room "
                + "reserved for the summary reply). "
                + "Enable Auto-Compact Paths for this model so the proxy can summarize the conversation in "
                + "chunks, or select a Compaction Model with a larger context window on the model that owns "
                + "the conversation.";

            Log.Warning(
                "Manual compaction rejected for model {Model}: ~{Estimated} tokens exceeds the {Budget} token budget "
                + "for a {Window} token context window, and automatic compaction is not enabled for this path.",
                targetMapping.ProxyName, estimated, budget, compactionWindow);

            log.StatusCode = 413;
            log.Status = RequestStatus.Error;
            log.ErrorMessage = message;

            resp.StatusCode = 413;
            await WriteJsonAsync(resp, new
            {
                error = new
                {
                    message,
                    type = "context_length_exceeded",
                    param = "model",
                    code = "compaction_context_too_large",
                },
            }, ct);
            return null;
        }

        var (baseUrl, timeout, apiKey) = ResolveUpstream(targetMapping.ProxyName);
        string compactModelName = string.IsNullOrWhiteSpace(targetMapping.ModelName)
            ? targetMapping.ProxyName
            : targetMapping.ModelName;
        int maxTokensPerChunk = budget;

        Log.Information(
            "Manual compaction for {Model} exceeds its {Budget} token budget (~{Estimated} tokens); "
            + "running chunked map-reduce summarization against the target's own upstream",
            targetMapping.ProxyName, budget, estimated);

        string sessionKey = $"manual:{targetMapping.ProxyName}:{bodyText.GetHashCode():X8}";
        string? compacted = await _autoCompactionService.CompactAsync(
            targetMapping,
            bodyText,
            sessionKey,
            baseUrl,
            apiKey,
            timeout,
            maxTokensPerChunk,
            compactModelName,
            compactionWindow,
            compactionWindow,
            ct,
            CompactionFormat.Proxy);

        if (compacted is null)
        {
            string message =
                $"Manual compaction for '{targetMapping.ProxyName}' needs ~{estimated} tokens, over its "
                + $"{budget} token budget, and chunked summarization did not produce a smaller result. "
                + "Check that the model is reachable and try a larger Compaction Model.";

            Log.Warning("Manual compaction map-reduce failed for model {Model}", targetMapping.ProxyName);

            log.StatusCode = 413;
            log.Status = RequestStatus.Error;
            log.ErrorMessage = message;

            resp.StatusCode = 413;
            await WriteJsonAsync(resp, new { error = message }, ct);
            return null;
        }

        _autoCompactionService.RecordSuccess(sessionKey);
        Log.Information(
            "Manual compaction for {Model} compacted ~{Original} → ~{Compacted} est. tokens",
            targetMapping.ProxyName, estimated, EstimateTokenCount(compacted));
        return compacted;
    }

    // ── POST /v1/responses/compact → OpenAI-compatible conversation compaction ──

    /// <summary>
    /// Handles <c>POST /v1/responses/compact</c> — forwards the compaction request to a model's
    /// upstream <c>/v1/chat/completions</c> and returns that model's response unchanged. The
    /// target is the mapping's configured compaction model when the mapping opts in via
    /// <see cref="ModelMapping.RedirectManualCompaction"/>, otherwise the request's own model.
    /// The proxy never synthesizes a summary here: a model always produces it.
    /// </summary>
    private async Task HandleCompactAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string bodyText = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);

        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.RequestBody = bodyText;

        // Extract the model name from the request body and resolve the compaction target.
        string originalModel = string.Empty;

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

        ModelMapping? mapping = _settings.FindModelMapping(originalModel);
        if (mapping is null)
        {
            resp.StatusCode = 404;
            await WriteJsonAsync(resp, new { error = $"Model '{originalModel}' not found." }, ct);
            return;
        }

        (ModelMapping target, bool redirected) = ResolveManualCompactTarget(mapping);

        await ForwardCompactToUpstreamAsync(target, bodyText, originalModel, redirected, resp, log, ct);
    }

    // ── POST /v1/chat/completions/compact ─────────────────────────────────

    /// <summary>
    /// Handles <c>POST /v1/chat/completions/compact</c> — manual context compaction endpoint.
    /// Accepts a chat completion request body and forwards it to a model's upstream so that
    /// model produces the summary, returning its response unchanged. The target is the
    /// mapping's configured compaction model when the mapping opts in via
    /// <see cref="ModelMapping.RedirectManualCompaction"/>, otherwise the request's own model.
    /// </summary>
    private async Task HandleManualCompactAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string bodyText = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);

        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.RequestBody = bodyText;

        string originalModel = string.Empty;

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

        ModelMapping? mapping = _settings.FindModelMapping(originalModel);
        if (mapping is null)
        {
            resp.StatusCode = 404;
            await WriteJsonAsync(resp, new { error = $"Model '{originalModel}' not found." }, ct);
            return;
        }

        (ModelMapping target, bool redirected) = ResolveManualCompactTarget(mapping);

        await ForwardCompactToUpstreamAsync(target, bodyText, originalModel, redirected, resp, log, ct);
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

    [GeneratedRegex(@"(?<size>\d+(?:\.\d+)?)[bB](?![A-Za-z])")]
    private static partial Regex ParameterSizeRegex();

    private static string GetParameterSize(string modelId)
    {
        Match match = ParameterSizeRegex().Match(modelId);
        return match.Success ? $"{match.Groups["size"].Value}B" : string.Empty;
    }

    [GeneratedRegex(@"(?<quant>q\d(?:_[a-z0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationLevelRegex();

    private static string GetQuantizationLevel(string modelId)
    {
        Match match = QuantizationLevelRegex().Match(modelId);
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
        // compaction model when the system/prompt is a Copilot session-summary prompt and the
        // mapping opted in via RedirectManualCompaction.
        string? firstContent = !string.IsNullOrEmpty(ollamaReq.System) ? ollamaReq.System : ollamaReq.Prompt;
        string effectiveModel = ResolveEffectiveModel(_settings, ollamaReq.Model, firstContent);
        bool compactRedirected = !string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase);

        if (compactRedirected)
        {
            log.OriginalModel = ollamaReq.Model;
            Log.Debug(
                "Context-summarize (/compact) generate request for {OriginalModel} redirected to compaction model {CompactModel}",
                ollamaReq.Model, effectiveModel);
        }
        else if (IsContextSummarizeRequest(firstContent))
        {
            Log.Debug(
                "Context-summarize (/compact) generate request for {OriginalModel} is not redirected: {Reason}",
                ollamaReq.Model,
                DescribeCompactSkipReason(_settings, ollamaReq.Model, firstContent));
        }

        string resolvedModel = _settings.ResolveModelName(effectiveModel);
        log.Model = resolvedModel;
        bool genDebug = _settings.DebugMode;
        StringBuilder? genDebugNotes = genDebug ? new StringBuilder() : null;
        if (genDebugNotes is not null && compactRedirected)
            genDebugNotes.AppendLine(DebugNotes.ContextSummarizeRedirect(ollamaReq.Model, effectiveModel));
        bool genMapped = !string.Equals(effectiveModel, resolvedModel, StringComparison.OrdinalIgnoreCase);
        if (genDebugNotes is not null)
            genDebugNotes.AppendLine(DebugNotes.ModelResolution(effectiveModel, resolvedModel, genMapped));
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (genBase, genTimeout, genApiKey) = ResolveUpstream(effectiveModel);

        // Build the prompt, composing the system prompt once on the same rule as the other two
        // paths: instruction text first, then the client's system text, joined by a blank line.
        // /api/generate carries system as a single string rather than a message array, so there is
        // no multi-system-message case to fold here.
        ModelMapping? mapping = _settings.FindModelMapping(effectiveModel);
        string? instructionText = GetInstructionTextForModel(_settings, effectiveModel);
        string systemPrefix = SystemPromptComposer.Merge(
            instructionText,
            string.IsNullOrWhiteSpace(ollamaReq.System) ? [] : [ollamaReq.System]);
        string prompt = string.IsNullOrEmpty(systemPrefix)
            ? ollamaReq.Prompt
            : $"{systemPrefix}{SystemPromptComposer.Separator}{ollamaReq.Prompt}";

        if (genDebugNotes is not null && mapping?.InstructionSetName is not null && instructionText is not null)
            genDebugNotes.AppendLine(DebugNotes.InstructionInjection(mapping.InstructionSetName));

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
            log.StopReason = chunk?.Choices?.FirstOrDefault()?.FinishReason ?? "stop";

            FillTokenStats(log, usage);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

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

            // Serialize the Ollama response once so the log captures exactly what the client
            // receives (the "after" of the OpenAI→Ollama response translation).
            string ollamaJson = JsonSerializer.Serialize(ollamaResp, _jsonOptions);
            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(ollamaJson, ollamaReq.Model);

            await WriteJsonRawAsync(resp, ollamaJson, ct);
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
        // compaction model when the first message is a Copilot session-summary prompt and the
        // mapping opted in via RedirectManualCompaction.
        string? chatFirstContent = ollamaReq.Messages.Count > 0 ? ollamaReq.Messages[0].Content : null;
        string effectiveModel = ResolveEffectiveModel(_settings, ollamaReq.Model, chatFirstContent);

        bool compactRedirected = !string.Equals(effectiveModel, ollamaReq.Model, StringComparison.OrdinalIgnoreCase);

        if (compactRedirected)
        {
            log.OriginalModel = ollamaReq.Model;
            Log.Debug(
                "Context-summarize (/compact) chat request for {OriginalModel} redirected to compaction model {CompactModel}",
                ollamaReq.Model, effectiveModel);
        }
        else if (IsContextSummarizeRequest(chatFirstContent))
        {
            Log.Debug(
                "Context-summarize (/compact) chat request for {OriginalModel} is not redirected: {Reason}",
                ollamaReq.Model,
                DescribeCompactSkipReason(_settings, ollamaReq.Model, chatFirstContent));
        }

        string resolvedModel = _settings.ResolveModelName(effectiveModel);
        log.Model = resolvedModel;
        bool debug = _settings.DebugMode;
        StringBuilder? debugNotes = debug ? new StringBuilder() : null;
        if (debugNotes is not null && compactRedirected)
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
        // Phase B: the IR-routed request translation (Plans/20260914-proxy-meai-phase-b-design.md)
        // behind a default-off flag; golden structural-parity tests pin it to the legacy mapper.
        List<LlamaCppMessage> messages = _settings.UseIrTranslation
            ? OllamaRequestTranslation.ToLlamaCppMessages(ollamaReq.Messages)
            : MapMessagesWithToolCorrelation(ollamaReq.Messages);
        bool removedAssistantPrefill = messages.Count > 0
            && ShouldApplyThinkingCompatibility(_settings, effectiveModel)
            && IsAssistantResponsePrefill(messages[^1]);
        if (removedAssistantPrefill)
        {
            messages.RemoveAt(messages.Count - 1);
            if (debugNotes is not null)
                debugNotes.AppendLine("messages: removed trailing assistant prefill (thinking compatibility)");
        }

        // Compose the system prompt exactly once, on the same rule as the /v1/* passthrough and
        // /api/generate: instruction text first, then every leading system message, folded into one.
        string? instructionText = GetInstructionTextForModel(_settings, effectiveModel);
        int leadingSystemCount = SystemPromptComposer.LeadingSystemCount(messages);

        if (SystemPromptComposer.ShouldRecompose(instructionText, leadingSystemCount))
        {
            string merged = SystemPromptComposer.Merge(
                instructionText,
                SystemPromptComposer.LeadingSystemContents(messages, leadingSystemCount));

            messages.RemoveRange(0, leadingSystemCount);
            messages.Insert(0, new LlamaCppMessage("system", merged));

            if (debugNotes is not null)
            {
                if (mapping?.InstructionSetName is not null)
                    debugNotes.AppendLine(DebugNotes.InstructionInjection(mapping.InstructionSetName));
                if (leadingSystemCount > 1)
                    debugNotes.AppendLine("messages: merged consecutive leading system messages into a single system message");
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

        // Proactive context-overflow check: when the estimated request exceeds the mapping's
        // configured threshold, compact the conversation before forwarding so we do not pay for
        // an upstream round-trip that is guaranteed to overflow.
        //
        // Only ONE compaction may act on a request. When the signature-based /compact redirect
        // already fired this request IS a compaction request, so compacting it again would
        // summarize a summary (and `mapping` is now the compaction target, not the client's model).
        if (compactRedirected)
        {
            Log.Debug(
                "Skipping proactive auto-compaction for {Model}: the request is already a compaction request redirected from {OriginalModel} (Ollama path)",
                effectiveModel, ollamaReq.Model);
        }
        else
        {
            // Per-mapping AutoCompactPaths is the only gate: TryProactiveOverflowAsync consults
            // IsAutoCompactActiveFor, the threshold, and the compaction target. There is
            // deliberately no global toggle, so a mapping left on "Disabled" never compacts.
            // For the Ollama path, pass null for the output stream (streaming progress
            // notifications are not implemented for this path).
            string? compactedBody = await TryProactiveOverflowAsync(mapping, upstreamBody, effectiveModel, resp, AutoCompactPaths.Ollama, null, ct);
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
            // Keep the connection alive during long thinking periods. This path is NDJSON rather
            // than SSE, so the keep-alive is an empty Ollama chunk rather than a comment frame.
            resp.KeepAlive = true;

            await StreamChatToOllamaAsync(
                upstreamResp,
                resp,
                ollamaReq.Model,
                log,
                _settings.CollectResponseDetails,
                responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                ShouldEmitSseKeepAlive(ollamaReq.Model),
                _settings.SseKeepAliveIntervalSeconds,
                mapping?.ThinkingMode ?? ThinkingMode.LeaveInline,
                sw,
                ct,
                () => _stats.IncrementSseKeepAlive(ollamaReq.Model),
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
            log.StopReason = upstreamFinishReason;
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
            // Only fill tool calls that the upstream did not already supply.
            toolCalls ??= toolCallExtraction.ToolCalls;

            content = toolCallExtraction.Content;

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

            // Serialize the Ollama response once so the log captures exactly what the client
            // receives (the "after" of the OpenAI→Ollama response translation).
            string ollamaJson = JsonSerializer.Serialize(ollamaResp, _jsonOptions);
            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(ollamaJson, ollamaReq.Model);

            await WriteJsonRawAsync(resp, ollamaJson, ct);
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

    /// <summary>
    /// Maps an upstream failure to a short, client-facing message for the terminal error chunk
    /// emitted on the SSE/NDJSON stream before the connection is closed. Timeouts and network
    /// drops are the common cases; everything else falls back to a generic message so internal
    /// details (hostnames, URIs) are never leaked to the client.
    /// </summary>
    private static string DescribeUpstreamStreamError(Exception ex)
    {
        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return "Upstream request timed out or was canceled before the response completed.";

        if (ex is HttpRequestException)
            return "Upstream connection error: the upstream server closed the connection or became unreachable.";

        if (ex is IOException)
            return "Upstream I/O error: the upstream connection was interrupted.";

        return "Upstream error: the response was interrupted before it completed.";
    }

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

        // Accumulate the Ollama-formatted NDJSON actually sent to the client so the log captures
        // the "after" of the OpenAI→Ollama response translation (not just the content text).
        using PooledCharBuffer? ollamaJsonAccumulator = collectResponse ? new PooledCharBuffer() : null;
        bool reachedDone = false;
        bool terminalChunkSent = false;
        bool upstreamFailed = false;
        long responseBytes = 0;
        string? stopReason = null;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The upstream stream failed mid-response. Push a terminal error chunk so the
                // client learns the stream was interrupted before the connection is closed.
                // A client-side cancellation (ct cancelled) is excluded so we never write to a
                // client that is already gone.
                upstreamFailed = true;
                stopReason = "error";
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                Log.Warning(ex, "Upstream stream failed mid-response for model {Model}", modelName);
                try
                {
                    var errorChunk = new OllamaGenerateResponse
                    {
                        Model = modelName,
                        Response = string.Empty,
                        Done = true,
                        DoneReason = "error",
                        Error = DescribeUpstreamStreamError(ex),
                    };
                    string errorJson = JsonSerializer.Serialize(errorChunk, _jsonOptions);
                    responseBytes += Encoding.UTF8.GetByteCount(errorJson);
                    ollamaJsonAccumulator?.Append(errorJson);
                    ollamaJsonAccumulator?.Append('\n');
                    await writer.WriteLineAsync(errorJson);
                    await writer.FlushAsync(CancellationToken.None);
                }
                catch
                {
                    // Client already disconnected; the error chunk could not be delivered.
                }
                break;
            }
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
                ollamaJsonAccumulator?.Append(doneJson);
                ollamaJsonAccumulator?.Append('\n');
                stopReason ??= "stop";
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

            if (done && choice?.FinishReason is not null)
                stopReason = choice.FinishReason;

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
            ollamaJsonAccumulator?.Append(chunkJson);
            ollamaJsonAccumulator?.Append('\n');
            await writer.WriteLineAsync(chunkJson);
            await writer.FlushAsync(ct);
        }

        if (ollamaJsonAccumulator is not null)
            log.ResponseBody = redactResponse(ollamaJsonAccumulator.ToString());

        log.StopReason = stopReason;
        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = upstreamFailed
            ? RequestStatus.Error
            : ct.IsCancellationRequested && !reachedDone
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
        bool enableKeepAlive,
        int keepAliveIntervalSeconds,
        ThinkingMode thinkingMode,
        Stopwatch sw,
        CancellationToken ct,
        Action? onKeepAliveSent = null,
        bool collectRawUpstream = false)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        // Accumulate the Ollama-formatted NDJSON actually sent to the client so the log captures
        // the "after" of the OpenAI→Ollama response translation (not just the content text).
        using PooledCharBuffer? ollamaJsonAccumulator = collectResponse ? new PooledCharBuffer() : null;
        // When debug capture is on, accumulate the raw upstream (OpenAI) SSE data lines so the
        // "before" of the response translation is visible alongside the Ollama "after".
        using PooledCharBuffer? rawUpstreamAccumulator = collectRawUpstream ? new PooledCharBuffer() : null;
        bool reachedDone = false;
        bool terminalChunkSent = false;
        bool upstreamFailed = false;
        string? stopReason = null;
        long responseBytes = 0;
        TimeSpan keepAliveInterval = AppSettings.SseKeepAliveInterval(keepAliveIntervalSeconds);
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
            string? line;
            try
            {
                line = await ReadLineWithOllamaChatKeepAliveAsync(
                    reader,
                    writer,
                    modelName,
                    enableKeepAlive,
                    keepAliveInterval,
                    ct,
                    onKeepAliveSent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The upstream stream failed mid-response (network drop, timeout, etc.).
                // Push a terminal error chunk so the client learns the stream was interrupted
                // before the connection is closed, rather than seeing a silently truncated
                // response. A client-side cancellation (ct cancelled) is excluded so we never
                // write to a client that is already gone.
                upstreamFailed = true;
                stopReason = "error";
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                Log.Warning(ex, "Upstream stream failed mid-response for model {Model}", modelName);
                try
                {
                    var errorChunk = new OllamaChatResponse
                    {
                        Model = modelName,
                        Message = new OllamaMessage("assistant", string.Empty),
                        Done = true,
                        DoneReason = "error",
                        Error = DescribeUpstreamStreamError(ex),
                    };
                    string errorJson = JsonSerializer.Serialize(errorChunk, _jsonOptions);
                    responseBytes += Encoding.UTF8.GetByteCount(errorJson);
                    ollamaJsonAccumulator?.Append(errorJson);
                    ollamaJsonAccumulator?.Append('\n');
                    await writer.WriteLineAsync(errorJson);
                    await writer.FlushAsync(CancellationToken.None);
                }
                catch
                {
                    // Client already disconnected; the error chunk could not be delivered.
                }
                break;
            }
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
                ollamaJsonAccumulator?.Append(doneJson);
                ollamaJsonAccumulator?.Append('\n');
                stopReason ??= "stop";
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

            string? doneReason = done ? (toolCalls is not null ? "tool_calls" : choice?.FinishReason ?? "stop") : null;
            if (done && doneReason is not null)
                stopReason = doneReason;

            long? terminalNs = done ? ElapsedNanos(sw) : null;
            var ollamaChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", token) { ToolCalls = toolCalls, Thinking = thinking },
                Done = done,
                DoneReason = doneReason,
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
            ollamaJsonAccumulator?.Append(chunkJson);
            ollamaJsonAccumulator?.Append('\n');
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
                ollamaJsonAccumulator?.Append(tailJson);
                ollamaJsonAccumulator?.Append('\n');
                stopReason ??= "stop";
                await writer.WriteLineAsync(tailJson);
                await writer.FlushAsync(ct);
            }
        }

        if (ollamaJsonAccumulator is not null)
            log.ResponseBody = redactResponse(ollamaJsonAccumulator.ToString());

        if (rawUpstreamAccumulator is not null && rawUpstreamAccumulator.Length > 0)
            log.UpstreamResponseBody = redactResponse(rawUpstreamAccumulator.ToString());

        log.StopReason = stopReason;
        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = upstreamFailed
            ? RequestStatus.Error
            : ct.IsCancellationRequested && !reachedDone
                ? RequestStatus.Cancelled
                : RequestStatus.Success;
    }

    /// <summary>
    /// Reads one NDJSON line from the upstream, emitting an empty Ollama chat chunk as the
    /// client-facing keep-alive whenever the upstream is silent for a full interval.
    /// </summary>
    /// <remarks>
    /// Unlike the SSE paths, the Ollama surface is NDJSON, so an SSE comment frame would be
    /// invalid here. The keep-alive is therefore a well-formed empty chunk with
    /// <c>done: false</c>, which Ollama clients already tolerate.
    /// </remarks>
    private static async Task<string?> ReadLineWithOllamaChatKeepAliveAsync(
        StreamReader reader,
        StreamWriter writer,
        string modelName,
        bool enableKeepAlive,
        TimeSpan keepAliveInterval,
        CancellationToken ct,
        Action? onKeepAliveSent = null)
    {
        Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

        while (enableKeepAlive && !readTask.IsCompleted)
        {
            Task delayTask = Task.Delay(keepAliveInterval, ct);
            Task completed = await Task.WhenAny(readTask, delayTask);
            if (completed == readTask)
                break;

            var keepAliveChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", string.Empty),
                Done = false,
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(keepAliveChunk, _jsonOptions));
            await writer.FlushAsync(ct);
            onKeepAliveSent?.Invoke();
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
    internal static List<LlamaCppMessage> MapMessagesWithToolCorrelation(List<OllamaMessage> source)
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

        MatchCollection matches = XmlToolCallRegex().Matches(content);

                if (matches.Count == 0)
                    return new(content, null);

                List<OllamaToolCall> toolCalls = [];
                foreach (Match match in matches)
                {
                    string name = match.Groups["name"].Value.Trim();
                    string body = match.Groups["body"].Value;
                    Dictionary<string, object?> arguments = new(StringComparer.OrdinalIgnoreCase);

                    foreach (Match parameterMatch in XmlToolCallParameterRegex().Matches(body))
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
    /// <summary>
        /// Captures the request payload and proxy error body for a request the proxy rejected without
        /// handing it to a model. Called only on the reject branches, because reading the request body
        /// drains the stream — on any path that later forwards the body this would consume it first.
        /// </summary>
        /// <remarks>
        /// Best-effort: a request rejected before routing may have no readable body (a GET, a
        /// content-length-less request, an already-consumed stream), and failing to capture it must not
        /// change the response the client receives.
        /// </remarks>
        private async Task CaptureRejectedBodyAsync(HttpListenerRequest req, RequestLog log, string errorJson, CancellationToken ct)
        {
            if (_settings.CollectRequestDetails)
            {
                try
                {
                    // ReadBodyAsync enforces the size limit and throws for an oversized body; the catch
                    // below records the reason instead of losing the row.
                    string body = await ReadBodyAsync(req, ct);
                                    if (!string.IsNullOrWhiteSpace(body))
                                                        log.RequestBody = RedactUnmappedRequestBodyForLog(body);
                }
                catch (RequestBodyTooLargeException)
                {
                    // The bytes are deliberately not buffered, so there is nothing to capture.
                    log.RequestBody = null;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not capture the request body of a rejected request");
                }
            }

            if (_settings.CollectResponseDetails)
                log.ResponseBody = errorJson;
        }

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
    private static async Task<T?> TryDeserializeRequestAsync<T>(string body, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
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
            log.ErrorMessage = "Invalid or malformed request body.";
            RecordResponseStatus(resp, log, 400);
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

    /// <summary>
    /// Best-effort delivery of an error response from a catch block. Returns false when the error could
    /// not be delivered, which happens when the response already started — <see cref="HttpListenerResponse"/>
    /// exposes no "headers sent" flag, so the only way to learn this is that setting the status code or
    /// content length throws. In that case the client is left holding a truncated response, and callers
    /// must say so in the log rather than swallowing the failure silently.
    /// </summary>
    private static async Task<bool> TryWriteErrorResponseAsync(
        HttpListenerResponse resp, RequestLog log, int statusCode, object payload, CancellationToken ct)
    {
        try
        {
            RecordResponseStatus(resp, log, statusCode);
            await WriteJsonAsync(resp, payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Net.HttpListenerException
            or IOException
            or ObjectDisposedException
            or OperationCanceledException)
        {
            Log.Debug(ex, "Could not deliver a {StatusCode} error response; the response had already started or the client disconnected", statusCode);
            return false;
        }
    }

    /// <summary>
    /// Closes a response that could not be answered properly, without throwing. Called after a failed
    /// error delivery so the connection is released rather than left dangling until it times out.
    /// </summary>
    private static void CloseResponseQuietly(HttpListenerResponse resp)
    {
        try { resp.Close(); }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Net.HttpListenerException
            or IOException
            or ObjectDisposedException)
        {
            Log.Debug(ex, "Response stream was already closed");
        }
    }

    /// <summary>
    /// Records that the client received a truncated response because no error could be delivered after
    /// the response had already started. Appended to <see cref="RequestLog.ErrorMessage"/> rather than
    /// stored in its own column: it qualifies the existing message and needs no schema change.
    /// </summary>
    private static void RecordUndeliverableError(RequestLog log, int statusCode)
    {
        const string suffix = " The response had already started, so no error could be delivered and the client received a truncated response.";

        // The response object is unusable here — it already committed — so the intended code is
        // recorded directly. Without this the entry would keep whatever status the streaming path
        // set before the failure, hiding that an error was never delivered.
        log.StatusCode = statusCode;
        log.Status = RequestLog.DeriveStatus(statusCode);

        log.ErrorMessage = string.IsNullOrEmpty(log.ErrorMessage)
            ? $"Failed to deliver a {statusCode} error response.{suffix}"
            : log.ErrorMessage + suffix;
    }

    /// <summary>
    /// Records an HTTP status on the log entry and sets it on the response together, so no branch
    /// can answer the client and then leave the entry at its <see cref="RequestStatus.Success"/>
    /// default. Every proxy-originated error goes through here.
    /// </summary>
    /// <remarks>
    /// The log entry is written first: <c>HttpListenerResponse.StatusCode</c> throws once the
    /// response has committed its headers, and recording first means the entry still carries the
    /// code the proxy intended to send when that happens.
    /// </remarks>
    private static void RecordResponseStatus(HttpListenerResponse resp, RequestLog log, int statusCode)
    {
        log.StatusCode = statusCode;
        log.Status = RequestLog.DeriveStatus(statusCode);
        resp.StatusCode = statusCode;
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
                "description": "Forwards the conversation to a model so that model produces the summary, and returns its response unchanged. Uses the mapping's compaction model when 'Redirect manual compaction' is enabled and a compaction model is selected; otherwise uses the model named in the request.",
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
        private readonly System.Threading.Timer _timer;
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

        // Clamped to the same bounds AppSettings.Normalize enforces, so a stale or hand-edited value
        // can never produce a timer that fires faster than the UI allows.
        private static TimeSpan GetInterval(int intervalSeconds)
            => AppSettings.HeartbeatInterval(intervalSeconds);

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
