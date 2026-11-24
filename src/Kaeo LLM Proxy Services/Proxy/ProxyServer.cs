using System.Net;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Serilog;

namespace Kaeo.LlmProxy.Services;

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

    // Number of requests currently being handled (slot acquired, inside HandleAsync). Lets StopAsync
    // drain in-flight work gracefully before forcing cancellation.
    private int _inFlightRequests;

    // Caps the number of concurrently processed requests. When saturated, new requests are
    // rejected with 503 instead of queueing without bound (which would exhaust memory/threads).
    // Recreated on Start() so the limit tracks the current settings value.
    private SemaphoreSlim? _concurrencyGate;

    // How long a request waits for a free concurrency slot before being rejected with 503.
    private static readonly TimeSpan _acquireTimeout = TimeSpan.FromSeconds(5);

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Display form of the address the listener bound to on the last successful start
    /// (e.g. "localhost", "0.0.0.0", or a specific NIC IP). Empty until the first start.
    /// </summary>
    public string ListenAddress { get; private set; } = string.Empty;

    /// <summary>Port the listener bound to on the last successful start. Zero until the first start.</summary>
    public int ListenPort { get; private set; }

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
        // http.sys timeout tuning is Windows-only; the Unix managed listener does not expose it
        // (accessing TimeoutManager throws PlatformNotSupportedException there).
        if (OperatingSystem.IsWindows())
        {
            _listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(2);
            _listener.TimeoutManager.HeaderWait = TimeSpan.FromMinutes(5);
            _listener.TimeoutManager.EntityBody = TimeSpan.FromMinutes(30);
            _listener.TimeoutManager.DrainEntityBody = TimeSpan.FromMinutes(5);
            _listener.TimeoutManager.RequestQueue = TimeSpan.FromMinutes(5);
        }

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
        catch (HttpListenerException ex) when (OperatingSystem.IsWindows() && ex.ErrorCode == 5)
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
        catch
        {
            // Any other start failure (port already in use, invalid prefix, etc.) must not leak the
            // partially-created listener (L10).
            _listener.Close();
            _listener = null;
            throw;
        }

        int gateSize = Math.Max(1, maxConcurrentRequests);
        _concurrencyGate?.Dispose();
        _concurrencyGate = new SemaphoreSlim(gateSize, gateSize);

        _cts = new CancellationTokenSource();
        _listenTask = AcceptLoopAsync(_cts.Token);
        IsRunning = true;

        // Display friendly address for status
        string displayHost = host == "+" ? "0.0.0.0" : host;
        ListenAddress = displayHost;
        ListenPort = port;
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

        // Stop accepting new connections first WITHOUT cancelling in-flight requests so they can
        // drain gracefully (L10). Closing the listener makes the accept loop's GetContextAsync fail
        // and exit; the server token stays uncancelled during the grace period.
        listener?.Close();

        if (listenTask is not null)
        {
            try { await listenTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Give in-flight requests up to 10s to finish before forcing cancellation.
        for (int i = 0; i < 200 && Volatile.Read(ref _inFlightRequests) > 0; i++)
            await Task.Delay(50).ConfigureAwait(false);

        cts?.Cancel();

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

            // Take a slot without blocking the accept loop (L10). When saturated, hand the context
            // to a background task that waits for a slot (up to the acquire timeout) and then either
            // handles it or sheds it with 503 — the loop keeps accepting and shedding load instead
            // of stalling for the whole acquire timeout.
            bool slotTaken;
            try
            {
                slotTaken = gate.Wait(0);
            }
            catch (ObjectDisposedException)
            {
                // Gate disposed during shutdown.
                try { context.Response.Close(); } catch { }
                break;
            }

            if (slotTaken)
            {
                // Fire-and-forget each request on the thread pool. Exceptions are observed
                // inside HandleRequestSafelyAsync so a client disconnect never surfaces as an
                // unobserved TaskScheduler exception. The slot is released there too.
                _ = Task.Run(() => HandleRequestSafelyAsync(context, gate, ct), ct);
            }
            else
            {
                _ = Task.Run(() => AwaitSlotThenHandleAsync(context, gate, ct), ct);
            }
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

    private async Task RejectOverloadedAsync(HttpListenerContext context)
    {
        // Recorded before the response is attempted: the client may already be gone, but a request
        // was still answered on this port and shedding it is exactly the kind of event that needs
        // to be visible. This runs before HandleAsync, so there is no RequestLog and no logging
        // finally to fall back on.
        try
        {
            _handler.RecordRejectedRequest(
                context.Request,
                503,
                $"Server at capacity: no concurrency slot became available within {_acquireTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            // This method runs fire-and-forget; nothing may escape as an unobserved fault.
            Log.Warning(ex, "Failed to record a rejected (503) request.");
        }

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

    private async Task AwaitSlotThenHandleAsync(HttpListenerContext context, SemaphoreSlim gate, CancellationToken ct)
    {
        bool acquired;
        try
        {
            acquired = await gate.WaitAsync(_acquireTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { context.Response.Close(); } catch { /* Client gone or server stopping. */ }
            return;
        }
        catch (ObjectDisposedException)
        {
            // Gate disposed during shutdown.
            try { context.Response.Close(); } catch { }
            return;
        }

        if (!acquired)
        {
            await RejectOverloadedAsync(context).ConfigureAwait(false);
            return;
        }

        await HandleRequestSafelyAsync(context, gate, ct).ConfigureAwait(false);
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, SemaphoreSlim gate, CancellationToken ct)
    {
        // Per-request cancellation scope linked to the server lifetime token (M16): the handler
        // flows this token into the upstream call, so this request can be cancelled on its own
        // (client abort, shutdown) without affecting other in-flight requests.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Increment(ref _inFlightRequests);
        try
        {
            await _handler.HandleAsync(context, requestCts.Token).ConfigureAwait(false);
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
            Interlocked.Decrement(ref _inFlightRequests);
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

        // Wait for the accept loop to complete with a timeout.
        // Use WaitAsync().GetAwaiter().GetResult() instead of .Wait() to avoid
        // SynchronizationContext deadlock (Dispose may be called from a UI thread
        // that captures the context).
        if (listenTask is not null)
        {
            try
            {
                listenTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
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
