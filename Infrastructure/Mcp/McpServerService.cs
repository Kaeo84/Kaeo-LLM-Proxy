using System.Net;
using System.Net.Sockets;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Mcp;

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

    private McpServerHost? _host;
    private McpApiExplorer? _apiExplorer;
    private string _status = "Stopped";

    public McpServerService(AppDatabase database, AppSettings settings, ModuleHost moduleHost)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(moduleHost);

        _repository = new McpServerSettingsRepository(database);
        _secrets = new ModuleSecretProvider(settings);
        _optionsFactory = new McpServerOptionsFactory(moduleHost);
        _hostInfo = moduleHost.BuildHostInfo();
    }

    public bool IsRunning => _host?.IsRunning == true;

    /// <summary>Client-facing MCP endpoint URL when running; otherwise null.</summary>
    public string? EndpointUrl => _host is { IsRunning: true } host ? host.EndpointUrl : null;

    /// <summary>Current display status (also delivered via <see cref="StatusChanged"/>).</summary>
    public string Status => _status;

    public event EventHandler<string>? StatusChanged;

    /// <summary>Explorer serving the MCP OpenAPI document and Scalar page while running.</summary>
    public McpApiExplorer? ApiExplorer => _apiExplorer;

    public McpServerSettings LoadSettings() => _repository.LoadServerSettings();

    public void SaveSettings(McpServerSettings settings) => _repository.SaveServerSettings(settings);

    /// <summary>Starts the server when the persisted enabled flag is set; otherwise a no-op.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        McpServerSettings settings = _repository.LoadServerSettings();
        if (!settings.Enabled)
        {
            RaiseStatus("Disabled");
            return;
        }

        try
        {
            if (_host is { IsRunning: true })
                await _host.StopAsync();

            // Recreate the host so fresh endpoint/auth settings always apply.
            _host = new McpServerHost(settings, _secrets, _optionsFactory.Build);
            _apiExplorer = new McpApiExplorer(_host, _hostInfo);
            _host.ApiExplorer = _apiExplorer;

            await _host.StartAsync(cancellationToken);
            RaiseStatus($"Running at {_host.EndpointUrl}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or HttpListenerException)
        {
            RaiseStatus($"Failed to start: {ex.Message}");
            Log.Error(ex, "MCP server failed to start");
        }
    }

    public async Task StopAsync()
    {
        if (_host is { IsRunning: true })
            await _host.StopAsync();

        RaiseStatus("Stopped");
    }

    /// <summary>Re-reads persisted settings and restarts (or stops) the listener accordingly.</summary>
    public async Task ApplySettingsAsync()
    {
        if (_repository.LoadServerSettings().Enabled)
        {
            await StartAsync();
        }
        else if (_host is { IsRunning: true })
        {
            await StopAsync();
        }
        else
        {
            RaiseStatus("Disabled");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { IsRunning: true })
            await _host.StopAsync();
    }

    private void RaiseStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
