using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace Kaeo.LlmProxy.Services.Mcp;

/// <summary>
/// One active MCP Streamable HTTP session: the transport carrying JSON-RPC messages and the
/// <see cref="McpServer"/> running the protocol loop for it.
/// </summary>
internal sealed class McpHttpSession
{
    public required string Id { get; init; }

    public required StreamableHttpServerTransport Transport { get; init; }

    public required McpServer Server { get; init; }

    public required CancellationTokenSource LifetimeCts { get; init; }

    public Task ServerRunTask { get; set; } = Task.CompletedTask;

    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    public void Touch() => LastActivityUtc = DateTime.UtcNow;

    private int _openGetStream;

    /// <summary>True while a server-to-client GET SSE stream is open on this session.</summary>
    public bool HasOpenGetStream => Volatile.Read(ref _openGetStream) == 1;

    /// <summary>Claims the single allowed GET stream; false when one is already open.</summary>
    public bool TryMarkGetOpen() => Interlocked.CompareExchange(ref _openGetStream, 1, 0) == 0;

    public void ClearGetOpen() => Volatile.Write(ref _openGetStream, 0);
}

/// <summary>
/// Hosts the module's MCP server on its own <see cref="HttpListener"/> (same http.sys mechanics
/// as the proxy, so elevation/urlacl behavior matches). Implements the MCP Streamable HTTP
/// transport contract: POST carries JSON-RPC messages (SSE-framed responses), GET opens the
/// optional server-to-client SSE stream, DELETE ends a session. Sessions are created by
/// <c>initialize</c> requests and swept after an idle timeout.
/// </summary>
internal sealed class McpServerHost : IAsyncDisposable
{
    public const string McpPath = "/mcp";
    public const string HealthPath = "/health";
    public const string SpecPath = "/openapi/v1/openapi.json";
    public const string ScalarPath = "/scalar";

    private const string SessionIdHeader = "Mcp-Session-Id";
    private const long MaxPostBodyBytes = 10 * 1024 * 1024;
    private const int MaxConcurrentSessions = 100;
    private const long MaxResponseCaptureBytes = 64 * 1024;
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(30);

