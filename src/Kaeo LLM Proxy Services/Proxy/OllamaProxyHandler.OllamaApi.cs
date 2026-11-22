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

partial class OllamaProxyHandler
{
    // â”€â”€ /api/ps â†’ running model stub â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task HandlePsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        // Report configured enabled mappings as "running" so clients see the proxy-facing names
        // rather than whatever ID the upstream happens to advertise. The expires_at field is a
        // stub â€” llama.cpp keeps the model permanently loaded.
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

    // /api/show â†’ answered entirely from local mapping config, no upstream call

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

        // /api/show asks the proxy what it has configured for a model â€” it isn't a
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
        return normalized.Count > 0 ? normalized : null; // Omit when empty â€” matches omitempty.
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
            return null; // Omit from JSON entirely â€” matches Ollama's Go omitempty behavior

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

    // â”€â”€ /api/generate â†’ POST /v1/completions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task HandleGenerateAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaGenerateRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaGenerateRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        // Context-summarize (/compact) redirect.
        string? firstContent = !string.IsNullOrEmpty(ollamaReq.System) ? ollamaReq.System : ollamaReq.Prompt;
        var (effectiveModel, compactRedirected, resolvedModel) =
            ResolveCompactRedirect(_settings, ollamaReq.Model, firstContent, log, "generate");
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
        };

        // Sampling rules shared with /api/chat live in ApplySamplingOptions so the two
        // translations cannot drift apart.
        ApplySamplingOptions(llamaReq, mapping, ollamaReq.Options);

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
            using var streamSession = _streamMonitor?.Begin(log, StreamKind.Ollama);
            await StreamCompletionToOllamaAsync(
                upstreamResp,
                resp,
                ollamaReq.Model,
                log,
                _settings.CollectResponseDetails,
                responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                sw,
                ct,
                session: streamSession);
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
            // receives (the "after" of the OpenAIâ†’Ollama response translation).
            string ollamaJson = JsonSerializer.Serialize(ollamaResp, _jsonOptions);
            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(ollamaJson, ollamaReq.Model);

            await WriteJsonRawAsync(resp, ollamaJson, ct);
            log.Status = RequestStatus.Success;
        }
    }

    // â”€â”€ /api/chat â†’ POST /v1/chat/completions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaChatRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaChatRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        // Context-summarize (/compact) redirect.
        string? chatFirstContent = ollamaReq.Messages.Count > 0 ? ollamaReq.Messages[0].Content : null;
        var (effectiveModel, compactRedirected, resolvedModel) =
            ResolveCompactRedirect(_settings, ollamaReq.Model, chatFirstContent, log, "chat");
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
            Mirostat = ollamaReq.Options?.Mirostat,
            MirostatTau = ollamaReq.Options?.MirostatTau,
            MirostatEta = ollamaReq.Options?.MirostatEta,
            NCtx = ollamaReq.Options?.NumCtx,
        };

        // Sampling rules shared with /api/generate live in ApplySamplingOptions so the two
        // translations cannot drift apart.
        ApplySamplingOptions(llamaReq, mapping, ollamaReq.Options);

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
                (float?)ollamaReq.Options?.Temperature,
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

        // Appended after the block above, which assigns the summary from scratch and would otherwise
        // discard this line. Names the coding so a live session can be audited for which path ran.
        AppendTranslationCoding(log, "/api/chat request", _settings.UseIrTranslation);

        string upstreamBody = JsonSerializer.Serialize(llamaReq, _jsonOptions);
        // Capture the upstream-bound (translated) body so proxy-injected values such as
        // reasoning_effort can be compared against the client body in the request log.
        if (_settings.CollectRequestDetails || debug)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, upstreamBody, effectiveModel);

        // Proactive context-overflow check: when the estimated request exceeds the mapping's
        // configured threshold, compact the conversation before forwarding so we do not pay for
        // an upstream round-trip that is guaranteed to overflow.
        //
        // A signature-redirected /compact request takes a different route. It is a request for a
        // summary whose body is the oversized conversation, and `mapping` here is already the
        // compaction target, which cannot summarize a conversation larger than its own window. The
        // body is bounded to that target's window before forwarding. This cannot go through
        // TryProactiveOverflowAsync below: that method resolves the mapping's *own* compaction
        // target, which a redirected request has already been redirected to, so it would look for
        // the target's own target and silently do nothing.
        if (compactRedirected && mapping is not null)
        {
            string boundedBody = await BoundRedirectedCompactBodyAsync(
                mapping,
                upstreamBody,
                ollamaReq.Model,
                CompactionFormat.Ollama,
                ct);
            LogCompactionReduction(effectiveModel, upstreamBody, boundedBody);
            upstreamBody = boundedBody;
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
            {
                LogCompactionReduction(effectiveModel, upstreamBody, compactedBody);
                upstreamBody = compactedBody;
            }
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

                        using var streamSession = _streamMonitor?.Begin(log, StreamKind.Ollama);
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
                            session: streamSession,
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
                : !string.IsNullOrWhiteSpace(delta?.ReasoningContent) ? delta.ReasoningContent : null;
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
            // receives (the "after" of the OpenAIâ†’Ollama response translation).
            string ollamaJson = JsonSerializer.Serialize(ollamaResp, _jsonOptions);
            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(ollamaJson, ollamaReq.Model);

            await WriteJsonRawAsync(resp, ollamaJson, ct);
            log.Status = RequestStatus.Success;
        }

        return;
    }

    // â”€â”€ /api/embeddings â†’ POST /v1/embeddings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Streaming helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

}
