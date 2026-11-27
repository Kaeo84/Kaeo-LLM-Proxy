using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// The dashboard host as the controller sees it. Implemented by the ASP.NET Core host in the Web
/// assembly, which the Services assembly must not reference: the controller stays UI-agnostic and
/// this interface is the only contract between the engine and the dashboard listener.
/// </summary>
internal interface IWebDashboardHost : IAsyncDisposable
{
    /// <summary>True while Kestrel is listening.</summary>
    bool IsRunning { get; }

    /// <summary>The browsable base URL, once listening; otherwise null.</summary>
    string? Url { get; }

    /// <summary>
    /// Starts the listener. <paramref name="force"/> starts it even when the dashboard is disabled
    /// in settings, which is what an explicit Start/Restart click means.
    /// </summary>
    Task StartAsync(bool force = false, CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Owns the web dashboard's lifecycle so both GUIs and the dashboard's own API can report and
/// control the listener. The host itself is created by a factory the process entry point
/// registers (only the tray and headless hosts reference the Web assembly); a host that is started
/// directly still reports its state here because it attaches itself on start and detaches on stop.
/// </summary>
internal sealed class WebDashboardService : IDisposable
{
    private readonly AppSettings _settings;

    // Serializes start/stop/restart so a dashboard click and a Windows GUI click cannot both
    // create hosts and race for the listener (leaking the loser's socket).
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Func<IWebDashboardHost>? _hostFactory;
    private IWebDashboardHost? _host;
    private string _status = "Stopped";
    private bool _disposed;

    public WebDashboardService(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Raised on the calling thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Registers the factory that creates the dashboard host. Without it the service can report
    /// state but cannot start the listener (the assembly that owns the host was not referenced).
    /// </summary>
    public void RegisterHostFactory(Func<IWebDashboardHost> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _hostFactory = factory;
    }

    public bool IsRunning => _host is { IsRunning: true };

    /// <summary>Browsable URL of the running dashboard; null while stopped.</summary>
    public string? Url => _host is { IsRunning: true } host ? host.Url : null;

    /// <summary>Current display status (also delivered via <see cref="StatusChanged"/>).</summary>
    public string Status => _status;

    /// <summary>Message from the most recent failed start; null after a successful one.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether this process can create the host (a factory was registered).</summary>
    public bool CanControl => _hostFactory is not null;

    /// <summary>Listen address the dashboard is configured to bind.</summary>
    public string ListenAddress => string.IsNullOrWhiteSpace(_settings.WebDashboard.ListenAddress)
        ? "localhost"
        : _settings.WebDashboard.ListenAddress.Trim();

    /// <summary>Effective listen port (0 in settings means "proxy port + 1").</summary>
    public int ListenPort => _settings.WebDashboard.GetEffectivePort(_settings.ListenPort);

    /// <summary>Configured scheme: "Http" or "Https".</summary>
    public string Scheme => string.Equals(_settings.WebDashboard.Scheme, "Https", StringComparison.OrdinalIgnoreCase)
        ? "Https"
        : "Http";

    /// <summary>
    /// Starts the listener when the dashboard is enabled, or unconditionally when
    /// <paramref name="forceStart"/> is true (an explicit Start click). Never throws: a bind or
    /// certificate failure is logged and surfaced through <see cref="Status"/>/<see cref="LastError"/>.
    /// </summary>
    public async Task StartAsync(bool forceStart = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(forceStart, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(bool forceStart, CancellationToken cancellationToken)
    {
        if (IsRunning)
            return;

        if (!forceStart && !_settings.WebDashboard.Enabled)
        {
            RaiseStatus("Disabled");
            return;
        }

        if (_hostFactory is null)
        {
            RaiseStatus("Unavailable in this host");
            return;
        }

        // Drop any previously stopped host so a restart never leaves its listener behind.
        await DisposeHostAsync().ConfigureAwait(false);

        try
        {
            IWebDashboardHost host = _hostFactory();
            // The host attaches itself on success, so /api/status is accurate no matter which
            // caller (this service, a test, or an entry point) created it.
            await host.StartAsync(forceStart, cancellationToken).ConfigureAwait(false);

            if (!host.IsRunning)
            {
                // Declined: the enabled flag is off in settings.
                await DisposeHostAsync(host).ConfigureAwait(false);
                RaiseStatus("Disabled");
                return;
            }

            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RaiseStatus($"Failed to start: {ex.Message}");
            Log.Error(ex, "Web dashboard failed to start on {Scheme}://{Address}:{Port}",
                Scheme, ListenAddress, ListenPort);
        }
    }

    /// <summary>Stops the listener and releases the host. Idempotent.</summary>
    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        IWebDashboardHost? host = _host;
        if (host is null)
        {
            if (!_settings.WebDashboard.Enabled)
                RaiseStatus("Disabled");
            else if (LastError is not null)
                RaiseStatus($"Failed to start: {LastError}");
            else
                RaiseStatus("Stopped");
            return;
        }

        _host = null;
        try
        {
            await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping web dashboard");
        }
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops the listener when running, then starts it again with the current settings.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            await StartCoreAsync(forceStart: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Schedules a stop after <paramref name="delay"/>. The dashboard's own Stop/Restart endpoints
    /// must not stop the listener inside the request that is being served by it: the response has
    /// to be written first, otherwise the stop waits on the request that waits on the stop.
    /// </summary>
    public void StopAfter(TimeSpan delay) => RunDeferred("stop", delay, StopAsync);

    /// <summary>Schedules a restart after <paramref name="delay"/>. See <see cref="StopAfter"/>.</summary>
    public void RestartAfter(TimeSpan delay) => RunDeferred("restart", delay, () => RestartAsync());

    private void RunDeferred(string operation, TimeSpan delay, Func<Task> action)
    {
        if (_disposed)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Deferred web dashboard {Operation} failed", operation);
            }
        });
    }

    /// <summary>
    /// Records a host that has just started. Called by the host itself so state is correct however
    /// it was created. Deliberately lock-free: the caller may already hold the lifecycle lock.
    /// </summary>
    public void Attach(IWebDashboardHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        LastError = null;
        RaiseStatus($"Running at {host.Url}");
    }

    /// <summary>Records that <paramref name="host"/> stopped. Lock-free, like <see cref="Attach"/>.</summary>
    public void Detach(IWebDashboardHost host)
    {
        if (!ReferenceEquals(_host, host))
            return;

        _host = null;
        RaiseStatus(_settings.WebDashboard.Enabled ? "Stopped" : "Disabled");
    }

    private async Task DisposeHostAsync(IWebDashboardHost? host = null)
    {
        host ??= _host;
        if (host is null)
            return;

        if (ReferenceEquals(_host, host))
            _host = null;

        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing web dashboard host");
        }
    }

    private void RaiseStatus(string status)
    {
        _status = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            // A throwing UI subscriber must not masquerade as a start failure.
            Log.Warning(ex, "Web dashboard status subscriber threw while handling status {Status}", status);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        IWebDashboardHost? host = _host;
        _host = null;
        if (host is not null)
        {
            try
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error disposing web dashboard during shutdown");
            }
        }

        _lifecycleLock.Dispose();
    }
}