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

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? StatusChanged;

    public void Start(int port, string listenAddress = "localhost")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        _listener = new HttpListener();

        // Configure timeouts for long-running AI requests (e.g., extended thinking)
        // These need to be set BEFORE calling Start()
        _listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(30);
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

        _listener.Prefixes.Add(prefix);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _listenTask = AcceptLoopAsync(_cts.Token);
        IsRunning = true;

        // Display friendly address for status
        string displayHost = host == "+" ? "0.0.0.0" : host;
        StatusChanged?.Invoke(this, $"Listening on {displayHost}:{port}");
    }

    public async Task RestartAsync(int port, string listenAddress = "localhost")
    {
        await StopAsync().ConfigureAwait(false);
        Start(port, listenAddress);
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

        listener?.Stop();
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

        StatusChanged?.Invoke(this, "Stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                var listener = _listener;
                if (listener is null)
                    break;
                context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            // Fire-and-forget each request on the thread pool. Exceptions are observed
            // here so a client disconnect (HttpListenerException / I/O abort) never
            // surfaces as an unobserved TaskScheduler exception.
            _ = Task.Run(() => HandleRequestSafelyAsync(context, ct), ct);
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, CancellationToken ct)
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
