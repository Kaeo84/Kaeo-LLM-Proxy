using System.Net;
using System.Net.Sockets;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Services.Mcp;

/// <summary>
/// Owns the built-in MCP server lifecycle: persists endpoint settings through
/// <see cref="McpServerSettingsRepository"/>, (re)starts the listener on apply, and surfaces
/// status for the MCP tab. Tools are contributed by loaded modules via
/// <see cref="McpServerOptionsFactory"/>.
/// </summary>
internal sealed class McpServerService : IAsyncDisposable
{
    private readonly McpServerSettingsRepository _repository;
    private readonly ModuleSecretProvider _secrets;
    private readonly McpServerOptionsFactory _optionsFactory;
    private readonly HostInfo _hostInfo;
    private readonly StatisticsService _statistics;

    private McpServerHost? _host;
    private McpApiExplorer? _apiExplorer;
    private string _status = "Stopped";

    // Serializes start/stop/restart/apply so a settings apply and a dashboard Start click cannot
    // both create hosts and race on the listener bind (leaking the losing host's listener).
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public McpServerService(AppDatabase database, AppSettings settings, ModuleHost moduleHost, StatisticsService statistics)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(moduleHost);
        ArgumentNullException.ThrowIfNull(statistics);

        _repository = new McpServerSettingsRepository(database);
        _secrets = new ModuleSecretProvider(settings);
        _optionsFactory = new McpServerOptionsFactory(moduleHost);
        _hostInfo = moduleHost.BuildHostInfo();
        _statistics = statistics;
    }

    public bool IsRunning => _host?.IsRunning == true;

    /// <summary>
    /// Listen address the running host bound to (from the settings snapshot used at start).
    /// Empty while not running.
    /// </summary>
    public string ListenAddress { get; private set; } = string.Empty;

    /// <summary>Listen port the running host bound to. Zero while not running.</summary>
    public int ListenPort { get; private set; }

    /// <summary>Client-facing MCP endpoint URL when running; otherwise null.</summary>
    public string? EndpointUrl => _host is { IsRunning: true } host ? host.EndpointUrl : null;

    /// <summary>Current display status (also delivered via <see cref="StatusChanged"/>).</summary>
    public string Status => _status;

    /// <summary>Aggregate statistics and request logs for the MCP server.</summary>
    public StatisticsService Statistics => _statistics;

    public event EventHandler<string>? StatusChanged;

    /// <summary>Explorer serving the MCP OpenAPI document and Scalar page while running.</summary>
    public McpApiExplorer? ApiExplorer => _apiExplorer;

    public McpServerSettings LoadSettings() => _repository.LoadServerSettings();

    public void SaveSettings(McpServerSettings settings) => _repository.SaveServerSettings(settings);

    /// <summary>
    /// Starts the server when the persisted enabled flag is set, or unconditionally when
    /// <paramref name="forceStart"/> is true (an explicit dashboard Start click). Otherwise a no-op.
    /// </summary>
    public async Task StartAsync(bool forceStart = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(forceStart, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(bool forceStart, CancellationToken cancellationToken)
    {
        McpServerSettings settings = _repository.LoadServerSettings();
        if (!settings.Enabled && !forceStart)
        {
            RaiseStatus("Disabled");
            return;
        }

        try
        {
            if (_host is { IsRunning: true })
                await _host.StopAsync();

            // Recreate the host so fresh endpoint/auth settings always apply. The explorer is
            // only attached when enabled; the host 404s the spec/scalar paths without it.
            _host = new McpServerHost(settings, _secrets, _optionsFactory.Build, _statistics);
            _apiExplorer = settings.EnableApiExplorer ? new McpApiExplorer(_host, _hostInfo) : null;
            _host.ApiExplorer = _apiExplorer;

            await _host.StartAsync(cancellationToken);
            ListenAddress = string.IsNullOrWhiteSpace(settings.ListenAddress)
                ? "localhost"
                : settings.ListenAddress.Trim();
            ListenPort = settings.ListenPort;
            RaiseStatus($"Running at {_host.EndpointUrl}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpListenerException or ArgumentException or InvalidOperationException)
        {
            // Do not leave the previous run's endpoint on display next to a failure status.
            ListenAddress = string.Empty;
            ListenPort = 0;
            _apiExplorer = null;
            RaiseStatus($"Failed to start: {ex.Message}");
            Log.Error(ex, "MCP server failed to start");
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_host is { IsRunning: true })
            await _host.StopAsync();

        _apiExplorer = null;
        ListenAddress = string.Empty;
        ListenPort = 0;
        RaiseStatus("Stopped");
    }

    /// <summary>Stops the server when running, then starts it again with the persisted settings.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_host is { IsRunning: true })
                await _host.StopAsync();

            await StartCoreAsync(forceStart: true, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Re-reads persisted settings and restarts (or stops) the listener accordingly.</summary>
    public async Task ApplySettingsAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_repository.LoadServerSettings().Enabled)
            {
                await StartCoreAsync(forceStart: false, CancellationToken.None);
            }
            else if (_host is { IsRunning: true })
            {
                await StopCoreAsync();
            }
            else
            {
                RaiseStatus("Disabled");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_host is { IsRunning: true })
                await _host.StopAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void RaiseStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
