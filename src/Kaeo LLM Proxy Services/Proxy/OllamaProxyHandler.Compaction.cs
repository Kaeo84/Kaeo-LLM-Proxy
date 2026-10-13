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
    // â”€â”€ Manual /compact forwarding â”€â”€

    /// <summary>
    /// Rewrites the top-level <c>model</c> property of a manual compaction request body so it
    /// names the model the target's upstream actually knows. Clients address the proxy by its
    /// display name (e.g. <c>claude-fast</c>) while upstreams expect their own identifier
    /// (e.g. a <c>.gguf</c> path), so the field must be swapped before forwarding. The rest of
    /// the body is passed through untouched â€” compaction requests are already valid
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
    /// model handles its own compaction â€” there is no global fallback target and the proxy never
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
    /// resolves â€” just to a model the user never chose. The finder prefers the stored proxy name
    /// and only falls back to the ID.
    /// </remarks>
    internal static (ModelMapping Target, bool Redirected) ResolveManualCompactTarget(
        AppSettings settings, ModelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mapping);

        // Log the raw manual-compaction configuration before any gate is applied, so a debug
        // session shows the requesting model, the compaction model name it is configured with, and
        // the stored compaction model id even when the redirect does not fire. The id is a
        // surrogate key and the name is what the user picked, so both are emitted side by side to
        // make a stale-name/mismatched-id situation visible at a glance.
        Log.Debug(
            "Manual compaction target resolution for {Model}: redirect enabled {RedirectEnabled}, "
            + "configured compaction model name {CompactModelName}, configured compaction model id {CompactModelId}",
            mapping.ProxyName,
            mapping.RedirectManualCompaction,
            string.IsNullOrWhiteSpace(mapping.ContextSummarizeModelName) ? "(none)" : mapping.ContextSummarizeModelName,
            mapping.ContextSummarizeModelId?.ToString() ?? "(none)");

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
    /// The mapping to forward to â€” the compaction target when the request is redirected,
    /// otherwise the request's own mapping.
    /// </param>
    /// <param name="bodyText">The original request body.</param>
    /// <param name="originalModel">The model name the client asked for, for logging.</param>
    /// <param name="redirected">Whether the request was redirected to a separate compaction model.</param>
    /// <remarks>
    /// Handles its own failures. Whether a 500 can still be written depends on whether the SSE
    /// headers were pre-committed for a streaming request, and that state only exists inside this
    /// method â€” <see cref="HttpListenerResponse"/> exposes no "headers sent" flag â€” so the error
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
        bool clientDisconnected = false;

        // Written by the pump's disconnect continuation and read by the compaction and the catch
        // filter, which are on different threads; int + Volatile rather than bool so the write is
        // visible to the request thread without a lock on the hot path.
        int clientDisconnectedFlag = 0;

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

            // Streaming is decided from what the client sent, before the pre-flight may rewrite the
            // body: the rewrite only swaps the model field, but deciding here keeps the commit
            // decision independent of anything the compaction does.
            bool isStreamingRequest = IsStreamingJsonBody(forwardedBody);

            // The keep-alive pump starts BEFORE the pre-flight map-reduce, not after it. The
            // pre-flight can run for minutes (one upstream round-trip per chunk, serialized behind
            // the compaction gate), and it used to run with no bytes on the wire at all â€” so a client
            // with a short network timeout abandoned a compaction that was still progressing
            // normally. The passthrough path has always committed headers before compacting (see
            // PassthroughCoreAsync); this brings the manual path in line.
            using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Cancels the compaction if the client leaves. Separate from preResponseCts because
            // preResponseCts exists to stop the pump, whereas this stops the work the pump is
            // watching for â€” cancelling the pump must not cancel its own reason for running.
            using var compactionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> preResponseKeepAliveTask = Task.FromResult(false);

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

                // The reporting form so a disconnect can stop the compaction below rather than let
                // it run to completion against the model for a result nobody will receive.
                preResponseKeepAliveTask = PumpPreResponseSseKeepAliveReportingDisconnectAsync(
                    resp.OutputStream,
                    _settings.SseKeepAliveIntervalSeconds,
                    preResponseCts.Token);

                // Observe the pump so its disconnect verdict cancels the compaction. Runs
                // synchronously on the pump's completion, and reads the flag directly rather than
                // through a captured closure over a bool, so the cancel is not racy.
                _ = preResponseKeepAliveTask.ContinueWith(
                    pump =>
                    {
                        if (pump.IsCompletedSuccessfully && pump.Result)
                        {
                            Volatile.Write(ref clientDisconnectedFlag, 1);
                            try { compactionCts.Cancel(); }
                            catch (ObjectDisposedException) { /* request already finished */ }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // Pre-flight: a manual /compact body is forwarded verbatim, so unlike the proactive and
            // reactive paths it has never been measured against the target model's window. An
            // embeddings-only or otherwise small compaction target receives Copilot's entire
            // conversation and the upstream rejects it with a raw overflow error the client cannot
            // act on. Run it through the same map-reduce summarizer when the target allows it, and
            // otherwise reject here with an actionable message instead of paying for a round-trip
            // that is guaranteed to fail.
            string? preparedBody;
            try
            {
                preparedBody = await PrepareManualCompactBodyAsync(
                    targetMapping, forwardedBody, resp, log, headersPreCommitted, compactionCts.Token);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref clientDisconnectedFlag) == 1)
            {
                // The client left while the summarizer was running; the pump saw it and cancelled
                // the compaction above. Nothing to deliver, so stop without writing a status.
                clientDisconnected = true;
                preparedBody = null;
            }

            if (clientDisconnected)
            {
                await preResponseCts.CancelAsync();
                await preResponseKeepAliveTask;
                return;
            }

            // A null result means the response has already been written (rejection, or the
            // summarizer could not reduce the body).
            if (preparedBody is null)
            {
                await preResponseCts.CancelAsync();
                await preResponseKeepAliveTask;
                return;
            }

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

            // The pre-response keep-alive pump is already running (started before the pre-flight above)
            // and is stopped once the upstream's headers arrive, at which point the response body
            // itself takes over keeping the connection busy. The upstream send shares the compaction
            // token so a client that leaves during the summarization wait also stops this request
            // rather than holding an upstream slot for an undeliverable answer.
            HttpResponseMessage upstreamResp;
            try
            {
                upstreamResp = await SendUpstreamAsync(
                    upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, compactionCts.Token);
            }
            finally
            {
                await preResponseCts.CancelAsync();
                clientDisconnected = await preResponseKeepAliveTask;
            }

            // The client left while the upstream was working; the compaction token is already
            // cancelled, so there is nothing to deliver and no status worth writing.
            if (clientDisconnected)
            {
                upstreamResp.Dispose();
                return;
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
                    // Headers already sent as 200/SSE â€” emit the error as a data frame so the
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
                using var streamSession = _streamMonitor?.Begin(log, StreamKind.Sse);
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
                        tokenCounts,
                        session: streamSession);

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
                        tokenCounts,
                        session: streamSession);
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
        catch (OperationCanceledException) when (clientDisconnected)
        {
            // The client left and the pump already logged the disconnect; there is nothing to
            // deliver, so this is not a failure worth logging as one.
            log.Status = RequestStatus.Error;
            log.ErrorMessage = "Client disconnected during manual compaction";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual compaction for model {Model} threw", originalModel);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;

            // Once the SSE headers are committed the status code is fixed at 200; report the
            // failure inside the stream instead so the client is not left with a silent close.
            if (headersPreCommitted)
            {
                await WriteCompactErrorFrameAsync(resp, ex.Message, 500);
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
    /// Bounds a manual compaction body to the target model's context window before it is forwarded,
    /// using the chunked map-reduce summarizer. The manual <c>/compact</c> endpoints relay the body
    /// verbatim, so â€” unlike the proactive and reactive paths â€” nothing else has measured it against
    /// the target's window, and an oversized conversation aimed at a smaller compaction target fails
    /// upstream with an error the client cannot act on.
    /// </summary>
    /// <remarks>
    /// There is no additional opt-in beyond the redirection already decided by
    /// <see cref="ResolveManualCompactTarget"/>: reaching this method means the operator either
    /// selected a global Copilot compaction model or enabled "Redirect manual compaction" with a
    /// chosen target, so the target is a deliberate choice. When the body fits, it is returned
    /// unchanged so nothing is rewritten unnecessarily; a failure to reduce it is reported as a 413
    /// with the reason rather than forwarding a body the upstream is known to reject.
    /// </remarks>
    /// <param name="targetMapping">The mapping whose upstream will receive the compaction request.</param>
    /// <param name="bodyText">The upstream-bound compaction body.</param>
    /// <param name="resp">Client response, written to when the request is rejected.</param>
    /// <param name="log">Request log, updated when the request is rejected.</param>
    /// <param name="headersPreCommitted">
    /// Whether the SSE headers have already been committed for this request. When they have, a
    /// rejection cannot be delivered as a 413 â€” the status is fixed at 200 â€” so the reason has to be
    /// written into the stream as an error frame instead.
    /// </param>
    /// <param name="ct">
    /// Cancellation token for the summarization. A caller may pass a token that is additionally
    /// cancelled when the client disconnects, so abandoning the request stops paying for chunks whose
    /// summaries can no longer be delivered.
    /// </param>
    /// <returns>
    /// The body to forward (unchanged, or compacted), or null when a rejection response has already
    /// been written and the caller must stop.
    /// </returns>
    private async Task<string?> PrepareManualCompactBodyAsync(
        ModelMapping targetMapping,
        string bodyText,
        HttpListenerResponse resp,
        RequestLog log,
        bool headersPreCommitted,
        CancellationToken ct)
    {
        // Chunk sizing uses the compaction window (explicit value or the global conservative cap),
        // never the advertised default: overestimating a compact model's capacity is precisely what
        // made every attempt overflow.
        int compactionWindow = targetMapping.GetCompactionContextWindow(_settings.CompactionFallbackContextTokens);
        int estimated = EstimateTokenCount(bodyText);

        // Same budget the map-reduce path chunks against, so a body measured as fitting here is a
        // body the summarizer can actually accept.
        int budget = AutoCompactionService.GetSummaryPromptBudget(
            compactionWindow,
            AutoCompactionService.EffectiveSummaryMaxTokensFor(_settings.CompactionTargetTokens));
        if (estimated <= budget)
            return bodyText;

        var (baseUrl, timeout, apiKey) = ResolveUpstream(targetMapping.ProxyName);
        string compactModelName = string.IsNullOrWhiteSpace(targetMapping.ModelName)
            ? targetMapping.ProxyName
            : targetMapping.ModelName;
        int maxTokensPerChunk = budget;

                Log.Information(
                    "Manual compaction for {Model} exceeds its {Budget} token budget (~{Estimated} tokens); "
                    + "running chunked map-reduce summarization against the target's own upstream",
                    targetMapping.ProxyName, budget, estimated);

                // Measure the silent window explicitly. This span is the interval during which the client
                // receives nothing but keep-alive frames, and it is the figure that decides whether a client
                // with a short network timeout gives up on a compaction that is progressing normally. Logged
                // on both outcomes so an abandoned run still reports how far it got.
                Stopwatch preFlight = Stopwatch.StartNew();

                string sessionKey = $"manual:{targetMapping.ProxyName}:{bodyText.GetHashCode():X8}";
                string? compacted;
                try
                {
                    compacted = await _autoCompactionService.CompactAsync(
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
                }
                catch (OperationCanceledException)
                {
                    preFlight.Stop();
                    Log.Information(
                        "Manual compaction pre-flight for {Model} cancelled after {ElapsedMs} ms "
                        + "(~{Estimated} tokens to reduce); in-flight chunk work was abandoned",
                        targetMapping.ProxyName, preFlight.ElapsedMilliseconds, estimated);
                    throw;
                }

                preFlight.Stop();

                        if (compacted is null)
                        {
                            // A cancelled compaction does not surface as an exception: the service catches the
                            // cancellation while waiting for its gate and returns null, which is indistinguishable
                            // from "could not reduce". Ask the caller's own disconnect verdict so an abandoned
                            // request is not misreported to the client as an oversized-body rejection.
                            ct.ThrowIfCancellationRequested();

                            string message =
                        $"Manual compaction for '{targetMapping.ProxyName}' needs ~{estimated} tokens, over its "
                        + $"{budget} token budget, and chunked summarization did not produce a smaller result. "
                        + "Check that the model is reachable and try a larger Compaction Model.";

                    Log.Warning(
                        "Manual compaction map-reduce failed for model {Model} after {ElapsedMs} ms",
                        targetMapping.ProxyName, preFlight.ElapsedMilliseconds);

                    log.StatusCode = 413;
                    log.Status = RequestStatus.Error;
                    log.ErrorMessage = message;

                    // With the SSE headers already committed the status is fixed at 200, so the reason has
                    // to travel as an in-stream error frame; writing a 413 here would be silently dropped.
                    if (headersPreCommitted)
                    {
                        await WriteCompactErrorFrameAsync(resp, message, 413);
                        return null;
                    }

                    resp.StatusCode = 413;
                    await WriteJsonAsync(resp, new { error = message }, ct);
                    return null;
                }

                _autoCompactionService.RecordSuccess(sessionKey);
                Log.Information(
                    "Manual compaction for {Model} compacted ~{Original} â†’ ~{Compacted} est. tokens "
                    + "in {ElapsedMs} ms of pre-flight summarization",
                    targetMapping.ProxyName, estimated, EstimateTokenCount(compacted), preFlight.ElapsedMilliseconds);
                return compacted;
            }

            /// <summary>
            /// Writes an SSE error frame followed by the terminal <c>[DONE]</c> marker, for a failure that
            /// occurs after the SSE headers are already committed.
            /// </summary>
            /// <remarks>
            /// Once a 200 has been sent the real status cannot be changed, so reporting a failure means
            /// putting it in the stream. The terminal marker is required as well, not merely tidy: a client
            /// built on Microsoft.Extensions.AI awaits it and would otherwise block indefinitely on a stream
            /// that has quietly stopped.
            /// </remarks>
            private static async Task WriteCompactErrorFrameAsync(HttpListenerResponse resp, string message, int code)
            {
                try
                {
                    string errorFrame =
                        $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(message)},\"code\":{code}}}}}\n\ndata: [DONE]\n\n";
                    await resp.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorFrame), CancellationToken.None);
                    await resp.OutputStream.FlushAsync(CancellationToken.None);
                    resp.OutputStream.Close();
                }
                catch (Exception ex) when (IsClientDisconnect(ex) || ex is ObjectDisposedException)
                {
                    // The client is already gone; there is nobody left to report to.
                    Log.Debug(ex, "Could not write a compaction error frame");
                }
            }

    /// <summary>
    /// Bounds a redirected Copilot <c>/compact</c> body to the compaction target's own context window
    /// before it is forwarded, using the same chunked map-reduce summarizer the rest of the pipeline
    /// uses.
    /// </summary>
    /// <remarks>
    /// A redirected request is a request for a summary whose body is the oversized conversation, and
    /// the target cannot summarize a conversation larger than its own window. Previously the redirect
    /// suppressed every compaction gate on the theory that compacting a compaction request would
    /// summarize a summary; the result was that an oversized body reached the target verbatim and the
    /// upstream rejected it with a raw <c>exceed_context_size_error</c>.
    /// <para>
    /// This is deliberately not routed through <see cref="TryProactiveOverflowAsync"/>: that method
    /// resolves the mapping's <em>own</em> configured compaction target, and a redirected request has
    /// already been redirected â€” re-resolving would find the target's own target (usually none) and
    /// silently do nothing. Here the target is known, so the work is done directly against it.
    /// </para>
    /// <para>
    /// There are no further conditions. A redirect only happens because the operator turned Copilot
    /// compaction routing on and selected this target (or, for other clients, because the mapping
    /// opted into manual redirect), so the target is already a deliberate choice. Requiring
    /// <see cref="ModelMapping.AutoCompactPaths"/> on top of that was a redundant second opt-in that
    /// silently did nothing but forward the oversized body â€” the exact failure the redirect exists to
    /// prevent. <see cref="ModelMapping.AutoCompactPaths"/> still governs proactive compaction of a
    /// mapping's own traffic; it is not consulted here.
    /// </para>
    /// <para>
    /// When the body already fits, the original text is returned unchanged so nothing is rewritten
    /// unnecessarily.
    /// </para>
    /// </remarks>
    /// <param name="targetMapping">The compaction target that will serve the redirected request.</param>
    /// <param name="bodyText">The upstream-bound request body.</param>
    /// <param name="originalModel">The model the client asked for, for logging.</param>
    /// <param name="format">The compacted-body format matching the path being served.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The body to forward, either compacted or unchanged.</returns>
    /// <remarks>
    /// Deliberately takes no output stream. The caller keeps the client's connection alive with valid
    /// SSE comment frames while this runs; writing progress text from here as well would put two
    /// writers on the same stream and interleave it.
    /// </remarks>
    private async Task<string> BoundRedirectedCompactBodyAsync(
        ModelMapping targetMapping,
        string bodyText,
        string originalModel,
        CompactionFormat format,
        CancellationToken ct)
    {
        int compactionWindow = targetMapping.GetCompactionContextWindow(_settings.CompactionFallbackContextTokens);
        int budget = AutoCompactionService.GetSummaryPromptBudget(
            compactionWindow,
            AutoCompactionService.EffectiveSummaryMaxTokensFor(_settings.CompactionTargetTokens));
        int estimated = EstimateTokenCount(bodyText);

        if (estimated <= budget)
            return bodyText;

        var (baseUrl, timeout, apiKey) = ResolveUpstream(targetMapping.ProxyName);
        string compactModelName = string.IsNullOrWhiteSpace(targetMapping.ModelName)
            ? targetMapping.ProxyName
            : targetMapping.ModelName;

        Log.Information(
            "Redirected compaction for {OriginalModel} exceeds compaction model {Target}'s {Budget} token budget "
            + "(~{Estimated} tokens); running chunked map-reduce summarization before forwarding",
            originalModel, targetMapping.ProxyName, budget, estimated);

        // The circuit breaker counts attempts before doing any work, so it is keyed per
        // conversation: a request that keeps overflowing backs off instead of re-summarizing
        // forever.
        string sessionKey = $"redirected:{targetMapping.ProxyName}:{bodyText.GetHashCode():X8}";

        string? compacted = await _autoCompactionService.CompactAsync(
            targetMapping,
            bodyText,
            sessionKey,
            baseUrl,
            apiKey,
            timeout,
            budget,
            compactModelName,
            compactionWindow,
            compactionWindow,
            ct,
            format,
            isCopilotSummary: IsCopilotCompactionBody(bodyText));

        if (compacted is null)
        {
            Log.Warning(
                "Redirected compaction for {OriginalModel} could not reduce the body below {Target}'s "
                + "{Budget} token budget; forwarding unchanged and letting the upstream report the overflow",
                originalModel, targetMapping.ProxyName, budget);
            return bodyText;
        }

        _autoCompactionService.RecordSuccess(sessionKey);
        Log.Information(
            "Redirected compaction for {OriginalModel} compacted ~{Original} â†’ ~{Compacted} est. tokens",
            originalModel, estimated, EstimateTokenCount(compacted));
        return compacted;
    }

    // â”€â”€ POST /v1/responses/compact â†’ OpenAI-compatible conversation compaction â”€â”€

    /// <summary>
    /// Handles <c>POST /v1/responses/compact</c> â€” forwards the compaction request to a model's
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

    // â”€â”€ POST /v1/chat/completions/compact â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Handles <c>POST /v1/chat/completions/compact</c> â€” manual context compaction endpoint.
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

}
