using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Web;
using Serilog;
using System.Diagnostics;

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
    private readonly ProxyHostController _controller;
    private readonly AppSettings _settings;
    private readonly AppDatabase _database;
    private readonly SynchronizationContext? _uiContext;
    private MainForm? _mainForm;
    private ToolStripMenuItem? _openWebDashboardItem;
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

        _uiContext = SynchronizationContext.Current;

        // The controller owns the service graph, logging init, and shutdown; the tray only adds
        // the icon, menu, main form, and UI-thread feedback.
        _controller = new ProxyHostController(settings, database, moduleHost);
        _settings = _controller.Settings;
        _database = _controller.Database;

        _trayIcon = new NotifyIcon
        {
            Icon = Program.GetApplicationIcon(),
            Text = "Kaeo LLM Proxy",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _controller.StatusChanged += OnServerStatusChanged;
        _controller.ProxyStartFailed += OnProxyStartFailed;

        // The dashboard host lives in the Web assembly, which the controller's assembly must not
        // reference, so this is the only place that can teach the controller how to create it.
        // Without the factory the controller can still report state but not (re)start the listener.
        _controller.WebDashboard.RegisterHostFactory(() => new WebDashboardHost(_settings, _controller));
        _controller.WebDashboard.StatusChanged += OnWebDashboardStatusChanged;

        if (_settings.AutoStartProxy)
            StartProxy();

        _controller.StartModules();
        _ = _controller.StartMcpServerAsync();
        _controller.StartModelDiscovery();
        _ = StartWebDashboardAsync();

        if (_settings.StartWithDashboardOpen)
            ShowMainForm();
    }

    private async Task StartWebDashboardAsync()
    {
        try
        {
            await _controller.WebDashboard.StartAsync();
            string? url = _controller.WebDashboard.Url;
            if (url is not null)
            {
                Log.Information("Web dashboard available at {Url}", url);
                PostToUi(UpdateWebDashboardMenuItem);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Web dashboard failed to start; continuing without it");
        }
    }

    private void OnWebDashboardStatusChanged(object? sender, string status)
        => PostToUi(UpdateWebDashboardMenuItem);

    private void UpdateWebDashboardMenuItem()
    {
        if (_openWebDashboardItem is null)
            return;

        string? url = _controller.WebDashboard.Url;
        _openWebDashboardItem.Enabled = url is not null;
        _openWebDashboardItem.ToolTipText = url ?? "Web dashboard is not running";
    }

    private void OnProxyStartFailed(object? sender, Exception ex)
    {
        MessageBox.Show($"Failed to start proxy: {ex.Message}", "Kaeo LLM Proxy",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Desktop Dashboard", null, OnOpenDashboard);
        _openWebDashboardItem = new ToolStripMenuItem("Open Web Dashboard", null, OnOpenWebDashboard)
        {
            Enabled = false,
            ToolTipText = "Web dashboard is not running",
        };
        menu.Items.Add(_openWebDashboardItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Proxy", null, OnStartProxy);
        menu.Items.Add("Stop Proxy", null, OnStopProxy);
        menu.Items.Add("Restart Proxy", null, OnRestartProxy);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);
        return menu;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread captured at construction. NotifyIcon and
    /// menu items are not thread-safe; dashboard startup completes on a threadpool thread.
    /// </summary>
    private void PostToUi(Action action)
    {
        if (_disposed)
            return;
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
#pragma warning disable VSTHRD001
            _uiContext.Post(_ => action(), null);
#pragma warning restore VSTHRD001
        }
        else
        {
            action();
        }
    }

    // Tray text and the error MessageBox are driven by the controller's StatusChanged /
    // ProxyStartFailed events (see OnServerStatusChanged / OnProxyStartFailed).
    private void StartProxy() => _controller.StartProxy();

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

    private void OnOpenWebDashboard(object? sender, EventArgs e)
    {
        string? url = _controller.WebDashboard.Url;
        if (url is null)
        {
            MessageBox.Show(
                "The web dashboard is not running. Start it from the dashboard or enable it in Settings → Web Dashboard.",
                "Kaeo LLM Proxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open web dashboard in the default browser");
            MessageBox.Show(
                $"Could not open the default browser: {ex.Message}\n\nThe dashboard is available at {url}",
                "Kaeo LLM Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnStartProxy(object? sender, EventArgs e)
    {
        if (!_controller.Server.IsRunning)
            StartProxy();
    }

    private async void OnStopProxy(object? sender, EventArgs e)
    {
        try
        {
            await _controller.Server.StopAsync();
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
            await _controller.Server.RestartAsync(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
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
            _mainForm = new MainForm(_settings, _controller.Stats, _controller.Server, _controller.Handler, _controller.PerfService, _database, _controller.Access, _controller.ModuleHost, _controller.McpServer, _controller.NonProxiedStats, _controller.WebStats, _controller.StreamMonitor, _controller.WebDashboard, _controller.ModelDiscovery);
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
            // The controller stops the web dashboard first (it is the control plane that drives
            // the rest), then the MCP server, modules, and the proxy.
            await _controller.StopAsync();
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
            _controller.StatusChanged -= OnServerStatusChanged;
            _controller.ProxyStartFailed -= OnProxyStartFailed;
            _controller.WebDashboard.StatusChanged -= OnWebDashboardStatusChanged;
            ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
            _trayIcon.Dispose();
            menu?.Dispose();
            // The tray/form icon is a shared cached instance owned by Program; do not dispose it.
            _mainForm?.Dispose();
            _mainForm = null;
            // The controller disposes the service graph and shuts down logging; the database is
            // owned by Program.Main and disposed there.
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }
}
