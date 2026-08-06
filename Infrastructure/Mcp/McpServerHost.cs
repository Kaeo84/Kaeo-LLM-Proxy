using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Modules;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Mcp;

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
    private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(30);

    private static readonly JsonTypeInfo<JsonRpcMessage> s_messageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    private static readonly JsonTypeInfo<JsonRpcError> s_errorTypeInfo =
        (JsonTypeInfo<JsonRpcError>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcError));

    private readonly McpServerSettings _settings;
    private readonly ISecretProvider _secrets;
    private readonly Func<McpServerOptions> _serverOptionsFactory;
    private readonly StatisticsService _statistics;
    private readonly ConcurrentDictionary<string, McpHttpSession> _sessions = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private System.Threading.Timer? _idleSweepTimer;
    private DateTime _startedUtc;

    /// <summary>
    /// Creates a host bound to <paramref name="settings"/>. <paramref name="serverOptionsFactory"/>
    /// builds fresh <see cref="McpServerOptions"/> (server info + currently enabled tools) for
    /// each new session.
    /// </summary>
    public McpServerHost(
        McpServerSettings settings,
        ISecretProvider secrets,
        Func<McpServerOptions> serverOptionsFactory,
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

        string host = _settings.ListenAddress.Trim();
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
            throw new IOException(
                $"Could not bind the MCP server to {prefix} ({ex.Message}, error code {ex.ErrorCode}). " +
                "Non-localhost bindings may require elevation or a netsh urlacl rule.",
                ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _startedUtc = DateTime.UtcNow;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _idleSweepTimer = new System.Threading.Timer(SweepIdleSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        IsRunning = true;

        Log.Information("MCP server listening at {EndpointUrl}", EndpointUrl);
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

        if (_cts is not null)
            await _cts.CancelAsync();

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

        foreach (string sessionId in _sessions.Keys.ToArray())
            await RemoveSessionAsync(sessionId);

        _cts?.Dispose();
        _cts = null;

        Log.Information("MCP server stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Accept loop & routing ───────────────────────────────────────────────

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
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
            _ = HandleRequestSafeAsync(context, ct);
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken ct)
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
            await HandleRequestAsync(context, ct);
            log.StatusCode = context.Response.StatusCode;
            log.Status = log.StatusCode >= 400 ? RequestStatus.Error : RequestStatus.Success;
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
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string path = request.Url?.AbsolutePath ?? "/";
        string method = request.HttpMethod;

        if (method == "GET" && path == HealthPath)
        {
            await WriteHealthAsync(response, ct);
            return;
        }

        if (method == "GET" && path == SpecPath && ApiExplorer is not null)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";
            byte[] spec = Encoding.UTF8.GetBytes(ApiExplorer.BuildSpecJson());
            response.ContentLength64 = spec.Length;
            await response.OutputStream.WriteAsync(spec, ct);
            return;
        }

        if (method == "GET" && path == ScalarPath && ApiExplorer is not null)
        {
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            byte[] page = Encoding.UTF8.GetBytes(await ApiExplorer.BuildScalarHtmlAsync(ct));
            response.ContentLength64 = page.Length;
            await response.OutputStream.WriteAsync(page, ct);
            return;
        }

        if (path == McpPath)
        {
            if (!IsAuthorized(request, out string? authError))
            {
                response.Headers[HttpResponseHeader.WwwAuthenticate] = "Bearer";
                await WriteJsonRpcErrorAsync(response, 401, default, -32001, authError ?? "Unauthorized", ct);
                return;
            }

            switch (method)
            {
                case "POST":
                    await HandleMcpPostAsync(context, ct);
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
        await WriteTextAsync(response, "Not Found", ct);
    }

    // ── Streamable HTTP verbs ───────────────────────────────────────────────

    private async Task HandleMcpPostAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        if (request.ContentLength64 > MaxPostBodyBytes)
        {
            response.StatusCode = 413;
            await WriteTextAsync(response, "Request body too large.", ct);
            return;
        }

        JsonRpcMessage? message;
        try
        {
            message = await JsonSerializer.DeserializeAsync(request.InputStream, s_messageTypeInfo, ct);
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

        RequestId requestId = message is JsonRpcRequest idRequest ? idRequest.Id : default;
        string? sessionIdHeader = request.Headers[SessionIdHeader];

        McpHttpSession? session;
        if (message is JsonRpcRequest { Method: RequestMethods.Initialize } && string.IsNullOrEmpty(sessionIdHeader))
        {
            session = CreateSession();
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
            response.StatusCode = 404;
            await WriteTextAsync(response, "Session not found. Start a new session with an initialize request.", ct);
            return;
        }

        session.Touch();

        // Response headers are committed lazily: the callback runs immediately before the
        // transport writes its first byte, so status/content type land on the real response.
        bool wroteResponse = await session.Transport.HandlePostRequestAsync(
            message,
            response.OutputStream,
            firstMessage =>
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;
                response.Headers[HttpResponseHeader.CacheControl] = "no-cache";
                return default;
            },
            ct);

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

        string accept = request.Headers["Accept"] ?? string.Empty;
        if (!accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonRpcErrorAsync(response, 406, default, -32000,
                "Not Acceptable: GET requires an Accept header including text/event-stream.", ct);
            return;
        }

        string? sessionIdHeader = request.Headers[SessionIdHeader];
        if (string.IsNullOrEmpty(sessionIdHeader) || !_sessions.TryGetValue(sessionIdHeader, out McpHttpSession? session))
        {
            response.StatusCode = 404;
            await WriteTextAsync(response, "Session not found.", ct);
            return;
        }

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
            // A second GET on the same session throws before any bytes are written, so the
            // status line can still be corrected.
            response.StatusCode = 409;
            response.ContentType = "application/json";
            response.SendChunked = false;
            await WriteTextAsync(response, "{\"error\":\"A GET stream is already open for this session.\"}", ct);
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

    private McpHttpSession CreateSession()
    {
        string sessionId = GenerateSessionId();
        CancellationTokenSource lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);

        StreamableHttpServerTransport transport = new() { SessionId = sessionId };
        McpServer server = McpServer.Create(transport, _serverOptionsFactory(), loggerFactory: null, serviceProvider: null);

        McpHttpSession session = new()
        {
            Id = sessionId,
            Transport = transport,
            Server = server,
            LifetimeCts = lifetimeCts,
        };

        session.ServerRunTask = Task.Run(() => RunSessionServerAsync(session));
        _sessions[sessionId] = session;

        Log.Debug("MCP session {SessionId} created", sessionId);
        return session;
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
            _sessions.TryRemove(session.Id, out _);
        }
    }

    private async Task RemoveSessionAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out McpHttpSession? session))
            return;

        try
        {
            // Disposal order matches the SDK's session teardown: complete the transport's
            // message reader, cancel the run loop, then dispose the server.
            await session.Transport.DisposeAsync();
            await session.LifetimeCts.CancelAsync();
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

            session.LifetimeCts.Dispose();
        }

        Log.Debug("MCP session {SessionId} closed", sessionId);
    }

    private void SweepIdleSessions(object? state)
    {
        DateTime cutoff = DateTime.UtcNow - IdleSessionTimeout;

        foreach (McpHttpSession session in _sessions.Values)
        {
            if (session.LastActivityUtc < cutoff)
            {
                Log.Information("Closing idle MCP session {SessionId}", session.Id);
                _ = RemoveSessionAsync(session.Id);
            }
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
            error = $"The credential '{credentialName}' configured for MCP authentication could not be resolved.";
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
