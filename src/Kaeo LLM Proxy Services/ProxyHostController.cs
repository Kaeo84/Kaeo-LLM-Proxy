using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services.Mcp;
using Serilog;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Owns the proxy engine's service graph and lifetime, independent of any UI. Both the Windows
/// tray host (<c>TrayApplicationContext</c>) and the cross-platform headless host build this
/// controller, subscribe to <see cref="StatusChanged"/> / <see cref="ProxyStartFailed"/> for their
/// own feedback, and drive <see cref="StartProxy"/> / <see cref="StartAsync"/> / <see cref="StopAsync"/>.
/// Nothing here references WinForms, so it runs unchanged on Linux and macOS.
/// </summary>
/// <remarks>
/// The caller owns the supplied <paramref name="database"/> — exactly one shared instance exists
/// per process and it is disposed by the caller, not here. <paramref name="settings"/> must already
/// have runtime settings, model mappings, and credentials applied (see the host entry point).
/// </remarks>
internal sealed class ProxyHostController : IAsyncDisposable, IDisposable
{
    private bool _disposed;
    private bool _stopped;

    public AppSettings Settings { get; }
    public AppDatabase Database { get; }
    public ModuleHost ModuleHost { get; }
    public StatisticsService Stats { get; }
    public StatisticsService McpStats { get; }
    public StatisticsService NonProxiedStats { get; }

    /// <summary>Traffic served by the web dashboard's own listener (page loads, sign-ins, API calls).</summary>
    public StatisticsService WebStats { get; }

    /// <summary>Lifecycle + status of the web dashboard listener, driven by both GUIs and its own API.</summary>
    public WebDashboardService WebDashboard { get; }

    public PerformanceService PerfService { get; }
    public OllamaProxyHandler Handler { get; }
    public ProxyServer Server { get; }
    public McpServerService McpServer { get; }
    public StreamMonitor StreamMonitor { get; }
    public ProxyAccessService Access { get; }

    /// <summary>
    /// Proxy status text (forwarded from <see cref="ProxyServer.StatusChanged"/>, plus "Listening …"
    /// on a successful start and "Error" on a failed one). May fire on a threadpool thread; UI hosts
    /// must marshal to their UI thread.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Raised when an auto/explicit proxy start fails (bind error, access denied, …). The exception
    /// is already logged; UI hosts surface it (e.g. a MessageBox), headless hosts log/exit.
    /// </summary>
    public event EventHandler<Exception>? ProxyStartFailed;

    /// <summary>
    /// Message from the most recent failed start, or null after a successful one. The dashboard
    /// surfaces it so a Restart that cannot rebind (port in use, missing URL ACL) explains itself
    /// instead of just coming back "stopped".
    /// </summary>
    public string? LastStartError { get; private set; }

    public ProxyHostController(AppSettings settings, AppDatabase database, ModuleHost moduleHost)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(moduleHost);

        Settings = settings;
        Database = database;
        ModuleHost = moduleHost;

        // Initialize Serilog first so all subsequent code can log.
        AppLogger.Initialize(Settings.Logging);

        Settings.InstructionSets = [.. Database.LoadInstructionSets()];

        Log.Information("Kaeo LLM Proxy starting. ListenAddress={Address} ListenPort={Port} MappingsCount={Count}",
            Settings.ListenAddress, Settings.ListenPort, Settings.ModelMappings.Count);

        Stats = new StatisticsService(Settings.MaxLogEntries, Database, Settings.Logging.LogRetentionHours,
            maxEntriesWithBodies: Settings.Logging.MaxLogEntriesWithBodies);
        McpStats = new StatisticsService(Settings.MaxLogEntries, Database, Settings.Logging.LogRetentionHours, LogSource.Mcp,
            maxEntriesWithBodies: Settings.Logging.MaxLogEntriesWithBodies);
        NonProxiedStats = new StatisticsService(Settings.MaxLogEntries, Database, Settings.Logging.LogRetentionHours, LogSource.NonProxied,
            maxEntriesWithBodies: Settings.Logging.MaxLogEntriesWithBodies);
        WebStats = new StatisticsService(Settings.MaxLogEntries, Database, Settings.Logging.LogRetentionHours, LogSource.Web,
            maxEntriesWithBodies: 0);

