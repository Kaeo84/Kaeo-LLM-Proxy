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
    // â”€â”€ /v1/* â†’ transparent passthrough â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
    /// <remarks>
    /// <see cref="LastUpstreamError"/> exists because once the headers are committed the failure can
    /// only be reported as a frame, and the cause is then invisible to the client. Without it a late
    /// upstream error arrived as a bare exception with the upstream's own explanation discarded, which
    /// left the request log reporting a generic stream failure that could not be acted on.
    /// </remarks>
    private sealed class PassthroughResponseState
    {
        /// <summary>True once <c>200 text/event-stream</c> has been flushed to the client.</summary>
        public bool HeadersCommitted { get; set; }

        /// <summary>
        /// The upstream's own error body and status, when the upstream answered with a failure.
        /// Captured so a later stream failure can name the real cause rather than only the symptom.
        /// </summary>
        public string? LastUpstreamError { get; set; }
    }

    /// <summary>
    /// Forwards any OpenAI-native <c>/v1/*</c> request to the resolved upstream, containing every
    /// failure so the client always receives a complete response. Once the SSE headers are committed
    /// a status code can no longer be changed, so a late failure is reported as an error frame
    /// followed by the <c>data: [DONE]</c> terminator instead of a bare connection close â€” without
    /// which a client awaiting the terminal stream event blocks indefinitely.
    /// </summary>
    private async Task PassthroughAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct,
        string? pathOverride = null)
    {
        PassthroughResponseState state = new();

        try
        {
            await PassthroughCoreAsync(req, resp, log, state, ct, pathOverride).ConfigureAwait(false);
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

            // Attach the exception so the full stack trace is persisted and linked to this entry.
            // The previous Log.Warning passed it to Serilog only, and that sink stores the message
            // without the trace, so a post-header failure left no diagnosable record at all.
            //
            // The upstream's own error body is named in the message when it exists, because a bare
            // "the response was interrupted" cannot be acted on and that body is the actionable part.
            log.Exception = ex;

                        // A client disconnect is normal operation, not a fault: Copilot abandons a turn around
                        // compaction routinely. Logging it at Warning buried the failures that matter, and the
                        // generic description previously blamed the upstream for it.
                        if (IsClientDisconnect(ex))
                        {
                            Log.Debug(
                                "Client disconnected during passthrough for {Method} {Path} (model {Model}, request {RequestId})",
                                log.Method,
                                log.OllamaPath,
                                string.IsNullOrWhiteSpace(log.Model) ? "(unknown)" : log.Model,
                                log.RequestId);
                        }
                        else
                        {
                            Log.Warning(
                                "Passthrough failed after the SSE headers were committed for {Method} {Path} (model {Model}, request {RequestId}); "
                                + "terminating the stream with an error frame. Cause: {Cause}. Upstream error: {UpstreamError}",
                                log.Method,
                                log.OllamaPath,
                                string.IsNullOrWhiteSpace(log.Model) ? "(unknown)" : log.Model,
                                log.RequestId,
                                description,
                                state.LastUpstreamError ?? "(none reported)");
                        }

                        await WritePostCommitErrorFramesAsync(resp, description, 502, ct).ConfigureAwait(false);
                        return;
        }
        catch (Exception ex)
        {
            // The upstream failed before the proxy put a single byte on the wire, so a real status
            // code is still available and that is how the failure has to be reported. Describing it
            // inside an already-committed 200 SSE stream instead is invisible: clients built on the
            // OpenAI SDK or Microsoft.Extensions.AI read such an error frame as an empty completion,
            // so the chat shows nothing at all. This branch is the reason the SSE headers are held
            // back for PreResponseCommitGrace rather than committed up front.
            string description = DescribePassthroughFailure(ex);

            log.Status = RequestStatus.Error;
            log.ErrorMessage = description;
            log.Exception = ex;

            if (IsClientDisconnect(ex))
            {
                Log.Debug(
                    "Client disconnected during passthrough for {Method} {Path} (model {Model}, request {RequestId})",
                    log.Method,
                    log.OllamaPath,
                    string.IsNullOrWhiteSpace(log.Model) ? "(unknown)" : log.Model,
                    log.RequestId);
                return;
            }

            Log.Warning(
                ex,
                "Passthrough failed before any response was sent for {Method} {Path} (model {Model}, request {RequestId}); "
                + "reporting 502 to the client. Cause: {Cause}",
                log.Method,
                log.OllamaPath,
                string.IsNullOrWhiteSpace(log.Model) ? "(unknown)" : log.Model,
                log.RequestId,
                description);

            if (!await TryWriteErrorResponseAsync(
                    resp, log, 502, new { error = description, requestId = log.RequestId }, CancellationToken.None))
            {
                RecordUndeliverableError(log, 502);
                CloseResponseQuietly(resp);
            }
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
    /// unavailability are named explicitly because those are the two cases a client can act on;
    /// everything else stays generic.
    /// </summary>
    /// <remarks>
    /// Deliberately does not echo <c>ex.Message</c>. An <see cref="HttpRequestException"/> carries the
    /// upstream URI and often a hostname, and that text goes into an SSE error frame the client reads
    /// â€” the same leak <see cref="DescribeUpstreamStreamError"/> exists to avoid. The full exception is
    /// still recorded on the request log, where an operator can see it and a client cannot.
    /// <para>
    /// <see cref="HttpListenerException"/> is the client disconnecting, not an upstream fault. It was
    /// previously unlisted â€” it derives from <see cref="System.ComponentModel.Win32Exception"/>, not
    /// <see cref="IOException"/> â€” and fell through to the generic arm, so every client disconnect was
    /// logged as an upstream error. That misattribution cost real diagnosis time.
    /// </para>
    /// </remarks>
    private static string DescribePassthroughFailure(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "Upstream request timed out.",
        HttpRequestException => "Upstream connection error: the upstream server closed the connection or became unreachable.",
        HttpListenerException => "The client disconnected before the response completed.",
        IOException => "Upstream I/O error: the upstream connection was interrupted.",
        _ => "Upstream error: the response was interrupted before it completed.",
    };

    /// <summary>
    /// Returns whether the failure is the client going away rather than anything the proxy or the
    /// upstream did wrong.
    /// </summary>
    /// <remarks>
    /// Kept separate from the description so the log can record it at Debug instead of Warning. A
    /// client that abandons a turn â€” which Copilot does routinely around compaction â€” is normal
    /// operation, and warning on it buries the failures that matter.
    /// </remarks>
    private static bool IsClientDisconnect(Exception ex) =>
        ex is HttpListenerException
            // A cancelled write to the response stream is also the client leaving; the cancellation
            // token is the listener's, not the caller's, so it lands here rather than as a cancellation.
            || (ex is IOException io && io.InnerException is System.Net.Sockets.SocketException);

    /// <summary>
    /// Test seam for <see cref="DescribePassthroughFailure"/>, so the no-leak guarantee can be
    /// asserted across the realistic exception shapes rather than inferred from reading the switch.
    /// </summary>
    internal static string DescribePassthroughFailureForTest(Exception ex) => DescribePassthroughFailure(ex);

        /// <summary>
        /// Test seam for <see cref="IsClientDisconnect"/>, so the classification that decides whether a
        /// failure is logged at Debug or Warning can be asserted directly.
        /// </summary>
        internal static bool IsClientDisconnectForTest(Exception ex) => IsClientDisconnect(ex);

    private async Task PassthroughCoreAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, PassthroughResponseState state, CancellationToken ct,
        string? pathOverride = null)
    {
        bool contextCompacted = false;

        // The upstream target path. A client whose base URL omits the OpenAI /v1 prefix (e.g. it
        // posts to /chat/completions) reaches here with the normalised path supplied by the router,
        // so the override replaces the raw request URL everywhere the upstream target is derived.
        string requestPathAndQuery = pathOverride ?? req.Url?.PathAndQuery ?? "/";
        string requestPath = pathOverride is not null
            ? pathOverride.Split('?')[0]
            : req.Url?.AbsolutePath ?? "/";

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

        // Deferred rather than immediate: see PreResponseCommitGrace. Committing the headers now
        // would fix the response at 200 before the upstream has even been contacted, so a request the
        // upstream then refuses would have no status code left to report the refusal with.
        async Task DeferredPreResponseKeepAliveAsync(CancellationToken graceCt)
        {
            try
            {
                await Task.Delay(PreResponseCommitGrace, graceCt);
            }
            catch (OperationCanceledException)
            {
                // The upstream answered â€” or failed â€” inside the grace window, so there is nothing to
                // keep the client alive through and the headers are still free to carry its status.
                return;
            }

            await CommitSseHeadersAsync();
            await PumpPreResponseSseKeepAliveAsync(
                resp.OutputStream,
                _settings.SseKeepAliveIntervalSeconds,
                graceCt);
        }

        using var upstreamReq = new HttpRequestMessage
        {
            Method = new HttpMethod(req.HttpMethod),
            // RequestUri is set to relative path; SendUpstreamAsync will make it absolute.
            RequestUri = new Uri(requestPathAndQuery, UriKind.Relative),
        };

        // Copy request headers, skipping hop-by-hop headers the HttpClient manages itself and
        // proxy/forwarding headers that a direct-access client could spoof (see ShouldSkipRequestHeader).
        // Headers that can't be placed on the request message (e.g. because Content is null yet);
        // they'll be applied after Content is assigned below.
        Dictionary<string, string>? deferredContentHeaders = null;

        foreach (string? name in req.Headers.AllKeys)
        {
            if (name is null) continue;
            if (ShouldSkipRequestHeader(name))
                continue;

            // Authorization is copied here so a client's own bearer token (e.g. Visual
            // Studio's OpenAI-compatible model connection) reaches the upstream for
            // mappings without their own configured credential. ApplyApiKey below overrides
            // this with the mapping's resolved credential secret when one is configured.
            // The copy is unconditional on the mapping because the mapping is not known yet at
            // this point - the body has to be parsed to learn which model was asked for - so
            // StripUnoptedClientAuthorization removes it after ResolveUpstream has settled the
            // mapping, unless the mapping opted in or its base URL is this machine.

            string value = req.Headers[name] ?? string.Empty;
            // Content is assigned later downstream; if the header can't be placed on the
            // request message, store it and apply it after Content is created instead of
            // falling back to Content?.Headers which is always null at this point.
            if (!upstreamReq.Headers.TryAddWithoutValidation(name, value))
            {
                if (deferredContentHeaders is null)
                    deferredContentHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                deferredContentHeaders[name] = value;
            }
        }

        // Track which original model was requested so we can resolve the upstream URL.
        string originalModel = string.Empty;
        bool isCompletionPath = IsChatCompletionsPath(requestPath)
            || requestPath.Equals("/v1/completions", StringComparison.OrdinalIgnoreCase);
        bool isStreamingRequest = false;
        // Captured upstream-bound JSON body so an overflow rejection can be compacted and retried.
        string? passthroughBody = null;
        string? consumedErrorBody = null;
        // Set when the signature-based /compact redirect already retargeted this request to the
        // mapping's compaction model, which means the client is asking for a summary and the body
        // it sent is the oversized conversation to summarize. The reactive path treats that as
        // terminal: on an exceeded-context error there is nothing left to summarize (the response
        // of a summarize request IS a summary), so it times out instead of compacting the summary.
        // The proactive path still runs â€” bounding the body to the target's window is exactly what
        // makes the summarize request succeed, and skipping it is what let an oversized body reach
        // the compaction model and fail with a raw upstream 400.
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

                // Phase B4: the ordered payload pipeline (Plans/20260914-proxy-meai-phase-b-design.md)
                // behind the same default-off flag as the IR frame translator; parity tests pin it to
                // the legacy method, which remains the default coding path.
                string rewritten;
                if (_settings.UseIrTranslation)
                {
                    RequestPayloadPipeline.Result pipeline = RequestPayloadPipeline.Run(
                        bodyText, _settings, log, RequestPayloadPipeline.DefaultSteps);
                    rewritten = pipeline.Body;
                    passthroughStreamOptions = pipeline.StreamOptions;
                }
                else
                {
                    rewritten = NormalizeRequestBody(
                        bodyText,
                        _settings,
                        log,
                        modelName => ShouldApplyThinkingCompatibility(_settings, modelName),
                        out passthroughStreamOptions);
                }

                // Name the coding so a live session can be audited for which path actually ran.
                AppendTranslationCoding(log, "passthrough request", _settings.UseIrTranslation);

                originalModel = log.Model; // set by the normalization path
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
                // A signature-redirected /compact request takes a different route. It is a request
                // for a summary whose body is the oversized conversation, and the compaction target
                // cannot summarize a conversation larger than its own window, so the body is bounded
                // to that target's window before forwarding. This cannot go through
                // TryProactiveOverflowAsync below: that method resolves the mapping's *own*
                // compaction target, which a redirected request has already been redirected to, so
                // it would look for the target's own target and silently do nothing.
                alreadyRedirectedForCompaction = !string.IsNullOrEmpty(log.OriginalModel);
                if (alreadyRedirectedForCompaction
                    && _settings.FindModelMapping(originalModel) is { } redirectTarget)
                {
                    // No SSE headers are committed and no keep-alive pump runs on this path.
                    //
                    // An earlier fix (65920a5) removed exactly this â€” a comment frame written before
                    // the compaction work â€” because committing the response headers before there is a
                    // response is wrong. Re-adding it as a keep-alive pump reproduced the failure: the
                    // stream opens, the client begins parsing SSE, and it receives comment frames only
                    // while a summary it is awaiting takes minutes to compute. Copilot then gives up
                    // or reports that there was nothing to compact.
                    //
                    // The pump is right for the pre-response wait, where the stream is about to carry
                    // the model's own tokens. It is wrong here, where the work produces a single
                    // non-streamed summary and nothing is written until it exists.
                    string beforeBounding = rewritten;
                    rewritten = await BoundRedirectedCompactBodyAsync(
                        redirectTarget,
                        rewritten,
                        originalModel,
                        CompactionFormat.Proxy,
                        ct);

                    contextCompacted = !ReferenceEquals(rewritten, beforeBounding);
                }
                else
                {
                    // Opened lazily, only if compaction actually runs and needs to write progress
                    // comments. Committing the SSE headers for every streaming request â€” what this
                    // block used to do â€” put a 200 on the wire before the upstream was contacted, so
                    // a request the upstream then refused could no longer be reported with an error
                    // status and reached the client as a silent empty stream.
                    Func<Task<Stream>>? openCompactionStream = isStreamingRequest
                        ? async () =>
                        {
                            await CommitSseHeadersAsync();
                            return resp.OutputStream;
                        }
                        : null;

                    // Per-mapping AutoCompactPaths is the only gate: TryProactiveOverflowAsync
                    // consults IsAutoCompactActiveFor, the threshold, and the compaction target.
                    // There is deliberately no global toggle, so a mapping left on "Disabled"
                    // never compacts and the model handles its own context.
                    string? compacted = await TryProactiveOverflowAsync(_settings.FindModelMapping(originalModel), rewritten, originalModel, resp, AutoCompactPaths.OpenAI, openCompactionStream, ct);
                    if (compacted is not null)
                    {
                        LogCompactionReduction(originalModel, rewritten, compacted);
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

            // Apply headers that couldn't be placed on the request message earlier
            // because Content was null; now Content is assigned.
            //
            // A header the proxy already set on the content it built must be skipped rather than
            // re-applied: HttpContentHeaders appends a second value instead of replacing, so
            // re-adding the client's Content-Type over the one this method wrote for the rewritten
            // body produced "application/json, application/json; charset=utf-8". Upstreams cannot
            // route a request with two content types and rejected it with a 400 ("Model not exist."
            // from the Alibaba compatible-mode endpoint). Content-Length is likewise owned by the
            // content the proxy constructed and must not be taken from the inbound request.
            if (deferredContentHeaders is not null && upstreamReq.Content is { } deferredTarget)
            {
                foreach (var kvp in deferredContentHeaders)
                {
                    if (deferredTarget.Headers.Contains(kvp.Key))
                        continue;

                    deferredTarget.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }
        }

        // Copilot compatibility: guarantee the stream reaches its terminal event. Clients built on
        // Microsoft.Extensions.AI await `data: [DONE]` â€” and, when they asked for it via
        // stream_options.include_usage, the terminal usage chunk the proxy stripped â€” and block
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

        ModelMapping upstreamMapping = ResolveMapping(originalModel);
        var (baseUrl, timeout, apiKey) = ResolveUpstream(upstreamMapping);
        ApplyApiKey(upstreamReq, apiKey);
        StripUnoptedClientAuthorization(upstreamReq, upstreamMapping, apiKey);

        // Append upstream routing info to the debug summary so it's visible which server
        // the request is actually being sent to, including after compact redirects.
        if (_settings.DebugMode && log.DebugSummary is not null)
        {
            string mappingName = string.IsNullOrEmpty(upstreamMapping.ProxyName) ? originalModel : upstreamMapping.ProxyName;
            log.DebugSummary += "\n" + DebugNotes.UpstreamRouting(
                mappingName, baseUrl, !string.IsNullOrWhiteSpace(apiKey), timeout);
        }

        // For streaming requests, hold the client-facing SSE headers back for a short grace period and
        // pump keep-alive comments only if the upstream has not answered by then. llama.cpp does not
        // send any HTTP headers until the first token is ready, so clients with a short NetworkTimeout
        // (e.g. the OpenAI .NET SDK default of 100 s) would otherwise time out silently during long
        // prompt-processing / thinking phases.
        //
        // The grace period is what keeps a refused request reportable: committing immediately fixes
        // the response at 200 text/event-stream, so an upstream that drops the connection mid-upload,
        // rejects the credentials, or refuses the body could only be described by a frame inside that
        // stream, which clients built on the OpenAI SDK or Microsoft.Extensions.AI read as an empty
        // completion. See PreResponseCommitGrace.
        //
        // This deliberately does NOT test state.HeadersCommitted. The proactive-compaction path above
        // may already have committed the SSE headers, in which case CommitSseHeadersAsync is a no-op
        // and only the pump is new.
        using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task preResponseKeepAliveTask = Task.CompletedTask;

        if (isStreamingRequest && ShouldEmitSseKeepAlive(originalModel))
        {
            preResponseKeepAliveTask = DeferredPreResponseKeepAliveAsync(preResponseCts.Token);
        }

        HttpResponseMessage upstreamResp;
        try
        {
            try
            {
                upstreamResp = await SendUpstreamAsync(
                    upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (
                passthroughBody is not null
                && !contextCompacted
                && EstimateTokenCount(passthroughBody) > LargeUpstreamRequestTokenThreshold)
            {
                // The upstream dropped the connection while the request was still being uploaded. For
                // a conversation this size the usual cause is a body the upstream will not accept, and
                // it is indistinguishable here from a context overflow because the connection dies
                // before any error body can arrive â€” the reactive path below only fires on an HTTP
                // status, so an oversize request had no recovery at all. Compact and retry once so an
                // oversize body is replaced by one the upstream will actually take.
                Log.Warning(ex,
                    "Upstream refused a large request for {Model} (~{EstimatedTokens} est. tokens); compacting before retrying",
                    originalModel, EstimateTokenCount(passthroughBody));

                // Stop the deferred keep-alive first: it must not commit the SSE headers in the middle
                // of a compaction that may still end in a real error status.
                await preResponseCts.CancelAsync();
                await preResponseKeepAliveTask;

                string? reactive = await TryReactiveCompactionAsync(
                    passthroughBody, originalModel, resp, state.HeadersCommitted, ct);

                if (reactive is null)
                    throw; // nothing can shrink this body; let the original failure surface to the client

                contextCompacted = true;
                passthroughBody = reactive;

                using var retryReq = BuildRetryRequest(requestPathAndQuery, reactive, upstreamReq, apiKey);

                upstreamResp = await SendUpstreamAsync(
                    retryReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
            }
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
        // Same gate the generic path above uses: only once, never when nothing can summarize.
        // A redirected /compact request IS allowed through here, because the redirect target is a
        // normal model that can itself overflow: when that happens there IS still work to do â€”
        // chunking the conversation down so the compaction model can read it. What must stay
        // impossible is recursive summarization, and that is prevented structurally rather than by
        // this flag: the compacted body the retry carries no longer matches the /compact signature
        // only when a redirect to a *different* model fired, and when target and declared model are
        // the same the reactive path resolves that model's own compaction target, which is a
        // different model by construction (ResolveManualCompactTarget rejects a self-reference).
        if (!upstreamResp.IsSuccessStatusCode
            && passthroughBody is not null
            && !contextCompacted)
        {
            string probe = await upstreamResp.Content.ReadAsStringAsync(ct);
            if (IsContextOverflowBody(probe))
            {
                // The upstream tells us the window it actually loaded. Trust it over the mapping's
                // declaration for this request: that disagreement is precisely what makes compaction
                // forward an oversized body in the belief that it fits.
                int reportedWindow = ExtractReportedContextWindow(probe);
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
                        passthroughBody, originalModel, resp, state.HeadersCommitted, ct, reportedWindow);
                    if (reactive is not null)
                    {
                        contextCompacted = true;
                        passthroughBody = reactive;
                        upstreamResp.Dispose();

                        using var retryReq = BuildRetryRequest(requestPathAndQuery, reactive, upstreamReq, apiKey);

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

            // Copy response headers (multi-value safe; hop-by-hop and Content-Length skipped).
            CopyUpstreamResponseHeaders(resp, upstreamResp);

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

            // Record the upstream's own explanation on the state, so a failure that happens after the
            // headers are committed can name it. Without this the later catch sees only a stream-level
            // symptom and the actionable part â€” what the upstream actually said â€” is gone.
            state.LastUpstreamError = $"HTTP {(int)upstreamResp.StatusCode}: {errorBody}";
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
                // Headers already sent as 200/SSE â€” emit the error as a data frame so the
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
        bool shouldMirrorReasoningContent = IsChatCompletionsPath(requestPath);

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
            using var streamSession = _streamMonitor?.Begin(log, StreamKind.Sse);
            if (shouldMirrorReasoningContent)
            {
                // The chat-completions stream is rewritten (thinking mode, XML tool calls), so
                // capture the raw upstream frames separately: ResponseBody keeps stripped/moved
                // thinking reviewable and UpstreamResponseBody records the DebugMode "before".
                using ResponseCaptureStream? rawCapture = collectResponse || debugCapture
                    ? new ResponseCaptureStream(Stream.Null)
                    : null;

                // Name the response-side coding too, since the request-side attribution above only
                // covers the request pipeline. A live audit needs both to tell which pair ran.
                AppendTranslationCoding(log, "passthrough response stream", _settings.UseIrTranslation);
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
                    session: streamSession,
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
                    ShouldEmitSseKeepAlive(originalModel),
                    _settings.SseKeepAliveIntervalSeconds,
                    ct,
                    () => _stats.IncrementSseKeepAlive(originalModel),
                    onUsage,
                    streamTerminator,
                    tokenCounts,
                    session: streamSession);
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
    /// Maps an OpenAI-compatible path that omits the <c>/v1</c> prefix onto its canonical
    /// <c>/v1/...</c> form. Clients differ in whether the base URL they were configured with already
    /// ends in <c>/v1</c>, so the same client can send either shape; accepting both means a base URL
    /// no longer has to be edited to add <c>/v1</c> before the proxy responds. Returns false for
    /// anything that is not a known OpenAI surface, so the caller still answers 404 for it.
    /// </summary>
    internal static bool TryNormalizeOpenAiPath(string path, out string normalized)
    {
        normalized = path.ToLowerInvariant() switch
        {
            "/chat/completions" => "/v1/chat/completions",
            "/completions" => "/v1/completions",
            "/embeddings" => "/v1/embeddings",
            "/responses" => "/v1/responses",
            _ => string.Empty,
        };

        if (normalized.Length > 0)
            return true;

        // Single-resource lookups, e.g. /responses/{id}.
        if (path.StartsWith("/responses/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "/v1" + path;
            return true;
        }

        return false;
    }

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
    /// forced to <c>"tool_calls"</c> â€” the non-streaming parity of the streaming
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
                // declared â€” a function part the client cannot bind (Copilot UI crash). Strip
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

                // XML tool-call extraction â€” only for choices the upstream did not already
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
        // Fast pre-filter: bodies without the literal key at all cannot match, and the vectorized
        // IndexOf behind Contains is far cheaper than a regex scan over a multi-megabyte payload.
        if (!json.Contains("\"stream\"", StringComparison.OrdinalIgnoreCase))
            return false;

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
    /// <remarks>
    /// A client that has gone away must not fault this task. Keep-alive is the only traffic on the
    /// wire during a redirected compaction, so the socket is where a disconnect surfaces â€” as an
    /// <see cref="HttpListenerException"/>, since the stream is an
    /// <see cref="HttpResponseStream"/> â€” and the callers observe the pump's exception by awaiting
    /// it in a <c>finally</c> block. Left to propagate, that exception skipped the compaction
    /// cancellation and the local-retry cleanup that share those blocks, and a normal client
    /// disconnect was reported as a passthrough failure. The pump has no useful work left once the
    /// client is gone, so it stops quietly; the real failure is reported by whatever path is
    /// awaiting the compaction itself.
    /// </remarks>
    private static async Task PumpPreResponseSseKeepAliveAsync(
        Stream output,
        int intervalSeconds,
        CancellationToken ct)
        => await PumpPreResponseSseKeepAliveCoreAsync(output, intervalSeconds, ct).ConfigureAwait(false);

    /// <summary>
    /// Disconnect-reporting form of <see cref="PumpPreResponseSseKeepAliveAsync"/>: returns true when
    /// the pump stopped because the client went away, false when it stopped for any other reason
    /// (normal cancellation, or shutdown).
    /// </summary>
    /// <remarks>
    /// The pump is the only writer on the wire during a long compaction, which makes its next write
    /// the earliest point a client disconnect can be observed at all â€” <see cref="System.Net.Http.HttpClient"/>
    /// does not cancel an in-flight request when the peer vanishes, and the request's
    /// <see cref="CancellationToken"/> is the listener's lifetime token, cancelled only by a proxy
    /// stop. So a caller that wants to stop paying for work whose result can no longer be delivered
    /// has to learn it from here.
    /// <para>
    /// Also the pump's resolution limit: a disconnect is seen at the next scheduled frame, so with the
    /// default 60-second interval the detection lags by up to that long. A shorter keep-alive interval
    /// therefore shortens both the client's timeout headroom and the abandoned-work window.
    /// </para>
    /// </remarks>
    internal static async Task<bool> PumpPreResponseSseKeepAliveReportingDisconnectAsync(
        Stream output,
        int intervalSeconds,
        CancellationToken ct)
        => await PumpPreResponseSseKeepAliveCoreAsync(output, intervalSeconds, ct).ConfigureAwait(false);

    /// <summary>
    /// Shared implementation behind both public shapes: pumps frames until cancelled, and reports
    /// whether it stopped because the client disconnected.
    /// </summary>
    private static Task<bool> PumpPreResponseSseKeepAliveCoreAsync(
        Stream output,
        int intervalSeconds,
        CancellationToken ct)
        => PumpPreResponseSseKeepAliveCoreAsync(
            output, AppSettings.SseKeepAliveInterval(intervalSeconds), ct);

    /// <summary>
    /// Cadence-driven core. Production callers always arrive through the seconds overload so the
    /// interval is clamped into the supported range; taking a <see cref="TimeSpan"/> here is what lets
    /// a test observe a frame without sitting through the
    /// <see cref="AppSettings.MinSseKeepAliveIntervalSeconds"/> floor.
    /// </summary>
    private static async Task<bool> PumpPreResponseSseKeepAliveCoreAsync(
        Stream output,
        TimeSpan interval,
        CancellationToken ct)
    {
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
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            Log.Debug(ex, "Client disconnected while the SSE keep-alive pump was writing");
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException)
        {
            // The response stream was closed underneath the pump â€” normally because this same
            // request wrote a terminal error frame and closed it. Not a client disconnect, and not
            // a failure: there is simply nothing left to pump into, so stop quietly rather than
            // faulting a task whose caller is awaiting it.
            Log.Debug(ex, "SSE keep-alive pump stopped because the response stream was closed");
        }

        return false;
    }

    /// <summary>
    /// Test seam for <see cref="PumpPreResponseSseKeepAliveAsync"/>, so the pump that keeps a client
    /// alive during a long compaction can be exercised without a live HTTP response.
    /// </summary>
    internal static Task PumpPreResponseSseKeepAliveForTest(
        Stream output, int intervalSeconds, CancellationToken ct) =>
        PumpPreResponseSseKeepAliveAsync(output, intervalSeconds, ct);

    /// <summary>
    /// Test seam that drives the pump on an explicit cadence, bypassing the
    /// <see cref="AppSettings.MinSseKeepAliveIntervalSeconds"/> floor. That floor exists so a
    /// misconfigured interval cannot spam a client, but it also means a test that needs to see a
    /// keep-alive write would otherwise wait five real seconds for one.
    /// </summary>
    internal static Task<bool> PumpPreResponseSseKeepAliveForTest(
        Stream output, TimeSpan interval, CancellationToken ct) =>
        PumpPreResponseSseKeepAliveCoreAsync(output, interval, ct);

    /// <summary>
    /// Test seam for <see cref="SseKeepAliveFrame"/>, so the frame's SSE-comment shape is asserted
    /// against what the proxy writes rather than against a copy of it living in the test.
    /// </summary>
    internal static string SseKeepAliveFrameForTest => SseKeepAliveFrame;

    /// <summary>
    /// Test seam for <see cref="PumpPreResponseSseKeepAliveReportingDisconnectAsync"/>, so the
    /// disconnect classification that decides whether in-flight compaction work is abandoned can be
    /// asserted against real stream failures rather than inferred.
    /// </summary>
    internal static Task<bool> PumpPreResponseSseKeepAliveReportingDisconnectForTest(
        Stream output, int intervalSeconds, CancellationToken ct) =>
        PumpPreResponseSseKeepAliveReportingDisconnectAsync(output, intervalSeconds, ct);

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
    /// <param name="session">
    /// Reports upstream/forwarded bytes and keep-alive frames to the live-stream dashboard. Null when
    /// no monitor is wired.
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
        Func<(int PromptTokens, int CompletionTokens)>? tokenCounts = null,
        StreamMonitorSession? session = null)
    {
        byte[] buffer = new byte[81920];
        TimeSpan keepAliveInterval = AppSettings.SseKeepAliveInterval(keepAliveIntervalSeconds);
        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        // Tracks whether the bytes forwarded so far end at an SSE frame boundary (a blank line).
        // A keep-alive comment ends with "\n\n"; injecting it mid-frame would terminate a partial
        // "data:" line and make the client dispatch incomplete JSON (M4). Start at a boundary so a
        // keep-alive before the first upstream byte is still safe.
        int trailingNewlines = 2;

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

                // Only inject at a frame boundary; mid-frame, wait for the upstream to complete it.
                if (trailingNewlines < 2)
                    continue;

                await destination.WriteAsync(SseKeepAliveFrameBytes, ct);
                await destination.FlushAsync(ct);
                onKeepAliveSent?.Invoke();
                session?.OnKeepAlive();
            }

            int bytesRead = await readTask;
            if (bytesRead == 0)
                break;

            // Decoded once and shared: this path forwards raw bytes rather than lines, so the usage
            // sniffer, the terminator, and the stream monitor all need the text form of the same chunk.
            string? chunkText = null;
            if (usageSniffer is not null || terminator is not null || session is not null)
            {
                chunkText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                usageSniffer?.Feed(chunkText);
                terminator?.Feed(chunkText);
            }

            session?.OnUpstreamChunk(chunkText ?? string.Empty, bytesRead);

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await destination.FlushAsync(ct);
            // This path is verbatim, so what the client receives is byte-for-byte the upstream chunk.
            session?.OnForwarded(chunkText ?? string.Empty);

            trailingNewlines = TrailingNewlineCount(buffer, bytesRead, trailingNewlines);
        }

        // Flush the sniffer first so the token totals it captured from the upstream's own usage frame
        // are available to a synthesized chunk, then emit whatever terminator the upstream omitted.
        usageSniffer?.Flush();
        await EmitTerminalFramesAsync(destination, terminator, tokenCounts, ct);
    }

    /// <summary>
    /// Counts the consecutive line-feed bytes at the end of the forwarded stream (carrying
    /// <paramref name="prior"/> forward when the whole chunk is newlines), treating CR as part of
    /// CRLF. Two or more means the stream ends at an SSE blank-line frame boundary.
    /// </summary>
    private static int TrailingNewlineCount(byte[] buffer, int count, int prior)
    {
        int i = count - 1;
        int run = 0;
        while (i >= 0)
        {
            byte b = buffer[i];
            if (b == (byte)'\n')
            {
                run++;
                i--;
            }
            else if (b == (byte)'\r')
            {
                i--;
            }
            else
            {
                break;
            }
        }

        return i < 0 ? prior + run : run;
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
    /// <param name="session">
    /// Reports upstream lines, forwarded frames, and keep-alive frames to the live-stream dashboard.
    /// Null when no monitor is wired.
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
        bool useIrTranslation = false,
        StreamMonitorSession? session = null)
    {
        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        TimeSpan keepAliveInterval = AppSettings.SseKeepAliveInterval(keepAliveIntervalSeconds);

        // Phase B: the IR-routed frame translator (Plans/20260914-proxy-meai-phase-b-design.md)
        // behind a default-off flag; golden byte-parity tests pin it to the legacy rewriter. Both
        // expose a Flush so buffered tails are drained before the terminal frames (M5).
        Func<string, IEnumerable<string>> rewriter;
        Func<IEnumerable<string>> rewriterFlush;
        if (useIrTranslation)
        {
            OpenAiSseFrameTranslation ir = new(thinkingMode, declaredToolNames);
            rewriter = ir.Process;
            rewriterFlush = ir.Flush;
        }
        else
        {
            OpenAiSseRewriter legacy = new(thinkingMode, declaredToolNames);
            rewriter = legacy.Process;
            rewriterFlush = legacy.Flush;
        }

        SseUsageSniffer? usageSniffer = onUsage is null ? null : new(onUsage);

        // Lines of the SSE event being accumulated. An event ends at a blank line; the universal
        // OpenAI shape is a single "data:" line, forwarded byte-for-byte exactly as before (M6).
        List<string> eventLines = [];

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
                session?.OnKeepAlive();
            }

            string? line = await readTask;
            if (line is null)
                break;

            // The raw upstream frame, before any thinking/tool-call rewriting is applied.
            session?.OnUpstreamLine(line);

            usageSniffer?.FeedLine(line);
            // The rewriter forwards [DONE] and usage frames verbatim, so observing the inbound line is
            // equivalent to observing what the client received.
            terminator?.ObserveLine(line);
            if (rawCapture is not null)
            {
                await rawCapture.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
                await rawCapture.WriteAsync(SseLineTerminatorBytes, ct);
            }

            // A blank line terminates the current SSE event: translate and emit it now.
            if (line.Length == 0)
            {
                await EmitSseEventAsync(eventLines, rewriter, destination, session, ct);
                eventLines.Clear();
                continue;
            }

            eventLines.Add(line);
        }

        // Emit any event still buffered at EOF (upstream omitted the trailing blank line).
        await EmitSseEventAsync(eventLines, rewriter, destination, session, ct);
        eventLines.Clear();

        // Drain rewriter state (a partial think tag or an unterminated call block) before the
        // terminal frames so nothing the upstream sent is silently lost (M5).
        foreach (string outgoingLine in rewriterFlush())
        {
            await WriteSseLineAsync(destination, outgoingLine, ct);
            session?.OnForwarded(outgoingLine + "\n\n");
        }
        await destination.FlushAsync(ct);

        // Flush the sniffer first so the token totals it captured from the upstream's own usage frame
        // are available to a synthesized chunk, then emit whatever terminator the upstream omitted.
        usageSniffer?.Flush();
        await EmitTerminalFramesAsync(destination, terminator, tokenCounts, ct);
    }

    /// <summary>
    /// Writes one outbound SSE line as a complete event ("line\n\n"), reusing a static terminator
    /// instead of allocating a concatenated string per frame (L8).
    /// </summary>
    private static async Task WriteSseLineAsync(Stream destination, string line, CancellationToken ct)
    {
        await destination.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
        await destination.WriteAsync(SseEventTerminatorBytes, ct);
    }

    /// <summary>
    /// Translates and emits one accumulated SSE event. The common single-"data:"-line case is
    /// forwarded through the rewriter exactly as before so output stays byte-identical; multi-line
    /// data events and field lines (event:/id:/retry:) are joined and kept attached to their data
    /// rather than split into separate, unparseable events (M6).
    /// </summary>
    private static async Task EmitSseEventAsync(
        List<string> eventLines,
        Func<string, IEnumerable<string>> rewriter,
        Stream destination,
        StreamMonitorSession? session,
        CancellationToken ct)
    {
        if (eventLines.Count == 0)
            return;

        if (eventLines.Count == 1)
        {
            foreach (string outgoingLine in rewriter(eventLines[0]))
            {
                await WriteSseLineAsync(destination, outgoingLine, ct);
                session?.OnForwarded(outgoingLine + "\n\n");
            }
            await destination.FlushAsync(ct);
            return;
        }

        List<string> fieldLines = [];
        List<string> dataPayloads = [];
        foreach (string l in eventLines)
        {
            if (l.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = l[5..];
                if (payload.StartsWith(' '))
                    payload = payload[1..];
                dataPayloads.Add(payload);
            }
            else
            {
                fieldLines.Add(l);
            }
        }

        if (dataPayloads.Count == 0)
        {
            // Comment/field-only event: forward verbatim, terminated by a blank line.
            foreach (string fl in fieldLines)
            {
                await destination.WriteAsync(Encoding.UTF8.GetBytes(fl), ct);
                await destination.WriteAsync(SseLineTerminatorBytes, ct);
            }
            await destination.WriteAsync(SseLineTerminatorBytes, ct);
            await destination.FlushAsync(ct);
            return;
        }

        string joined = "data: " + string.Join("\n", dataPayloads);
        bool first = true;
        foreach (string outgoingLine in rewriter(joined))
        {
            if (first)
            {
                foreach (string fl in fieldLines)
                {
                    await destination.WriteAsync(Encoding.UTF8.GetBytes(fl), ct);
                    await destination.WriteAsync(SseLineTerminatorBytes, ct);
                }
                first = false;
            }
            await WriteSseLineAsync(destination, outgoingLine, ct);
            session?.OnForwarded(outgoingLine + "\n\n");
        }
        await destination.FlushAsync(ct);
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
            Drain();
        }

        /// <summary>
        /// Feeds one complete line (without its terminator) from the line-based copier, avoiding a
        /// per-line "line + \n" concatenation. The newline is appended directly to the buffer.
        /// </summary>
        public void FeedLine(string line)
        {
            _pending.Append(line);
            _pending.Append('\n');
            Drain();
        }

        // Extracts every complete line in one pass. Each line is materialised on its own rather than
        // calling ToString() on the whole buffer per line, so a large chunk costs O(n) not O(n²).
        private void Drain()
        {
            int start = 0;
            while (true)
            {
                int newline = IndexOfNewline(start);
                if (newline < 0)
                    break;

                ProcessLine(_pending.ToString(start, newline - start));
                start = newline + 1;
            }

            if (start > 0)
                _pending.Remove(0, start);
        }

        private int IndexOfNewline(int start)
        {
            for (int i = start; i < _pending.Length; i++)
            {
                if (_pending[i] == '\n')
                    return i;
            }

            return -1;
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
    ///   â€¢ mirrors <c>reasoning_content</c> into <c>content</c> when <c>content</c> is empty,
    ///   â€¢ detects inline XML tool-call blocks (<c>&lt;tool_call&gt;&lt;function=NAME&gt;&lt;parameter=K&gt;V&lt;/parameter&gt;â€¦&lt;/function&gt;&lt;/tool_call&gt;</c>)
    ///     emitted by some llama.cpp templates and converts them into proper OpenAI streaming
    ///     <c>tool_calls</c> deltas so that downstream OpenAI SDK clients (e.g. VS Copilot agent mode)
    ///     execute the tool instead of receiving raw XML text,
    ///   â€¢ forces <c>finish_reason</c> to <c>"tool_calls"</c> on the terminal chunk when tool calls were emitted,
    ///   â€¢ keeps only tool calls (structured or synthesised) whose function name appears in
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

        // Envelope metadata from the most recent upstream frame, so frames synthesised at flush
        // time carry the same id/object/created/model as the stream the client already received.
        private JsonObject? _frameTemplate;

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

            _frameTemplate = new JsonObject
            {
                ["id"] = root["id"]?.DeepClone(),
                ["object"] = root["object"]?.DeepClone(),
                ["created"] = root["created"]?.DeepClone(),
                ["model"] = root["model"]?.DeepClone(),
            };

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
                    // - non-empty remainder â†’ set it (will be post-processed by tool-call ingestion below)
                    // - empty remainder but incoming was present (thinking consumed it or partial-tag
                    //   buffering) â†’ remove the key so reasoning-only / role-only deltas are clean
                    // - no incoming content at all â†’ leave delta untouched (don't fabricate "")
                    if (content.Length > 0)
                        inlineDelta["content"] = JsonValue.Create(content);
                    else if (incoming.Length > 0)
                        inlineDelta.Remove("content");
                }

                // LeaveInline leaves the upstream's reasoning_content where it is; mirroring it
                // into content made clients that render both fields show the reasoning twice (L15).

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
                        if (callIndex > cs.MaxNativeToolCallIndex)
                            cs.MaxNativeToolCallIndex = callIndex;
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
                            // Argument fragment of an already-evaluated call â€” inherit its verdict.
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

        /// <summary>
        /// Drains buffered per-choice state at end of stream: a partial think tag held by the
        /// extractor, a partial call marker, or an unterminated call block. Returns the frames to
        /// write before the terminal events so nothing the upstream sent is silently lost (M5).
        /// </summary>
        public IEnumerable<string> Flush()
        {
            JsonObject template = _frameTemplate ?? new JsonObject();
            List<string> frames = [];

            foreach ((int index, ChoiceState cs) in _state)
            {
                string reasoningTail = string.Empty;
                string contentTail = string.Empty;
                if (_thinkExtractors.TryGetValue(index, out ThinkTagExtractor? extractor))
                    (reasoningTail, contentTail) = extractor.Flush();

                string visible = string.Empty;
                List<JsonObject> tailFrames = [];
                if (contentTail.Length > 0)
                    visible = cs.IngestToken(contentTail, template, template, index, tailFrames);

                visible += cs.FlushToolScanner();

                if (reasoningTail.Length > 0 && _thinkingMode != ThinkingMode.StripFromOutput)
                    frames.Add($"data: {BuildFlushFrame(template, index, new JsonObject { ["reasoning_content"] = reasoningTail }).ToJsonString(_jsonOptions)}");

                if (visible.Length > 0)
                    frames.Add($"data: {BuildFlushFrame(template, index, new JsonObject { ["content"] = visible }).ToJsonString(_jsonOptions)}");

                foreach (JsonObject tf in tailFrames)
                    frames.Add($"data: {tf.ToJsonString(_jsonOptions)}");
            }

            _state.Clear();
            _thinkExtractors.Clear();
            return frames;
        }

        private static JsonObject BuildFlushFrame(JsonObject root, int choiceIndex, JsonObject delta) => new()
        {
            ["id"] = root["id"]?.DeepClone(),
            ["object"] = root["object"]?.DeepClone(),
            ["created"] = root["created"]?.DeepClone(),
            ["model"] = root["model"]?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = choiceIndex,
                ["delta"] = delta,
                ["finish_reason"] = null,
            }),
        };

        private sealed class ChoiceState(IReadOnlySet<string>? declaredToolNames)
        {
            private readonly IReadOnlySet<string>? _declaredToolNames = declaredToolNames;
            private readonly StringBuilder _toolBuffer = new();
            private bool _inToolCall;

            // Trailing text that could be the prefix of an open/close marker split across frames.
            // Held back (never emitted, never buffered into the call) so the next token completes it.
            private string _pending = string.Empty;

            public int EmittedToolCallCount { get; private set; }

            /// <summary>Filter verdict per native (upstream-sent) tool-call stream index.</summary>
            public Dictionary<int, bool> NativeCallValidity { get; } = [];

            /// <summary>True when at least one upstream tool-call delta survived filtering.</summary>
            public bool HadValidToolCall { get; set; }

            /// <summary>Highest native tool-call stream index observed, so synthesised calls never collide.</summary>
            public int MaxNativeToolCallIndex { get; set; } = -1;

            /// <summary>Next stream index available for a synthesised call.</summary>
            public int NextSyntheticToolIndex { get; set; }

            /// <summary>
            /// Consumes the next content token, returns the substring that should remain
            /// visible to the client (anything outside of a <c>&lt;tool_call&gt;</c> block),
            /// and appends synthesised <c>tool_calls</c> delta frames to <paramref name="extraFrames"/>
            /// once a complete block has been seen.
            /// </summary>
            public string IngestToken(string token, JsonObject root, JsonObject choice, int choiceIndex, List<JsonObject> extraFrames)
            {
                StringBuilder visible = new();
                // Scan the concatenation of any held-back partial marker and the new token so a
                // marker split across SSE chunks is recognised (H2). Reassigning the local keeps the
                // marker searches below operating on the combined text.
                token = _pending + token;
                _pending = string.Empty;
                int cursor = 0;

                while (cursor < token.Length)
                {
                    if (_inToolCall)
                    {
                        int end = token.IndexOf("</tool_call>", cursor, StringComparison.OrdinalIgnoreCase);
                        if (end < 0)
                        {
                            // Hold back a possible partial close marker so a close split across
                            // frames is still found; the rest joins the captured call body.
                            int hold = PartialMarkerSuffixLength(token, cursor, InlineXmlToolCall.CloseMarker);
                            int appendEnd = token.Length - hold;
                            if (appendEnd > cursor)
                                _toolBuffer.Append(token, cursor, appendEnd - cursor);
                            if (hold > 0)
                                _pending = token[appendEnd..];
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
                            int hold = PartialMarkerSuffixLength(token, cursor, InlineXmlToolCall.OpenMarker);
                            int emitEnd = token.Length - hold;
                            if (emitEnd > cursor)
                                visible.Append(token, cursor, emitEnd - cursor);
                            if (hold > 0)
                                _pending = token[emitEnd..];
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

            /// <summary>
            /// Drains the scanner at end of stream. An unterminated call block is returned verbatim
            /// (the caller re-emits it as visible text rather than losing it); a held-back partial
            /// marker is returned as the literal text it turned out to be.
            /// </summary>
            public string FlushToolScanner()
            {
                string tail = _inToolCall ? _toolBuffer.ToString() + _pending : _pending;
                _toolBuffer.Clear();
                _pending = string.Empty;
                _inToolCall = false;
                return tail;
            }

            /// <summary>
            /// Number of trailing characters of <paramref name="work"/> (from <paramref name="start"/>)
            /// that form a proper prefix of <paramref name="marker"/> — the amount to hold back so a
            /// marker split across frames is recognised on the next call.
            /// </summary>
            private static int PartialMarkerSuffixLength(string work, int start, string marker)
            {
                int max = Math.Min(marker.Length - 1, work.Length - start);
                for (int len = max; len > 0; len--)
                {
                    if (work.AsSpan(work.Length - len, len).Equals(marker.AsSpan(0, len), StringComparison.OrdinalIgnoreCase))
                        return len;
                }

                return 0;
            }

            // The incoming choice object is not read: the frame is rebuilt from the parsed XML and the
            // buffered call state, so only the index and the outgoing frame list are needed.
            private void EmitToolCallFromBuffer(JsonObject root, JsonObject _, int choiceIndex, List<JsonObject> extraFrames)
            {
                string xml = _toolBuffer.ToString();
                Match m = XmlToolCallRegex().Match(xml);
                if (!m.Success)
                {
                    // Unparseable block (e.g. a JSON-body template variation): hand it back as
                    // visible text rather than deleting the model's output (H3).
                    extraFrames.Add(BuildChoiceFrame(root, choiceIndex, new JsonObject { ["content"] = xml.Trim() }));
                    return;
                }

                string name = m.Groups["name"].Value.Trim();

                // Tool-less or differently-declared clients cannot execute this call: hand the
                // raw block back as visible content instead of a tool-call delta.
                if (!IsToolNameAllowed(_declaredToolNames, name))
                {
                    extraFrames.Add(BuildChoiceFrame(root, choiceIndex, new JsonObject { ["content"] = xml.Trim() }));
                    return;
                }

                Dictionary<string, object?> args = new(StringComparer.Ordinal);
                foreach (Match pm in XmlParameterRegex().Matches(m.Groups["body"].Value))
                {
                    args[pm.Groups["n"].Value.Trim()] = ParseXmlToolParameterValue(pm.Groups["v"].Value.Trim());
                }

                string argsJson = JsonSerializer.Serialize(args, _jsonOptions);
                string callId = "call_" + Guid.NewGuid().ToString("N")[..16];
                // Allocate above every native index seen so a synthesised call never merges into a
                // native call's name/arguments on clients that key deltas by index (M7).
                int toolIndex = Math.Max(NextSyntheticToolIndex, MaxNativeToolCallIndex + 1);
                NextSyntheticToolIndex = toolIndex + 1;
                EmittedToolCallCount++;

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

            // Context-summarize (/compact) redirect.
            string? firstContent = GetFirstMessageContent(root);
            var (effectiveModel, compactRedirected, resolved) =
                ResolveCompactRedirect(settings, original, firstContent, log, "request");
            log.Model = effectiveModel;
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

            // Nothing to rewrite â€” return original text unchanged.
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
        catch (JsonException ex)
        {
            // Non-JSON or malformed body — forward as-is.
            Log.Debug(ex, "Request body is not valid JSON; forwarding un-normalized");
            return json;
        }
        catch (Exception ex)
        {
            // An internal normalization failure still forwards the original body, but must leave a
            // trail: silently forwarding an un-normalized request (model unrewritten, stream_options
            // intact, prefill present) produces confusing upstream errors with no diagnosis (M17).
            Log.Error(ex, "Request body normalization failed; forwarding un-normalized");
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

        // Only a genuine empty/whitespace prefill is stripped; a trailing assistant turn carrying
        // real content is legitimate history and must be preserved (M1). Mirrors
        // RequestPayloadReads.IsAssistantResponsePrefill so the legacy and IR paths agree.
        if (!message.TryGetProperty("content", out JsonElement content))
            return true;

        return content.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(content.GetString()),
            JsonValueKind.Array => !HasNonEmptyTextPart(content),
            _ => false,
        };
    }

    private static bool HasNonEmptyTextPart(JsonElement contentArray)
    {
        foreach (JsonElement part in contentArray.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return true;
            }
        }

        return false;
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
    /// Redacts a request body from a caller with no resolved model mapping â€” an unknown endpoint, an
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
    /// re-serializing the document. Everything that is not a sensitive value â€” whitespace,
    /// key order, string escaping â€” is preserved byte-for-byte, so a clean body is returned
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

        // Scalar: number, true, false, null â€” read until whitespace, comma, close, or end.
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
        // left intact â€” when body capture is enabled the content is exactly what the
        // user opted to inspect, and redacting it would make the logs useless.
        return propertyName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apikey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("password", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssistantResponsePrefill(LlamaCppMessage message)
    {
        if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return false;
        if (message.ToolCalls is { Count: > 0 })
            return false;
        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            return false;

        // Only a genuine empty/whitespace prefill is stripped; real content is legitimate history (M1).
        return message.Content switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            JsonElement { ValueKind: JsonValueKind.Null } => true,
            JsonElement { ValueKind: JsonValueKind.String } je => string.IsNullOrWhiteSpace(je.GetString()),
            JsonElement { ValueKind: JsonValueKind.Array } arr => !HasNonEmptyTextPart(arr),
            _ => false,
        };
    }

}
