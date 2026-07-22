using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Serilog;

namespace Kaeo.LlmProxy.Core.Services;

/// <summary>
/// Handles translation between Ollama API requests and llama.cpp OpenAI-compatible API requests.
/// Supports streaming, non-streaming, tool calls, JSON format mode, and batch embeddings.
/// </summary>
internal sealed class OllamaProxyHandler(AppSettings settings, StatisticsService stats) : IDisposable
{
    private const string RedactedBodyText = "[REDACTED BY MODEL LOG REDACTION SETTINGS]";
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
    private readonly ConcurrentDictionary<string, PeriodicHeartbeatState> _periodicHeartbeats = new(StringComparer.OrdinalIgnoreCase);

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
        ApplyApiKey(request, mapping.ApiKey);
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
            return (mapping.UpstreamUrl.TrimEnd('/'), timeout, mapping.ApiKey);
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
            return (fallback.UpstreamUrl.TrimEnd('/'), timeout, fallback.ApiKey);
        }

        throw new InvalidOperationException(
            $"No mapping found for model '{ollamaModel}'. " +
            "Add a mapping in settings with ProxyName, ModelName, and UpstreamUrl.");
    }

    private bool ShouldApplyThinkingCompatibility(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableThinkingCompatibility ?? true;
    }

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

        if ((int)response.StatusCode != 500)
            return (false, string.Empty);

        try
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return (false, body);

            // Try to parse as structured error
            LlamaCppErrorResponse? errorResp = JsonSerializer.Deserialize<LlamaCppErrorResponse>(body, _jsonOptions);
            string? errorMessage = errorResp?.Error?.Message;

            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = body;

            // Check for common context overflow patterns
            bool isOverflow = errorMessage.Contains("context", StringComparison.OrdinalIgnoreCase)
                && (errorMessage.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too large", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too long", StringComparison.OrdinalIgnoreCase));

            return (isOverflow, body);
        }
        catch
        {
            return (false, string.Empty);
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
            MaxConnectionsPerServer = 64,
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
                return;
            }
        }

        // Static version probe answered without logging — infrastructure noise that would inflate
        // the request log on every client connection.
        if (method == "GET" && path == "/api/version")
        {
            await WriteJsonAsync(resp, new { version = "0.1.0" }, ct);
            return;
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
            // mappings without their own configured ApiKey. ApplyApiKey below overrides
            // this with the mapping's key when one is configured.

            string value = req.Headers[name] ?? string.Empty;
            if (!upstreamReq.Headers.TryAddWithoutValidation(name, value))
                upstreamReq.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        // Track which original model was requested so we can resolve the upstream URL.
        string originalModel = string.Empty;
        bool isStreamingRequest = false;

        if (req.HasEntityBody)
        {
            bool isJsonPost = req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                           && (req.ContentType?.StartsWith("application/json",
                               StringComparison.OrdinalIgnoreCase) ?? false);

            if (isJsonPost)
            {
                string bodyText = await ReadBodyAsync(req, ct);
                log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);
                string rewritten = NormalizeRequestBody(bodyText, log, ShouldApplyThinkingCompatibility);
                originalModel = log.Model; // set by NormalizeRequestBody
                if (_settings.CollectRequestDetails)
                    log.RequestBody = RedactRequestBodyForLog(bodyText, originalModel);
                isStreamingRequest = IsChatCompletionsPath(req.Url?.AbsolutePath) && IsStreamingJsonBody(bodyText);
                byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
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

        // For streaming chat requests, pre-commit SSE headers to the client immediately and
        // pump heartbeat comments while waiting for the upstream to send its first response
        // header. llama.cpp does not send any HTTP headers until the first token is ready,
        // so clients with a short NetworkTimeout (e.g. the OpenAI .NET SDK default of 100 s)
        // would otherwise time out silently during long prompt-processing / thinking phases.
        bool headersPreCommitted = false;
        using var preResponseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task preResponseHeartbeatTask = Task.CompletedTask;

        if (isStreamingRequest && ShouldEmitHeartbeats(originalModel))
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
            // Read error body so it can be logged before forwarding.
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";

            if (headersPreCommitted)
            {
                // Headers already sent as 200/SSE — emit the error as a data frame so the
                // client sees it rather than getting a silent stream close.
                string errorFrame = $"data: {{\"error\":{{\"message\":{JsonSerializer.Serialize(errorBody)},\"code\":{(int)upstreamResp.StatusCode}}}}}\n\n";
                byte[] errorFrameBytes = Encoding.UTF8.GetBytes(errorFrame);
                await resp.OutputStream.WriteAsync(errorFrameBytes, ct);
            }
            else
            {
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

        if (_settings.CollectResponseDetails)
        {
            using ResponseCaptureStream captureStream = new(countingStream);
            if (isServerSentEvents)
            {
                if (shouldMirrorReasoningContent)
                {
                    await CopyOpenAiChatCompletionSseStreamAsync(
                        upstreamStream,
                        captureStream,
                        ShouldEmitHeartbeats(originalModel),
                        _settings.StreamingHeartbeatIntervalSeconds,
                        ct,
                        () => _stats.IncrementHeartbeat(originalModel));
                }
                else
                {
                    await CopyStreamWithSseHeartbeatsAsync(
                        upstreamStream,
                        captureStream,
                        ShouldEmitHeartbeats(originalModel),
                        _settings.StreamingHeartbeatIntervalSeconds,
                        ct,
                        () => _stats.IncrementHeartbeat(originalModel));
                }
            }
            else
            {
                await upstreamStream.CopyToAsync(captureStream, ct);
            }

            log.ResponseBody = RedactResponseBodyForLog(captureStream.GetCapturedText(), originalModel);
        }
        else if (isServerSentEvents)
        {
            if (shouldMirrorReasoningContent)
            {
                await CopyOpenAiChatCompletionSseStreamAsync(
                    upstreamStream,
                    countingStream,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel));
            }
            else
            {
                await CopyStreamWithSseHeartbeatsAsync(
                    upstreamStream,
                    countingStream,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel));
            }
        }
        else
        {
            await upstreamStream.CopyToAsync(countingStream, ct);
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
        Action? onHeartbeatSent = null)
    {
        byte[] buffer = new byte[81920];
        byte[] heartbeatBytes = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));

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

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await destination.FlushAsync(ct);
        }
    }

    private static async Task CopyOpenAiChatCompletionSseStreamAsync(
        Stream source,
        Stream destination,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        byte[] heartbeatBytes = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));
        OpenAiSseRewriter rewriter = new();

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
    private sealed class OpenAiSseRewriter
    {
        // Per-choice buffer of streamed delta.content prior to/within a tool_call block.
        private readonly Dictionary<int, ChoiceState> _state = [];

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

                // Mirror reasoning_content → content (when content is empty/null).
                JsonObject? delta = choice["delta"] as JsonObject;
                if (delta is not null
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
    /// </list>
    /// Returns the original text unchanged if the body isn't valid JSON.
    /// </summary>
    private string NormalizeRequestBody(string json, RequestLog log, Func<string, bool>? shouldApplyThinkingCompatibility = null)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Read model name for logging and rewriting.
            string original = root.TryGetProperty("model", out JsonElement modelEl)
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;

            string resolved = _settings.ResolveModelName(original);
            log.Model = original;
            bool applyThinkingCompatibility = shouldApplyThinkingCompatibility?.Invoke(original) ?? true;
            string? injectedInstructions = GetInstructionTextForModel(original);

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
                && !shouldInjectInstructions)
                return json;

            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
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

            writer.WriteEndObject();
            writer.Flush();

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            // Non-JSON or malformed body — forward as-is.
            return json;
        }
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

    private string? GetInstructionTextForModel(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        InstructionSet? instructionSet = _settings.FindInstructionSet(mapping?.InstructionSetName);
        return string.IsNullOrWhiteSpace(instructionSet?.Instructions)
            ? null
            : instructionSet.Instructions;
    }

    private string RedactRequestBodyForLog(string body, string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
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

    private static string RedactSensitiveJsonFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true });
            WriteRedactedJsonElement(writer, doc.RootElement);
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void WriteRedactedJsonElement(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && IsSensitiveJsonProperty(propertyName))
        {
            writer.WriteStringValue(RedactedValueText);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJsonElement(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteRedactedJsonElement(writer, item, propertyName);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveJsonProperty(string propertyName)
    {
        return propertyName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apikey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("password", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("system", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("messages", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("input", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("content", StringComparison.OrdinalIgnoreCase);
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

        log.StatusCode = 200;
        log.Status = RequestStatus.Success;
        await WriteJsonAsync(resp, tags, ct);
    }

    // ── /api/ps → running model stub ────────────────────────────────────────

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
                    details = CreateOllamaModelDetails(new LlamaCppModel { Id = m.ModelName }),
                    expires_at = DateTime.UtcNow.AddHours(1).ToString("o"),
                    size_vram = 0L,
                };
            })
            .ToList();

        log.Status = RequestStatus.Success;
        await WriteJsonAsync(resp, new { models = running }, ct);
    }

    // /api/show → answered entirely from local mapping config, no upstream call

    private async Task HandleShowAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        OllamaShowRequest? showReq = JsonSerializer.Deserialize<OllamaShowRequest>(body, _jsonOptions);
        string requestedModel = showReq?.Model ?? showReq?.Name ?? string.Empty;
        ModelMapping? mapping = _settings.FindModelMapping(requestedModel);
        string modelName = mapping?.ModelName ?? _settings.ResolveModelName(requestedModel);
        log.Model = modelName;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, requestedModel);

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
            Details = CreateOllamaModelDetails(placeholderModel),
            ModelInfo = CreateOllamaModelInfo(mapping, placeholderModel),
            Capabilities = BuildCapabilities(mapping, modelName),
        };

        log.Status = mapping is not null ? RequestStatus.Success : RequestStatus.Error;
        await WriteJsonAsync(resp, showResp, ct);
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
            Details = CreateOllamaModelDetails(new LlamaCppModel { Id = mapping.ModelName }),
        };
    }

    private static OllamaModelDetails CreateOllamaModelDetails(LlamaCppModel model)
    {
        string family = GetModelFamily(model.Id);

        return new OllamaModelDetails
        {
            Format = "openai-compatible",
            Family = family,
            Families = [family],
            ParameterSize = GetParameterSize(model.Id),
            QuantizationLevel = GetQuantizationLevel(model.Id),
        };
    }

    /// <summary>
    /// Reports Ollama-style capability tokens. Tools are always supported for OpenAI-compatible
    /// upstreams; "thinking" is reported when the mapping has thinking compatibility enabled;
    /// "embedding" is inferred when the model name suggests an embedding model.
    /// </summary>
    private static List<string> BuildCapabilities(ModelMapping? mapping, string modelId)
    {
        List<string> caps = ["completion", "tools"];

        if (mapping?.EnableThinkingCompatibility ?? true)
            caps.Add("thinking");

        string lowered = modelId?.ToLowerInvariant() ?? string.Empty;
        if (lowered.Contains("embed", StringComparison.Ordinal)
            || lowered.Contains("bge", StringComparison.Ordinal)
            || lowered.Contains("e5", StringComparison.Ordinal))
        {
            caps.Add("embedding");
        }

        if (lowered.Contains("vision", StringComparison.Ordinal)
            || lowered.Contains("llava", StringComparison.Ordinal)
            || lowered.Contains("vl", StringComparison.Ordinal))
        {
            caps.Add("vision");
        }

        return caps;
    }

    private static Dictionary<string, object> CreateOllamaModelInfo(ModelMapping? mapping, LlamaCppModel? model)
    {
        string id = model?.Id ?? mapping?.ModelName ?? string.Empty;
        Dictionary<string, object> modelInfo = new(StringComparer.OrdinalIgnoreCase)
        {
            ["general.architecture"] = GetModelFamily(id),
            ["general.basename"] = id,
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
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaGenerateRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaGenerateRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (genBase, genTimeout, genApiKey) = ResolveUpstream(ollamaReq.Model);

        // Build the prompt, optionally injecting custom instructions
        string prompt = ollamaReq.Prompt;
        string? systemPrefix = ollamaReq.System;

        // Inject custom instructions if configured for this model mapping
        ModelMapping? mapping = _settings.FindModelMapping(ollamaReq.Model);
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
                Temperature = ollamaReq.Options?.Temperature,
                TopP = ollamaReq.Options?.TopP,
                TopK = ollamaReq.Options?.TopK,
                MinP = ollamaReq.Options?.MinP,
                MaxTokens = ollamaReq.Options?.NumPredict,
                Stop = ollamaReq.Options?.Stop,
                Seed = ollamaReq.Options?.Seed,
                PresencePenalty = ollamaReq.Options?.PresencePenalty,
                FrequencyPenalty = ollamaReq.Options?.FrequencyPenalty,
                RepeatPenalty = ollamaReq.Options?.RepeatPenalty,
            };

        using StringContent genContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
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

            var ollamaResp = new OllamaGenerateResponse
            {
                Model = ollamaReq.Model,
                Response = text,
                Done = true,
                DoneReason = "stop",
                PromptEvalCount = usage?.PromptTokens,
                EvalCount = usage?.CompletionTokens,
            };

            await WriteJsonAsync(resp, ollamaResp, ct);
            log.Status = RequestStatus.Success;
        }
    }

    // ── /api/chat → POST /v1/chat/completions ──────────────────────────────

    private async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaChatRequest? ollamaReq = await TryDeserializeRequestAsync<OllamaChatRequest>(body, resp, log, ct);
        if (ollamaReq is null)
            return;

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
            var (chatBase, chatTimeout, chatApiKey) = ResolveUpstream(ollamaReq.Model);

        // Get model mapping for context management settings
        ModelMapping? mapping = _settings.FindModelMapping(ollamaReq.Model);
        bool enableAutoSummarization = mapping?.EnableAutoSummarization ?? true;
        int preserveRecentCount = mapping?.PreserveRecentMessageCount ?? 4;
        int maxRetries = mapping?.MaxSummarizationRetries ?? 2;

        // Map messages, preserving / synthesising tool_call IDs so OpenAI-compatible
        // upstreams can correlate assistant tool_calls with the following role:"tool" replies.
        List<LlamaCppMessage> messages = MapMessagesWithToolCorrelation(ollamaReq.Messages);
        if (messages.Count > 0
            && ShouldApplyThinkingCompatibility(ollamaReq.Model)
            && IsAssistantResponsePrefill(messages[^1]))
        {
            messages.RemoveAt(messages.Count - 1);
        }

        // Inject custom instructions if configured for this model mapping
        if (mapping?.InstructionSetName is not null)
        {
            InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);
            if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            {
                // Prepend system message with custom instructions
                messages.Insert(0, new LlamaCppMessage("system", instructionSet.Instructions));
            }
        }

        // Retry loop for context overflow handling
        int retryCount = 0;
        int originalMessageCount = messages.Count;

        while (retryCount <= maxRetries)
        {
            var llamaReq = new LlamaCppChatRequest
            {
                Model = resolvedModel,
                Messages = messages,
                Stream = ollamaReq.Stream,
                Tools = MapTools(ollamaReq.Tools),
                ResponseFormat = ResolveResponseFormat(ollamaReq.Format),
                Temperature = ollamaReq.Options?.Temperature,
                TopP = ollamaReq.Options?.TopP,
                TopK = ollamaReq.Options?.TopK,
                MinP = ollamaReq.Options?.MinP,
                MaxTokens = ollamaReq.Options?.NumPredict,
                Stop = ollamaReq.Options?.Stop,
                Seed = ollamaReq.Options?.Seed,
                PresencePenalty = ollamaReq.Options?.PresencePenalty,
                FrequencyPenalty = ollamaReq.Options?.FrequencyPenalty,
                RepeatPenalty = ollamaReq.Options?.RepeatPenalty
                    ?? (mapping is not null && mapping.RepeatPenalty != 1.0 ? (float)mapping.RepeatPenalty : null),
                Mirostat = ollamaReq.Options?.Mirostat,
                MirostatTau = ollamaReq.Options?.MirostatTau,
                MirostatEta = ollamaReq.Options?.MirostatEta,
                NCtx = ollamaReq.Options?.NumCtx,
            };

            using StringContent chatContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
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
                // If context overflow and auto-summarization is enabled, try to summarize and retry
                if (isContextOverflow && enableAutoSummarization && retryCount < maxRetries)
                {
                    retryCount++;
                    log.ErrorMessage = $"Context overflow detected (attempt {retryCount}/{maxRetries}), summarizing...";

                    // Summarize the conversation
                    List<LlamaCppMessage> summarizedMessages = await SummarizeConversationAsync(
                        messages,
                        preserveRecentCount,
                        chatBase,
                        chatTimeout,
                        chatApiKey,
                        resolvedModel,
                        ct);

                    // If summarization didn't reduce message count, stop retrying
                    if (summarizedMessages.Count >= messages.Count)
                    {
                        log.Status = RequestStatus.Error;
                        log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: Context overflow, summarization did not help";
                        log.SummarizationRetries = retryCount;
                        log.OriginalMessageCount = originalMessageCount;
                        resp.StatusCode = (int)upstreamResp.StatusCode;
                        resp.Close();
                        return;
                    }

                    // Track summarization in log
                    log.SummarizationRetries = retryCount;
                    log.OriginalMessageCount = originalMessageCount;
                    log.SummarizedMessageCount = summarizedMessages.Count;

                    // Update messages for next retry
                    messages = summarizedMessages;
                    continue; // Retry with summarized context
                }

                // Non-retriable error or retries exhausted
                log.Status = RequestStatus.Error;
                log.ErrorMessage = isContextOverflow
                    ? $"Upstream {(int)upstreamResp.StatusCode}: Context overflow after {retryCount} summarization attempts"
                    : $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";

                if (retryCount > 0)
                {
                    log.SummarizationRetries = retryCount;
                    log.OriginalMessageCount = originalMessageCount;
                    log.SummarizedMessageCount = messages.Count;
                }

                resp.StatusCode = (int)upstreamResp.StatusCode;
                resp.Close();
                return;
            }

            // Success! Track summarization stats if any occurred
            if (retryCount > 0)
            {
                log.SummarizationRetries = retryCount;
                log.OriginalMessageCount = originalMessageCount;
                log.SummarizedMessageCount = messages.Count;
            }
            if (ollamaReq.Stream)
            {
                resp.ContentType = "application/x-ndjson";
                resp.SendChunked = true;
                resp.KeepAlive = true; // Keep connection alive during long thinking periods

                // Notify the calling AI in-band that the conversation was summarized
                // before forwarding the real response. This is the first chunk so the
                // AI sees it at the start of its context window update.
                if (retryCount > 0)
                    await WriteContextSummarizedNoticeAsync(resp, ollamaReq.Model, originalMessageCount, messages.Count, ct);

                await StreamChatToOllamaAsync(
                    upstreamResp,
                    resp,
                    ollamaReq.Model,
                    log,
                    _settings.CollectResponseDetails,
                    responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                    ShouldEmitHeartbeats(ollamaReq.Model),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(ollamaReq.Model));
            }
            else
            {
                string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);

                // Non-streaming: prefer .message over .delta (OpenAI non-streaming uses message)
                LlamaCppChoice? firstChoice = chunk?.Choices?.FirstOrDefault();
                LlamaCppDelta? delta = firstChoice?.Message ?? firstChoice?.Delta;
                string? upstreamFinishReason = firstChoice?.FinishReason;
                LlamaCppUsage? usage = chunk?.Usage;
                FillTokenStats(log, usage);
                log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

                List<OllamaToolCall>? toolCalls = MapToolCallsToOllama(delta?.ToolCalls);

                // Mirror reasoning_content to content if content is empty
                string? content = delta?.Content;
                if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(delta?.ReasoningContent))
                    content = delta.ReasoningContent;

                ToolCallExtraction toolCallExtraction = ExtractXmlToolCalls(content);
                if (toolCalls is null)
                    toolCalls = toolCallExtraction.ToolCalls;

                content = toolCallExtraction.Content;

                // Prepend a brief notice to the content so the calling AI sees it
                // in its own context window.
                if (retryCount > 0)
                {
                    string notice = BuildContextSummarizedNotice(originalMessageCount, messages.Count);
                    content = string.IsNullOrEmpty(content) ? notice : notice + content;
                }

                if (_settings.CollectResponseDetails)
                    log.ResponseBody = RedactResponseBodyForLog(content ?? string.Empty, ollamaReq.Model);

                var ollamaResp = new OllamaChatResponse
                {
                    Model = ollamaReq.Model,
                    Message = new OllamaMessage("assistant", content) { ToolCalls = toolCalls },
                    Done = true,
                    DoneReason = toolCalls?.Count > 0 ? "tool_calls" : upstreamFinishReason ?? "stop",
                    PromptEvalCount = usage?.PromptTokens,
                    EvalCount = usage?.CompletionTokens,
                };

                await WriteJsonAsync(resp, ollamaResp, ct);
                log.Status = RequestStatus.Success;
            }

            // Request succeeded, break out of retry loop
            return;
        }

        // Should never reach here, but handle it gracefully
        log.Status = RequestStatus.Error;
        log.ErrorMessage = "Max retries exceeded";
        resp.StatusCode = 500;
        resp.Close();
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
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        var (embedBase, embedTimeout, embedApiKey) = ResolveUpstream(ollamaReq.Model);

        // Resolve input: prefer new `input` (string or string[]), fall back to legacy `prompt`.
        object resolvedInput = ResolveEmbeddingInput(ollamaReq);
        bool isBatch = resolvedInput is string[] batch && batch.Length > 1;

        var llamaReq = new LlamaCppEmbeddingsRequest { Model = resolvedModel, Input = resolvedInput };

        using StringContent embedContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
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
        await WriteJsonAsync(resp, ollamaResp, ct);
    }

    // ── Streaming helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the one-line notice injected into assistant content when the conversation
    /// was auto-summarized due to context overflow.
    /// </summary>
    private static string BuildContextSummarizedNotice(int originalCount, int summarizedCount) =>
        $"[Note: conversation history was automatically summarized " +
        $"({originalCount} → {summarizedCount} messages) because the context window was exhausted.]\n\n";

    /// <summary>
    /// Writes a single non-done streaming chunk containing the summarization notice
    /// so the calling AI sees it as the first token of the reply.
    /// </summary>
    private static async Task WriteContextSummarizedNoticeAsync(
        HttpListenerResponse resp, string modelName,
        int originalCount, int summarizedCount,
        CancellationToken ct)
    {
        string notice = BuildContextSummarizedNotice(originalCount, summarizedCount);
        var noticeChunk = new OllamaChatResponse
        {
            Model = modelName,
            Message = new OllamaMessage("assistant", notice),
            Done = false,
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(noticeChunk, _jsonOptions) + "\n");
        await resp.OutputStream.WriteAsync(bytes, ct);
        await resp.OutputStream.FlushAsync(ct);
    }

    private static async Task StreamCompletionToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        Func<string, string> redactResponse,
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

                var doneChunk = new OllamaGenerateResponse
                {
                    Model = modelName,
                    Response = string.Empty,
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
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

            FillTokenStats(log, chunk.Usage);

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
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        using PooledCharBuffer? responseAccumulator = collectResponse ? new PooledCharBuffer() : null;
        bool reachedDone = false;
        bool terminalChunkSent = false;
        long responseBytes = 0;
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));
        Dictionary<int, StreamingToolCallBuilder> toolCallBuilders = [];
        StringBuilder xmlToolCallBuilder = new();
        bool capturingXmlToolCall = false;

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

            if (line == "[DONE]")
            {
                reachedDone = true;
                if (terminalChunkSent)
                    break;

                var doneChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", string.Empty),
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
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

            FillTokenStats(log, chunk.Usage);

            LlamaCppChoice? choice = chunk.Choices?.FirstOrDefault();
            LlamaCppDelta? delta = choice?.Delta;
            string token = (string.IsNullOrEmpty(delta?.Content) ? delta?.ReasoningContent : delta?.Content) ?? string.Empty;
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

            var ollamaChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", token) { ToolCalls = toolCalls },
                Done = done,
                DoneReason = done ? toolCalls is not null ? "tool_calls" : choice?.FinishReason ?? "stop" : null,
            };

            if (done)
                terminalChunkSent = true;

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

    // ── Context Management ───────────────────────────────────────────────────

    /// <summary>
    /// Summarizes older messages in a conversation to reduce context size while preserving recent exchanges.
    /// Keeps system messages and the most recent N message exchanges, summarizes everything in between.
    /// </summary>
    private async Task<List<LlamaCppMessage>> SummarizeConversationAsync(
        List<LlamaCppMessage> messages,
        int preserveRecentCount,
        string baseUrl,
        int timeoutSeconds,
        string? apiKey,
        string modelName,
        CancellationToken ct)
    {
        if (messages.Count <= preserveRecentCount + 1)
            return messages; // Nothing to summarize

        // Separate system messages, messages to summarize, and recent messages
        List<LlamaCppMessage> systemMessages = [];

        // First pass: collect system messages
        foreach (LlamaCppMessage msg in messages)
        {
            if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                systemMessages.Add(msg);
        }

        // Second pass: collect non-system messages
        List<LlamaCppMessage> conversationMessages = [];
        foreach (LlamaCppMessage msg in messages)
        {
            if (!msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                conversationMessages.Add(msg);
        }

        // Split conversation into old (to summarize) and recent (to preserve)
        int messagesToPreserve = Math.Min(preserveRecentCount, conversationMessages.Count);
        int splitPoint = conversationMessages.Count - messagesToPreserve;

        if (splitPoint <= 0)
            return messages; // Nothing to summarize

        List<LlamaCppMessage> oldMessages = [.. conversationMessages.Take(splitPoint)];
        List<LlamaCppMessage> recentMessages = [.. conversationMessages.Skip(splitPoint)];

        if (oldMessages.Count == 0)
            return messages; // Nothing to summarize

        // Build summarization prompt
        string conversationText = BuildConversationTextForSummary(oldMessages);

        List<LlamaCppMessage> summaryRequest =
        [
            new LlamaCppMessage("system", 
                "You are a helpful assistant that summarizes conversations concisely. " +
                "Preserve key facts, decisions, context, and important details. " +
                "Focus on what's essential for continuing the conversation. " +
                "Respond with ONLY the summary, no preamble or meta-commentary."),
            new LlamaCppMessage("user", 
                $"Please provide a concise summary of the following conversation:\n\n{conversationText}")
        ];

        var summaryRequestObj = new LlamaCppChatRequest
        {
            Model = modelName,
            Messages = summaryRequest,
            Stream = false,
            Temperature = 0.3f, // Lower temperature for consistent summarization
            MaxTokens = 500, // Limit summary length
        };

        try
        {
            using StringContent summaryContent = new(
                JsonSerializer.Serialize(summaryRequestObj, _jsonOptions), 
                Encoding.UTF8, 
                "application/json");

            using var summaryReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = summaryContent };
            ApplyApiKey(summaryReqMsg, apiKey);
            using HttpResponseMessage summaryResp = await SendUpstreamAsync(
                summaryReqMsg,
                baseUrl, 
                timeoutSeconds, 
                HttpCompletionOption.ResponseHeadersRead, 
                ct);

            if (!summaryResp.IsSuccessStatusCode)
                return messages; // Summarization failed, return original

            string respBody = await summaryResp.Content.ReadAsStringAsync(ct);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);

            LlamaCppDelta? delta = chunk?.Choices?.FirstOrDefault()?.Message 
                                ?? chunk?.Choices?.FirstOrDefault()?.Delta;

            if (delta?.Content is null || string.IsNullOrWhiteSpace(delta.Content))
                return messages; // No summary generated, return original

            // Build new message list: system messages + summary + recent messages
            List<LlamaCppMessage> result = [.. systemMessages];

            // Add summary as an assistant message
            result.Add(new LlamaCppMessage("assistant", 
                $"[Previous conversation summary: {delta.Content}]"));

            result.AddRange(recentMessages);

            return result;
        }
        catch
        {
            // If summarization fails for any reason, return original messages
            return messages;
        }
    }

    /// <summary>
    /// Builds a formatted text representation of messages for summarization.
    /// </summary>
    private static string BuildConversationTextForSummary(List<LlamaCppMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (LlamaCppMessage msg in messages)
        {
            string role = msg.Role switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                "system" => "System",
                _ => msg.Role
            };

            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine($"{role}: {msg.Content}");
                sb.AppendLine();
            }
            else if (msg.ToolCalls?.Count > 0)
            {
                sb.AppendLine($"{role}: [Called tools: {string.Join(", ", msg.ToolCalls.Select(tc => tc.Function?.Name ?? "unknown"))}]");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
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

    private static void FillTokenStats(RequestLog log, LlamaCppUsage? usage)
    {
        if (usage is null) return;
        log.PromptTokens = usage.PromptTokens;
        log.CompletionTokens = usage.CompletionTokens;
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
            ApiKey = mapping.ApiKey,
            UpstreamType = mapping.UpstreamType,
            UpstreamUrl = mapping.UpstreamUrl,
            UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds,
            RepeatPenalty = mapping.RepeatPenalty,
            Temperature = mapping.Temperature,
            EnableAutoSummarization = mapping.EnableAutoSummarization,
            PreserveRecentMessageCount = mapping.PreserveRecentMessageCount,
            MaxSummarizationRetries = mapping.MaxSummarizationRetries,
            InstructionSetName = mapping.InstructionSetName,
            RedactRequestBodies = mapping.RedactRequestBodies,
            RedactResponseBodies = mapping.RedactResponseBodies,
            RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields,
        };
    }
}