        // Modules were initialized with a forwarding sink; point it at the real MCP log store
        // so module activity (e.g. SSH connections) appears in the MCP logs.
        ModuleHost.BindActivityLog(new McpActivityLogAdapter(McpStats));

        Access = new ProxyAccessService(Database);
        McpServer = new McpServerService(Database, Settings, ModuleHost, McpStats, Access);
        WebDashboard = new WebDashboardService(Settings);
        PerfService = new PerformanceService(Settings.EnablePerformanceSampling);
        StreamMonitor = new StreamMonitor();
        Handler = new OllamaProxyHandler(Settings, Stats, ModuleHost, McpServer, NonProxiedStats, streamMonitor: StreamMonitor, access: Access);
        Handler.StartHeartbeatMonitors();
        Server = new ProxyServer(Handler);

        Server.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
    }

    /// <summary>
    /// Starts the proxy listener. Never throws: a failure is logged and surfaced via
    /// <see cref="ProxyStartFailed"/> and a "Error" status so each host can react in its own way.
    /// </summary>
    public void StartProxy()
    {
        try
        {
            Server.Start(Settings.ListenPort, Settings.ListenAddress, Settings.MaxConcurrentRequests);
            LastStartError = null;
            Log.Information("Proxy started on {Address}:{Port}", Settings.ListenAddress, Settings.ListenPort);
            StatusChanged?.Invoke(this, $"Listening {Settings.ListenAddress}:{Settings.ListenPort}");
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            Log.Error(ex, "Failed to start proxy on {Address}:{Port}", Settings.ListenAddress, Settings.ListenPort);
            StatusChanged?.Invoke(this, "Error");
            ProxyStartFailed?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Stops the listener (draining in-flight requests first) and starts it again on the configured
    /// endpoint. Never throws: a rebind failure is handled exactly like a failed
    /// <see cref="StartProxy"/>.
    /// </summary>
    public async Task RestartProxyAsync()
    {
        await Server.StopAsync();
        StartProxy();
    }

    /// <summary>
    /// Convenience startup: auto-start the proxy when configured, then start modules and the MCP
    /// server. The tray host calls the individual steps instead so proxy feedback stays on the UI
    /// thread; the headless host uses this.
    /// </summary>
    public async Task StartAsync()
    {
        if (Settings.AutoStartProxy)
            StartProxy();

        StartModules();
        await StartMcpServerAsync();
    }

    /// <summary>
    /// Starts every loaded runnable module. Modules decide internally whether to actually start
    /// based on their own persisted enabled state; a module that fails to start logs a warning and
    /// never blocks the host or other modules.
    /// </summary>
    public void StartModules()
    {
        foreach (LoadedModule loaded in ModuleHost.LoadedModules)
        {
            if (loaded.Module is not IRunnableModule runnable)
                continue;

            _ = StartModuleAsync(loaded.Entry.Name, runnable);
        }
    }

    private static async Task StartModuleAsync(string? moduleName, IRunnableModule module)
    {
        try
        {
            await module.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Module {ModuleName} failed to start", moduleName);
        }
    }

    /// <summary>
    /// Starts the built-in MCP server. It gates itself on the persisted enabled flag; a bind failure
    /// logs a warning and never blocks host startup.
    /// </summary>
    public async Task StartMcpServerAsync()
    {
        try
        {
            await McpServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MCP server failed to start");
        }
    }

    /// <summary>
    /// Graceful shutdown: stop the web dashboard, then the MCP server, then modules, then drain the
    /// proxy. Each step is isolated so one failure never prevents the others. Idempotent.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;

        try
        {
            // First: the dashboard is the control plane that drives the steps below, so it stops
            // before the services its endpoints call into.
            await WebDashboard.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping web dashboard during shutdown");
        }
        try
        {
            await McpServer.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping MCP server during shutdown");
        }
        try
        {
            await ModuleHost.StopAllAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping modules during shutdown");
        }
        try
        {
            await Server.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping proxy during shutdown");
        }
    }

    /// <summary>Stops the engine (if not already stopped) and releases the service graph.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Server.Dispose();
        Handler.Dispose();
        Stats.Dispose();
        McpStats.Dispose();
        NonProxiedStats.Dispose();
        WebStats.Dispose();
        WebDashboard.Dispose();
        PerfService.Dispose();
        // The database is owned by the host entry point (created and disposed there); only one
        // shared instance exists per process, so it must not be disposed here.
        AppLogger.Shutdown();
    }
}