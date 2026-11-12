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
internal sealed partial class OllamaProxyHandler(AppSettings settings, StatisticsService stats, ModuleHost moduleHost, McpServerService mcpServer, StatisticsService? nonProxiedStats = null, StreamMonitor? streamMonitor = null) : IDisposable
{
    internal const string RedactedBodyText = "[REDACTED BY MODEL LOG REDACTION SETTINGS]";
    private const string RedactedValueText = "[REDACTED]";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private volatile AppSettings _settings = settings;

    // Shared pooled HttpClient â€” avoids socket exhaustion under load.
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

                /// <summary>
                /// Tracks streaming responses while they are in flight so the dashboard can show them live.
                /// Null when the host does not wire a monitor (tests, headless hosts); every use is guarded so
                /// the streaming paths carry no cost in that case.
                /// </summary>
                private readonly StreamMonitor? _streamMonitor = streamMonitor;
            private readonly ModuleHost _moduleHost = moduleHost;
    private readonly McpServerService _mcpServer = mcpServer;
    private readonly ConcurrentDictionary<string, PeriodicHeartbeatState> _periodicHeartbeats = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoCompactionService _autoCompactionService = CreateAutoCompactionService(settings);

        /// <summary>
        /// Builds the compaction service and applies the installation-wide compaction settings to it.
        /// </summary>
        /// <remarks>
        /// A static factory rather than a field initializer that configures the service afterwards,
        /// because this handler uses a primary constructor: there is no constructor body to run the
        /// configuration in, and the service must never be reachable in an unconfigured state. Mirrors
        /// the same call made from <see cref="UpdateSettings"/>, so a running proxy and a starting one
        /// compact with the same instructions and ceiling.
        /// </remarks>
        private static AutoCompactionService CreateAutoCompactionService(AppSettings settings)
        {
            AutoCompactionService service = new(BuildHttpClient());
            service.Configure(settings.ResolveCompactionInstructions(), settings.CompactionTargetTokens);
            service.SetMaxConcurrentCompactions(settings.MaxConcurrentCompactions);
            return service;
        }

        /// <summary>
        /// Pushes the installation-wide compaction settings into <see cref="_autoCompactionService"/>.
        /// </summary>
        /// <remarks>
        /// The service takes its instructions and advisory ceiling as parameters rather than reading
        /// settings itself, so this is the one place that supplies them. Called from the constructor for
        /// the initial configuration and again from <see cref="UpdateSettings"/> so a change on the
        /// Settings tab takes effect without restarting the proxy.
        /// </remarks>
        private void ApplyCompactionSettings()
                {
                    _autoCompactionService.Configure(
                        _settings.ResolveCompactionInstructions(),
                        _settings.CompactionTargetTokens);

                    _autoCompactionService.SetMaxConcurrentCompactions(_settings.MaxConcurrentCompactions);
                }

