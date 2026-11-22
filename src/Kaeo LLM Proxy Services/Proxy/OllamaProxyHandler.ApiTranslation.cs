using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services.Translation;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Cross-format API translation. OpenAI is the hub wire format: an inbound request is detected
/// from its path (OpenAI <c>/v1/chat/completions</c>, Anthropic <c>/v1/messages</c>, Ollama
/// <c>/api/chat</c>) and the upstream format from the resolved mapping's <see cref="UpstreamType"/>.
/// When the two differ and the mapping has <see cref="ModelMapping.EnableApiTranslation"/> set, the
/// request body is converted to the upstream's format and the reply is converted back to the format
/// the client asked for. The pure conversions live in <see cref="OllamaFormatTranslator"/> and
/// <see cref="AnthropicFormatTranslator"/>; this partial owns the HTTP send, the streaming pumps and
/// the SSE/NDJSON framing.
/// </summary>
partial class OllamaProxyHandler
{
    /// <summary>
    /// Applies the upstream credential in the shape the target API expects. Anthropic authenticates
    /// with <c>x-api-key</c> plus a required <c>anthropic-version</c> header; every other upstream
    /// uses the bearer scheme <see cref="ApplyApiKey"/> already implements.
    /// </summary>
    private static void ApplyTranslatedUpstreamAuth(HttpRequestMessage upstreamReq, ModelMapping mapping, string? apiKey)
    {
        if (mapping.UpstreamType == UpstreamType.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                upstreamReq.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            upstreamReq.Headers.TryAddWithoutValidation(
                "anthropic-version",
                string.IsNullOrWhiteSpace(mapping.AnthropicApiVersion) ? "2023-06-01" : mapping.AnthropicApiVersion);
            return;
        }

        ApplyApiKey(upstreamReq, apiKey);
    }

