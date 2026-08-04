using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Modules;
using Serilog;

namespace Kaeo.LlmProxy;

/// <summary>
/// Manages the system tray icon, the proxy server lifetime, and the main form visibility.
/// The application runs entirely from the tray — no taskbar entry is shown.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly PerformanceService _perfService;
    private readonly OllamaProxyHandler _handler;
    private readonly ProxyServer _server;
    private readonly AppDatabase _database;
    private readonly ModuleHost _moduleHost;
    private MainForm? _mainForm;
    private bool _disposed;

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

        // Initialize Serilog first so all subsequent code can log.
        AppLogger.Initialize(_settings.Logging);

        _settings.InstructionSets = [.. _database.LoadInstructionSets()];

        Log.Information("Kaeo LLM Proxy starting. ListenAddress={Address} ListenPort={Port} MappingsCount={Count}",
            _settings.ListenAddress, _settings.ListenPort, _settings.ModelMappings.Count);

        _stats = new StatisticsService(_settings.MaxLogEntries, _database, _settings.Logging.LogRetentionHours);
        _perfService = new PerformanceService(_settings.EnablePerformanceSampling);
        _handler = new OllamaProxyHandler(_settings, _stats, _moduleHost);
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

        if (_settings.StartWithDashboardOpen)
            ShowMainForm();
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
            _trayIcon.Text = $"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}";
            Log.Information("Proxy started on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start proxy on {Address}:{Port}", _settings.ListenAddress, _settings.ListenPort);
            _trayIcon.Text = "Kaeo LLM Proxy — Error";
            MessageBox.Show($"Failed to start proxy: {ex.Message}", "Kaeo LLM Proxy",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnServerStatusChanged(object? sender, string status)
    {
        _trayIcon.Text = $"Kaeo LLM Proxy — {status}";
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
            _trayIcon.Text = $"Kaeo LLM Proxy — Listening {_settings.ListenAddress}:{_settings.ListenPort}";
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
            _mainForm = new MainForm(_settings, _stats, _server, _handler, _perfService, _database, _moduleHost);
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
        _database.SaveRuntimeSettings(_settings.CreateRuntimeSettings());
        _settings.Save();
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        Log.Information("Kaeo LLM Proxy shutting down");
        _trayIcon.Visible = false;
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
            _trayIcon.Dispose();
            _server.Dispose();
            _handler.Dispose();
            _stats.Dispose();
            _perfService.Dispose();
            // The database is owned by Program.Main (created and disposed there); only one
            // shared instance exists per process, so it must not be disposed here.
            AppLogger.Shutdown();
        }
        base.Dispose(disposing);
    }
}
