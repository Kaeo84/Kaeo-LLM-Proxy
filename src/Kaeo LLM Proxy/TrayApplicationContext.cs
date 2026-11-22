using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy;

/// <summary>
/// Manages the system tray icon, the proxy server lifetime, and the main form visibility.
/// The main form carries a taskbar entry while it is open so it can be found and switched to
/// like any other window; closing it with the window's X hides it to the tray instead of exiting,
/// leaving the tray icon as the single always-present entry point.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly StatisticsService _mcpStats;

    /// <summary>
    /// Store for requests the proxy answered without calling a model. Owned here alongside the
    /// other two so every log source has exactly one lifetime and one persistence channel.
    /// </summary>
    private readonly StatisticsService _nonProxiedStats;
    private readonly PerformanceService _perfService;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _server;
    private readonly AppDatabase _database;
    private readonly ModuleHost _moduleHost;
    private readonly McpServerService _mcpServer;
    private readonly StreamMonitor _streamMonitor;
    private readonly SynchronizationContext? _uiContext;
    private MainForm? _mainForm;
    private bool _disposed;
    private bool _exiting;

    /// <summary>
    /// Creates the tray context. <paramref name="settings"/> must already have runtime settings,
    /// model mappings, and credentials applied (see <c>Program.Main</c>). The caller owns the
    /// supplied <paramref name="database"/> — exactly one shared instance exists per process.
    /// </summary>
    public TrayApplicationContext(AppSettings settings, AppDatabase database, ModuleHost moduleHost)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(moduleHost);

        _settings = settings;
        _database = database;
        _moduleHost = moduleHost;
        _uiContext = SynchronizationContext.Current;

        // Initialize Serilog first so all subsequent code can log.
        AppLogger.Initialize(_settings.Logging);

        _settings.InstructionSets = [.. _database.LoadInstructionSets()];

        Log.Information("Kaeo LLM Proxy starting. ListenAddress={Address} ListenPort={Port} MappingsCount={Count}",
            _settings.ListenAddress, _settings.ListenPort, _settings.ModelMappings.Count);

        _stats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours,
            maxEntriesWithBodies: _settings.Logging.MaxLogEntriesWithBodies);
        _mcpStats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours, LogSource.Mcp,
            maxEntriesWithBodies: _settings.Logging.MaxLogEntriesWithBodies);
        _nonProxiedStats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours, LogSource.NonProxied,
            maxEntriesWithBodies: _settings.Logging.MaxLogEntriesWithBodies);

        // Modules were initialized with a forwarding sink; point it at the real MCP log store
        // so module activity (e.g. SSH connections) appears in the MCP logs.
        _moduleHost.BindActivityLog(new McpActivityLogAdapter(_mcpStats));

        _mcpServer = new McpServerService(_database, _settings, _moduleHost, _mcpStats);
        _perfService = new PerformanceService(_settings.EnablePerformanceSampling);
        _streamMonitor = new StreamMonitor();
        _handler = new OllamaProxyHandler(_settings, _stats, _moduleHost, _mcpServer, _nonProxiedStats, streamMonitor: _streamMonitor);
        _handler.StartHeartbeatMonitors();
        _server = new ProxyServer(_handler);

        _trayIcon = new NotifyIcon
        {
            Icon = Program.GetApplicationIcon(),
            Text = "Kaeo LLM Proxy",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _server.StatusChanged += OnServerStatusChanged;

        if (_settings.AutoStartProxy)
            StartProxy();

        StartModules();
        _ = StartMcpServerAsync();

        if (_settings.StartWithDashboardOpen)
            ShowMainForm();
    }

    /// <summary>
    /// Starts the built-in MCP server after the modules. It gates itself on the persisted
    /// enabled flag; a bind failure logs a warning and never blocks host startup.
    /// </summary>
    private async Task StartMcpServerAsync()
    {
        try
        {
            await _mcpServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MCP server failed to start");
        }
    }

    /// <summary>
    /// Starts every loaded runnable module after the proxy. Modules decide internally whether
    /// to actually start based on their own persisted enabled state; a module that fails to
    /// start (e.g. a port bind error) logs a warning and never blocks the host or other modules.
    /// </summary>
    private void StartModules()
    {
        foreach (LoadedModule loaded in _moduleHost.LoadedModules)
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

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, OnOpenDashboard);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Proxy", null, OnStartProxy);
        menu.Items.Add("Stop Proxy", null, OnStopProxy);
        menu.Items.Add("Restart Proxy", null, OnRestartProxy);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);
        return menu;
    }

    private void StartProxy()
    {
        try
        {
            _server.Start(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
            SetTrayText($"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}");
            Log.Information("Proxy started on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start proxy on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
            SetTrayText("Kaeo LLM Proxy — Error");
            MessageBox.Show($"Failed to start proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Sets the tray tooltip text, truncated to the NotifyIcon limit. Call only on the UI thread.
    /// </summary>
    private void SetTrayText(string text)
    {
        if (_disposed)
            return;
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void OnServerStatusChanged(object? sender, string status)
    {
        // StatusChanged can fire on a threadpool thread (Stop path continuations); NotifyIcon
        // is not thread-safe, so marshal to the UI thread captured at construction.
        string text = $"Kaeo LLM Proxy — {status}";
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            // WinForms tray app: there is no JoinableTaskFactory here; the captured
            // WindowsFormsSynchronizationContext is the correct marshaling mechanism.
#pragma warning disable VSTHRD001
            _uiContext.Post(_ => SetTrayText(text), null);
#pragma warning restore VSTHRD001
        }
        else
            SetTrayText(text);
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e) => ShowMainForm();

    private void OnOpenDashboard(object? sender, EventArgs e) => ShowMainForm();

    private void OnStartProxy(object? sender, EventArgs e)
    {
        if (!_server.IsRunning)
            StartProxy();
    }

    private async void OnStopProxy(object? sender, EventArgs e)
    {
        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while stopping proxy");
            MessageBox.Show($"Error stopping proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void OnRestartProxy(object? sender, EventArgs e)
    {
        try
        {
            await _server.RestartAsync(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
            SetTrayText($"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart proxy");
            MessageBox.Show($"Error restarting proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_settings, _stats, _server, _handler, _perfService, _database, _moduleHost, _mcpServer, _nonProxiedStats, _streamMonitor);
            _mainForm.FormClosed += OnMainFormClosed;
            _mainForm.MinimizedToTray += OnMainFormMinimizedToTray;
        }

        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    private void OnMainFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is MainForm mainForm)
            mainForm.MinimizedToTray -= OnMainFormMinimizedToTray;

        _mainForm = null;
    }

    private void OnMainFormMinimizedToTray(object? sender, EventArgs e)
    {
        if (!_settings.ShowCloseToTrayNotification)
            return;

        using Form dialog = new()
        {
            Text = "Still Running",
            ClientSize = new Size(420, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
        };

        TableLayoutPanel layout = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label message = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "Kaeo LLM Proxy is still running and is available in the notification area.",
        };

        CheckBox dontShowAgain = new()
        {
            AutoSize = true,
            Text = "Don't show this again",
        };

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        Button okButton = new()
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "OK",
        };

        buttons.Controls.Add(okButton);
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(dontShowAgain, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.AcceptButton = okButton;
        dialog.Controls.Add(layout);

        dialog.ShowDialog();

        if (!dontShowAgain.Checked)
            return;

        _settings.ShowCloseToTrayNotification = false;
        try
        {
            _database.SaveRuntimeSettings(_settings.CreateRuntimeSettings());
            _settings.Save();
        }
        catch (IOException ex)
        {
            // Another instance may hold the DB/settings file; degrade instead of crashing.
            Log.Warning(ex, "Could not persist 'close to tray' preference");
        }
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;

        Log.Information("Kaeo LLM Proxy shutting down");
        _trayIcon.Visible = false;
        try
        {
            await _mcpServer.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping MCP server during shutdown");
        }
        try
        {
            await _moduleHost.StopAllAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping modules during shutdown");
        }
        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping proxy during shutdown");
        }
        finally
        {
            Application.Exit();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
        {
            _server.StatusChanged -= OnServerStatusChanged;
            ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
            _trayIcon.Dispose();
            menu?.Dispose();
            // The tray/form icon is a shared cached instance owned by Program; do not dispose it.
            _mainForm?.Dispose();
            _mainForm = null;
            _server.Dispose();
            _handler.Dispose();
            _stats.Dispose();
            _mcpStats.Dispose();
            _nonProxiedStats.Dispose();
            _perfService.Dispose();
            // The database is owned by Program.Main (created and disposed there); only one
            // shared instance exists per process, so it must not be disposed here.
            AppLogger.Shutdown();
        }
        base.Dispose(disposing);
    }
}
