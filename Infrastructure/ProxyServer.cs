using System.Net;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// HTTP listener that accepts incoming Ollama-compatible requests and dispatches
/// them to <see cref="OllamaProxyHandler"/>.
/// </summary>
internal sealed class ProxyServer(OllamaProxyHandler handler) : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly OllamaProxyHandler _handler = handler;
    private bool _disposed;

    // Caps the number of concurrently processed requests. When saturated, new requests are
    // rejected with 503 instead of queueing without bound (which would exhaust memory/threads).
    // Recreated on Start() so the limit tracks the current settings value.
    private SemaphoreSlim? _concurrencyGate;

    // How long a request waits for a free concurrency slot before being rejected with 503.
    private static readonly TimeSpan _acquireTimeout = TimeSpan.FromSeconds(5);

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? StatusChanged;

    public void Start(int port, string listenAddress = "localhost", int maxConcurrentRequests = 64)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        _listener = new HttpListener();

        // Configure timeouts for long-running AI requests (e.g., extended thinking)
        // These need to be set BEFORE calling Start()
        // IdleConnection only applies to keep-alive connections BETWEEN requests (long-running
        // requests are governed by the other timeouts), so a short value recycles idle client
        // sockets quickly instead of holding them for 30 minutes.
        _listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(2);
        _listener.TimeoutManager.HeaderWait = TimeSpan.FromMinutes(5);
        _listener.TimeoutManager.EntityBody = TimeSpan.FromMinutes(30);
        _listener.TimeoutManager.DrainEntityBody = TimeSpan.FromMinutes(5);
        _listener.TimeoutManager.RequestQueue = TimeSpan.FromMinutes(5);

        // Normalize the listen address
        string host = listenAddress.Trim();

        // Handle special cases
        if (string.IsNullOrWhiteSpace(host))
            host = "localhost";

        // Convert "0.0.0.0" to "+" for HttpListener (which means all interfaces)
        if (host == "0.0.0.0")
            host = "+";

        // Build prefix - using "+" or specific IPs may require admin or netsh urlacl reservation
        string prefix = $"http://{host}:{port}/";

        try
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            // ERROR_ACCESS_DENIED. Windows' http.sys only allows non-elevated processes to
            // reserve "localhost" bindings for free; binding to "+" (all interfaces), "0.0.0.0",
            // or a specific machine IP requires either running elevated or a one-time URL ACL
            // reservation. Surface the exact command so the user can fix it without guessing.
            _listener.Close();
            _listener = null;

            string netshCommand = $"netsh http add urlacl url=http://{host}:{port}/ user=Everyone";
            throw new InvalidOperationException(
                $"Access denied while starting the proxy on {prefix}. " +
                $"Listening on an address other than 'localhost' requires either running this application " +
                $"as Administrator, or a one-time URL reservation. Open an elevated Command Prompt and run:\n\n" +
                $"{netshCommand}\n\n" +
                "Then try starting the proxy again.", ex);
        }

        int gateSize = Math.Max(1, maxConcurrentRequests);
        _concurrencyGate?.Dispose();
        _concurrencyGate = new SemaphoreSlim(gateSize, gateSize);

        _cts = new CancellationTokenSource();
        _listenTask = AcceptLoopAsync(_cts.Token);
        IsRunning = true;

        // Display friendly address for status
        string displayHost = host == "+" ? "0.0.0.0" : host;
        StatusChanged?.Invoke(this, $"Listening on {displayHost}:{port}");
    }

    public async Task RestartAsync(int port, string listenAddress = "localhost", int maxConcurrentRequests = 64)
    {
        await StopAsync().ConfigureAwait(false);
        Start(port, listenAddress, maxConcurrentRequests);
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        IsRunning = false;

        CancellationTokenSource? cts = _cts;
        HttpListener? listener = _listener;
        Task? listenTask = _listenTask;

        cts?.Cancel();

        // Close() alone stops the listener and releases its resources; a separate Stop() is redundant.
        listener?.Close();

        if (listenTask is not null)
        {
            try { await listenTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();
        _cts = null;
        _listener = null;
        _listenTask = null;

        _concurrencyGate?.Dispose();
        _concurrencyGate = null;

        StatusChanged?.Invoke(this, "Stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Tracks the in-flight GetContextAsync task so we can observe its exception
        // if the loop exits while it is still pending (prevents unobserved task exceptions
        // when the listener is stopped/disposed during shutdown).
        Task<HttpListenerContext>? pendingGetContext = null;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                var listener = _listener;
                if (listener is null)
                    break;
                pendingGetContext = listener.GetContextAsync();
                context = await pendingGetContext.WaitAsync(ct).ConfigureAwait(false);
                pendingGetContext = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            SemaphoreSlim? gate = _concurrencyGate;
            if (gate is null)
                break;

            // Acquire a concurrency slot before dispatching. If none frees up within the
            // acquire timeout, shed load immediately with 503 rather than queueing without
            // bound (which would exhaust memory and thread-pool resources).
            bool acquired;
            try
            {
                acquired = await gate.WaitAsync(_acquireTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!acquired)
            {
                _ = Task.Run(() => RejectOverloadedAsync(context), ct);
                continue;
            }

            // Fire-and-forget each request on the thread pool. Exceptions are observed
            // here so a client disconnect (HttpListenerException / I/O abort) never
            // surfaces as an unobserved TaskScheduler exception. The acquired slot is
            // released inside HandleRequestSafelyAsync.
            _ = Task.Run(() => HandleRequestSafelyAsync(context, gate, ct), ct);
        }

        // If the loop exited while GetContextAsync was still pending (e.g. cancellation
        // won the race against WaitAsync), the original task will eventually fault with
        // ObjectDisposedException when the listener is stopped. Observe it here so it
        // never surfaces as an unobserved TaskScheduler exception.
        if (pendingGetContext is not null)
        {
            _ = pendingGetContext.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task RejectOverloadedAsync(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            byte[] body = System.Text.Encoding.UTF8.GetBytes(
                "{\"error\":\"Server is at capacity. Please retry shortly.\"}");
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            // Client may have disconnected; nothing to do.
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await _handler.HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server is stopping or the request was cancelled — expected, ignore.
        }
        catch (HttpListenerException ex)
        {
            // Client disconnected mid-request (e.g. idle keep-alive drop). Common and benign.
            Log.Debug(ex, "Client connection aborted while handling request");
        }
        catch (ObjectDisposedException)
        {
            // Response stream was already closed by a disconnect. Benign.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled error while processing proxy request");
        }
        finally
        {
            try { context.Response.Close(); }
            catch { /* Already closed or aborted. */ }

            // Release the concurrency slot. Guard against disposal during shutdown.
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsRunning = false;

        CancellationTokenSource? cts = _cts;
        Task? listenTask = _listenTask;
        _cts = null;
        _listenTask = null;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        cts?.Dispose();

        _listener?.Close();
        _listener = null;

        _concurrencyGate?.Dispose();
        _concurrencyGate = null;

        // Wait for the accept loop to complete with a timeout
        if (listenTask is not null)
        {
            try
            {
                listenTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation occurs
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // Expected when cancellation occurs
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error waiting for accept loop to complete during disposal");
            }
        }
    }
}