    private static readonly JsonTypeInfo<JsonRpcMessage> s_messageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    private static readonly JsonTypeInfo<JsonRpcError> s_errorTypeInfo =
        (JsonTypeInfo<JsonRpcError>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcError));

    private readonly McpServerSettings _settings;
    private readonly ISecretProvider _secrets;
    private readonly Func<McpSessionInfo, McpServerOptions> _serverOptionsFactory;
    private readonly StatisticsService _statistics;
    private readonly ConcurrentDictionary<string, McpHttpSession> _sessions = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private System.Threading.Timer? _idleSweepTimer;
    private DateTime _startedUtc;

    // In-flight request handlers, drained by StopAsync before sessions/CTS are disposed so a
    // restart cannot fault active tool calls with ObjectDisposedException races.
    private int _inFlightHandlers;
    private TaskCompletionSource _handlersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // L1: admission gate reserved BEFORE session creation so concurrent POSTs cannot overshoot the
    // cap (a plain Count check is TOCTOU). One slot is released per disposed session.
    private readonly SemaphoreSlim _sessionSlots = new(MaxConcurrentSessions, MaxConcurrentSessions);

    // L3: skip re-entrant idle sweeps; a slow sweep (100 sessions x bounded waits) can outlast the
    // 1-minute timer period and overlap itself.
    private int _sweepRunning;

    // L10: Host-header validation is enforced only for loopback binds, where DNS rebinding is the
    // threat. A wildcard/non-loopback bind is intentionally reachable by hostname and relies on
    // Origin + auth instead.
    private bool _loopbackBind;

    // L2: per-client token buckets bounding the stale-session recovery path (recreate + synthetic
    // handshake). Per-client so one looping client cannot drain a shared bucket and block everyone;
    // refills use the monotonic Environment.TickCount64 so a wall-clock jump cannot stall refills.
    private const long MaxRecoveryTokens = 20;
    private const int MaxRecoveryBuckets = 1024;
    private readonly ConcurrentDictionary<string, RecoveryBucket> _recoveryBuckets = new(StringComparer.Ordinal);

    private sealed class RecoveryBucket
    {
        public readonly object Lock = new();
        public long Tokens = MaxRecoveryTokens;
        public long LastRefillMs = Environment.TickCount64;
    }

    /// <summary>
    /// Creates a host bound to <paramref name="settings"/>. <paramref name="serverOptionsFactory"/>
    /// builds fresh <see cref="McpServerOptions"/> (server info + currently enabled tools) for
    /// each new session.
    /// </summary>
    public McpServerHost(
        McpServerSettings settings,
        ISecretProvider secrets,
        Func<McpSessionInfo, McpServerOptions> serverOptionsFactory,
        StatisticsService statistics)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _serverOptionsFactory = serverOptionsFactory ?? throw new ArgumentNullException(nameof(serverOptionsFactory));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
    }

    public bool IsRunning { get; private set; }

    /// <summary>Client-facing endpoint URL, e.g. <c>http://localhost:8388/mcp</c>.</summary>
    public string EndpointUrl { get; private set; } = string.Empty;

    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Optional explorer serving <see cref="SpecPath"/> and <see cref="ScalarPath"/>.</summary>
    public McpApiExplorer? ApiExplorer { get; set; }

    /// <summary>Starts the listener. Throws <see cref="IOException"/> when the bind fails.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return Task.CompletedTask;

        // L6: honor the caller's cancellation before doing any bind work.
        cancellationToken.ThrowIfCancellationRequested();

        string host = (_settings.ListenAddress ?? "localhost").Trim();
        if (host.Length == 0)
            host = "localhost";
        string listenerHost = host switch
        {
            "" or "*" or "0.0.0.0" or "+" => "+",
            "::" or "[::]" => "[::]",
            _ => host,
        };

        string prefix = $"http://{listenerHost}:{_settings.ListenPort}/";
        string displayHost = listenerHost is "+" or "[::]" ? "localhost" : listenerHost;
        EndpointUrl = $"http://{displayHost}:{_settings.ListenPort}{McpPath}";

        HttpListener listener = new();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            string guidance = OperatingSystem.IsWindows()
                ? "Non-localhost bindings may require elevation or a netsh urlacl rule."
                : "Non-localhost bindings may require additional privileges; ports below 1024 need root or cap_net_bind_service.";
            throw new IOException(
                $"Could not bind the MCP server to {prefix} ({ex.Message}, error code {ex.ErrorCode}). " +
                guidance,
                ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _startedUtc = DateTime.UtcNow;
        _handlersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _idleSweepTimer = new System.Threading.Timer(SweepIdleSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        IsRunning = true;

        Log.Information("MCP server listening at {EndpointUrl}", EndpointUrl);

        // Fail-open auth is only defensible on a loopback bind; warn prominently otherwise.
        bool loopbackBind = listenerHost is not ("+" or "[::]")
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host is "127.0.0.1" or "::1" or "[::1]"
                || (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? bindAddress) && IPAddress.IsLoopback(bindAddress)));
        _loopbackBind = loopbackBind;
        if (!loopbackBind && string.IsNullOrWhiteSpace(_settings.AuthCredentialName))
        {
            Log.Warning(
                "MCP server is listening on non-loopback address {Host}:{Port} with authentication DISABLED. " +
                "Any host that can reach this port can execute MCP tools. Configure an auth credential in the MCP settings.",
                host, _settings.ListenPort);
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the listener and closes every session. Safe to call when not running.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        IsRunning = false;

        _idleSweepTimer?.Dispose();
        _idleSweepTimer = null;

        // Stop the listener first so no new sessions arrive and the accept loop unblocks.
        // The host CTS is cancelled only AFTER sessions are drained below: cancelling it
        // first would trip each session's linked LifetimeCts, making the run task self-remove
        // and race the orderly teardown (leaving transports/servers undisposed).
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping the MCP listener");
        }
        _listener = null;

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
            _acceptLoopTask = null;
        }

        // Drain in-flight request handlers (bounded) before disposing sessions and the CTS they
        // may still reference; a restart while a tool call is streaming is a routine occurrence.
        bool drainTimedOut = false;
        if (Volatile.Read(ref _inFlightHandlers) > 0)
        {
            try
            {
                await _handlersDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                drainTimedOut = true;
                Log.Warning("MCP stop timed out draining {Count} in-flight request handlers", Volatile.Read(ref _inFlightHandlers));
            }
        }

        // Orderly per-session teardown (transport → run loop → server → CTS) while the host
        // CTS is still alive, so each session's linked token is cancelled by RemoveSessionAsync
        // rather than by the host cancellation racing it.
        foreach (string sessionId in _sessions.Keys.ToArray())
            await RemoveSessionAsync(sessionId);

        if (_cts is not null)
            await _cts.CancelAsync();

        // L7: when the drain timed out, handlers may still reference the CTS; disposing it would
        // fault them with ObjectDisposedException. Leave it for the GC to reclaim instead.
        if (!drainTimedOut)
            _cts?.Dispose();
        _cts = null;

        Log.Information("MCP server stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Accept loop & routing ───────────────────────────────────────────────

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        // Tracks the in-flight GetContextAsync task so we can observe its exception if the loop
        // exits while it is still pending. When cancellation wins the WaitAsync race, the inner
        // GetContextAsync task later faults (listener stop aborts the I/O); without observation
        // that surfaces as an UnobservedTaskException from the finalizer thread.
        Task<HttpListenerContext>? pendingGetContext = null;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                pendingGetContext = listener.GetContextAsync();
                context = await pendingGetContext.WaitAsync(ct);
                pendingGetContext = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                break;
            }

            // Each request runs independently; all exceptions are observed inside the handler.
            // L8: capture the current drain TCS so a straggler from this run signals the TCS it
            // started under, never a replacement created by a later in-place restart.
            TaskCompletionSource drained = _handlersDrained;
            Interlocked.Increment(ref _inFlightHandlers);
            _ = HandleRequestSafeAsync(context, ct, drained);
        }

        // If the loop exited while GetContextAsync was still pending (e.g. cancellation won the
        // race against WaitAsync), the original task will eventually fault with an aborted-I/O
        // exception when the listener stops. Observe it here so it never reaches the finalizer.
        if (pendingGetContext is not null)
        {
            _ = pendingGetContext.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken ct, TaskCompletionSource drained)
    {
        RequestLog log = new()
        {
            Method = context.Request.HttpMethod,
            OllamaPath = context.Request.Url?.AbsolutePath ?? "/",
            RequestBytes = Math.Max(0, context.Request.ContentLength64),
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await HandleRequestAsync(context, log, ct);
            log.StatusCode = context.Response.StatusCode;
            log.Status = log.StatusCode >= 400 ? RequestStatus.Error : RequestStatus.Success;
            if (log.ResponseBytes <= 0)
                log.ResponseBytes = context.Response.ContentLength64 >= 0 ? context.Response.ContentLength64 : -1;
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
        {
            // The client went away mid-request; nothing to report beyond the cancelled status.
            log.Status = RequestStatus.Cancelled;
            log.StatusCode = 499;
        }
        catch (Exception ex)
        {
            log.Status = RequestStatus.Error;
            log.StatusCode = 500;
            log.ErrorMessage = ex.Message;

            Log.Error(ex, "Unhandled error handling MCP request {Method} {Path}",
                context.Request.HttpMethod, context.Request.Url?.AbsolutePath);

            try
            {
                context.Response.StatusCode = 500;
            }
            catch (Exception)
            {
                // The response may already be unusable; the error is logged above.
            }
        }
        finally
        {
            stopwatch.Stop();
            log.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            _statistics.AddLog(log);

            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // Client already disconnected.
            }

            if (Interlocked.Decrement(ref _inFlightHandlers) == 0)
                drained.TrySetResult();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, RequestLog log, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string path = request.Url?.AbsolutePath ?? "/";
        string method = request.HttpMethod;

        // Some clients and copied URLs carry a trailing slash; route it like the exact path.
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        // DNS-rebinding / drive-by browser protection (MCP spec): a foreign Origin header means a
        // web page is trying to drive local tools. Non-browser clients send no Origin at all.
        string? origin = request.Headers["Origin"];
        if (origin is not null && !IsAllowedOrigin(origin))
        {
            Log.Warning("Rejected MCP request with foreign Origin '{Origin}' from {Remote}", origin, request.RemoteEndPoint);
            response.StatusCode = 403;
            await WriteTextAsync(response, "Forbidden: cross-origin requests are not allowed.", ct);
            return;
        }

        // L10: DNS-rebinding guidance also recommends validating Host. Enforced for loopback binds
        // (the rebinding threat model); a wildcard/non-loopback bind is intentionally reachable by
        // hostname and relies on Origin + auth, so it is not constrained here.
        if (_loopbackBind && !IsAllowedLoopbackHost(request))
        {
            Log.Warning("Rejected MCP request with non-loopback Host '{Host}' from {Remote}", request.Headers["Host"], request.RemoteEndPoint);
            response.StatusCode = 403;
            await WriteTextAsync(response, "Forbidden: invalid Host header.", ct);
            return;
        }

        if (method == "GET" && path == HealthPath)
        {
            await WriteHealthAsync(response, ct);
            return;
        }

        if (method == "GET" && path == SpecPath && ApiExplorer is not null)
        {
            // The spec lists every tool name/description: reconnaissance aid when auth is on.
            if (!IsAuthorized(request, out string? specAuthError))
            {
                response.Headers[HttpResponseHeader.WwwAuthenticate] = "Bearer";
                response.StatusCode = 401;
                await WriteTextAsync(response, specAuthError ?? "Unauthorized", ct);
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "application/json";
            byte[] spec = Encoding.UTF8.GetBytes(ApiExplorer.BuildSpecJson());
            response.ContentLength64 = spec.Length;
            await response.OutputStream.WriteAsync(spec, ct);
            return;
        }

        if (method == "GET" && path == ScalarPath && ApiExplorer is not null)
        {
            if (!IsAuthorized(request, out string? scalarAuthError))
            {
                response.Headers[HttpResponseHeader.WwwAuthenticate] = "Bearer";
                response.StatusCode = 401;
                await WriteTextAsync(response, scalarAuthError ?? "Unauthorized", ct);
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            byte[] page = Encoding.UTF8.GetBytes(await ApiExplorer.BuildScalarHtmlAsync(ct));
            response.ContentLength64 = page.Length;
            await response.OutputStream.WriteAsync(page, ct);
            return;
        }

        // Some clients (notably GitHub Copilot in Visual Studio) connect to the server's base
        // URL and cannot be pointed at the /mcp suffix, so serve the endpoint on the root too.
        if (path is McpPath or "/")
        {
            if (!IsAuthorized(request, out string? authError))
            {
                response.Headers[HttpResponseHeader.WwwAuthenticate] = "Bearer";
                await WriteJsonRpcErrorAsync(response, 401, TryProbeRequestId(request, ct), -32001, authError ?? "Unauthorized", ct);
                return;
            }

            switch (method)
            {
                case "POST":
                    await HandleMcpPostAsync(context, log, ct);
                    return;
                case "GET":
                    await HandleMcpGetAsync(context, ct);
                    return;
                case "DELETE":
                    await HandleMcpDeleteAsync(context, ct);
                    return;
                default:
                    response.StatusCode = 405;
                    response.Headers["Allow"] = "GET, POST, DELETE";
                    await WriteTextAsync(response, "Method Not Allowed", ct);
                    return;
            }
        }

        response.StatusCode = 404;
        await WriteTextAsync(response,
            $"Not Found. The MCP endpoint is served at {McpPath} and at the server root (POST an initialize request to start a session).", ct);
    }

    // ── Streamable HTTP verbs ───────────────────────────────────────────────

    private async Task HandleMcpPostAsync(HttpListenerContext context, RequestLog log, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        if (request.ContentLength64 > MaxPostBodyBytes)
        {
            response.StatusCode = 413;
            await WriteTextAsync(response, "Request body too large.", ct);
            return;
        }

        // Read the full body into a buffer so it can be captured for the log AND deserialized.
        // The cap is enforced while copying: a chunked request has no Content-Length, so the
        // pre-check above alone would let an unbounded body buffer in memory.
        byte[] bodyBytes;
        await using (var bodyBuffer = new MemoryStream())
        {
            byte[] readBuffer = new byte[81920];
            int read;
            while ((read = await request.InputStream.ReadAsync(readBuffer, ct)) > 0)
            {
                if (bodyBuffer.Length + read > MaxPostBodyBytes)
                {
                    response.StatusCode = 413;
                    await WriteTextAsync(response, "Request body too large.", ct);
                    return;
                }
                bodyBuffer.Write(readBuffer, 0, read);
            }
            bodyBytes = bodyBuffer.ToArray();
        }

        if (_settings.CollectRequestDetails && bodyBytes.Length > 0)
            log.RequestBody = SecretRedactor.Redact(Encoding.UTF8.GetString(bodyBytes));

        JsonRpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(bodyBytes, s_messageTypeInfo);
        }
        catch (JsonException)
        {
            await WriteJsonRpcErrorAsync(response, 400, default, (int)McpErrorCode.InvalidRequest,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.", ct);
            return;
        }

        if (message is null)
        {
            await WriteJsonRpcErrorAsync(response, 400, default, (int)McpErrorCode.InvalidRequest,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.", ct);
            return;
        }

        // Annotate the request log row with the JSON-RPC message this POST carried; otherwise
        // every MCP row would just read "POST /mcp" regardless of what happened.
        if (message is JsonRpcRequest rpcRequest)
        {
            string? toolName = rpcRequest.Method == RequestMethods.ToolsCall
                ? ExtractToolName(rpcRequest)
                : null;

            log.OllamaPath = toolName is null
                ? $"{log.OllamaPath} {rpcRequest.Method}"
                : $"{log.OllamaPath} {rpcRequest.Method} {toolName}";
        }

        RequestId requestId = message is JsonRpcRequest idRequest ? idRequest.Id : default;
        string? sessionIdHeader = request.Headers[SessionIdHeader];

        McpHttpSession? session;
        if (message is JsonRpcRequest { Method: RequestMethods.Initialize } && string.IsNullOrEmpty(sessionIdHeader))
        {
            session = await TryCreateSessionAsync(GetClientAddress(request.RemoteEndPoint));
            if (session is null)
            {
                await WriteSessionUnavailableAsync(response, ct);
                return;
            }
            response.Headers[SessionIdHeader] = session.Id;
        }
        else if (string.IsNullOrEmpty(sessionIdHeader))
        {
            await WriteJsonRpcErrorAsync(response, 400, requestId, (int)McpErrorCode.InvalidRequest,
                "Bad Request: A new session can only be created by an initialize request. " +
                "Include the Mcp-Session-Id header for subsequent requests.", ct);
            return;
        }
        else if (!_sessions.TryGetValue(sessionIdHeader, out session))
        {
            // Lenient recovery path: clients that cached a session id across a server restart
            // often resend initialize together with the stale header. Treat it as a fresh
            // initialize instead of failing the reconnect with 404.
            if (message is JsonRpcRequest { Method: RequestMethods.Initialize })
            {
                session = await TryCreateSessionAsync(GetClientAddress(request.RemoteEndPoint));
                if (session is null)
                {
                    await WriteSessionUnavailableAsync(response, ct);
                    return;
                }
                response.Headers[SessionIdHeader] = session.Id;
            }
            else
            {
                // Other clients (notably GitHub Copilot in Visual Studio) keep sending requests
                // on the stale id and never re-initialize after a 404. Recreate the session and
                // replay the lifecycle handshake locally so the request executes anyway; the
                // returned Mcp-Session-Id header lets the client switch to the new session.
                // Rate-limited: an unbounded recreate path lets any client mint sessions in a loop.
                string? recoveryClientAddress = GetClientAddress(request.RemoteEndPoint);
                if (!TryAcquireRecoveryToken(recoveryClientAddress))
                {
                    response.StatusCode = 404;
                    await WriteTextAsync(response, "Session not found and recovery rate limit reached. Send an initialize request to start a new session.", ct);
                    return;
                }

                session = await TryCreateSessionAsync(recoveryClientAddress);
                if (session is null)
                {
                    await WriteSessionUnavailableAsync(response, ct);
                    return;
                }
                response.Headers[SessionIdHeader] = session.Id;

                // L9: if the synthetic handshake fails, the freshly created session would otherwise
                // linger until the idle sweep. Remove it and answer 500 so the client re-initializes.
                try
                {
                    await ReplayLifecycleHandshakeAsync(session, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "MCP session recovery handshake failed for session {SessionId}; removing orphaned session", session.Id);
                    await RemoveSessionAsync(session.Id);
                    response.Headers.Remove(SessionIdHeader);
                    response.StatusCode = 500;
                    await WriteTextAsync(response, "Session recovery failed. Send an initialize request to start a new session.", ct);
                    return;
                }
            }
        }

        session.Touch();

        // Response headers are committed lazily: the callback runs immediately before the
        // transport writes its first byte, so status/content type land on the real response.
        Stream outputStream = response.OutputStream;
        MemoryStream? responseCapture = null;
        TeeStream? tee = null;
        if (_settings.CollectResponseDetails)
        {
            responseCapture = new MemoryStream();
            tee = new TeeStream(response.OutputStream, responseCapture, MaxResponseCaptureBytes);
            outputStream = tee;
        }

        bool wroteResponse = await session.Transport.HandlePostRequestAsync(
            message,
            outputStream,
            firstMessage =>
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;
                response.Headers[HttpResponseHeader.CacheControl] = "no-cache";
                return default;
            },
            ct);

        if (responseCapture is not null && tee is not null && responseCapture.Length > 0)
        {
            log.ResponseBody = SecretRedactor.Redact(Encoding.UTF8.GetString(responseCapture.ToArray()));
            log.ResponseBytes = tee.TotalBytesWritten;
            if (tee.TotalBytesWritten > responseCapture.Length)
                log.ResponseBody += "\n... [response capture truncated at 64 KB]";
        }

        if (!wroteResponse)
        {
            // Notifications produce no response body: nothing has been written, so the status
            // line can still be chosen freely.
            response.StatusCode = 202;
            response.ContentLength64 = 0;
        }
    }

    private async Task HandleMcpGetAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        string? sessionIdHeader = request.Headers[SessionIdHeader];
        if (string.IsNullOrEmpty(sessionIdHeader))
        {
            // No established session yet: this is typically a client probing for SSE support.
            // 405 is the spec-recommended signal; compliant clients fall back to POST-only mode.
            response.StatusCode = 405;
            response.Headers["Allow"] = "POST, DELETE";
            await WriteTextAsync(response,
                "GET requires an established MCP session. Send an initialize request via POST first.", ct);
            return;
        }

        string accept = request.Headers["Accept"] ?? string.Empty;
        if (!accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonRpcErrorAsync(response, 406, default, -32000,
                "Not Acceptable: GET requires an Accept header including text/event-stream.", ct);
            return;
        }

        if (!_sessions.TryGetValue(sessionIdHeader, out McpHttpSession? session))
        {
            response.StatusCode = 404;
            await WriteTextAsync(response, "Session not found.", ct);
            return;
        }

        // Detect the duplicate-GET condition BEFORE the prime comment commits the 200 headers;
        // afterwards the status line can no longer be rewritten to 409.
        if (!session.TryMarkGetOpen())
        {
            response.StatusCode = 409;
            response.ContentType = "application/json";
            await WriteTextAsync(response, "{\"error\":\"A GET stream is already open for this session.\"}", ct);
            return;
        }

        try
        {
            session.Touch();
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            response.Headers[HttpResponseHeader.CacheControl] = "no-cache";

            // Commit the response immediately with an SSE comment line (ignored by SSE parsers).
            // HttpListener defers sending response headers until the first body byte is written,
            // and an idle stream may carry no events for a long time.
            byte[] prime = ": stream open\n\n"u8.ToArray();
            await response.OutputStream.WriteAsync(prime, ct);
            await response.OutputStream.FlushAsync(ct);

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.LifetimeCts.Token);
            try
            {
                // Holds the connection open, streaming unsolicited server-to-client messages until
                // the session is closed or the client disconnects.
                await session.Transport.HandleGetRequestAsync(response.OutputStream, linked.Token);
            }
            catch (InvalidOperationException)
            {
                // Headers are already committed (the prime was flushed), so the status cannot be
                // corrected here; just end the stream.
                Log.Debug("MCP GET stream for session {SessionId} was rejected by the transport after headers were committed", session.Id);
            }
        }
        finally
        {
            session.ClearGetOpen();
        }
    }

    private async Task HandleMcpDeleteAsync(HttpListenerContext context, CancellationToken ct)
    {
        string? sessionIdHeader = context.Request.Headers[SessionIdHeader];
        if (string.IsNullOrEmpty(sessionIdHeader) || !_sessions.ContainsKey(sessionIdHeader))
        {
            context.Response.StatusCode = 404;
            await WriteTextAsync(context.Response, "Session not found.", ct);
            return;
        }

        await RemoveSessionAsync(sessionIdHeader);
        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = 0;
    }

    // ── Sessions ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a session unless the concurrent-session cap is reached or the host is stopping,
    /// in which case null is returned and the caller answers 503.
    /// </summary>
    private async Task<McpHttpSession?> TryCreateSessionAsync(string? clientAddress)
    {
        // L1: reserve an admission slot atomically before any creation work. Wait(0) is a
        // non-blocking try-enter; failure means the cap is genuinely reached right now.
        if (!_sessionSlots.Wait(0))
        {
            Log.Warning("MCP session limit ({Limit}) reached; rejecting new session from {ClientAddress}",
                MaxConcurrentSessions, clientAddress ?? "unknown");
            return null;
        }

        // The slot is owned by this method until the session is successfully created; afterwards
        // ownership transfers to the session and DisposeSessionAsync releases it. Any early return
        // or throw below releases the reservation here.
        bool slotOwnedHere = true;
        try
        {
            CancellationTokenSource? hostCts = _cts;
            if (hostCts is null)
                return null;

            string sessionId = GenerateSessionId();
            CancellationTokenSource lifetimeCts;
            try
            {
                lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(hostCts.Token);
            }
            catch (ObjectDisposedException)
            {
                // StopAsync disposed the host CTS between the null check and the link.
                return null;
            }

            McpSessionInfo sessionInfo = new(sessionId, clientAddress);
            StreamableHttpServerTransport transport = new() { SessionId = sessionId };
            McpServer server;
            try
            {
                server = McpServer.Create(transport, _serverOptionsFactory(sessionInfo), loggerFactory: null, serviceProvider: null);
            }
            catch
            {
                // The options factory runs module code and may throw. Release the transport and the
                // linked CTS registration so a consistently-failing module cannot leak one of each
                // per failed POST (unbounded callback growth on the host CTS).
                try
                {
                    await transport.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error disposing transport after MCP session creation failure");
                }

                lifetimeCts.Dispose();
                throw;
            }

            McpHttpSession session = new()
            {
                Id = sessionId,
                Transport = transport,
                Server = server,
                LifetimeCts = lifetimeCts,
            };

            session.ServerRunTask = Task.Run(() => RunSessionServerAsync(session));
            _sessions[sessionId] = session;
            slotOwnedHere = false; // ownership transfers to the session (released on disposal)

            Log.Debug("MCP session {SessionId} created for client {ClientAddress}", sessionId, clientAddress ?? "unknown");
            return session;
        }
        finally
        {
            if (slotOwnedHere)
                _sessionSlots.Release();
        }
    }

    private static async Task WriteSessionUnavailableAsync(HttpListenerResponse response, CancellationToken ct)
    {
        response.StatusCode = 503;
        await WriteTextAsync(response, "Too many active MCP sessions or the server is stopping. Try again later.", ct);
    }

    /// <summary>
    /// Per-client token bucket for the stale-session recovery path: refills one token per second up
    /// to <see cref="MaxRecoveryTokens"/>, so legitimate reconnect bursts pass while a loop on random
    /// session ids is throttled. L2: keyed by client address so one abusive client cannot drain a
    /// shared bucket, and timed with the monotonic <see cref="Environment.TickCount64"/>.
    /// </summary>
    private bool TryAcquireRecoveryToken(string? clientAddress)
    {
        string key = clientAddress ?? "unknown";
        RecoveryBucket bucket = _recoveryBuckets.GetOrAdd(key, static _ => new RecoveryBucket());

        lock (bucket.Lock)
        {
            long now = Environment.TickCount64;
            long elapsedMs = now - bucket.LastRefillMs;
            if (elapsedMs >= 1000)
            {
                bucket.Tokens = Math.Min(MaxRecoveryTokens, bucket.Tokens + elapsedMs / 1000);
                bucket.LastRefillMs = now;
            }

            if (bucket.Tokens <= 0)
                return false;

            bucket.Tokens--;
            return true;
        }
    }

    private const string SyntheticInitializeJson = """
        {"jsonrpc":"2.0","id":"synthetic-initialize","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"kaeo-session-recovery","version":"1.0"}}}
        """;

    private const string SyntheticInitializedJson = """
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        """;

    /// <summary>
    /// Replays the MCP lifecycle handshake (initialize request + initialized notification)
    /// against a freshly created session so a request arriving with a stale session id can be
    /// executed without the client re-initializing. Both responses are discarded; only the
    /// session's protocol state advances.
    /// </summary>
    private static async Task ReplayLifecycleHandshakeAsync(McpHttpSession session, CancellationToken ct)
    {
        JsonRpcMessage? initialize = JsonSerializer.Deserialize(SyntheticInitializeJson, s_messageTypeInfo);
        if (initialize is not null)
            await session.Transport.HandlePostRequestAsync(initialize, Stream.Null, static _ => default, ct);

        JsonRpcMessage? initialized = JsonSerializer.Deserialize(SyntheticInitializedJson, s_messageTypeInfo);
        if (initialized is not null)
            await session.Transport.HandlePostRequestAsync(initialized, Stream.Null, static _ => default, ct);
    }

    /// <summary>
    /// Extracts the client's IP address from <paramref name="remoteEndPoint"/> as a display
    /// string. IPv4 is preferred: IPv4-mapped IPv6 addresses are reported as plain IPv4.
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

    private async Task RunSessionServerAsync(McpHttpSession session)
    {
        try
        {
            await session.Server.RunAsync(session.LifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal session teardown.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MCP session {SessionId} ended with an error", session.Id);
        }
        finally
        {
            // A self-terminating session (fault / transport completion) must still be fully
            // disposed, not just removed. Whoever wins TryRemove owns disposal; do NOT await
            // ServerRunTask here — this finally runs inside that very task.
            if (_sessions.TryRemove(session.Id, out _))
                await DisposeSessionAsync(session, waitForRunTask: false);
        }
    }

    private async Task RemoveSessionAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out McpHttpSession? session))
            return;

        await DisposeSessionAsync(session, waitForRunTask: true);
        Log.Debug("MCP session {SessionId} closed", sessionId);
    }

    /// <summary>
    /// Disposes a session's transport, server, and lifetime CTS in the SDK's teardown order.
    /// Shared by <see cref="RemoveSessionAsync"/> (waits for the run loop) and the run task's
    /// own finally (must not wait for itself). Idempotent ownership is guaranteed by the
    /// caller's successful TryRemove.
    /// </summary>
    private async Task DisposeSessionAsync(McpHttpSession session, bool waitForRunTask)
    {
        try
        {
            // Disposal order matches the SDK's session teardown: complete the transport's
            // message reader, cancel the run loop, then dispose the server.
            await session.Transport.DisposeAsync();
            await session.LifetimeCts.CancelAsync();
            if (waitForRunTask)
                await session.ServerRunTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error closing MCP session {SessionId}", session.Id);
        }
        finally
        {
            try
            {
                await session.Server.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error disposing MCP server for session {SessionId}", session.Id);
            }

            // L3: guard the CTS dispose so a throw here cannot escape as an unobserved task
            // exception from the fire-and-forget idle sweep.
            try
            {
                session.LifetimeCts.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error disposing lifetime CTS for MCP session {SessionId}", session.Id);
            }

            // L1: release the admission slot reserved when this session was created. Exactly one
            // caller wins TryRemove per session, so this runs once per admitted session.
            _sessionSlots.Release();
        }
    }

    private void SweepIdleSessions(object? state)
    {
        // L3: skip re-entrant sweeps so a slow sweep cannot overlap itself.
        if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0)
            return;

        try
        {
            DateTime cutoff = DateTime.UtcNow - IdleSessionTimeout;

            foreach (McpHttpSession session in _sessions.Values)
            {
                // An open GET SSE stream is liveness even when no POSTs arrive: a quiet-but-connected
                // client must not be swept out from under its stream.
                if (session.LastActivityUtc < cutoff && !session.HasOpenGetStream)
                {
                    Log.Information("Closing idle MCP session {SessionId}", session.Id);
                    // L3: observe the removal task so a fault is logged rather than unobserved.
                    _ = RemoveSessionAsync(session.Id).ContinueWith(
                        static t => Log.Warning(t.Exception, "Error sweeping idle MCP session"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            // L2: prune fully-refilled (idle) recovery buckets when the table grows, so a spray of
            // distinct client addresses cannot grow it without bound.
            if (_recoveryBuckets.Count > MaxRecoveryBuckets)
            {
                foreach (KeyValuePair<string, RecoveryBucket> pair in _recoveryBuckets)
                {
                    RecoveryBucket bucket = pair.Value;
                    lock (bucket.Lock)
                    {
                        if (bucket.Tokens >= MaxRecoveryTokens)
                            _recoveryBuckets.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    private static string GenerateSessionId()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    // ── Auth & responses ────────────────────────────────────────────────────

    /// <summary>
    /// Enforces optional bearer-token authentication. The expected token is resolved live from
    /// the host's credential store on every request, so credential edits apply immediately.
    /// </summary>
    private bool IsAuthorized(HttpListenerRequest request, out string? error)
    {
        error = null;

        string? credentialName = _settings.AuthCredentialName;
        if (string.IsNullOrWhiteSpace(credentialName))
            return true;

        string? expected = _secrets.ResolveSecret(credentialName);
        if (string.IsNullOrWhiteSpace(expected))
        {
            // L5: keep the configured credential name server-side; the caller gets a generic message
            // so an unauthenticated client cannot enumerate which credential is misconfigured.
            Log.Warning("MCP authentication credential '{CredentialName}' could not be resolved; rejecting request", credentialName);
            error = "MCP authentication is misconfigured on the server.";
            return false;
        }

        string? header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            error = "A bearer token is required. Send 'Authorization: Bearer <token>'.";
            return false;
        }

        string token = header["Bearer ".Length..].Trim();
        bool match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));

        if (!match)
            error = "The supplied bearer token is not valid.";

        return match;
    }

    /// <summary>
    /// Validates a browser-sent Origin header against loopback hosts (DNS-rebinding protection).
    /// Only loopback origins are accepted: a page served from any other host must not drive the
    /// local MCP tools, regardless of which address the server itself is bound to.
    /// </summary>
    private static bool IsAllowedOrigin(string origin)
    {
        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// L10: accepts a Host header naming a loopback alias (localhost / 127.0.0.0/8 / ::1). Used only
    /// for loopback binds to defeat DNS rebinding, where an attacker's domain resolves to loopback
    /// but the browser still sends the attacker's Host. A missing Host header (HTTP/1.0) is allowed;
    /// Origin validation remains the primary browser guard.
    /// </summary>
    private static bool IsAllowedLoopbackHost(HttpListenerRequest request)
    {
        string? authority = request.Headers["Host"];
        if (string.IsNullOrWhiteSpace(authority))
            return true;

        string hostPart;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close < 0)
                return false;
            hostPart = authority[1..close];
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            hostPart = colon >= 0 && int.TryParse(authority[(colon + 1)..], out _) ? authority[..colon] : authority;
        }

        if (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(hostPart, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Best-effort peek at the JSON-RPC request id before auth rejection so a 401 can be
    /// correlated by the client. Only probes a seekable, length-bounded body and restores the
    /// stream position; the body is fully validated later regardless.
    /// </summary>
    private const int MaxProbeBytes = 4096; // L4: only peek a small prefix before auth.

    private static RequestId TryProbeRequestId(HttpListenerRequest request, CancellationToken ct)
    {
        if (request.HttpMethod != "POST"
            || !request.InputStream.CanSeek
            || request.ContentLength64 is <= 0 or > MaxPostBodyBytes)
            return default;

        long originalPosition = request.InputStream.Position;
        try
        {
            // L4: read at most a 4 KB prefix and scan it with a forward-only reader instead of
            // parsing the whole (up to 10 MB) body before authorization. isFinalBlock:false lets the
            // reader stop cleanly at the prefix boundary without throwing on truncated JSON.
            int probeLength = (int)Math.Min(request.ContentLength64, MaxProbeBytes);
            byte[] buffer = new byte[probeLength];
            int read = request.InputStream.Read(buffer, 0, probeLength);
            if (read <= 0)
                return default;

            Utf8JsonReader reader = new(buffer.AsSpan(0, read), isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.CurrentDepth == 1
                    && reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("id"))
                {
                    if (!reader.Read())
                        return default;
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long numericId))
                        return new RequestId(numericId);
                    if (reader.TokenType == JsonTokenType.String)
                        return new RequestId(reader.GetString() ?? string.Empty);
                    return default;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            // Correlation is cosmetic; the real parse happens after auth.
        }
        finally
        {
            request.InputStream.Position = originalPosition;
        }

        return default;
    }

    private async Task WriteHealthAsync(HttpListenerResponse response, CancellationToken ct)
    {
        object health = new
        {
            status = "ok",
            server = "Kaeo LLM Proxy MCP",
            uptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
            activeSessions = _sessions.Count,
        };

        response.StatusCode = 200;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, health, cancellationToken: ct);
    }

    /// <summary>
    /// Extracts the tool name from a <c>tools/call</c> request's parameters for log annotation.
    /// Returns null when absent; annotation must never break request handling.
    /// </summary>
    private static string? ExtractToolName(JsonRpcRequest request)
    {
        try
        {
            if (request.Params is JsonObject parameters
                && parameters.TryGetPropertyValue("name", out JsonNode? name)
                && name is JsonValue value
                && value.TryGetValue(out string? toolName))
            {
                return toolName;
            }
        }
        catch (Exception)
        {
            // Purely cosmetic; fall through and log without the tool name.
        }

        return null;
    }

    private static async Task WriteJsonRpcErrorAsync(
        HttpListenerResponse response, int statusCode, RequestId requestId, int errorCode, string message, CancellationToken ct)
    {
        JsonRpcError error = new()
        {
            Id = requestId,
            Error = new JsonRpcErrorDetail { Code = errorCode, Message = message },
        };

        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, error, s_errorTypeInfo, ct);
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(response.ContentType))
            response.ContentType = "text/plain";

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }
}

/// <summary>
/// Writes all bytes to both the primary stream (the real HTTP response) and a secondary
/// capture stream (for log recording). Does not dispose either stream on its own —
/// the caller owns the lifecycle of both.
/// </summary>
internal sealed class TeeStream : Stream
{
    private readonly Stream _primary;
    private readonly Stream _secondary;
    private readonly long _maxSecondaryBytes;
    private long _secondaryWritten;
    private long _totalWritten;

    public TeeStream(Stream primary, Stream secondary, long maxSecondaryBytes = long.MaxValue)
    {
        _primary = primary;
        _secondary = secondary;
        _maxSecondaryBytes = maxSecondaryBytes;
    }

    /// <summary>Total bytes written to the primary stream, including any beyond the capture cap.</summary>
    public long TotalBytesWritten => Interlocked.Read(ref _totalWritten);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        CaptureToSecondary(buffer, offset, count);
        Interlocked.Add(ref _totalWritten, count);
        _primary.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        CaptureToSecondary(buffer, offset, count);
        Interlocked.Add(ref _totalWritten, count);
        return _primary.WriteAsync(buffer, offset, count, cancellationToken);
    }

    private void CaptureToSecondary(byte[] buffer, int offset, int count)
    {
        long remaining = _maxSecondaryBytes - _secondaryWritten;
        if (remaining <= 0)
            return;

        int toWrite = (int)Math.Min(count, remaining);
        _secondary.Write(buffer, offset, toWrite);
        _secondaryWritten += toWrite;
    }

    public override void Flush() => _primary.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _primary.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        // Intentionally does not dispose _primary or _secondary; the caller owns them.
    }
}