        /// <summary>Called from the Settings UI after the user saves new settings.</summary>
        public void UpdateSettings(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            // Publish a private copy rather than the caller's live object. The Settings tab keeps
            // mutating that object in place - the model-mapping list, the credential list and the
            // instruction-set list are rebuilt on every save - while request threads enumerate
            // whatever this field points at. Sharing one instance across both sides is what makes a
            // save under load throw InvalidOperationException out of a live request, or worse, let a
            // request see a half-applied mapping and report an upstream authentication failure.
            // Request paths only ever read this reference, so a deep copy here is the whole contract.
            _settings = settings.Clone();
            ApplyCompactionSettings();
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
            catch (Exception ex)
            {
                // Best-effort wait; always dispose below.
                Log.Debug(ex, "Error while waiting for in-flight requests before disposing old HttpClient");
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

    /// <summary>
    /// Cap on how much of a heartbeat probe's response body is drained. The probe only cares
    /// about the status line, but the body must still be consumed so the pooled connection is
    /// reusable; the bound keeps a misbehaving upstream from buffering an unbounded payload.
    /// </summary>
    private const long MaxHeartbeatBodyBytes = 256 * 1024;

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

        // ResponseHeadersRead leaves the body unread, and disposing a response with an unread
        // body tears the pooled connection down instead of returning it — a periodic probe would
        // churn sockets (TIME_WAIT growth) instead of keeping one warm. Drain it with a bound;
        // an oversized or unreadable body merely loses connection reuse for that one probe.
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxHeartbeatBodyBytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Probe outcomes never depend on the body, so drain failures are not worth logging.
        }

        if (response.IsSuccessStatusCode)
        {
            // This is the periodic upstream liveness probe, not a client-facing SSE keep-alive,
            // so it records probe state rather than bumping the keep-alive frame counter.
            _stats.RecordHeartbeatProbeSuccess(modelName);
            return;
        }

        string error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        _stats.RecordHeartbeatFailure(modelName, error);

        // Same treatment as the exception path above: a non-success probe answer repeats forever for a
        // model that is down, so it is Debug plus the marker rather than Warning, and the configured
        // heartbeat threshold decides whether it is recorded at all. The message marker additionally
        // lets the System Logs tab isolate or hide these lines.
        Log.ForContext(AppLogger.HeartbeatMarkerProperty, true)
            .Debug(AppLogger.HeartbeatMessage("Heartbeat probe for model {Model} returned {Error}"), modelName, error);
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

        // Mirror the same in-flight guard used in UpdateSettings: wait for active
        // requests to drain before disposing the HttpClient to avoid
        // ObjectDisposedException on streams still being consumed.
        HttpClient clientToDispose = _httpClient;
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
                clientToDispose.Dispose();
            }
        });
    }

    /// <summary>
    /// Returns the base URL, timeout, and optional bearer API key to use for a given Ollama model name.
    /// Requires each mapping to have its own upstream URL configured.
    /// If ollamaModel is null or empty and there's at least one mapping configured,
    /// returns the first mapping's upstream settings as a fallback.
    /// </summary>
    private (string BaseUrl, int TimeoutSeconds, string? ApiKey) ResolveUpstream(string ollamaModel)
        => ResolveUpstream(ResolveMapping(ollamaModel));

    /// <summary>
    /// The single mapping-selection authority behind <see cref="ResolveUpstream"/>: an exact enabled
    /// match on the proxy name or the upstream name, else â€” only when no model was requested â€” the
    /// first enabled mapping. Every caller that needs to know which mapping a request targets must go
    /// through here rather than calling <see cref="AppSettings.FindModelMapping"/> directly, so the
    /// routing decision and the per-mapping request gates (the client-credential relay, the upstream
    /// origin check) cannot resolve a different mapping than the one the request is sent to.
    /// </summary>
    private ModelMapping ResolveMapping(string ollamaModel)
    {
        ModelMapping? mapping = _settings.FindModelMapping(ollamaModel);
        if (mapping is not null)
            return mapping;

        // Fallback: if model name is empty/null and we have at least one mapping,
        // use the first configured mapping's upstream URL (common for single-model setups)
        if (string.IsNullOrWhiteSpace(ollamaModel))
        {
            return _settings.ModelMappings.FirstOrDefault(m => m.IsEnabled)
                ?? throw new InvalidOperationException("No enabled model mappings are configured.");
        }

        throw new InvalidOperationException(
            $"No mapping found for model '{ollamaModel}'. " +
            "Add a mapping in settings with ProxyName, ModelName, and UpstreamUrl.");
    }

    private (string BaseUrl, int TimeoutSeconds, string? ApiKey) ResolveUpstream(ModelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
            throw new InvalidOperationException(
                $"Model mapping '{mapping.ProxyName}' has no upstream URL configured. " +
                "Each mapping must specify its own UpstreamUrl.");

        int timeout = mapping.UpstreamTimeoutSeconds > 0 ? mapping.UpstreamTimeoutSeconds : 300;
        return (mapping.UpstreamUrl.TrimEnd('/'), timeout, _settings.ResolveApiKey(mapping));
    }

    internal static bool ShouldApplyThinkingCompatibility(AppSettings settings, string modelName)
    {
        ModelMapping? mapping = settings.FindModelMapping(modelName);
        return mapping?.EnableThinkingCompatibility ?? true;
    }

    /// <summary>
    /// Appends the coding attribution for a translated surface to the request's debug summary.
    /// </summary>
    /// <remarks>
    /// Written unconditionally rather than only under <see cref="AppSettings.DebugMode"/>: the whole
    /// point of the IR switch is comparing the two codings on live traffic, so gating the attribution
    /// behind debug mode would mean a user turns the switch on and sees no evidence of which path ran.
    /// The summary is displayed and persisted whenever it is non-empty, so no other flag is needed.
    /// Must be called after any path that assigns <c>log.DebugSummary</c> from scratch, or the line
    /// will be overwritten.
    /// </remarks>
    private static void AppendTranslationCoding(RequestLog log, string surface, bool ir)
    {
        string note = DebugNotes.TranslationCoding(surface, ir);
        log.DebugSummary = string.IsNullOrEmpty(log.DebugSummary) ? note : log.DebugSummary + "\n" + note;
    }

    /// <summary>
    /// Returns the Copilot context-summarize system prompt carried by message[0] of a request body,
    /// or null when the body has no such message.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a compaction run is producing a summary for GitHub Copilot, which
    /// demands a specific output format. The check runs on the body being compacted rather than on the
    /// inbound request, because the two are not the same thing: a redirected request has already been
    /// retargeted, and the reactive and manual paths reach compaction without an inbound signature test
    /// at all.
    /// </remarks>
    internal static bool IsCopilotCompactionBody(string bodyText)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bodyText);
            return IsContextSummarizeRequest(GetFirstMessageContent(doc.RootElement));
        }
        catch (JsonException)
        {
            return false;
        }
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
    /// Copilot-detected summarizations have a dedicated path: when a usable global target is selected
    /// (and <see cref="AppSettings.EnableCopilotCompactionRouting"/> is on) the request is routed to
    /// the single global <see cref="AppSettings.CopilotCompactionModelName"/>. That target applies to
    /// every model rather than the ones that opted in, because Copilot picks the model for its
    /// compaction turn itself and cannot be steered per model â€” acting globally is the only control
    /// that actually works. When no usable global target exists, resolution falls back to the
    /// per-mapping <see cref="ResolveManualCompactTarget(AppSettings, ModelMapping)"/> redirect so
    /// other tools keep getting their configured target.
    /// </remarks>
    internal static string ResolveEffectiveModel(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return originalModel;

        // The Copilot /compact detection matched on the system prompt's summary signature. Log the
        // detection itself, separately from the redirect resolution reported by
        // ResolveManualCompactTarget, so a debug session shows that the signature test fired even
        // when no mapping or no redirect applies.
        Log.Debug(
            "Context-summarize (/compact) system-prompt signature detected for model {Model}; resolving compaction target",
            originalModel);

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null)
            return originalModel;

        // Global Copilot path: the single target applies to every model, since Copilot ignores any
        // per-model compaction choice and would otherwise compact with whichever model it happened to
        // address. When routing is on but no usable global target is selected the request goes to the
        // model the client asked for, rather than silently falling through to a per-mapping target
        // that was chosen for a different tool and a different client.
        if (settings.EnableCopilotCompactionRouting)
        {
            ModelMapping? globalTarget = settings.FindCopilotCompactionTarget();
            Log.Debug(
                "Copilot-compatible compaction for {Model}: global compaction model {GlobalModel}",
                mapping.ProxyName,
                globalTarget?.ProxyName ?? "(none)");

            return globalTarget?.ProxyName ?? originalModel;
        }

        (ModelMapping target, bool redirected) = ResolveManualCompactTarget(settings, mapping);
        return redirected ? target.ProxyName : originalModel;
    }

    /// <summary>
    /// Resolves the effective model for a /compact redirect, logs the redirect (or skip reason),
    /// and returns the three values every handler needs: the effective model, whether a redirect
    /// occurred, and the resolved upstream model name.
    /// </summary>
    /// <param name="surface">A short name for the request surface, used in log messages only â€” e.g. "chat", "generate", or "request".</param>
    internal static (string EffectiveModel, bool CompactRedirected, string ResolvedModel) ResolveCompactRedirect(
        AppSettings settings,
        string originalModel,
        string? firstMessageContent,
        RequestLog log,
        string surface)
    {
        string effectiveModel = ResolveEffectiveModel(settings, originalModel, firstMessageContent);
        bool compactRedirected = !string.Equals(effectiveModel, originalModel, StringComparison.OrdinalIgnoreCase);

        if (compactRedirected)
        {
            log.OriginalModel = originalModel;
            Log.Debug(
                "Context-summarize (/compact) {Surface} request for {OriginalModel} redirected to compaction model {CompactModel}",
                surface, originalModel, effectiveModel);
        }
        else if (IsContextSummarizeRequest(firstMessageContent))
        {
            Log.Debug(
                "Context-summarize (/compact) {Surface} request for {OriginalModel} is not redirected: {Reason}",
                surface, originalModel,
                DescribeCompactSkipReason(settings, originalModel, firstMessageContent));
        }

        return (effectiveModel, compactRedirected, settings.ResolveModelName(effectiveModel));
    }

    /// <summary>
    /// Extracts the first message's text content from an OpenAI-style request body root
    /// (a <c>messages</c> array).
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
    /// passed (which would mean a redirect was expected but did not occur). Shared with the Phase-B
    /// payload pipeline so both report the same reason.
    /// </summary>
    internal static string DescribeCompactSkipReason(AppSettings settings, string originalModel, string? firstMessageContent)
    {
        if (!IsContextSummarizeRequest(firstMessageContent))
            return "first message did not match a /compact signature";

        ModelMapping? mapping = settings.FindModelMapping(originalModel);
        if (mapping is null)
            return $"no mapping found for model '{originalModel}'";

        // Copilot-compatible mappings route to the single global target, not the per-mapping one, so
        // the reason must be reported against that path or a missing global target would be blamed on
        // the per-mapping redirect checkbox. Mirrors the order in ResolveEffectiveModel: the global
        // target is consulted first and only a missing one falls through.
        if (settings.FindCopilotCompactionTarget() is null
            && settings.EnableCopilotCompactionRouting)
        {
            return settings.CopilotCompactionModelName is null or ""
                ? "Copilot compaction routing is on but no global Copilot compaction model is selected, "
                    + "so the request goes to the model Copilot asked for"
                : $"Copilot compaction routing is on but the selected global model "
                    + $"'{settings.CopilotCompactionModelName}' is not usable (missing, disabled, or has no upstream URL)";
        }

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
    /// Applies the resolved reasoning effort â€” the mapping's configured value under Proxy
    /// priority, the client's <c>think</c> field under Client App priority â€” to a translated
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
    /// Applies the client/mapping sampling priority rules to an outbound llama.cpp request.
    /// Shared by the /api/generate and /api/chat translations so the identical block cannot
    /// drift between the two wire shapes.
    /// </summary>
    private static void ApplySamplingOptions(ILlamaCppSamplingRequest request, ModelMapping? mapping, OllamaOptions? options)
    {
        request.Temperature = ResolveSamplingValue(
            mapping?.TemperaturePriority ?? SamplingPriority.ClientApp,
            options?.Temperature,
            (float)(mapping?.Temperature ?? 0.7));
        request.TopP = options?.TopP;
        request.TopK = options?.TopK;
        request.MinP = options?.MinP;
        request.MaxTokens = options?.NumPredict;
        request.Stop = options?.Stop;
        request.Seed = options?.Seed;
        request.PresencePenalty = options?.PresencePenalty;
        request.FrequencyPenalty = options?.FrequencyPenalty;
        request.RepeatPenalty = ResolveSamplingValue(
            mapping?.RepeatPenaltyPriority ?? SamplingPriority.ClientApp,
            options?.RepeatPenalty,
            (float)(mapping?.RepeatPenalty ?? 1.0));
    }

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
    /// Extracts the context window an upstream says it actually loaded, from a
    /// <c>exceed_context_size_error</c> body. llama.cpp reports a structured <c>n_ctx</c>; providers
    /// that only describe the limit in prose ("available context size (256000 tokens)") are covered
    /// by pattern-matching the message. Returns 0 when the body carries no usable figure.
    /// </summary>
    /// <remarks>
    /// The mapping's configured Context Window is a declaration of intent, not a measurement: it can
    /// be larger than what the server actually loaded (a server started with a smaller <c>-c</c>, or
    /// a value the operator has not reconciled). Compaction sized against the declared figure then
    /// concludes an oversized body "fits" and forwards it untouched, which is why this value has to
    /// be read back from the server's own rejection.
    /// </remarks>
    /// <param name="body">The upstream error body.</param>
    /// <returns>The reported window in tokens, or 0 when the body reports none.</returns>
    internal static int ExtractReportedContextWindow(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return 0;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // llama.cpp puts n_ctx beside the message inside the error object; tolerate it at
                // the root too, since a wrapper may hoist it.
                if (doc.RootElement.TryGetProperty("n_ctx", out JsonElement rootCtx)
                    && rootCtx.ValueKind == JsonValueKind.Number
                    && rootCtx.TryGetInt32(out int rootValue) && rootValue > 0)
                {
                    return rootValue;
                }

                if (doc.RootElement.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("n_ctx", out JsonElement nestedCtx)
                    && nestedCtx.ValueKind == JsonValueKind.Number
                    && nestedCtx.TryGetInt32(out int nestedValue) && nestedValue > 0)
                {
                    return nestedValue;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the prose scan below.
        }

        // Prose fallback for providers that only describe the limit in the message. Covers
        // llama.cpp's "available context size (256000 tokens)" and the "maximum context length is
        // 128000 tokens" phrasing used by OpenAI-style services.
        Match match = Regex.Match(body, @"context\s+(?:size|length|window)[^0-9]{0,30}(\d+)", RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups[1].Value, out int parsed)
            && parsed > 0
            ? parsed
            : 0;
    }

    /// <summary>
    /// Estimates the token count of a serialized request body using a ~4 chars/token heuristic.
    /// Intentionally conservative (overestimates) so compaction thresholds favor compacting
    /// early rather than missing an overflow. Delegates to the shared estimator so the gate in
    /// <see cref="AutoCompactionService.ShouldCompact"/> and the callers here always measure a
    /// body identically â€” a divergence made the two disagree on non-ASCII bodies.
    /// </summary>
    private static int EstimateTokenCount(string body) => AutoCompactionService.EstimateTokenCount(body);

    /// <summary>
    /// When the mapping's compaction threshold is exceeded, summarizes the conversation with the
    /// mapping's compaction target and returns the compacted request body so the caller can forward
    /// it upstream. Returns null when compaction is disabled for the path, no threshold or compaction
    /// target is configured, the request is already under the threshold, or compaction failed â€” in
    /// every one of those cases the caller proceeds with the original body and the upstream returns
    /// its own authoritative error if it really does overflow.
    /// </summary>
    /// <remarks>
    /// This deliberately never short-circuits the request with a 413. The previous behavior
    /// summarized the conversation (paying for a compaction model call), discarded the summary, and
    /// told the client to retry with reduced context â€” so the client had to send the whole
    /// conversation again. The compacted body is now forwarded, which is the only way the
    /// summarization work is useful.
    /// </remarks>
    private async Task<string?> TryProactiveOverflowAsync(
        ModelMapping? mapping,
        string body,
        string model,
        HttpListenerResponse resp,
        AutoCompactPaths requestPath,
        Func<Task<Stream>>? openOutputStream,
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
            // Auto-compaction requires a resolved target â€” with none configured (dropdown =
            // None) it does nothing and lets upstream decide (no fallback to the original model).
            ModelMapping? compactMapping = _settings.FindContextSummarizeTarget(mapping);
            if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                compactMapping = null;

            if (compactMapping is null)
            {
                Log.Debug("Auto-compaction for model {Model} skipped: no compaction model selected", model);
                return null;
            }

            // Opened only now that compaction is definitely going to run. The caller hands over a
            // factory rather than a stream because opening it commits the SSE headers, which fixes
            // the client response at 200; doing that for a request that ends up not compacting leaves
            // a later upstream failure with no status code left to report itself with.
            Stream? outputStream = openOutputStream is null ? null : await openOutputStream();

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
                int maxTokensPerChunk = AutoCompactionService.GetSummaryPromptBudget(
                    compactModelContext,
                    AutoCompactionService.EffectiveSummaryMaxTokensFor(_settings.CompactionTargetTokens));
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
                // Fall through â€” do not send a proactive 413; allow upstream to decide.
            }
        }

        // No compaction produced. Do not short-circuit with 413 here â€” let upstream
        // return the authoritative error if it overflows.
        return null;
    }

    /// <summary>
    /// Attempts local context compaction after the upstream rejected the prompt with a
    /// context-size overflow error. The caller retries the original request once with the
    /// returned compacted body. This self-heals oversized prompts even when no proactive
    /// threshold was configured for the mapping.
    /// </summary>
    /// <remarks>
    /// The rescue needs only a usable compaction target â€” either a selected global Copilot compaction
    /// model or the overflowed mapping's own configured target. Nothing else gates it: the request has
    /// just been rejected for exceeding a window, so summarizing with the chosen target is exactly the
    /// outcome the target exists for. Requiring the target's <see cref="ModelMapping.AutoCompactPaths"/>
    /// as well was a redundant second opt-in that turned a recoverable overflow into a raw upstream
    /// error.
    /// </remarks>
    /// <param name="reportedContextWindow">
    /// The context window the upstream said it actually loaded, when it reported one. Preferred over
    /// the mapping's declared window for the post-compaction fit check, because a declaration can be
    /// larger than what the server really has and would let an oversized result through.
    /// </param>
    private async Task<string?> TryReactiveCompactionAsync(
        string body,
        string model,
        HttpListenerResponse resp,
        bool streamAlreadyOpen,
        CancellationToken ct,
        int reportedContextWindow = 0)
    {
        ModelMapping? mapping = _settings.FindModelMapping(model);
        if (mapping is null)
            return null;

        try
        {
            // Resolve the compaction target the same way the /compact redirect did, so the rescue uses the
            // model the request was actually routed to. A usable global Copilot target wins when
            // routing is on; otherwise the per-mapping target applies. The finder returns null when
            // routing is off, so both terms collapse correctly without duplicating the gate.
            ModelMapping? compactMapping = (_settings.EnableCopilotCompactionRouting
                ? _settings.FindCopilotCompactionTarget()
                : null)
                ?? _settings.FindContextSummarizeTarget(mapping);
            if (compactMapping is not null && (!compactMapping.IsEnabled || string.IsNullOrWhiteSpace(compactMapping.UpstreamUrl)))
                compactMapping = null;

            if (compactMapping is null)
            {
                Log.Debug("Reactive auto-compaction for model {Model} skipped: no compaction model selected", model);
                return null;
            }

            // No further gate. A resolved compaction target is already a deliberate choice â€” either a
            // selected global Copilot compaction model or a mapping's configured target â€” and the
            // request has just been rejected for exceeding the window, so summarizing is exactly what
            // the target exists for. Requiring AutoCompactPaths here too was a redundant second opt-in
            // that turned a recoverable overflow into a raw upstream error.

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
            int maxTokensPerChunk = AutoCompactionService.GetSummaryPromptBudget(
                    compactModelContext,
                    AutoCompactionService.EffectiveSummaryMaxTokensFor(_settings.CompactionTargetTokens));

            // The oversized request is being compacted because its destination model rejected it. The
            // destination is `mapping` when no redirect fired, but under a /compact redirect the
            // request was routed to the compaction model itself, so that is the window the result
            // must fit. The server's reported figure wins over the mapping's declaration when the
            // upstream supplied one, so a window the operator has not reconciled cannot let an
            // oversized result through.
            int destinationWindow = compactMapping.Id == mapping.Id
                ? mapping.GetEffectiveContextWindow()
                : compactMapping.GetCompactionContextWindow(_settings.CompactionFallbackContextTokens);
            if (reportedContextWindow > 0 && reportedContextWindow < destinationWindow)
            {
                Log.Warning(
                    "Upstream reported a {Reported} token context window for model {Model}, smaller than the "
                    + "{Declared} tokens configured on the mapping. Using the reported figure for this request; "
                    + "reconcile the mapping's Context Window to avoid repeated overflows.",
                    reportedContextWindow, model, destinationWindow);
                destinationWindow = reportedContextWindow;
            }

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
                destinationWindow,
                compactModelContext,
                ct);

            if (compacted is not null && streamAlreadyOpen)
            {
                byte[] done = Encoding.UTF8.GetBytes(
                    $": <ignorethis>kaeo-compaction-complete: Conversation compacted ({EstimateTokenCount(body)} â†’ {EstimateTokenCount(compacted)} est. tokens). Retrying upstream...</ignorethis>\n\n");
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

    /// <summary>
    /// Drops a client's own <c>Authorization</c> header from an upstream-bound request when the
    /// resolved mapping did not opt into relaying it.
    /// </summary>
    /// <remarks>
    /// Runs after <see cref="ApplyApiKey"/> rather than inside the inbound header-copy loop, because
    /// that loop runs before the model name is parsed and the mapping is resolved: the decision needs
    /// the base URL the request is actually going to, and the header copy does not have it yet. A
    /// mapping that supplies its own credential has already had its header replaced above, so this only
    /// ever sees the client's own token on credential-less mappings â€” exactly the case the
    /// <see cref="ModelMapping.ForwardClientAuthorization"/> flag exists to gate.
    /// <para>
    /// Loopback is allowed through without the flag so the long-standing behaviour of a local proxy
    /// serving a local llama.cpp server is unchanged: the token never leaves the machine. Anything
    /// else is a third-party host and needs the explicit opt-in, because a listener bound to
    /// <c>0.0.0.0</c> or a NIC IP turns "whatever the client sent" into an exfiltration primitive.
    /// </para>
    /// </remarks>
    private static void StripUnoptedClientAuthorization(HttpRequestMessage request, ModelMapping mapping, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mapping);

        // A mapping with its own credential (apiKey non-null) has already had its Authorization
        // header set by ApplyApiKey above. Removing it here would defeat the configured credential
        // and force every mapping with a key to also check ForwardClientAuthorization â€” a checkbox
        // that is about relaying the *client's* token, not the mapping's own.
        if (!string.IsNullOrWhiteSpace(apiKey))
            return;

        if (mapping.ForwardClientAuthorization || NetworkingHelpers.IsLoopbackHost(mapping.UpstreamUrl))
            return;

        request.Headers.Remove(HttpHeaderNames.Authorization);
    }

    private static HttpClient BuildHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Single-user proxy: the cap is a ceiling on pooled sockets per host, not a
            // preallocation — connections are created lazily and recycled. 16 leaves headroom
            // for several concurrent client streams plus compaction calls and heartbeat probes
            // against the same upstream before requests start queueing at the handler.
            MaxConnectionsPerServer = 16,
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
            // Captured here rather than in each handler so every path â€” including the ones that
            // answer without a body â€” carries the caller's headers. Credential values are always
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
    /// <see cref="HandleAsync"/> â€” currently only the 503 shed when no concurrency slot frees within
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
                UpstreamPath = "(rejected before routing â€” no upstream call)",
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
    /// is proxied â€” meaning it reached, or was meant to reach, an upstream model. The boundary is
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
    /// reading of the boundary â€” the request was addressed to a model â€” and it keeps a client's
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

        if (path is "/scalar" or "/scalar/" || path == EndpointPaths.OpenApiSpec)
            return NonProxiedCategory.VersionAndExplorer;

        // Answered entirely from local mapping configuration, with no upstream call.
        if (path is "/api/ps" or "/api/show")
            return NonProxiedCategory.LocalStubs;

        if (isGet && (path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase)
                   || path.Equals("/models", StringComparison.OrdinalIgnoreCase)
                   || path.Equals("/models/", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)))
            return NonProxiedCategory.LocalStubs;

        // Routes addressed to a model stay in the Proxy log â€” including when they fail, because the
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
        // were already returned above, so any remaining /v1* path is a model call. The /v1-less
        // OpenAI paths the router normalises are the same model traffic as their /v1 twins, so they
        // stay in the Proxy log rather than being filed as rejected requests.
        return path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || TryNormalizeOpenAiPath(path, out _);
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
        // log entry at all â€” even with CollectAllTraffic on.
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
            // never write body bytes â€” HttpListener treats the response as having a 0-byte entity
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

            // Static version probe â€” infrastructure noise that would inflate the request log on
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

            // Scalar API explorer â€” served only when explicitly enabled in settings.
            if (_settings.EnableApiExplorer && method == "GET")
            {
                if (path is "/scalar" or "/scalar/")
                {
                    RecordResponseStatus(resp, log, 200);
                    await WriteHtmlAsync(resp, await BuildApiExplorerHtmlAsync(ct).ConfigureAwait(false), ct);
                    return;
                }

                if (path == EndpointPaths.OpenApiSpec)
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
                log.UpstreamPath = "(local mapping â€” no upstream call)";
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
                log.UpstreamPath = "(local mapping â€” no upstream call)";
                await HandleV1ModelsAsync(resp, log, ct);
            }
            else if (method == "GET" && path.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "(local mapping â€” no upstream call)";
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
                // endpoint toggle â€” per-mapping RedirectManualCompaction + the compaction target
                // decide whether the request is redirected or goes to the client's own model.
                log.UpstreamPath = "/v1/chat/completions/compact";
                await HandleManualCompactAsync(req, resp, log, ct);
            }
            else if (path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                  || path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            {
                // Transparent passthrough â€” forward OpenAI-native requests (e.g. from VS Copilot,
                // OpenAI SDKs) directly to the upstream llama.cpp /v1/* surface unchanged.
                log.UpstreamPath = path;
                await PassthroughAsync(req, resp, log, ct);
            }
            // OpenAI-compatible paths that omit the /v1 prefix. A client whose configured base URL is
            // the bare host sends /chat/completions, which used to reach the 404 branch below even
            // though the identical endpoint is served under /v1/chat/completions. Normalising here
            // means both base-URL shapes respond, so a client no longer has to be edited to add /v1
            // just to make the proxy answer.
            else if (method == "GET" && (path.Equals("/models", StringComparison.OrdinalIgnoreCase)
                                                  || path.Equals("/models/", StringComparison.OrdinalIgnoreCase)))
                        {
                            log.UpstreamPath = "(local mapping, no upstream call)";
                            await HandleV1ModelsAsync(resp, log, ct);
                        }
            else if (method == "GET" && path.StartsWith("/models/", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "(local mapping, no upstream call)";
                await HandleV1ModelAsync("/v1" + path, resp, log, ct);
            }
            else if (method == "POST" && path.Equals("/chat/completions/compact", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "/v1/chat/completions/compact";
                await HandleManualCompactAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path.Equals("/responses/compact", StringComparison.OrdinalIgnoreCase))
            {
                log.UpstreamPath = "/v1/responses/compact";
                await HandleCompactAsync(req, resp, log, ct);
            }
            else if (TryNormalizeOpenAiPath(path, out string normalizedOpenAiPath))
            {
                string normalizedPathAndQuery = normalizedOpenAiPath + (req.Url?.Query ?? string.Empty);
                log.UpstreamPath = normalizedPathAndQuery;
                await PassthroughAsync(req, resp, log, ct, normalizedPathAndQuery);
            }
            else
            {
                // Unknown endpoint. Recorded explicitly so an unrecognised call â€” a scanner, a
                // misconfigured client, or an endpoint this proxy does not implement â€” is visible
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
            // Oversized request body â€” reject before buffering to protect against memory exhaustion.
            log.ErrorMessage = ex.Message;
            Log.Warning("Rejected oversized request body on {Path}: {Message}", path, ex.Message);

            // The body is rejected before anything is streamed, so this should always be deliverable.
            if (!await TryWriteErrorResponseAsync(resp, log, 413, new { error = "Request body too large.", requestId }, CancellationToken.None))
                RecordUndeliverableError(log, 413);

            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        catch (RequestBodyTimeoutException ex)
        {
            // The client stopped sending its body (a chunked request has no Content-Length to
            // fast-reject against). Abandon it rather than hold the slot indefinitely.
            log.ErrorMessage = ex.Message;
            Log.Warning("Abandoned stalled request body on {Path}: {Message}", path, ex.Message);

            if (!await TryWriteErrorResponseAsync(resp, log, 408, new { error = "Request body timed out.", requestId }, CancellationToken.None))
                RecordUndeliverableError(log, 408);

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

            // Skip the finally AddLog â€” we already logged above with the exception.
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
            // here would race that write â€” and its status is already correct.
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
                // DeriveStatus treats as an error â€” the safe direction for a missing status.
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
                // recorded Error or Cancelled knows something the code cannot express â€” the
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
                    // Pass any exception attached by a handler, so its detail is stored and linked.
                    // A post-header passthrough failure records one because the client can no longer
                    // receive the cause; without this the entry would be a bare status with no
                    // explanation anywhere.
                    _stats.AddLog(log, log.Exception);
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

    /// <summary>
    /// How long a streaming passthrough waits for the upstream's first response header before it
    /// commits the client-facing SSE headers and starts pumping keep-alive comments.
    /// </summary>
    /// <remarks>
    /// Short enough that a client with a modest network timeout still sees bytes well before it gives
    /// up on a slow upstream, and long enough that an upstream which refuses the request outright â€” a
    /// body it will not accept, a dropped connection, bad credentials â€” fails inside the window and is
    /// still reported with a real HTTP status. Committing the headers first, which this path used to
    /// do, fixes the response at <c>200 text/event-stream</c>; from then on the only way to describe a
    /// failure is a frame inside that stream, and clients built on the OpenAI SDK or
    /// Microsoft.Extensions.AI parse such a frame as an empty completion, so the chat shows nothing.
    /// </remarks>
    private static readonly TimeSpan PreResponseCommitGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Estimated token count above which a request is large enough that an upstream which drops the
    /// connection during upload was probably refusing its size. Matches the threshold the
    /// proactive-overflow path already uses to warn about an unconfigured compaction setup.
    /// </summary>
    private const int LargeUpstreamRequestTokenThreshold = 50_000;

}
