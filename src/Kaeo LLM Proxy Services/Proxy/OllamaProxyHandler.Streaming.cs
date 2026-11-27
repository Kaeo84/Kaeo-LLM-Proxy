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
    /// <summary>Elapsed stopwatch time in nanoseconds â€” Ollama's duration unit.</summary>
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
        CancellationToken ct,
        StreamMonitorSession? session = null)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        // No BOM: Encoding.UTF8 writes a preamble on first flush, which strict NDJSON parsers
        // (e.g. Go's json.Unmarshal used by Ollama clients) reject on the very first line.
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);

        // Accumulate the Ollama-formatted NDJSON actually sent to the client so the log captures
        // the "after" of the OpenAIâ†’Ollama response translation (not just the content text).
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
                    session?.OnForwarded(errorJson + "\n");
                }
                catch
                {
                    // Client already disconnected; the error chunk could not be delivered.
                }
                break;
            }
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            session?.OnUpstreamLine(line);

            // SSE format: "data: {...}" or "data:{...}" (the space is optional per the SSE spec).
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                line = line[5..];
                if (line.StartsWith(' '))
                    line = line[1..];
            }

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
                session?.OnForwarded(doneJson + "\n");
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
                        session?.OnForwarded(chunkJson + "\n");
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
        bool collectRawUpstream = false,
                StreamMonitorSession? session = null)
            {
                await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
                using StreamReader reader = new(stream, Encoding.UTF8);
                // No BOM: a preamble before the first NDJSON line breaks strict parsers.
                await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);

                // Accumulate the Ollama-formatted NDJSON actually sent to the client so the log captures
                // the "after" of the OpenAIâ†’Ollama response translation (not just the content text).
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
                    session?.OnForwarded(errorJson + "\n");
                }
                catch
                {
                    // Client already disconnected; the error chunk could not be delivered.
                }
                break;
            }
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            // The space after "data:" is optional per the SSE spec.
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                line = line[5..];
                if (line.StartsWith(' '))
                    line = line[1..];
            }

            // Capture the raw OpenAI upstream chunk (the "before" of the response translation).
            rawUpstreamAccumulator?.Append(line);
            rawUpstreamAccumulator?.Append('\n');
            session?.OnUpstreamLine(line);

            if (line == "[DONE]")
            {
                reachedDone = true;
                if (terminalChunkSent)
                    break;

                long elapsedNs = ElapsedNanos(sw);
                (string doneToken, string? doneThinking) = DrainInlineExtractor(inlineExtractor);
                var doneChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", doneToken) { Thinking = doneThinking },
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
                terminalChunkSent = true;
                await writer.WriteLineAsync(doneJson);
                await writer.FlushAsync(ct);
                session?.OnForwarded(doneJson + "\n");
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
            // Use IsNullOrWhiteSpace to reject chunks where the upstream sends
            // whitespace-only reasoning_content (e.g. bare newlines), which would
            // otherwise produce blank lines in the client's thinking panel.
            string? nativeThinking = emitThinking && !string.IsNullOrWhiteSpace(delta?.ReasoningContent)
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

            // The finish_reason frame is the last one the client reads, so the extractor's held-back
            // tail has to ride on it: a partial tag buffered at the end of the stream would otherwise
            // be lost along with the last characters of the answer.
            bool done = choice?.FinishReason != null;
            if (done && inlineExtractor is not null)
            {
                (string tailToken, string? tailThinking) = DrainInlineExtractor(inlineExtractor);
                if (tailThinking is not null)
                    thinking = thinking is null ? tailThinking : thinking + tailThinking;
                if (tailToken.Length > 0)
                    token = tailToken + token;
            }

            AppendStreamingToolCalls(toolCallBuilders, delta?.ToolCalls);
            token = CaptureXmlToolCallToken(token, xmlToolCallBuilder, ref capturingXmlToolCall);
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
            session?.OnForwarded(chunkJson + "\n");
        }

        // Drain any text the extractor held back because the stream ended without a terminal chunk
        // at all (a truncated upstream that never sent finish_reason or [DONE]). The terminal paths
        // above already drained it into their own frame.
        if (inlineExtractor is not null && !terminalChunkSent)
        {
            (string tailToken, string? tailThinking) = DrainInlineExtractor(inlineExtractor);
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
                session?.OnForwarded(tailJson + "\n");
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
    /// Drains the inline think-tag extractor at the end of a stream, returning the buffered tail as
    /// an Ollama answer token plus an optional thinking fragment (null when empty, so the serializer
    /// omits the field). A partial tag held back at the stream's end is the only thing that can be
    /// buffered, so this is normally a couple of characters — but dropping it silently truncates the
    /// answer, and the terminal frame is the last chance to deliver it.
    /// </summary>
    private static (string Token, string? Thinking) DrainInlineExtractor(ThinkTagExtractor? extractor)
    {
        if (extractor is null)
            return (string.Empty, null);

        (string tailReasoning, string tailAnswer) = extractor.Flush();
        return (tailAnswer, tailReasoning.Length > 0 ? tailReasoning : null);
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

    // â”€â”€ Mapping helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

}