    private static string NewCompletionId() => "chatcmpl-" + Guid.NewGuid().ToString("N");
    private static string NewMessageId() => "msg_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// OpenAI-inbound chat completion whose resolved upstream speaks Ollama or Anthropic. The body
    /// has already been rewritten by the OpenAI payload pipeline (model resolution, sampling,
    /// instruction injection, compaction), so this only converts formats and forwards.
    /// </summary>
    private async Task HandleOpenAiChatTranslatedAsync(
        HttpListenerResponse resp,
        RequestLog log,
        string openAiBody,
        ModelMapping mapping,
        string baseUrl,
        int timeout,
        string? apiKey,
        string clientModel,
        bool isStreaming,
        CancellationToken ct)
    {
        string completionId = NewCompletionId();
        string upstreamModel = string.IsNullOrWhiteSpace(mapping.ModelName) ? clientModel : mapping.ModelName;

        string targetPath;
        string targetBody;
        if (mapping.UpstreamType == UpstreamType.Ollama)
        {
            targetPath = "/api/chat";
            targetBody = OllamaFormatTranslator.OpenAiRequestToOllama(openAiBody, upstreamModel, isStreaming);
        }
        else
        {
            targetPath = "/v1/messages";
            targetBody = AnthropicFormatTranslator.OpenAiRequestToAnthropic(
                openAiBody, upstreamModel, isStreaming, mapping.AnthropicMaxTokens);
        }

        log.Streaming = isStreaming;
        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, targetBody, clientModel);

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, new Uri(targetPath, UriKind.Relative))
        {
            Content = new StringContent(targetBody, Encoding.UTF8, "application/json"),
        };
        ApplyTranslatedUpstreamAuth(upstreamReq, mapping, apiKey);

        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            await WriteOpenAiUpstreamErrorAsync(resp, log, (int)upstreamResp.StatusCode, errorBody, ct);
            return;
        }

        if (!isStreaming)
        {
            string body = await upstreamResp.Content.ReadAsStringAsync(ct);
            string translated = mapping.UpstreamType == UpstreamType.Ollama
                ? OllamaFormatTranslator.OllamaResponseToOpenAi(body, clientModel, completionId)
                : AnthropicFormatTranslator.AnthropicResponseToOpenAi(body, clientModel, completionId);
            CaptureTranslatedResponse(log, translated);
            resp.StatusCode = 200;
            await WriteJsonRawAsync(resp, translated, ct);
            log.Status = RequestStatus.Success;
            return;
        }

        CommitOpenAiSseHeaders(resp);
        if (mapping.UpstreamType == UpstreamType.Ollama)
            await PumpOllamaNdjsonToOpenAiSseAsync(upstreamResp, resp, log, clientModel, completionId, ct);
        else
            await PumpAnthropicSseToOpenAiSseAsync(upstreamResp, resp, log, clientModel, completionId, ct);
    }

    /// <summary>
    /// Anthropic-inbound <c>/v1/messages</c> request. Routes to an Anthropic upstream as a raw
    /// passthrough, or translates to OpenAI / Ollama upstreams and converts the reply back to the
    /// Anthropic Messages shape the client expects.
    /// </summary>
    private async Task HandleAnthropicMessagesAsync(
        HttpListenerRequest req,
        HttpListenerResponse resp,
        RequestLog log,
        CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            await TryWriteErrorResponseAsync(resp, log, 400, AnthropicErrorPayload(
                "invalid_request_error", "Request body is not valid JSON: " + ex.Message), ct);
            return;
        }

        string clientModel = root?["model"]?.GetValue<string>() ?? string.Empty;
        bool isStreaming = root?["stream"]?.GetValue<bool>() ?? false;
        log.Model = clientModel;
        log.Streaming = isStreaming;
        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.RequestBody = RedactRequestBodyForLog(_settings, body, clientModel);

        ModelMapping mapping = ResolveMapping(clientModel);
        var (baseUrl, timeout, apiKey) = ResolveUpstream(mapping);
        string upstreamModel = string.IsNullOrWhiteSpace(mapping.ModelName) ? clientModel : mapping.ModelName;
        string messageId = NewMessageId();

        // Anthropic upstream: no conversion needed, forward the body verbatim.
        if (mapping.UpstreamType == UpstreamType.Anthropic)
        {
            await ForwardAnthropicPassthroughAsync(resp, log, body, mapping, baseUrl, timeout, apiKey, isStreaming, ct);
            return;
        }

        if (!mapping.EnableApiTranslation)
        {
            await TryWriteErrorResponseAsync(resp, log, 400, AnthropicErrorPayload(
                "invalid_request_error",
                $"Model '{clientModel}' targets a {mapping.UpstreamType.ToDisplayName()} upstream but API translation is disabled. " +
                "Enable API translation on the mapping or point it at an Anthropic upstream."), ct);
            return;
        }

        // Anthropic -> OpenAI hub body, reused for both OpenAI and Ollama upstreams.
        string openAiBody = AnthropicFormatTranslator.AnthropicRequestToOpenAi(body, upstreamModel, isStreaming);
        string completionId = NewCompletionId();

        string targetPath;
        string targetBody;
        if (mapping.UpstreamType == UpstreamType.Ollama)
        {
            targetPath = "/api/chat";
            targetBody = OllamaFormatTranslator.OpenAiRequestToOllama(openAiBody, upstreamModel, isStreaming);
        }
        else
        {
            targetPath = "/v1/chat/completions";
            targetBody = openAiBody;
        }

        if (_settings.CollectRequestDetails || _settings.DebugMode)
            log.UpstreamRequestBody = RedactRequestBodyForLog(_settings, targetBody, clientModel);

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, new Uri(targetPath, UriKind.Relative))
        {
            Content = new StringContent(targetBody, Encoding.UTF8, "application/json"),
        };
        ApplyTranslatedUpstreamAuth(upstreamReq, mapping, apiKey);

        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            await WriteAnthropicUpstreamErrorAsync(resp, log, (int)upstreamResp.StatusCode, errorBody, ct);
            return;
        }

        if (!isStreaming)
        {
            string upstreamBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            string openAiResponse = mapping.UpstreamType == UpstreamType.Ollama
                ? OllamaFormatTranslator.OllamaResponseToOpenAi(upstreamBody, clientModel, completionId)
                : upstreamBody;
            string anthropicResponse = AnthropicFormatTranslator.OpenAiResponseToAnthropic(openAiResponse, clientModel, messageId);
            CaptureTranslatedResponse(log, anthropicResponse);
            resp.StatusCode = 200;
            await WriteJsonRawAsync(resp, anthropicResponse, ct);
            log.Status = RequestStatus.Success;
            return;
        }

        CommitAnthropicSseHeaders(resp);
        if (mapping.UpstreamType == UpstreamType.Ollama)
            await PumpOllamaNdjsonToAnthropicSseAsync(upstreamResp, resp, log, clientModel, messageId, completionId, ct);
        else
            await PumpOpenAiSseToAnthropicSseAsync(upstreamResp, resp, log, clientModel, messageId, ct);
    }

    /// <summary>Raw Anthropic-to-Anthropic forward: no body conversion, only credential shaping.</summary>
    private async Task ForwardAnthropicPassthroughAsync(
        HttpListenerResponse resp,
        RequestLog log,
        string body,
        ModelMapping mapping,
        string baseUrl,
        int timeout,
        string? apiKey,
        bool isStreaming,
        CancellationToken ct)
    {
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/messages", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyTranslatedUpstreamAuth(upstreamReq, mapping, apiKey);

        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;
        resp.StatusCode = (int)upstreamResp.StatusCode;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            await WriteAnthropicUpstreamErrorAsync(resp, log, (int)upstreamResp.StatusCode, errorBody, ct);
            return;
        }

        if (!isStreaming)
        {
            string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            CaptureTranslatedResponse(log, respBody);
            await WriteJsonRawAsync(resp, respBody, ct);
            log.Status = RequestStatus.Success;
            return;
        }

        CommitAnthropicSseHeaders(resp);
        await PumpRawSseAsync(upstreamResp, resp, log, ct);
    }

    // ---- Streaming pumps ---------------------------------------------------------------------

    private static void CommitOpenAiSseHeaders(HttpListenerResponse resp)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.SendChunked = true;
        resp.KeepAlive = true;
        resp.Headers["Cache-Control"] = "no-cache";
    }

    private static void CommitAnthropicSseHeaders(HttpListenerResponse resp)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.SendChunked = true;
        resp.KeepAlive = true;
        resp.Headers["Cache-Control"] = "no-cache";
    }

    private void CaptureTranslatedResponse(RequestLog log, string body)
    {
        log.ResponseBytes = Encoding.UTF8.GetByteCount(body);
        if (_settings.CollectResponseDetails || _settings.DebugMode)
            log.ResponseBody = RedactResponseBodyForLog(body, log.Model);
    }

    /// <summary>Ollama NDJSON upstream -> OpenAI SSE client.</summary>
    private async Task PumpOllamaNdjsonToOpenAiSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        RequestLog log,
        string clientModel,
        string completionId,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        using PooledCharBuffer? accumulator = (_settings.CollectResponseDetails || _settings.DebugMode) ? new PooledCharBuffer() : null;

        var state = OllamaFormatTranslator.CreateOllamaToOpenAiState(clientModel, completionId);
        long responseBytes = 0;
        bool doneSent = false;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                log.StopReason = "error";
                Log.Warning(ex, "Ollama upstream stream failed mid-response for model {Model}", clientModel);
                await TryWriteOpenAiErrorChunkAsync(writer, clientModel, completionId, DescribeUpstreamStreamError(ex));
                break;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            IReadOnlyList<string> chunks;
            try
            {
                chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, line);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to translate Ollama stream line for model {Model}", clientModel);
                continue;
            }

            foreach (string chunk in chunks)
            {
                string frame = "data: " + chunk + "\n\n";
                responseBytes += Encoding.UTF8.GetByteCount(frame);
                accumulator?.Append(frame);
                await writer.WriteAsync(frame);
            }
            if (chunks.Count > 0)
                await writer.FlushAsync(CancellationToken.None);

            if (state.Finished)
            {
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(CancellationToken.None);
                doneSent = true;
                break;
            }
        }

        if (!doneSent && !ct.IsCancellationRequested)
        {
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(CancellationToken.None);
        }

        FinishStreamLog(log, accumulator, responseBytes);
        resp.Close();
    }

    /// <summary>Anthropic SSE upstream -> OpenAI SSE client.</summary>
    private async Task PumpAnthropicSseToOpenAiSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        RequestLog log,
        string clientModel,
        string completionId,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        using PooledCharBuffer? accumulator = (_settings.CollectResponseDetails || _settings.DebugMode) ? new PooledCharBuffer() : null;

        var state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState(clientModel, completionId);
        long responseBytes = 0;
        bool doneSent = false;
        string? eventType = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                log.StopReason = "error";
                Log.Warning(ex, "Anthropic upstream stream failed mid-response for model {Model}", clientModel);
                await TryWriteOpenAiErrorChunkAsync(writer, clientModel, completionId, DescribeUpstreamStreamError(ex));
                break;
            }
            if (line is null) break;

            if (line.Length == 0)
            {
                // Blank line dispatches the accumulated event.
                if (dataBuilder.Length > 0)
                {
                    string dataJson = dataBuilder.ToString();
                    dataBuilder.Clear();
                    string type = eventType ?? string.Empty;
                    eventType = null;

                    IReadOnlyList<string> chunks;
                    try
                    {
                        chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, type, dataJson);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to translate Anthropic event {Event} for model {Model}", type, clientModel);
                        continue;
                    }

                    foreach (string chunk in chunks)
                    {
                        string frame = "data: " + chunk + "\n\n";
                        responseBytes += Encoding.UTF8.GetByteCount(frame);
                        accumulator?.Append(frame);
                        await writer.WriteAsync(frame);
                    }
                    if (chunks.Count > 0)
                        await writer.FlushAsync(CancellationToken.None);

                    if (state.Finished)
                    {
                        await writer.WriteAsync("data: [DONE]\n\n");
                        await writer.FlushAsync(CancellationToken.None);
                        doneSent = true;
                        break;
                    }
                }
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string data = line[5..];
                if (data.StartsWith(' ')) data = data[1..];
                if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                dataBuilder.Append(data);
            }
        }

        if (!doneSent && !ct.IsCancellationRequested)
        {
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(CancellationToken.None);
        }

        FinishStreamLog(log, accumulator, responseBytes);
        resp.Close();
    }

    /// <summary>OpenAI SSE upstream -> Anthropic SSE client.</summary>
    private async Task PumpOpenAiSseToAnthropicSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        RequestLog log,
        string clientModel,
        string messageId,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        using PooledCharBuffer? accumulator = (_settings.CollectResponseDetails || _settings.DebugMode) ? new PooledCharBuffer() : null;

        var state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState(clientModel, messageId);
        long responseBytes = 0;
        bool stopSent = false;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                log.StopReason = "error";
                Log.Warning(ex, "OpenAI upstream stream failed mid-response for model {Model}", clientModel);
                await TryWriteAnthropicErrorEventAsync(writer, DescribeUpstreamStreamError(ex));
                break;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line[5..];
            if (data.StartsWith(' ')) data = data[1..];
            if (data == "[DONE]") break;

            IReadOnlyList<string> events;
            try
            {
                events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, data);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to translate OpenAI chunk to Anthropic for model {Model}", clientModel);
                continue;
            }

            foreach (string ev in events)
            {
                responseBytes += Encoding.UTF8.GetByteCount(ev);
                accumulator?.Append(ev);
                await writer.WriteAsync(ev);
                if (ev.Contains("event: message_stop", StringComparison.Ordinal))
                    stopSent = true;
            }
            if (events.Count > 0)
                await writer.FlushAsync(CancellationToken.None);

            if (stopSent) break;
        }

        if (!stopSent && !ct.IsCancellationRequested)
        {
            string stop = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            responseBytes += Encoding.UTF8.GetByteCount(stop);
            accumulator?.Append(stop);
            await writer.WriteAsync(stop);
            await writer.FlushAsync(CancellationToken.None);
        }

        FinishStreamLog(log, accumulator, responseBytes);
        resp.Close();
    }

    /// <summary>Ollama NDJSON upstream -> Anthropic SSE client, composed through the OpenAI hub.</summary>
    private async Task PumpOllamaNdjsonToAnthropicSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        RequestLog log,
        string clientModel,
        string messageId,
        string completionId,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        using PooledCharBuffer? accumulator = (_settings.CollectResponseDetails || _settings.DebugMode) ? new PooledCharBuffer() : null;

        var ollamaState = OllamaFormatTranslator.CreateOllamaToOpenAiState(clientModel, completionId);
        var anthropicState = AnthropicFormatTranslator.CreateOpenAiToAnthropicState(clientModel, messageId);
        long responseBytes = 0;
        bool stopSent = false;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                log.StopReason = "error";
                Log.Warning(ex, "Ollama upstream stream failed mid-response for model {Model}", clientModel);
                await TryWriteAnthropicErrorEventAsync(writer, DescribeUpstreamStreamError(ex));
                break;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            IReadOnlyList<string> openAiChunks;
            try
            {
                openAiChunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(ollamaState, line);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to translate Ollama stream line for model {Model}", clientModel);
                continue;
            }

            foreach (string chunk in openAiChunks)
            {
                IReadOnlyList<string> events;
                try
                {
                    events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(anthropicState, chunk);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to translate OpenAI chunk to Anthropic for model {Model}", clientModel);
                    continue;
                }

                foreach (string ev in events)
                {
                    responseBytes += Encoding.UTF8.GetByteCount(ev);
                    accumulator?.Append(ev);
                    await writer.WriteAsync(ev);
                    if (ev.Contains("event: message_stop", StringComparison.Ordinal))
                        stopSent = true;
                }
                if (events.Count > 0)
                    await writer.FlushAsync(CancellationToken.None);
            }

            if (stopSent) break;
        }

        if (!stopSent && !ct.IsCancellationRequested)
        {
            string stop = "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            responseBytes += Encoding.UTF8.GetByteCount(stop);
            accumulator?.Append(stop);
            await writer.WriteAsync(stop);
            await writer.FlushAsync(CancellationToken.None);
        }

        FinishStreamLog(log, accumulator, responseBytes);
        resp.Close();
    }

    /// <summary>Forwards an upstream SSE body verbatim (Anthropic-to-Anthropic passthrough).</summary>
    private async Task PumpRawSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        RequestLog log,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        using PooledCharBuffer? accumulator = (_settings.CollectResponseDetails || _settings.DebugMode) ? new PooledCharBuffer() : null;
        long responseBytes = 0;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.ErrorMessage = DescribeUpstreamStreamError(ex);
                log.StopReason = "error";
                Log.Warning(ex, "Anthropic passthrough stream failed mid-response for model {Model}", log.Model);
                break;
            }
            if (line is null) break;

            string frame = line + "\n";
            responseBytes += Encoding.UTF8.GetByteCount(frame);
            accumulator?.Append(frame);
            await writer.WriteAsync(frame);
            if (line.Length == 0)
                await writer.FlushAsync(CancellationToken.None);
        }

        await writer.FlushAsync(CancellationToken.None);
        FinishStreamLog(log, accumulator, responseBytes);
        resp.Close();
    }

    private void FinishStreamLog(RequestLog log, PooledCharBuffer? accumulator, long responseBytes)
    {
        log.ResponseBytes = responseBytes;
        if (accumulator is not null)
            log.ResponseBody = RedactResponseBodyForLog(accumulator.ToString(), log.Model);
        log.Status = string.IsNullOrEmpty(log.ErrorMessage) ? RequestStatus.Success : RequestStatus.Error;
    }

    private static async Task TryWriteOpenAiErrorChunkAsync(StreamWriter writer, string model, string completionId, string message)
    {
        try
        {
            var error = new JsonObject
            {
                ["id"] = completionId,
                ["object"] = "chat.completion.chunk",
                ["model"] = model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject { ["content"] = message },
                        ["finish_reason"] = "error",
                    },
                },
            };
            await writer.WriteAsync("data: " + error.ToJsonString() + "\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Client already disconnected.
        }
    }

    private static async Task TryWriteAnthropicErrorEventAsync(StreamWriter writer, string message)
    {
        try
        {
            var error = new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
            };
            await writer.WriteAsync("event: error\ndata: " + error.ToJsonString() + "\n\n");
            await writer.FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Client already disconnected.
        }
    }

    private async Task WriteOpenAiUpstreamErrorAsync(
        HttpListenerResponse resp, RequestLog log, int statusCode, string upstreamErrorBody, CancellationToken ct)
    {
        log.Status = RequestStatus.Error;
        log.ErrorMessage = upstreamErrorBody;
        string message = TryExtractErrorMessage(upstreamErrorBody) ?? upstreamErrorBody;
        var error = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "upstream_error",
                ["code"] = statusCode,
            },
        };
        resp.StatusCode = statusCode;
        await WriteJsonRawAsync(resp, error.ToJsonString(), ct);
    }

    private async Task WriteAnthropicUpstreamErrorAsync(
        HttpListenerResponse resp, RequestLog log, int statusCode, string upstreamErrorBody, CancellationToken ct)
    {
        log.Status = RequestStatus.Error;
        log.ErrorMessage = upstreamErrorBody;
        string message = TryExtractErrorMessage(upstreamErrorBody) ?? upstreamErrorBody;
        var error = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
        };
        resp.StatusCode = statusCode;
        await WriteJsonRawAsync(resp, error.ToJsonString(), ct);
    }

    private static JsonObject AnthropicErrorPayload(string type, string message) => new()
    {
        ["type"] = "error",
        ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
    };

    private static string? TryExtractErrorMessage(string body)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(body);
            return node?["error"]?["message"]?.GetValue<string>()
                ?? node?["message"]?.GetValue<string>()
                ?? node?["error"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}