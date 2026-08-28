using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Security;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Mcp;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Mcp;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy;

internal partial class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly ProxyServer _server;
    private readonly OllamaProxyHandler _handler;
    private readonly PerformanceService _perfService;
    private readonly AppDatabase _database;
    private readonly ModuleHost _moduleHost;
    private readonly McpServerService _mcpServer;
    private readonly StatisticsService _mcpStats;

    // Tabs injected by loaded modules
    // module is disabled or unregistered while the dashboard is open.
    private readonly Dictionary<string, TabPage> _moduleTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TabPage> _moduleHelpPages = new(StringComparer.OrdinalIgnoreCase);
    private TabControl _helpModulesTabs = null!;
    private TabPage _helpModulesPlaceholder = null!;

    // Cached snapshot of log summaries backing the virtual-mode ListView. Only visible rows
    // (plus a small buffer) are materialized as ListViewItem objects via RetrieveVirtualItem.
    private IReadOnlyList<RequestLog> _logCache = [];
    private IReadOnlyList<RequestLog> _mcpLogCache = [];

    // Set while LoadSettingsToForm populates controls so the immediate-save event handlers
    // do not persist values that are merely being loaded.
    private bool _loadingSettings;

    // Set while a dashboard start/stop/restart operation is in flight so rapid repeated clicks
    // cannot start overlapping operations; the refresh methods keep the control buttons
    // disabled for the duration.
    private bool _proxyOperationInProgress;
    private bool _mcpDashOperationInProgress;

    // Coalesces concurrent MCP settings applies so rapid successive changes produce at most
    // one in-flight apply plus one follow-up.
    private bool _applyingMcpSettings;
    private bool _mcpSettingsApplyPending;

    internal event EventHandler? MinimizedToTray;

    private const string TestConsoleHeartbeatMarker = "__kaeo_test_console_heartbeat__";

    private static readonly JsonSerializerOptions _indentedJsonOptions = new() { WriteIndented = true };

    // Shared client for the test console. Creating a new HttpClient per test send causes socket
    // churn; per-request timeouts are enforced with a linked CancellationTokenSource instead.
    private static readonly HttpClient _testConsoleClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public MainForm(AppSettings settings, StatisticsService stats, ProxyServer server, OllamaProxyHandler handler, PerformanceService perfService, AppDatabase database, ModuleHost moduleHost, McpServerService mcpServer)
    {
        _settings = settings;
        _stats = stats;
        _server = server;
        _handler = handler;
        _perfService = perfService;
        _database = database;
        _moduleHost = moduleHost;
        _mcpServer = mcpServer;
        _mcpStats = mcpServer.Statistics;

        InitializeComponent();
        Icon = Program.GetApplicationIcon();

        // Inject configuration tabs for already-loaded modules and track module registry
        // changes so imports, enables, disables, and removals update the tabs live.
        _moduleHost.ModulesChanged += OnModulesChanged;
        AddModuleTabs();
        BuildHelpContent();
        AddModuleHelpPages();

        // Virtual mode materializes only the visible rows (plus a small buffer) instead of
        // creating a ListViewItem for every log entry on each refresh.
        _lstLogs.VirtualMode = true;
        _lstLogs.RetrieveVirtualItem += LstLogs_RetrieveVirtualItem;
        _lstMcpLogs.VirtualMode = true;
        _lstMcpLogs.RetrieveVirtualItem += LstMcpLogs_RetrieveVirtualItem;

        _stats.StatsChanged += OnStatsChanged;
        _mcpStats.StatsChanged += OnMcpStatsChanged;
        _server.StatusChanged += OnServerStatusChanged;
        _perfService.Sampled += OnPerfSampled;
        _chkApiExplorer.CheckedChanged += (_, _) => UpdateApiExplorerUrlLabel();
        _lblApiExplorerUrl.Click += LblApiExplorerUrl_Click;
        _lblApiSpecUrl.Click += LblApiSpecUrl_Click;
        _lblMcpApiExplorerUrl.Click += LblMcpApiExplorerUrl_Click;
        _lblMcpSpecUrl.Click += LblMcpSpecUrl_Click;

        // Settings on the Settings tab persist immediately when changed; only the Listener
        // group (port/address) requires an explicit save because it needs a proxy restart.
        _txtMaxLogs.Validated += (_, _) => SaveGeneralSettings();
        _chkAutoStart.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkStartWithDashboard.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkRunAsAdmin.CheckedChanged += (_, _) => SaveGeneralSettings();
#if DEBUG
        // Debug builds never force elevation so the running instance stays attachable;
        // disable the control so it does not suggest otherwise.
        _chkRunAsAdmin.Enabled = false;
        _chkRunAsAdmin.Text += " (disabled in debug builds)";
#endif
        _chkCollectDetails.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkCollectResponseDetails.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkDebugMode.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkPerformanceSampling.CheckedChanged += (_, _) => SaveGeneralSettings();
        _chkApiExplorer.CheckedChanged += (_, _) => SaveGeneralSettings();
        _txtLogDir.Validated += (_, _) => SaveLoggingSettings();
        _cmbMinLevel.SelectedIndexChanged += (_, _) => SaveLoggingSettings();
        _txtAppLogSize.Validated += (_, _) => SaveLoggingSettings();
        _txtAppLogRetain.Validated += (_, _) => SaveLoggingSettings();
        _txtReqLogSize.Validated += (_, _) => SaveLoggingSettings();
        _txtRequestDbPath.Validated += (_, _) => SaveLoggingSettings();
        _txtLogRetention.Validated += (_, _) => SaveLoggingSettings();

        // MCP tab settings persist and restart the server immediately, like the Settings tab.
        _mcpServer.StatusChanged += OnMcpStatusChanged;
        _chkMcpEnabled.CheckedChanged += (_, _) => OnMcpSettingChanged();
        _chkMcpApiExplorer.CheckedChanged += (_, _) => UpdateMcpApiExplorerUrlLabel();
        _chkMcpApiExplorer.CheckedChanged += (_, _) => OnMcpSettingChanged();
        _chkMcpCollectRequest.CheckedChanged += (_, _) => OnMcpSettingChanged();
        _chkMcpCollectResponse.CheckedChanged += (_, _) => OnMcpSettingChanged();
        _nudMcpPort.Validated += (_, _) => OnMcpSettingChanged();
        _cboMcpListenAddress.SelectedIndexChanged += (_, _) => OnMcpSettingChanged();
        _cboMcpListenAddress.Validated += (_, _) => OnMcpSettingChanged();
        _btnMcpApply.Click += (_, _) => OnMcpSettingChanged();

        // System Logs tab
        _cboSysLogLevel.Items.Add("(All)");
        _cboSysLogLevel.Items.Add("Verbose");
        _cboSysLogLevel.Items.Add("Debug");
        _cboSysLogLevel.Items.Add("Information");
        _cboSysLogLevel.Items.Add("Warning");
        _cboSysLogLevel.Items.Add("Error");
        _cboSysLogLevel.Items.Add("Fatal");
        _cboSysLogLevel.SelectedIndex = 0;
        _cboSysLogLevel.SelectedIndexChanged += (_, _) => RefreshSystemLogs();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadSettingsToForm();
        RefreshStatus();
        RefreshStats();
        RefreshMcpStats();
        RefreshLogs();
        RefreshMcpLogs();
        RefreshSystemLogs();
        RefreshHeartbeats();
        RefreshCredentials();
        RefreshModules();
        LoadMcpSettingsToForm();
        UpdateMcpStatusDisplays();
        _stats.HeartbeatsChanged += OnHeartbeatsChanged;
        _cmbRefreshInterval.SelectedIndex = 1; // default: 2 s
        _refreshTimer.Start();
        _tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide to tray instead of closing when user clicks X.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            MinimizedToTray?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _stats.StatsChanged -= OnStatsChanged;
        _mcpStats.StatsChanged -= OnMcpStatsChanged;
        _stats.HeartbeatsChanged -= OnHeartbeatsChanged;
        _server.StatusChanged -= OnServerStatusChanged;
        _perfService.Sampled -= OnPerfSampled;
        _moduleHost.ModulesChanged -= OnModulesChanged;
        _mcpServer.StatusChanged -= OnMcpStatusChanged;
        base.OnFormClosed(e);
    }

    // ── MCP tab ─────────────────────────────────────────────────────────────

    private void LoadMcpSettingsToForm()
    {
        _loadingSettings = true;
        try
        {
            McpServerSettings settings = _mcpServer.LoadSettings();

            _chkMcpEnabled.Checked = settings.Enabled;
            _chkMcpApiExplorer.Checked = settings.EnableApiExplorer;
            _chkMcpCollectRequest.Checked = settings.CollectRequestDetails;
            _chkMcpCollectResponse.Checked = settings.CollectResponseDetails;
            _nudMcpPort.Value = Math.Clamp(settings.ListenPort, (int)_nudMcpPort.Minimum, (int)_nudMcpPort.Maximum);
            PopulateListenAddressOptions(_cboMcpListenAddress, settings.ListenAddress);
            UpdateMcpApiExplorerUrlLabel();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveMcpSettingsFromForm()
    {
        McpServerSettings settings = new()
        {
            Enabled = _chkMcpEnabled.Checked,
            ListenPort = (int)_nudMcpPort.Value,
            ListenAddress = _cboMcpListenAddress.Text.Trim(),
            EnableApiExplorer = _chkMcpApiExplorer.Checked,
            CollectRequestDetails = _chkMcpCollectRequest.Checked,
            CollectResponseDetails = _chkMcpCollectResponse.Checked,
            AuthCredentialName = null,
        };

        _mcpServer.SaveSettings(settings);
    }

    private void OnMcpSettingChanged()
    {
        if (_loadingSettings)
            return;

        UpdateMcpApiExplorerUrlLabel();
        SaveMcpSettingsFromForm();
        ApplyMcpServerSettingsAsync();
    }

    private async void ApplyMcpServerSettingsAsync()
    {
        if (_applyingMcpSettings)
        {
            _mcpSettingsApplyPending = true;
            return;
        }

        _applyingMcpSettings = true;
        _btnMcpApply.Enabled = false;

        try
        {
            do
            {
                _mcpSettingsApplyPending = false;

                try
                {
                    await _mcpServer.ApplySettingsAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to apply MCP server settings");
                }
            } while (_mcpSettingsApplyPending);
        }
        finally
        {
            _applyingMcpSettings = false;
            _btnMcpApply.Enabled = true;
            UpdateMcpStatusDisplays();
        }
    }

    private void OnMcpStatusChanged(object? sender, string status)
    {
        if (IsHandleCreated)
            BeginInvoke(UpdateMcpStatusDisplays);
        else
            UpdateMcpStatusDisplays();
    }

    private void UpdateMcpStatusLabel() => _lblMcpStatus.Text = _mcpServer.Status;

    /// <summary>
    /// Updates the MCP tab's status label and the dashboard MCP Status group from the
    /// service's current runtime state.
    /// </summary>
    private void UpdateMcpStatusDisplays()
    {
        UpdateMcpStatusLabel();
        RefreshMcpDashboardStatus();
    }

    /// <summary>
    /// Refreshes the dashboard MCP Status group: shows the status plus the address and port
    /// the MCP server is currently running on (fixed until the service restarts) and
    /// enables/disables the MCP control buttons accordingly.
    /// </summary>
    private void RefreshMcpDashboardStatus()
    {
        bool running = _mcpServer.IsRunning;
        string status = _mcpServer.Status;

        _lblDashMcpStatusValue.Text = running ? "Running" : status;
        _lblDashMcpStatusValue.ForeColor = running
            ? Color.Green
            : status.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                ? Color.Red
                : SystemColors.ControlText;

        _lblDashMcpAddressValue.Text = running ? _mcpServer.ListenAddress : "-";
        _lblDashMcpPortValue.Text = running ? _mcpServer.ListenPort.ToString() : "-";

        _btnDashMcpStart.Enabled = !running && !_mcpDashOperationInProgress;
        _btnDashMcpStop.Enabled = running && !_mcpDashOperationInProgress;
        _btnDashMcpRestart.Enabled = running && !_mcpDashOperationInProgress;
    }

    /// <summary>
    /// Builds the MCP API Explorer (Scalar) URL from the form's listen address and port,
    /// substituting localhost for wildcard bind addresses.
    /// </summary>
    private string BuildMcpApiExplorerUrl() => BuildMcpUrl(McpServerHost.ScalarPath);

    /// <summary>
    /// Builds the MCP OpenAPI specification (JSON) URL from the form's listen address and
    /// port, substituting localhost for wildcard bind addresses.
    /// </summary>
    private string BuildMcpSpecUrl() => BuildMcpUrl(McpServerHost.SpecPath);

    private string BuildMcpUrl(string path)
    {
        string host = _cboMcpListenAddress.Text.Trim();
        if (host is "" or "*" or "0.0.0.0" or "+" or "::" or "[::]")
            host = "localhost";

        return $"http://{host}:{(int)_nudMcpPort.Value}{path}";
    }

    /// <summary>
    /// Updates the MCP API Explorer and OpenAPI spec URL note labels based on the current
    /// enable state, listen address, and port.
    /// </summary>
    private void UpdateMcpApiExplorerUrlLabel()
    {
        if (!_chkMcpApiExplorer.Checked)
        {
            _lblMcpApiExplorerUrl.Text = "API Explorer URL: (enable to see URL)";
            _lblMcpApiExplorerUrl.ForeColor = SystemColors.GrayText;
            _lblMcpApiExplorerUrl.Cursor = Cursors.Default;
            _lblMcpSpecUrl.Text = "OpenAPI Spec URL: (enable to see URL)";
            _lblMcpSpecUrl.ForeColor = SystemColors.GrayText;
            _lblMcpSpecUrl.Cursor = Cursors.Default;
            return;
        }

        _lblMcpApiExplorerUrl.Text = $"API Explorer URL: {BuildMcpApiExplorerUrl()}";
        _lblMcpApiExplorerUrl.ForeColor = SystemColors.Highlight;
        _lblMcpApiExplorerUrl.Cursor = Cursors.Hand;
        _lblMcpSpecUrl.Text = $"OpenAPI Spec URL: {BuildMcpSpecUrl()}";
        _lblMcpSpecUrl.ForeColor = SystemColors.Highlight;
        _lblMcpSpecUrl.Cursor = Cursors.Hand;
    }

    private void LblMcpApiExplorerUrl_Click(object? sender, EventArgs e)
    {
        if (_chkMcpApiExplorer.Checked)
            OpenUrlInBrowser(BuildMcpApiExplorerUrl());
    }

    private void LblMcpSpecUrl_Click(object? sender, EventArgs e)
    {
        if (_chkMcpApiExplorer.Checked)
            OpenUrlInBrowser(BuildMcpSpecUrl());
    }

    // ── Status ──────────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the dashboard Proxy Status group: shows the status plus the address and port
    /// the proxy is currently bound to (fixed until the proxy restarts) and enables/disables
    /// the proxy control buttons accordingly.
    /// </summary>
    private void RefreshStatus()
    {
        bool running = _server.IsRunning;
        _lblStatusValue.Text = running ? "Running" : "Stopped";
        _lblStatusValue.ForeColor = running ? Color.Green : Color.Red;
        _lblStatusAddressValue.Text = running ? _server.ListenAddress : "-";
        _lblStatusPortValue.Text = running ? _server.ListenPort.ToString() : "-";
        _btnStart.Enabled = !running && !_proxyOperationInProgress;
        _btnStop.Enabled = running && !_proxyOperationInProgress;
        _btnRestart.Enabled = running && !_proxyOperationInProgress;
    }

    /// <summary>
    /// Builds the proxy API Explorer (Scalar) URL from the persisted listener settings,
    /// substituting localhost for wildcard bind addresses.
    /// </summary>
    private string BuildApiExplorerUrl() => BuildProxyUrl("/scalar");

    /// <summary>
    /// Builds the proxy OpenAPI specification (JSON) URL from the persisted listener settings,
    /// substituting localhost for wildcard bind addresses.
    /// </summary>
    private string BuildApiSpecUrl() => BuildProxyUrl("/openapi/v1/openapi.json");

    private string BuildProxyUrl(string path)
    {
        string host = _settings.ListenAddress.Trim();
        if (host is "0.0.0.0" or "+" or "")
            host = "localhost";

        return $"http://{host}:{_settings.ListenPort}{path}";
    }

    /// <summary>
    /// Updates the API Explorer and OpenAPI spec URL note labels based on the current enable
    /// state, listen address, and port.
    /// </summary>
    private void UpdateApiExplorerUrlLabel()
    {
        if (!_chkApiExplorer.Checked)
        {
            _lblApiExplorerUrl.Text = "API Explorer URL: (enable to see URL)";
            _lblApiExplorerUrl.ForeColor = SystemColors.GrayText;
            _lblApiExplorerUrl.Cursor = Cursors.Default;
            _lblApiSpecUrl.Text = "OpenAPI Spec URL: (enable to see URL)";
            _lblApiSpecUrl.ForeColor = SystemColors.GrayText;
            _lblApiSpecUrl.Cursor = Cursors.Default;
            return;
        }

        _lblApiExplorerUrl.Text = $"API Explorer URL: {BuildApiExplorerUrl()}";
        _lblApiExplorerUrl.ForeColor = SystemColors.Highlight;
        _lblApiExplorerUrl.Cursor = Cursors.Hand;
        _lblApiSpecUrl.Text = $"OpenAPI Spec URL: {BuildApiSpecUrl()}";
        _lblApiSpecUrl.ForeColor = SystemColors.Highlight;
        _lblApiSpecUrl.Cursor = Cursors.Hand;
    }

    private void LblApiExplorerUrl_Click(object? sender, EventArgs e)
    {
        if (_chkApiExplorer.Checked)
            OpenUrlInBrowser(BuildApiExplorerUrl());
    }

    private void LblApiSpecUrl_Click(object? sender, EventArgs e)
    {
        if (_chkApiExplorer.Checked)
            OpenUrlInBrowser(BuildApiSpecUrl());
    }

    /// <summary>Opens the given URL in the system's default browser.</summary>
    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warning(ex, "Failed to open the default browser for {Url}", url);
            MessageBox.Show($"Could not open the default browser: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnServerStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStatus);
            return;
        }
        RefreshStatus();
    }

    /// <summary>
    /// Runs a synchronous button operation with the button disabled so a click re-entering
    /// through a MessageBox message pump cannot retrigger it.
    /// </summary>
    private void RunOnceWhileDisabled(Button button, Action operation)
    {
        if (!button.Enabled)
            return;

        button.Enabled = false;
        try
        {
            operation();
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (_proxyOperationInProgress)
            return;

        _proxyOperationInProgress = true;
        RefreshStatus();

        try
        {
            _server.Start(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _proxyOperationInProgress = false;
            RefreshStatus();
        }
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        if (_proxyOperationInProgress)
            return;

        _proxyOperationInProgress = true;
        RefreshStatus();

        try
        {
            await _server.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error stopping: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _proxyOperationInProgress = false;
            RefreshStatus();
        }
    }

    private async void BtnRestart_Click(object? sender, EventArgs e)
    {
        if (_proxyOperationInProgress)
            return;

        _proxyOperationInProgress = true;
        RefreshStatus();

        try
        {
            await _server.RestartAsync(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error restarting: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _proxyOperationInProgress = false;
            RefreshStatus();
        }
    }

    private async void BtnDashMcpStart_Click(object? sender, EventArgs e)
    {
        if (_mcpDashOperationInProgress)
            return;

        _mcpDashOperationInProgress = true;
        RefreshMcpDashboardStatus();

        try
        {
            await _mcpServer.StartAsync(forceStart: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start the MCP server from the dashboard");
            MessageBox.Show($"Failed to start the MCP server: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _mcpDashOperationInProgress = false;
            RefreshMcpDashboardStatus();
        }
    }

    private async void BtnDashMcpStop_Click(object? sender, EventArgs e)
    {
        if (_mcpDashOperationInProgress)
            return;

        _mcpDashOperationInProgress = true;
        RefreshMcpDashboardStatus();

        try
        {
            await _mcpServer.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop the MCP server from the dashboard");
            MessageBox.Show($"Error stopping the MCP server: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _mcpDashOperationInProgress = false;
            RefreshMcpDashboardStatus();
        }
    }

    private async void BtnDashMcpRestart_Click(object? sender, EventArgs e)
    {
        if (_mcpDashOperationInProgress)
            return;

        _mcpDashOperationInProgress = true;
        RefreshMcpDashboardStatus();

        try
        {
            await _mcpServer.RestartAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart the MCP server from the dashboard");
            MessageBox.Show($"Error restarting the MCP server: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _mcpDashOperationInProgress = false;
            RefreshMcpDashboardStatus();
        }
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    private void RefreshStats()
    {
        _lblTotalRequestsValue.Text = _stats.TotalRequests.ToString("N0");
        _lblTotalErrorsValue.Text = _stats.TotalErrors.ToString("N0");
        _lblPromptTokensValue.Text = _stats.TotalPromptTokens.ToString("N0");
        _lblCompletionTokensValue.Text = _stats.TotalCompletionTokens.ToString("N0");
        _lblRpsValue.Text = _stats.RequestsPerSecond.ToString("F2");
    }

    private void OnStatsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStats);
            return;
        }
        RefreshStats();
    }

    private void RefreshMcpStats()
    {
        _lblMcpTotalRequestsValue.Text = _mcpStats.TotalRequests.ToString("N0");
        _lblMcpTotalErrorsValue.Text = _mcpStats.TotalErrors.ToString("N0");
        _lblMcpRpsValue.Text = _mcpStats.RequestsPerSecond.ToString("F2");
    }

    private void OnMcpStatsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(RefreshMcpStats);
            return;
        }
        RefreshMcpStats();
    }

    private void OnPerfSampled(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(UpdatePerfLabels);
            return;
        }
        UpdatePerfLabels();
    }

    private void UpdatePerfLabels()
    {
        _lblCpuValue.Text = $"{_perfService.CpuPercent:F1}%";
        _lblRamValue.Text = $"{_perfService.MemoryMb:F0} MB";
    }

    private void BtnResetStats_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnResetStats, () =>
        {
            _stats.Reset();
            RefreshStats();
            RefreshLogs();
        });

    private void BtnResetMcpStats_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnResetMcpStats, () =>
        {
            _mcpStats.Reset();
            RefreshMcpStats();
            RefreshMcpLogs();
        });

    // ── Logs ─────────────────────────────────────────────────────────────────

    private void RefreshLogs()
    {
        _logCache = _stats.GetRecentLogs();
        _lstLogs.VirtualListSize = _logCache.Count;
        _lstLogs.Invalidate();
    }

    private void RefreshMcpLogs()
    {
        _mcpLogCache = _mcpStats.GetRecentLogs();
        _lstMcpLogs.VirtualListSize = _mcpLogCache.Count;
        _lstMcpLogs.Invalidate();
    }

    private void LstLogs_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _logCache.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        RequestLog log = _logCache[e.ItemIndex];
        var item = new ListViewItem(log.Timestamp.ToString("M/d HH:mm:ss"));
        item.SubItems.Add(log.Method);
        item.SubItems.Add(log.OllamaPath);
        item.SubItems.Add(log.Model);
        item.SubItems.Add(log.Status.ToString());
        item.SubItems.Add($"{log.DurationMs:F0}");
        item.SubItems.Add(log.PromptTokens > 0 ? log.PromptTokens.ToString() : string.Empty);
        item.SubItems.Add(log.CompletionTokens > 0 ? log.CompletionTokens.ToString() : string.Empty);
        item.SubItems.Add(log.ReasoningTokens > 0 ? log.ReasoningTokens.ToString() : string.Empty);
        item.SubItems.Add(log.CachedPromptTokens > 0 ? log.CachedPromptTokens.ToString() : string.Empty);
        item.SubItems.Add(log.DraftN > 0 ? $"{(double)log.DraftNAccepted / log.DraftN * 100:F0}%" : string.Empty);
        item.SubItems.Add(FormatBytes(log.RequestBytes, log.ResponseBytes));
        item.Tag = log;

        item.ForeColor = log.Status switch
        {
            RequestStatus.Error => Color.Red,
            RequestStatus.Cancelled => Color.DarkOrange,
            _ => SystemColors.WindowText,
        };

        e.Item = item;
    }

    private void LstMcpLogs_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _mcpLogCache.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        RequestLog log = _mcpLogCache[e.ItemIndex];
        var item = new ListViewItem(log.Timestamp.ToString("M/d HH:mm:ss"));
        item.SubItems.Add(log.Method);
        item.SubItems.Add(log.OllamaPath);
        item.SubItems.Add(log.Status.ToString());
        item.SubItems.Add($"{log.DurationMs:F0}");
        item.SubItems.Add(FormatBytes(log.RequestBytes, log.ResponseBytes));
        item.Tag = log;

        item.ForeColor = log.Status switch
        {
            RequestStatus.Error => Color.Red,
            RequestStatus.Cancelled => Color.DarkOrange,
            _ => SystemColors.WindowText,
        };

        e.Item = item;
    }

    private void BtnClearLogs_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnClearLogs, () =>
        {
            if (_logSubTabs.SelectedTab == _logMcpPage)
            {
                _mcpStats.ClearLogs();
                _mcpLogCache = [];
                _lstMcpLogs.VirtualListSize = 0;
                _lstMcpLogs.Invalidate();
            }
            else if (_logSubTabs.SelectedTab == _tabSysLogs)
            {
                _database.ClearSystemLogs();
                AppLogger.SysLog.Clear();
                RefreshSystemLogs();
            }
            else
            {
                _stats.ClearLogs();
                _logCache = [];
                _lstLogs.VirtualListSize = 0;
                _lstLogs.Invalidate();
            }
        });

    private void BtnRefreshLogs_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnRefreshLogs, RefreshActiveLogTab);

    private void BtnLogDetails_Click(object? sender, EventArgs e)
    {
        if (_logSubTabs.SelectedTab == _logMcpPage)
            ShowSelectedLogDetails(_lstMcpLogs, _mcpLogCache, LogSource.Mcp);
        else if (_logSubTabs.SelectedTab == _tabSysLogs)
            ShowSelectedSysLogDetails();
        else
            ShowSelectedLogDetails(_lstLogs, _logCache, LogSource.Proxy);
    }

    private void ShowSelectedSysLogDetails()
    {
        if (_lstSysLogs.SelectedItems.Count == 0)
        {
            MessageBox.Show("Select a log entry first.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_lstSysLogs.SelectedItems[0].Tag is not SystemLogEntry entry)
            return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Timestamp : {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Level     : {entry.Level}");
        sb.AppendLine($"Source    : {entry.SourceContext ?? "—"}");
        sb.AppendLine();
        sb.AppendLine("── Message ──────────────────────────────────────────────────────");
        sb.AppendLine(entry.Message);
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            sb.AppendLine();
            sb.AppendLine("── Exception ──────────────────────────────────────────────────");
            sb.AppendLine(entry.Exception);
        }

        using var form = new Form
        {
            Text = $"System Log — {entry.Timestamp:HH:mm:ss} [{entry.Level}]",
            Size = new Size(780, 540),
            MinimumSize = new Size(500, 300),
            MaximizeBox = true,
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
        };
        form.Controls.Add(CreateLogDetailsTextBox(sb.ToString()));
        form.ShowDialog(this);
    }

    private void LstSysLogs_DoubleClick(object? sender, EventArgs e) =>
        ShowSelectedSysLogDetails();

    private void ShowSelectedLogDetails(ListView list, IReadOnlyList<RequestLog> cache, LogSource source)
    {
        if (list.SelectedIndices.Count == 0)
        {
            MessageBox.Show("Select a log entry first.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int index = list.SelectedIndices[0];
        if (index >= 0 && index < cache.Count)
            ShowLogDetails(cache[index], source);
    }

    private void LstLogs_DoubleClick(object? sender, EventArgs e) =>
        ShowSelectedLogDetails(_lstLogs, _logCache, LogSource.Proxy);

    private void LstMcpLogs_DoubleClick(object? sender, EventArgs e) =>
        ShowSelectedLogDetails(_lstMcpLogs, _mcpLogCache, LogSource.Mcp);

    private void ShowLogDetails(RequestLog log, LogSource source)
    {
        // The in-memory entry is a lightweight summary without request/response bodies.
        // Load the full entry from SQLite on demand so large bodies stay out of memory.
        RequestLog? full = _database.LoadFullLogEntry(log.Timestamp, source);
        if (full is null)
        {
            MessageBox.Show("Log entry not found in the database. It may have been pruned.",
                "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        log = full;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Timestamp : {log.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Method    : {log.Method}");
        sb.AppendLine($"Path      : {log.OllamaPath}");
        if (source == LogSource.Proxy)
        {
            sb.AppendLine($"Upstream  : {log.UpstreamPath}");
            sb.AppendLine($"Model     : {log.Model}");
        }
        sb.AppendLine($"Status    : {log.Status} ({log.StatusCode})");
        if (source == LogSource.Proxy)
            sb.AppendLine($"Streaming : {log.Streaming}");
        sb.AppendLine($"Duration  : {log.DurationMs:F1} ms");
        if (source == LogSource.Proxy)
        {
            sb.AppendLine($"Tokens    : {log.PromptTokens} prompt + {log.CompletionTokens} completion (total {log.TotalTokens})");
            sb.AppendLine($"            {log.CachedPromptTokens} cached prompt, {log.ReasoningTokens} reasoning");
        }
        sb.AppendLine($"Bytes     : {FormatBytes(log.RequestBytes, log.ResponseBytes)} (request / response)");

        if (!string.IsNullOrEmpty(log.DebugSummary))
        {
            sb.AppendLine();
            sb.AppendLine("── Debug: applied overrides & transformations ────────────────");
            sb.AppendLine(log.DebugSummary);
        }

        if (!string.IsNullOrEmpty(log.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("── Error ──────────────────────────────────────────────────────");
            sb.AppendLine(log.ErrorMessage);
        }

        if (log.ExceptionId.HasValue)
        {
            ExceptionDetail? ex = _stats.GetException(log.ExceptionId.Value);
            if (ex is not null)
            {
                sb.AppendLine();
                sb.AppendLine("── Exception ──────────────────────────────────────────────────");
                sb.AppendLine($"Type    : {ex.ExceptionType}");
                sb.AppendLine($"Message : {ex.Message}");

                if (ex.InnerExceptions.Count > 0)
                {
                    sb.AppendLine("Inner   :");
                    foreach (string inner in ex.InnerExceptions)
                        sb.AppendLine($"  {inner}");
                }

                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    sb.AppendLine();
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(ex.StackTrace);
                }
            }
        }

        using var detailForm = new Form
        {
            Text = $"Log Details — {log.Timestamp:HH:mm:ss} {log.OllamaPath}",
            Size = new Size(780, 540),
            MinimumSize = new Size(500, 300),
            MaximizeBox = true,
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
        };

        TabControl tabControl = new()
        {
            Dock = DockStyle.Fill,
            Name = "_tabLogDetails",
        };

        TabPage summaryTab = new()
        {
            Name = "_tabLogSummary",
            Padding = new Padding(8),
            Text = "Summary",
        };

        TextBox summaryText = CreateLogDetailsTextBox(sb.ToString());
        summaryTab.Controls.Add(summaryText);
        tabControl.Controls.Add(summaryTab);

        if (log.RequestBody is not null)
        {
            TabPage requestTab = new()
            {
                Name = "_tabLogRequestBody",
                Padding = new Padding(8),
                Text = "Request Body",
            };

            requestTab.Controls.Add(CreateLogDetailsTextBox(FormatBody(log.RequestBody)));
            tabControl.Controls.Add(requestTab);
        }

        if (log.UpstreamRequestBody is not null)
        {
            TabPage upstreamTab = new()
            {
                Name = "_tabLogUpstreamRequestBody",
                Padding = new Padding(8),
                Text = "Upstream Request Body",
            };

            upstreamTab.Controls.Add(CreateLogDetailsTextBox(FormatBody(log.UpstreamRequestBody)));
            tabControl.Controls.Add(upstreamTab);
        }

        if (log.UpstreamResponseBody is not null)
        {
            TabPage upstreamResponseTab = new()
            {
                Name = "_tabLogUpstreamResponseBody",
                Padding = new Padding(8),
                Text = "Upstream Response Body (OpenAI)",
            };

            upstreamResponseTab.Controls.Add(CreateLogDetailsTextBox(FormatBody(log.UpstreamResponseBody)));
            tabControl.Controls.Add(upstreamResponseTab);
        }

        if (log.ResponseBody is not null)
        {
            TabPage responseTab = new()
            {
                Name = "_tabLogResponseBody",
                Padding = new Padding(8),
                Text = "Response Body (Ollama)",
            };

            TextBox responseText = CreateLogDetailsTextBox(FormatBody(log.ResponseBody));
            responseTab.Controls.Add(responseText);
            tabControl.Controls.Add(responseTab);
        }

        detailForm.Controls.Add(tabControl);
        detailForm.ShowDialog(this);
    }

    private static TextBox CreateLogDetailsTextBox(string text)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9F),
            Text = text,
        };
    }

    private static string FormatBytes(long requestBytes, long responseBytes)
    {
        static string Fmt(long b) => b switch
        {
            < 0 => "?",
            < 1024 => $"{b} B",
            < 1024 * 1024 => $"{b / 1024.0:F1} KB",
            _ => $"{b / (1024.0 * 1024):F2} MB",
        };
        return $"{Fmt(requestBytes)} / {Fmt(responseBytes)}";
    }

    private static string FormatBody(string body)
    {
        if (TryFormatJson(body, out string? formattedJson))
            return formattedJson;

        if (LooksLikeServerSentEvents(body))
            return FormatServerSentEvents(body);

        return body;
    }

    private static bool TryFormatJson(string body, out string formattedJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            formattedJson = JsonSerializer.Serialize(doc, _indentedJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            formattedJson = string.Empty;
            return false;
        }
    }

    private static bool LooksLikeServerSentEvents(string body)
    {
        ReadOnlySpan<char> span = body.AsSpan().TrimStart();
        return span.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("retry:", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatServerSentEvents(string body)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(body);

        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                string payload = line[5..].TrimStart();
                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    sb.AppendLine("data: [DONE]");
                    continue;
                }

                if (TryFormatJson(payload, out string? formattedPayload))
                {
                    sb.AppendLine("data:");
                    using var payloadReader = new StringReader(formattedPayload);
                    while (payloadReader.ReadLine() is string payloadLine)
                        sb.Append("  ").AppendLine(payloadLine);
                    continue;
                }
            }

            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    // ── System Logs tab ─────────────────────────────────────────────────────

    private void RefreshSystemLogs()
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            string? levelFilter = _cboSysLogLevel.SelectedIndex > 0
                ? _cboSysLogLevel.Items[_cboSysLogLevel.SelectedIndex].ToString()
                : null;

            IReadOnlyList<SystemLogEntry> entries = _database.GetSystemLogs(levelFilter, 500);

            bool dbHealthy = AppLogger.DbSink?.IsUsingDatabase ?? true;
            _lblSysLogStatus.Text = dbHealthy
                ? $"Database ({entries.Count} entries)"
                : "Fallback file active (DB unavailable)";
            _lblSysLogStatus.ForeColor = dbHealthy ? SystemColors.ControlText : Color.OrangeRed;

            _lstSysLogs.BeginUpdate();
            _lstSysLogs.Items.Clear();

            foreach (SystemLogEntry entry in entries)
            {
                var item = new ListViewItem(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(entry.Level);
                item.SubItems.Add(entry.Message);
                item.SubItems.Add(entry.SourceContext ?? "");

                item.ForeColor = entry.Level switch
                {
                    "Error" or "Fatal" => Color.OrangeRed,
                    "Warning" => Color.DarkOrange,
                    _ => SystemColors.ControlText,
                };
                item.Tag = entry;

                _lstSysLogs.Items.Add(item);
            }

            _lstSysLogs.EndUpdate();
        }
        catch (Exception ex)
        {
            _lblSysLogStatus.Text = $"Error: {ex.Message}";
            _lblSysLogStatus.ForeColor = Color.OrangeRed;
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_tabControl.SelectedTab == _tabLogs && _chkAutoRefresh.Checked)
            RefreshActiveLogTab();
    }

    private void RefreshActiveLogTab()
    {
        if (_logSubTabs.SelectedTab == _logMcpPage)
            RefreshMcpLogs();
        else if (_logSubTabs.SelectedTab == _tabSysLogs)
            RefreshSystemLogs();
        else
            RefreshLogs();
    }

    private void LogSubTabs_SelectionChanged(object? sender, EventArgs e) =>
        RefreshActiveLogTab();

    // ── Heartbeats tab ──────────────────────────────────────────────────────

    private void OnHeartbeatsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(RefreshHeartbeats);
            return;
        }
        RefreshHeartbeats();
    }

    private void RefreshHeartbeats()
    {
        Dictionary<string, HeartbeatSnapshot> snapshots = _stats.GetHeartbeatStats()
            .ToDictionary(s => s.Model, StringComparer.OrdinalIgnoreCase);
        List<HeartbeatDisplayRow> rows = [];

        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            string modelName = string.IsNullOrWhiteSpace(mapping.ProxyName)
                ? mapping.ModelName
                : mapping.ProxyName;

            if (string.IsNullOrWhiteSpace(modelName))
                continue;

            snapshots.TryGetValue(mapping.ProxyName, out HeartbeatSnapshot? proxySnapshot);
            snapshots.TryGetValue(mapping.ModelName, out HeartbeatSnapshot? modelSnapshot);
            HeartbeatSnapshot? snapshot = (proxySnapshot?.Count ?? 0) >= (modelSnapshot?.Count ?? 0)
                ? proxySnapshot
                : modelSnapshot;

            if (!string.IsNullOrWhiteSpace(mapping.ProxyName))
                snapshots.Remove(mapping.ProxyName);
            if (!string.IsNullOrWhiteSpace(mapping.ModelName))
                snapshots.Remove(mapping.ModelName);

            rows.Add(new HeartbeatDisplayRow(
                modelName,
                mapping.IsEnabled && mapping.EnableHeartbeats && _settings.EnableStreamingHeartbeats,
                snapshot?.Attempts ?? 0,
                snapshot?.Count ?? 0,
                snapshot?.Failures ?? 0,
                snapshot?.LastAttemptUtc ?? default,
                snapshot?.LastSentUtc ?? default,
                snapshot?.LastStatus ?? "Not checked",
                snapshot?.LastError ?? string.Empty));
        }

        rows.AddRange(snapshots.Values.Select(s => new HeartbeatDisplayRow(
            s.Model,
            true,
            s.Attempts,
            s.Count,
            s.Failures,
            s.LastAttemptUtc,
            s.LastSentUtc,
            s.LastStatus,
            s.LastError)));

        _lstHeartbeats.BeginUpdate();
        _lstHeartbeats.Items.Clear();

        foreach (HeartbeatDisplayRow row in rows
            .OrderByDescending(r => r.LastSentUtc)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
        {
            ListViewItem item = new(row.Model);
            item.SubItems.Add(row.Enabled ? "Yes" : "No");
            item.SubItems.Add(row.LastStatus);
            item.SubItems.Add(row.Attempts.ToString("N0"));
            item.SubItems.Add(row.Count.ToString("N0"));
            item.SubItems.Add(row.Failures.ToString("N0"));
            item.SubItems.Add(row.LastAttemptUtc == default
                ? "—"
                : row.LastAttemptUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(row.LastSentUtc == default
                ? "—"
                : row.LastSentUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(string.IsNullOrWhiteSpace(row.LastError) ? "—" : row.LastError);
            if (!row.Enabled)
                item.ForeColor = SystemColors.GrayText;
            else if (row.Failures > 0 && row.Count == 0)
                item.ForeColor = Color.Firebrick;
            else
                item.ForeColor = SystemColors.WindowText;
            _lstHeartbeats.Items.Add(item);
        }

        _lstHeartbeats.EndUpdate();
    }

    private void BtnSaveHeartbeats_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnSaveHeartbeats, () =>
        {
            if (!int.TryParse(_txtHeartbeatInterval.Text, out int heartbeatIntervalSeconds)
                || heartbeatIntervalSeconds < 5
                || heartbeatIntervalSeconds > 300)
            {
                MessageBox.Show("Heartbeat interval must be a number between 5 and 300 seconds.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.EnableStreamingHeartbeats = _chkStreamingHeartbeats.Checked;
            _settings.StreamingHeartbeatIntervalSeconds = heartbeatIntervalSeconds;
            _settings.Save();
            _handler.UpdateSettings(_settings);
            RefreshHeartbeats();

            MessageBox.Show("Heartbeat settings saved.", "Saved",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

    private void BtnResetHeartbeats_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnResetHeartbeats, () =>
        {
            _stats.ResetHeartbeats();
            RefreshHeartbeats();
        });

    private readonly struct HeartbeatDisplayRow
    {
        public HeartbeatDisplayRow(
            string model,
            bool enabled,
            long attempts,
            long count,
            long failures,
            DateTime lastAttemptUtc,
            DateTime lastSentUtc,
            string lastStatus,
            string lastError)
        {
            Model = model;
            Enabled = enabled;
            Attempts = attempts;
            Count = count;
            Failures = failures;
            LastAttemptUtc = lastAttemptUtc;
            LastSentUtc = lastSentUtc;
            LastStatus = lastStatus;
            LastError = lastError;
        }

        public readonly string Model;
        /// <summary>
        /// Effective heartbeat-enabled state: the model is enabled in settings, heartbeats are
        /// enabled for that model, and the global streaming-heartbeat switch is on. Any of
        /// these being false makes this model not send heartbeats.
        /// </summary>
        public readonly bool Enabled;
        public readonly long Attempts;
        public readonly long Count;
        public readonly long Failures;
        public readonly DateTime LastAttemptUtc;
        public readonly DateTime LastSentUtc;
        public readonly string LastStatus;
        public readonly string LastError;
    }

    private void CmbRefreshInterval_SelectedIndexChanged(object? sender, EventArgs e)
    {
        int intervalMs = _cmbRefreshInterval.SelectedIndex switch
        {
            0 => 1_000,
            1 => 2_000,
            2 => 5_000,
            3 => 10_000,
            4 => 30_000,
            _ => 2_000,
        };
        _refreshTimer.Interval = intervalMs;
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the Listen Address dropdown with "all interfaces" (0.0.0.0), "localhost", and
    /// every non-loopback IPv4/IPv6 address currently assigned to the machine, then selects
    /// the value that matches the current setting (or adds it as a custom entry if it's an
    /// address the dropdown doesn't otherwise enumerate, e.g. a specific NIC IP that changed).
    /// </summary>
    private void PopulateListenAddressOptions() =>
        PopulateListenAddressOptions(_cmbListenAddress, _settings.ListenAddress);

    /// <summary>
    /// Fills a listen-address dropdown with "all interfaces" (0.0.0.0), "localhost", and every
    /// non-loopback IPv4/IPv6 address currently assigned to the machine, then selects the given
    /// current value (adding it as a custom entry when not otherwise enumerated, e.g. a specific
    /// NIC IP that changed).
    /// </summary>
    private static void PopulateListenAddressOptions(ComboBox combo, string currentValue)
    {
        combo.Items.Clear();
        combo.Items.Add("0.0.0.0");
        combo.Items.Add("localhost");

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    if (addr.Address.IsIPv6LinkLocal)
                        continue;

                    string ip = addr.Address.ToString();
                    if (!combo.Items.Contains(ip))
                        combo.Items.Add(ip);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate local network interfaces for the Listen Address dropdown");
        }

        string current = string.IsNullOrWhiteSpace(currentValue) ? "localhost" : currentValue.Trim();
        if (!combo.Items.Contains(current))
            combo.Items.Add(current);

        combo.Text = current;
    }

    private void LoadSettingsToForm()
    {
        _loadingSettings = true;
        try
        {
        _txtListenPort.Text = _settings.ListenPort.ToString();
        PopulateListenAddressOptions();
        _txtMaxLogs.Text = _settings.MaxLogEntries.ToString();
        _chkAutoStart.Checked = _settings.AutoStartProxy;
        _chkStartWithDashboard.Checked = _settings.StartWithDashboardOpen;
        _chkRunAsAdmin.Checked = _settings.RunAsAdministrator;
        _chkCollectDetails.Checked = _settings.CollectRequestDetails;
        _chkCollectResponseDetails.Checked = _settings.CollectResponseDetails;
        _chkDebugMode.Checked = _settings.DebugMode;
        _chkPerformanceSampling.Checked = _settings.EnablePerformanceSampling;
        _chkApiExplorer.Checked = _settings.EnableApiExplorer;
        _chkStreamingHeartbeats.Checked = _settings.EnableStreamingHeartbeats;
        _txtHeartbeatInterval.Text = _settings.StreamingHeartbeatIntervalSeconds.ToString();

        _dgvMappings.Rows.Clear();
        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            // Carry the full per-row advanced configuration on the row Tag — these fields are
            // edited in the modal Configure dialog. Clone so no property is dropped when the
            // grid is rebuilt (e.g. after instruction-set edits).
            AddMappingRow(mapping.Clone());
        }

        // Load instructions list
        RefreshInstructionsList();

        // Logging settings
        _txtLogDir.Text = _settings.Logging.LogDirectory;
        int levelIndex = _cmbMinLevel.FindStringExact(_settings.Logging.MinimumLevel);
        _cmbMinLevel.SelectedIndex = levelIndex >= 0 ? levelIndex : 2; // default Information
        _txtAppLogSize.Text = _settings.Logging.AppLogFileSizeLimitMb.ToString();
        _txtAppLogRetain.Text = _settings.Logging.AppLogRetainedFileCount.ToString();
        _txtReqLogSize.Text = _settings.Logging.RequestLogFileSizeLimitMb.ToString();
        _txtRequestDbPath.Text = _settings.Logging.GetApplicationDatabasePath();
        _txtLogRetention.Text = _settings.Logging.LogRetentionHours.ToString();

        UpdateApiExplorerUrlLabel();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>
    /// Adds a mapping to the grid. Display columns show only summary values; the full
    /// per-model configuration is carried on the row Tag and edited via the Configure dialog.
    /// </summary>
    private int AddMappingRow(ModelMapping mapping)
    {
        int idx = _dgvMappings.Rows.Add(
            mapping.IsEnabled ? "Yes" : "No",
            mapping.ProxyName,
            mapping.ModelName,
            mapping.InstructionSetName ?? string.Empty,
            mapping.ReasoningEffort ?? string.Empty,
            mapping.Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase) ? "Yes" : "No");

        _dgvMappings.Rows[idx].Tag = mapping;
        return idx;
    }

    /// <summary>
    /// Saves the Listener group (port/address). These settings need an explicit save because a
    /// proxy restart is required for them to take effect; everything else on the Settings tab
    /// persists immediately when changed.
    /// </summary>
    private void BtnSaveListener_Click(object? sender, EventArgs e) =>
        RunOnceWhileDisabled(_btnSaveListener, () =>
        {
            if (!int.TryParse(_txtListenPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Listen port must be a number between 1 and 65535.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.ListenPort = port;
            _settings.ListenAddress = string.IsNullOrWhiteSpace(_cmbListenAddress.Text) ? "localhost" : _cmbListenAddress.Text.Trim();

            PersistSettingsCore();

            MessageBox.Show("Listener settings saved. Restart the proxy for the changes to take effect.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshStatus();
            UpdateApiExplorerUrlLabel();
        });

    /// <summary>
    /// Persists the immediately-saved general Settings tab options (everything except the
    /// listener group, model mappings, and logging group, which are saved separately).
    /// </summary>
    private void SaveGeneralSettings()
    {
        if (_loadingSettings)
            return;

        if (!int.TryParse(_txtMaxLogs.Text, out int maxLogs) || maxLogs < 1)
            return;

        _settings.MaxLogEntries = maxLogs;
        _settings.AutoStartProxy = _chkAutoStart.Checked;
        _settings.StartWithDashboardOpen = _chkStartWithDashboard.Checked;
        _settings.RunAsAdministrator = _chkRunAsAdmin.Checked;
        _settings.CollectRequestDetails = _chkCollectDetails.Checked;
        _settings.CollectResponseDetails = _chkCollectResponseDetails.Checked;
        _settings.DebugMode = _chkDebugMode.Checked;
        _settings.EnablePerformanceSampling = _chkPerformanceSampling.Checked;
        _settings.EnableApiExplorer = _chkApiExplorer.Checked;

        _stats.UpdateMaxEntries(maxLogs);
        _mcpStats.UpdateMaxEntries(maxLogs);
        _perfService.SetEnabled(_settings.EnablePerformanceSampling);

        PersistSettingsCore();
    }

    /// <summary>
    /// Persists the logging group values immediately when they change. Invalid or incomplete
    /// input is ignored (the control also keeps focus after a failed validation) so a partially
    /// typed value is never persisted.
    /// </summary>
    private void SaveLoggingSettings()
    {
        if (_loadingSettings)
            return;

        if (string.IsNullOrWhiteSpace(_txtLogDir.Text))
            return;

        string requestDbPath = _txtRequestDbPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(requestDbPath)
            || string.IsNullOrWhiteSpace(Path.GetDirectoryName(requestDbPath)))
            return;

        if (!int.TryParse(_txtAppLogSize.Text, out int appLogSize) || appLogSize < 1)
            return;

        if (!int.TryParse(_txtAppLogRetain.Text, out int appLogRetain) || appLogRetain < 1)
            return;

        if (!int.TryParse(_txtReqLogSize.Text, out int reqLogSize) || reqLogSize < 1)
            return;

        if (!int.TryParse(_txtLogRetention.Text, out int logRetentionHours) || logRetentionHours < 0)
            return;

        _settings.Logging.LogDirectory = _txtLogDir.Text.Trim();
        _settings.Logging.MinimumLevel = _cmbMinLevel.SelectedItem?.ToString() ?? "Information";
        _settings.Logging.AppLogFileSizeLimitMb = appLogSize;
        _settings.Logging.AppLogRetainedFileCount = appLogRetain;
        _settings.Logging.RequestLogFileSizeLimitMb = reqLogSize;
        _settings.Logging.ApplicationDatabasePath = requestDbPath;
        _settings.Logging.LogRetentionHours = logRetentionHours;

        _stats.UpdateRetentionHours(logRetentionHours);
        _mcpStats.UpdateRetentionHours(logRetentionHours);

        // Re-apply logging config immediately so the new level/size/dir is active.
        AppLogger.Initialize(_settings.Logging);

        PersistSettingsCore();
    }

    /// <summary>
    /// Writes the current in-memory settings, credentials, mappings, and instruction sets to
    /// their stores. Shared by the immediate-save paths and the listener save button.
    /// </summary>
    private void PersistSettingsCore()
    {
        // Encrypt credential secrets for persistence while keeping plaintext in memory for the
        // running proxy. Model mappings only reference credentials by name and carry no secrets.
        if (!TryEncryptCredentialsForSave(out List<StoredCredential> persistedCredentials))
            return;

        _database.SaveModelMappings(_settings.ModelMappings);
        _database.SaveCredentials(persistedCredentials);
        _database.SaveInstructionSets(_settings.InstructionSets);
        _database.SaveRuntimeSettings(_settings.CreateRuntimeSettings());
        _settings.Save();
        _handler.UpdateSettings(_settings);
    }

    /// <summary>
    /// Rebuilds <see cref="AppSettings.ModelMappings"/> from the mappings grid and persists the
    /// change immediately. Called whenever a mapping row is added, removed, duplicated, or
    /// edited. Shows a warning and leaves the previous mappings in place when the grid is invalid.
    /// </summary>
    private void CommitMappingsFromGrid()
    {
        if (_loadingSettings)
            return;

        if (!TryCommitMappings(out string? error))
        {
            MessageBox.Show(error, "Model Mappings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PersistSettingsCore();
    }

    /// <summary>
    /// Validates the mappings grid and, when valid, rebuilds
    /// <see cref="AppSettings.ModelMappings"/> from the rows. Returns false with an error message
    /// when a proxy name is duplicated or a mapping lacks a valid upstream URL.
    /// </summary>
    private bool TryCommitMappings(out string? error)
    {
        error = null;
        List<ModelMapping> committed = [];
        HashSet<string> seenProxyNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            string? proxyName  = row.Cells[_colProxyName.Name].Value?.ToString();
            string? modelName  = row.Cells[_colModelName.Name].Value?.ToString();

            // Advanced per-model settings (including the upstream URL and type) live on the
            // row Tag and are edited via the Configure dialog.
            ModelMapping? advanced = row.Tag as ModelMapping;

            if (string.IsNullOrWhiteSpace(proxyName) || string.IsNullOrWhiteSpace(modelName))
                continue;

            string trimmedProxy = proxyName.Trim();

            if (!seenProxyNames.Add(trimmedProxy))
            {
                error = $"Duplicate proxy model name '{trimmedProxy}'. Proxy names must be unique.";
                return false;
            }

            // Validate upstream URL is required (carried on the row Tag; the grid no longer
            // displays the URL column).
            string upstreamUrl = advanced?.UpstreamUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(upstreamUrl) ||
                !Uri.TryCreate(upstreamUrl, UriKind.Absolute, out _))
            {
                error = $"Model mapping '{trimmedProxy}' requires a valid upstream URL.";
                return false;
            }

            // Clone the advanced configuration carried on the row Tag so every per-model
            // property survives the commit; only the grid-editable fields are overridden.
            ModelMapping committedMapping = advanced?.Clone() ?? new ModelMapping();
            committedMapping.ProxyName = trimmedProxy;
            committedMapping.ModelName = modelName.Trim();

            committed.Add(committedMapping);
        }

        _settings.ModelMappings = committed;
        return true;
    }

    /// <summary>
    /// Ensures a session passphrase is available, prompting the user when necessary. The passphrase
    /// is persisted to settings only when the user opts in via the "remember" checkbox; otherwise
    /// any previously stored value is cleared. Returns false when the user cancels the prompt.
    /// </summary>
    private bool EnsurePassphrase()
    {
        if (!string.IsNullOrEmpty(_settings.RuntimePassphrase))
            return true;

        if (!PassphraseDialog.Prompt(
                this,
                "One or more secrets need to be encrypted.\nEnter a passphrase to encrypt them before saving.",
                out string passphrase,
                out bool remember))
        {
            return false;
        }

        _settings.RuntimePassphrase = passphrase;

        // Persist the passphrase only when the user opted in; otherwise clear any stored value.
        _settings.SecurityPassphrase = remember ? passphrase : null;
        return true;
    }

    /// <summary>
    /// Ensures a session passphrase is available (prompting when necessary) and returns a copy of
    /// the stored credentials with secret material encrypted for persistence. The in-memory
    /// <see cref="AppSettings.Credentials"/> keep plaintext secrets so the running proxy can resolve
    /// them. Returns false — aborting the save — when the user cancels the passphrase prompt.
    /// </summary>
    private bool TryEncryptCredentialsForSave(out List<StoredCredential> persistedCredentials)
    {
        bool anySecret = _settings.Credentials.Any(c => c.HasSecretMaterial);

        if (anySecret && string.IsNullOrEmpty(_settings.RuntimePassphrase))
        {
            if (!EnsurePassphrase())
            {
                MessageBox.Show(
                    "A passphrase is required to encrypt stored credentials. Settings were not saved.",
                    "Save Cancelled",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                persistedCredentials = [];
                return false;
            }
        }

        persistedCredentials = _settings.Credentials.Select(credential =>
        {
            if (!credential.HasSecretMaterial || string.IsNullOrEmpty(_settings.RuntimePassphrase))
            {
                return credential;
            }

            return new StoredCredential
            {
                Name = credential.Name,
                Description = credential.Description,
                Username = credential.Username,
                Secret = EncryptForSave(credential.Secret, _settings.RuntimePassphrase) ?? string.Empty,
                PrivateKey = EncryptForSave(credential.PrivateKey, _settings.RuntimePassphrase),
                Certificate = EncryptForSave(credential.Certificate, _settings.RuntimePassphrase),
            };
        }).ToList();

        return true;
    }

    /// <summary>
    /// Encrypts a secret-material value for persistence. Already-encrypted values pass through
    /// unchanged; null or whitespace-only values return null.
    /// </summary>
    private static string? EncryptForSave(string? value, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return SecretProtector.IsEncrypted(value)
            ? value
            : SecretProtector.Encrypt(value, passphrase);
    }

    private void BtnBrowseRequestDb_Click(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = "db",
            Filter = "LiteDB database (*.db)|*.db|All files (*.*)|*.*",
            FileName = Path.GetFileName(_txtRequestDbPath.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(_txtRequestDbPath.Text))
                ? Path.GetDirectoryName(_txtRequestDbPath.Text)
                : _settings.Logging.LogDirectory,
            OverwritePrompt = false,
            Title = "Choose Application Database File",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _txtRequestDbPath.Text = dialog.FileName;
    }

    private void BtnAddMapping_Click(object? sender, EventArgs e)
    {
        // All editing happens in the modal. Create a fresh mapping, let the user
        // configure it, and only add a grid row on OK.
        ModelMapping mapping = new();

        if (!ModelMappingDialog.ShowConfigureDialog(this, mapping, _settings.InstructionSets, _settings.Credentials, [], CollectUpstreamUrls(), _settings, _stats, out _))
            return;

        int idx = AddMappingRow(mapping);
        DataGridViewRow row = _dgvMappings.Rows[idx];

        _dgvMappings.ClearSelection();
        row.Selected = true;

        CommitMappingsFromGrid();
    }

    private void BtnConfigureMapping_Click(object? sender, EventArgs e)
    {
        DataGridViewRow? row = GetSelectedMappingRow();
        if (row is null)
        {
            MessageBox.Show("Select a model mapping row to configure.", "Configure Model",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ConfigureMappingRow(row);
    }

    private void DgvMappings_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _dgvMappings.Rows.Count)
            return;

        ConfigureMappingRow(_dgvMappings.Rows[e.RowIndex]);
    }

    private void ConfigureMappingRow(DataGridViewRow row)
    {
        if (row.Tag is not ModelMapping mapping)
        {
            mapping = new ModelMapping();
            row.Tag = mapping;
        }

        // Reflect the current row values in the dialog so it can edit and fetch
        // models for this specific upstream. The upstream URL stays on the mapping
        // itself (the grid no longer displays it).
        mapping.ProxyName = row.Cells[_colProxyName.Name].Value?.ToString() ?? string.Empty;
        mapping.ModelName = row.Cells[_colModelName.Name].Value?.ToString() ?? string.Empty;

        List<string> existingItems = string.IsNullOrWhiteSpace(mapping.ModelName)
            ? []
            : [mapping.ModelName];

        if (ModelMappingDialog.ShowConfigureDialog(this, mapping, _settings.InstructionSets, _settings.Credentials, existingItems, CollectUpstreamUrls(), _settings, _stats, out _))
        {
            // Write user-edited values back into the grid cells. The grid is read-only;
            // these values come exclusively from the modal.
            row.Cells[_colMappingEnabled.Name].Value = mapping.IsEnabled ? "Yes" : "No";
            row.Cells[_colProxyName.Name].Value = mapping.ProxyName;
            row.Cells[_colModelName.Name].Value = mapping.ModelName;
            row.Cells[_colInstructionSet.Name].Value = mapping.InstructionSetName ?? string.Empty;
            row.Cells[_colReasoningEffort.Name].Value = mapping.ReasoningEffort ?? string.Empty;
            row.Cells[_colVision.Name].Value = mapping.Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase) ? "Yes" : "No";

            CommitMappingsFromGrid();
        }
    }

    private DataGridViewRow? GetSelectedMappingRow()
    {
        foreach (DataGridViewRow row in _dgvMappings.SelectedRows)
        {
            if (!row.IsNewRow)
                return row;
        }

        if (_dgvMappings.CurrentRow is { IsNewRow: false } current)
            return current;

        return null;
    }

    /// <summary>
    /// Collects distinct upstream URLs from all model mappings in the grid for reuse in the dialog.
    /// </summary>
    private List<string> CollectUpstreamUrls()
    {
        var urls = new List<string>();
        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            if (row.IsNewRow || row.Tag is not ModelMapping mapping)
                continue;

            if (!string.IsNullOrWhiteSpace(mapping.UpstreamUrl))
                urls.Add(mapping.UpstreamUrl);
        }

        return urls;
    }

    private void BtnRemoveMapping_Click(object? sender, EventArgs e)
    {
        foreach (DataGridViewRow row in _dgvMappings.SelectedRows)
        {
            if (!row.IsNewRow)
                _dgvMappings.Rows.Remove(row);
        }

        CommitMappingsFromGrid();
    }

    // ── Instruction Sets ──────────────────────────────────────────────────────

    private void BtnDuplicateMapping_Click(object? sender, EventArgs e)
    {
        DataGridViewRow? row = GetSelectedMappingRow();
        if (row is null)
        {
            MessageBox.Show("Select a model mapping row to duplicate.", "Duplicate Model",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (row.Tag is not ModelMapping originalMapping)
        {
            MessageBox.Show("The selected row does not contain a valid mapping.", "Duplicate Model",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ModelMapping duplicatedMapping = originalMapping.Clone();
        duplicatedMapping.ProxyName = GenerateUniqueProxyName(originalMapping.ProxyName);

        int idx = AddMappingRow(duplicatedMapping);
        DataGridViewRow newRow = _dgvMappings.Rows[idx];

        _dgvMappings.ClearSelection();
        newRow.Selected = true;

        CommitMappingsFromGrid();
    }

    private string GenerateUniqueProxyName(string baseName)
    {
        HashSet<string> existingNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            if (row.IsNewRow)
                continue;

            string? proxyName = row.Cells[_colProxyName.Name].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(proxyName))
                existingNames.Add(proxyName);
        }

        string candidateName = $"{baseName} - Copy";
        if (!existingNames.Contains(candidateName))
            return candidateName;

        int counter = 2;
        while (existingNames.Contains($"{baseName} - Copy {counter}"))
            counter++;

        return $"{baseName} - Copy {counter}";
    }

    private void RefreshInstructionsList()
    {
        _lstInstructions.BeginUpdate();
        _lstInstructions.Items.Clear();

        foreach (InstructionSet instructionSet in _settings.InstructionSets)
        {
            var item = new ListViewItem(instructionSet.Name);
            item.SubItems.Add(instructionSet.Description ?? string.Empty);
            item.Tag = instructionSet;
            _lstInstructions.Items.Add(item);
        }

        _lstInstructions.EndUpdate();
        RefreshInstructionPreview();
    }

    private void RefreshInstructionPreview()
    {
        if (_lstInstructions.SelectedItems.Count > 0 && _lstInstructions.SelectedItems[0].Tag is InstructionSet selected)
        {
            _txtInstructionPreview.Text = selected.Instructions;
        }
        else
        {
            _txtInstructionPreview.Text = string.Empty;
        }
    }

    private static void RefreshInstructionDropdowns()
    {
        // Instruction set selection has moved to the modal ModelMappingDialog,
        // which populates its own combo from _settings.InstructionSets each time
        // it is opened. No grid-level dropdown to refresh.
    }

    private void LstInstructions_SelectedIndexChanged(object? sender, EventArgs e)
    {
        RefreshInstructionPreview();
    }

    private void LstInstructions_DoubleClick(object? sender, EventArgs e)
    {
        BtnEditInstruction_Click(sender, e);
    }

    private void BtnAddInstruction_Click(object? sender, EventArgs e)
    {
        InstructionSet? newSet = InstructionSetDialog.ShowAddEditDialog(this);
        if (newSet is null)
            return;

        // Check for duplicate name
        if (_settings.InstructionSets.Any(i => string.Equals(i.Name, newSet.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"An instruction set named '{newSet.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.InstructionSets.Add(newSet);
        _database.SaveInstructionSets(_settings.InstructionSets);
        _settings.Save();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();
    }

    private void BtnEditInstruction_Click(object? sender, EventArgs e)
    {
        if (_lstInstructions.SelectedItems.Count == 0)
            return;

        if (_lstInstructions.SelectedItems[0].Tag is not InstructionSet existing)
            return;

        InstructionSet? edited = InstructionSetDialog.ShowAddEditDialog(this, existing);
        if (edited is null)
            return;

        string oldName = existing.Name;

        // Check for duplicate name (excluding the one being edited)
        if (_settings.InstructionSets.Any(i => i != existing && 
            string.Equals(i.Name, edited.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"An instruction set named '{edited.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Update in place
        existing.Name = edited.Name;
        existing.Description = edited.Description;
        existing.Instructions = edited.Instructions;

        if (!string.Equals(oldName, edited.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ModelMapping mapping in _settings.ModelMappings)
            {
                if (string.Equals(mapping.InstructionSetName, oldName, StringComparison.OrdinalIgnoreCase))
                    mapping.InstructionSetName = edited.Name;
            }
        }

        _settings.Save();
        _database.SaveInstructionSets(_settings.InstructionSets);
        _database.SaveModelMappings(_settings.ModelMappings);
        LoadSettingsToForm();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();
    }

    private void BtnRemoveInstruction_Click(object? sender, EventArgs e)
    {
        if (_lstInstructions.SelectedItems.Count == 0)
            return;

        if (_lstInstructions.SelectedItems[0].Tag is not InstructionSet toRemove)
            return;

        DialogResult result = MessageBox.Show(
            $"Are you sure you want to remove the instruction set '{toRemove.Name}'?",
            "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        int clearedMappings = 0;
        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            if (string.Equals(mapping.InstructionSetName, toRemove.Name, StringComparison.OrdinalIgnoreCase))
            {
                mapping.InstructionSetName = null;
                clearedMappings++;
            }
        }

        _settings.InstructionSets.Remove(toRemove);
        _database.SaveInstructionSets(_settings.InstructionSets);
        _database.SaveModelMappings(_settings.ModelMappings);
        _settings.Save();
        LoadSettingsToForm();
        RefreshInstructionsList();
        RefreshInstructionDropdowns();

        if (clearedMappings > 0)
        {
            MessageBox.Show($"Removed instruction set and cleared it from {clearedMappings} model mapping(s).",
                "Instruction Set Removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── Credentials ─────────────────────────────────────────────────────────

    private void RefreshCredentials()
    {
        _lstCredentials.BeginUpdate();
        _lstCredentials.Items.Clear();

        foreach (StoredCredential credential in _settings.Credentials)
        {
            // The secret is intentionally never displayed in the list.
            ListViewItem item = new(credential.Name);
            item.SubItems.Add(credential.Description ?? string.Empty);
            item.Tag = credential;
            _lstCredentials.Items.Add(item);
        }

        _lstCredentials.EndUpdate();
    }

    private void BtnAddCredential_Click(object? sender, EventArgs e)
    {
        StoredCredential? newCredential = CredentialDialog.ShowAddEditDialog(this);
        if (newCredential is null)
            return;

        if (_settings.Credentials.Any(c => string.Equals(c.Name, newCredential.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"A credential named '{newCredential.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.Credentials.Add(newCredential);
        RefreshCredentials();
    }

    private void BtnEditCredential_Click(object? sender, EventArgs e)
    {
        if (_lstCredentials.SelectedItems.Count == 0)
        {
            MessageBox.Show("Select a credential to edit.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_lstCredentials.SelectedItems[0].Tag is not StoredCredential existing)
            return;

        StoredCredential? edited = CredentialDialog.ShowAddEditDialog(this, existing);
        if (edited is null)
            return;

        string oldName = existing.Name;

        if (!string.Equals(oldName, edited.Name, StringComparison.OrdinalIgnoreCase)
            && _settings.Credentials.Any(c => c != existing
                && string.Equals(c.Name, edited.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"A credential named '{edited.Name}' already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        existing.Name = edited.Name;
        existing.Secret = edited.Secret;
        existing.Description = edited.Description;
        existing.Username = edited.Username;
        existing.PrivateKey = edited.PrivateKey;
        existing.Certificate = edited.Certificate;

        if (!string.Equals(oldName, edited.Name, StringComparison.OrdinalIgnoreCase))
            PropagateCredentialReferenceChange(oldName, edited.Name);

        RefreshCredentials();
    }

    private void BtnRemoveCredential_Click(object? sender, EventArgs e)
    {
        if (_lstCredentials.SelectedItems.Count == 0)
        {
            MessageBox.Show("Select a credential to remove.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_lstCredentials.SelectedItems[0].Tag is not StoredCredential toRemove)
            return;

        DialogResult result = MessageBox.Show(
            $"Are you sure you want to remove the credential '{toRemove.Name}'?\n"
            + "Model mappings that reference it will fall back to their own API key.",
            "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        _settings.Credentials.Remove(toRemove);
        PropagateCredentialReferenceChange(toRemove.Name, null);
        RefreshCredentials();
    }

    private void LstCredentials_DoubleClick(object? sender, EventArgs e)
    {
        BtnEditCredential_Click(sender, e);
    }

    /// <summary>
    /// Updates every model mapping that references a credential named <paramref name="oldName"/>
    /// so it references <paramref name="newName"/> instead (null clears the reference). Applies to
    /// both the in-memory mappings used by the running proxy and the grid row tags that are read
    /// back on save.
    /// </summary>
    private void PropagateCredentialReferenceChange(string oldName, string? newName)
    {
        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            if (row.Tag is ModelMapping mapping
                && string.Equals(mapping.CredentialName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                mapping.CredentialName = newName;
            }
        }

        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            if (string.Equals(mapping.CredentialName, oldName, StringComparison.OrdinalIgnoreCase))
                mapping.CredentialName = newName;
        }
    }

    // ── Modules ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps the module list and injected module tabs in sync with the module registry after
    /// any import, enable, disable, or remove operation.
    /// </summary>
    private void OnModulesChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        RefreshModules();
        AddModuleTabs();
        RemoveStaleModuleTabs();
        AddModuleHelpPages();
        RemoveStaleModuleHelpPages();
    }

    /// <summary>
    /// Appends the configuration tab of every loaded module that does not have one yet.
    /// Modules build and own their entire tab page; the host only appends it.
    /// </summary>
    private void AddModuleTabs()
    {
        foreach (LoadedModule loaded in _moduleHost.LoadedModules)
        {
            string moduleId = loaded.Entry.ModuleId ?? loaded.Entry.AssemblyPath;
            if (_moduleTabs.ContainsKey(moduleId))
                continue;

            TabPage page;
            try
            {
                page = loaded.Module.CreateConfigPage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Module {Name} failed to build its configuration page", loaded.Entry.Name);
                continue;
            }

            page.Tag = moduleId;
            _mcpSubTabs.TabPages.Add(page);
            _moduleTabs[moduleId] = page;
        }
    }

    /// <summary>Removes tabs belonging to modules that are no longer loaded.</summary>
    private void RemoveStaleModuleTabs()
    {
        HashSet<string> loadedIds = [.. _moduleHost.LoadedModules
            .Select(m => m.Entry.ModuleId ?? m.Entry.AssemblyPath)];

        foreach ((string moduleId, TabPage page) in _moduleTabs
            .Where(kvp => !loadedIds.Contains(kvp.Key))
            .ToList())
        {
            _mcpSubTabs.TabPages.Remove(page);
            page.Dispose();
            _moduleTabs.Remove(moduleId);
        }
    }

    private void RefreshModules()
    {
        _lstModules.BeginUpdate();

        try
        {
            _lstModules.Items.Clear();

            foreach (ModuleRegistryEntry entry in _moduleHost.GetRegistryEntries())
            {
                string state;
                if (!entry.IsEnabled)
                {
                    state = "Disabled";
                }
                else if (!string.IsNullOrWhiteSpace(entry.LastError))
                {
                    state = "Error";
                }
                else
                {
                    state = _moduleHost.LoadedModules.Any(m => m.Entry.Id == entry.Id)
                        ? "Loaded"
                        : "Enabled";
                }

                ListViewItem item = new(entry.Name ?? Path.GetFileName(entry.AssemblyPath));
                item.SubItems.Add(entry.Version ?? string.Empty);
                item.SubItems.Add(state);
                item.SubItems.Add(entry.AssemblyPath);
                item.Tag = entry;
                _lstModules.Items.Add(item);
            }
        }
        finally
        {
            _lstModules.EndUpdate();
        }

        UpdateModuleStatusLabel();
        UpdateModuleButtons();
    }

    private void UpdateModuleStatusLabel()
    {
        if (_lstModules.SelectedItems.Count == 0
            || _lstModules.SelectedItems[0].Tag is not ModuleRegistryEntry entry)
        {
            _lblModuleStatus.Text = string.Empty;
            _lblModuleStatus.ForeColor = SystemColors.GrayText;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.LastError))
        {
            _lblModuleStatus.ForeColor = Color.Red;
            _lblModuleStatus.Text = $"Last error: {entry.LastError}";
            return;
        }

        _lblModuleStatus.ForeColor = SystemColors.GrayText;
        _lblModuleStatus.Text = entry.AssemblyPath;
    }

    private void UpdateModuleButtons()
    {
        bool selected = _lstModules.SelectedItems.Count > 0;
        _btnToggleModule.Enabled = selected;
        _btnRemoveModule.Enabled = selected;
    }

    private void LstModules_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateModuleStatusLabel();
        UpdateModuleButtons();
    }

    private void BtnImportModule_Click(object? sender, EventArgs e)
    {
        // Open in the app's own Modules/ subdirectory — where the build copies the freshly
        // built module DLLs — so the user sees the current modules first instead of whatever
        // folder was last browsed.
        string modulesDir = Path.Combine(AppContext.BaseDirectory, "Modules");
        using OpenFileDialog dialog = new()
        {
            Title = "Import Kaeo LLM Proxy Module",
            Filter = "Kaeo LLM Proxy modules (*.dll)|*.dll",
            Multiselect = false,
            InitialDirectory = Directory.Exists(modulesDir) ? modulesDir : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        try
        {
            _moduleHost.Import(dialog.FileName);
            RefreshModules();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import module {Path}", dialog.FileName);
            MessageBox.Show(this,
                $"Failed to import module:\n\n{ex.Message}",
                "Import Module", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void BtnToggleModule_Click(object? sender, EventArgs e)
    {
        if (_lstModules.SelectedItems.Count == 0
            || _lstModules.SelectedItems[0].Tag is not ModuleRegistryEntry entry)
        {
            return;
        }

        try
        {
            await _moduleHost.SetEnabledAsync(entry, !entry.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to change enabled state for module {Path}", entry.AssemblyPath);
            MessageBox.Show(this,
                $"Failed to change module state:\n\n{ex.Message}",
                "Module", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshModules();
    }

    private async void BtnRemoveModule_Click(object? sender, EventArgs e)
    {
        if (_lstModules.SelectedItems.Count == 0
            || _lstModules.SelectedItems[0].Tag is not ModuleRegistryEntry entry)
        {
            return;
        }

        DialogResult result = MessageBox.Show(this,
            $"Remove module '{entry.Name ?? entry.AssemblyPath}' from the registry?\n\n" +
            "The module file itself is not deleted.",
            "Remove Module", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (result != DialogResult.OK)
            return;

        try
        {
            await _moduleHost.RemoveAsync(entry);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove module {Path}", entry.AssemblyPath);
            MessageBox.Show(this,
                $"Failed to remove module:\n\n{ex.Message}",
                "Remove Module", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshModules();
    }

    // ── Help ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the host-owned Help pages: one blurb per dashboard tab, an MCP page with
    /// Server/Modules sub-pages, and the Modules page that receives injected module help.
    /// </summary>
    private void BuildHelpContent()
    {
        _helpTabs.TabPages.Add(HelpPages.TextPage("Dashboard", HelpPages.Dashboard));
        _helpTabs.TabPages.Add(HelpPages.TextPage("Logs", HelpPages.Logs));
        _helpTabs.TabPages.Add(HelpPages.TextPage("Settings", HelpPages.Settings));
        _helpTabs.TabPages.Add(HelpPages.TextPage("Instructions", HelpPages.Instructions));
        _helpTabs.TabPages.Add(HelpPages.TextPage("Credentials", HelpPages.Credentials));

        TabPage mcpPage = new() { Text = "MCP", Padding = new Padding(8) };
        TabControl mcpSub = new() { Dock = DockStyle.Fill };
        mcpSub.TabPages.Add(HelpPages.TextPage("Server", HelpPages.McpServer));
        mcpSub.TabPages.Add(HelpPages.TextPage("Modules", HelpPages.McpModules));
        mcpPage.Controls.Add(mcpSub);
        _helpTabs.TabPages.Add(mcpPage);

        _helpTabs.TabPages.Add(HelpPages.TextPage("Test", HelpPages.Test));
        _helpTabs.TabPages.Add(HelpPages.TextPage("Heartbeats", HelpPages.Heartbeats));

        _helpModulesPlaceholder = HelpPages.TextPage("Modules", HelpPages.ModulesPlaceholder);
        _helpModulesTabs = new TabControl { Dock = DockStyle.Fill };
        _helpModulesTabs.TabPages.Add(_helpModulesPlaceholder);

        TabPage modulesPage = new() { Text = "Modules", Padding = new Padding(8) };
        modulesPage.Controls.Add(_helpModulesTabs);
        _helpTabs.TabPages.Add(modulesPage);
    }

    /// <summary>Appends the help page of every loaded IHelpModule module that lacks one.</summary>
    private void AddModuleHelpPages()
    {
        foreach (LoadedModule loaded in _moduleHost.LoadedModules)
        {
            string moduleId = loaded.Entry.ModuleId ?? loaded.Entry.AssemblyPath;
            if (_moduleHelpPages.ContainsKey(moduleId))
                continue;

            if (loaded.Module is not IHelpModule helpModule)
                continue;

            TabPage page;
            try
            {
                page = helpModule.CreateHelpPage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Module {Name} failed to build its help page", loaded.Entry.Name);
                continue;
            }

            page.Tag = moduleId;
            _helpModulesTabs.TabPages.Remove(_helpModulesPlaceholder);
            _helpModulesTabs.TabPages.Add(page);
            _moduleHelpPages[moduleId] = page;
        }
    }

    /// <summary>Removes help pages belonging to modules that are no longer loaded.</summary>
    private void RemoveStaleModuleHelpPages()
    {
        HashSet<string> loadedIds = [.. _moduleHost.LoadedModules
            .Select(m => m.Entry.ModuleId ?? m.Entry.AssemblyPath)];

        foreach ((string moduleId, TabPage page) in _moduleHelpPages
            .Where(kvp => !loadedIds.Contains(kvp.Key))
            .ToList())
        {
            _helpModulesTabs.TabPages.Remove(page);
            page.Dispose();
            _moduleHelpPages.Remove(moduleId);
        }

        if (_moduleHelpPages.Count == 0 && !_helpModulesTabs.TabPages.Contains(_helpModulesPlaceholder))
            _helpModulesTabs.TabPages.Add(_helpModulesPlaceholder);
    }

    // ── Test Console ──────────────────────────────────────────────────────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = LoadTestModelsAsync();
    }

    private void TabControl_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_tabControl.SelectedTab == _tabTest)
            _ = LoadTestModelsAsync();
    }

    /// <summary>Populates the test console model combo from configured proxy mappings.</summary>
    private readonly Dictionary<string, ModelMapping> _testProxyNameToMapping = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _testSendCts;

    private async Task LoadTestModelsAsync()
    {
        _lblTestStatus.Text = "Loading models…";

        try
        {
            _cmbTestModel.Items.Clear();
            _testProxyNameToMapping.Clear();

            List<ModelMapping> mappings = [.. _settings.ModelMappings
                .Where(m => m.IsEnabled && !string.IsNullOrWhiteSpace(m.ProxyName))
                .OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase)];

            if (mappings.Count == 0)
            {
                _cmbTestModel.Items.Add("(No model mappings configured)");
                if (_cmbTestModel.Items.Count > 0)
                    _cmbTestModel.SelectedIndex = 0;
                _lblTestStatus.Text = "Configure model mappings in Settings first.";
                return;
            }

            foreach (ModelMapping mapping in mappings)
            {
                string proxyName = mapping.ProxyName.Trim();
                _cmbTestModel.Items.Add(proxyName);
                _testProxyNameToMapping[proxyName] = mapping;
            }

            _cmbTestModel.SelectedIndex = 0;
            ApplySelectedTestModelDefaults();
            _lblTestStatus.Text = $"Loaded {mappings.Count} configured proxy model(s). Ready.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadTestModels] {ex}");
            if (System.Diagnostics.Debugger.IsAttached)
                System.Diagnostics.Debugger.Break();
            _lblTestStatus.Text = $"Model load failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async void BtnTestSend_Click(object? sender, EventArgs e)
    {
        string prompt = _txtTestPrompt.Text.Trim();

        if (string.IsNullOrEmpty(prompt))
        {
            _lblTestStatus.Text = "Enter a prompt first.";
            return;
        }

        string proxyName = _cmbTestModel.SelectedItem?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(proxyName))
        {
            _lblTestStatus.Text = "Select a model first.";
            return;
        }

        if (!_testProxyNameToMapping.TryGetValue(proxyName, out ModelMapping? mapping))
        {
            _lblTestStatus.Text = "Selected proxy model is no longer configured. Reload the Test Console.";
            return;
        }

        _btnTestSend.Enabled = false;
        _btnTestCancel.Enabled = true;
        _lblTestStatus.Text = "Sending\u2026";
        _txtTestResponse.Clear();

        _testSendCts?.Dispose();
        _testSendCts = new CancellationTokenSource();
        CancellationToken ct = _testSendCts.Token;

        string? upstreamUrl = mapping.UpstreamUrl;
        string upstreamModel = string.IsNullOrWhiteSpace(mapping.ModelName)
            ? proxyName
            : mapping.ModelName;

        // The Test Console acts as a regular client app: it sends the proxy model name and its
        // own sampling values, then runs the body through the same normalization pipeline the
        // /v1 passthrough uses, so model rewriting, instruction injection and the per-model
        // sampling priorities (including reasoning_effort) apply identically.
        var messages = new List<object>
        {
            new { role = "user", content = prompt },
        };

        JsonArray messagesArray = [];
        foreach (object message in messages)
            messagesArray.Add(JsonSerializer.SerializeToNode(message));

        JsonObject payload = new()
        {
            ["model"] = proxyName,
            ["stream"] = true,
            ["messages"] = messagesArray,
            ["temperature"] = (double)_nudTestTemp.Value,
            ["repeat_penalty"] = (double)_nudTestRepeatPenalty.Value,
            // Ask OpenAI-compatible upstreams to include a usage block in the terminal stream
            // chunk so the request log can report prompt/completion/reasoning/cached tokens.
            ["stream_options"] = new JsonObject
            {
                ["include_usage"] = true,
            },
        };
        string clientBody = payload.ToJsonString(_indentedJsonOptions);

        var log = new RequestLog
        {
            Method = "POST",
            OllamaPath = "(test console)",
            UpstreamPath = "/v1/chat/completions",
            Model = proxyName,
            Streaming = true,
            Status = RequestStatus.Success,
            RequestBytes = Encoding.UTF8.GetByteCount(clientBody),
        };

        string requestBody = OllamaProxyHandler.NormalizeRequestBody(
            clientBody,
            _settings,
            log,
            modelName => OllamaProxyHandler.ShouldApplyThinkingCompatibility(_settings, modelName));

        // Capture the client's original body and the upstream-bound (rewritten) body side by
        // side, mirroring the passthrough path, so proxy-injected values are visible in the log.
        if (_settings.CollectRequestDetails)
        {
            log.RequestBody = OllamaProxyHandler.RedactRequestBodyForLog(_settings, clientBody, proxyName);
            log.UpstreamRequestBody = OllamaProxyHandler.RedactRequestBodyForLog(_settings, requestBody, proxyName);
        }

        var responseBuilder = new StringBuilder();
        bool hasThinkingOutput = false;
        Exception? capturedException = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int tokenCount = 0;
        int heartbeatCount = 0;
        var streamDiagnostics = new TestConsoleStreamDiagnostics();

        try
        {
            await foreach (TestConsoleToken token in StreamChatAsync(upstreamModel, upstreamUrl, mapping, requestBody, streamDiagnostics, ct))
            {
                if (token.Text == TestConsoleHeartbeatMarker)
                {
                    heartbeatCount++;
                    _stats.IncrementHeartbeat(mapping.ProxyName);
                    continue;
                }

                tokenCount++;
                AppendTestConsoleToken(token, responseBuilder, ref hasThinkingOutput);
            }

            sw.Stop();
            if (tokenCount == 0 && streamDiagnostics.HasDiagnostics)
            {
                string diagnosticText = streamDiagnostics.BuildEmptyResponseMessage(heartbeatCount);
                _txtTestResponse.AppendText(diagnosticText);
                responseBuilder.Append(diagnosticText);
            }

            _lblTestStatus.Text = tokenCount == 0
                ? $"Done in {sw.Elapsed.TotalSeconds:F2}s but no visible tokens were received from the upstream."
                : $"Done in {sw.Elapsed.TotalSeconds:F2}s ({tokenCount} chunks).";
        }
        catch (OperationCanceledException ocEx)
        {
            sw.Stop();
            log.Status = RequestStatus.Cancelled;
            log.ErrorMessage = "Cancelled by user.";
            capturedException = ocEx;
            _lblTestStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            capturedException = ex;
            HandleTestConsoleException(ex);
        }
        finally
        {
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            // Prefer the upstream usage block (prompt/completion/reasoning/cached) when the
            // provider reports it; otherwise fall back to the streamed chunk count so the
            // completion column still reflects activity.
            if (streamDiagnostics.HasUsage)
            {
                log.PromptTokens = streamDiagnostics.PromptTokens;
                log.CompletionTokens = streamDiagnostics.CompletionTokens;
                log.ReasoningTokens = streamDiagnostics.ReasoningTokens;
                log.CachedPromptTokens = streamDiagnostics.CachedPromptTokens;
            }
            else
            {
                log.CompletionTokens = tokenCount;
            }
            string responseText = responseBuilder.ToString();
            log.ResponseBytes = Encoding.UTF8.GetByteCount(responseText);
            if (_settings.CollectResponseDetails)
                log.ResponseBody = responseText;
            if (log.DurationMs > 0)
                log.TokensPerSecond = tokenCount / (log.DurationMs / 1000.0);

            _stats.AddLog(log, capturedException);

            _btnTestSend.Enabled = true;
            _btnTestCancel.Enabled = false;
            _testSendCts?.Dispose();
            _testSendCts = null;
        }
    }

    private void TxtTestPrompt_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
            return;

        e.SuppressKeyPress = true;

        if (_btnTestSend.Enabled)
            BtnTestSend_Click(_btnTestSend, EventArgs.Empty);
    }

    private void CmbTestModel_SelectedIndexChanged(object? sender, EventArgs e)
    {
        ApplySelectedTestModelDefaults();
    }

    private void ApplySelectedTestModelDefaults()
    {
        string model = _cmbTestModel.SelectedItem?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
            return;

        if (!_testProxyNameToMapping.TryGetValue(model, out ModelMapping? mapping))
            return;

        _nudTestTemp.Value = ClampDecimal(mapping.Temperature, _nudTestTemp.Minimum, _nudTestTemp.Maximum, _nudTestTemp.Value);
        _nudTestTemp.Enabled = mapping.TemperaturePriority != SamplingPriority.Provider;
        _nudTestRepeatPenalty.Value = ClampDecimal(
            mapping.RepeatPenalty,
            _nudTestRepeatPenalty.Minimum,
            _nudTestRepeatPenalty.Maximum,
            _nudTestRepeatPenalty.Value);
        _nudTestRepeatPenalty.Enabled = mapping.RepeatPenaltyPriority != SamplingPriority.Provider;
    }

    private static decimal ClampDecimal(double value, decimal min, decimal max, decimal fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;

        decimal decimalValue = (decimal)value;
        if (decimalValue < min)
            return min;
        if (decimalValue > max)
            return max;

        return decimalValue;
    }

    private void AppendTestConsoleToken(
        TestConsoleToken token,
        StringBuilder responseBuilder,
        ref bool hasThinkingOutput)
    {
        if (token.IsThinking)
        {
            if (!hasThinkingOutput)
            {
                AppendTestConsoleText("[Thinking]\r\n");
                responseBuilder.Append("[Thinking]\r\n");
                hasThinkingOutput = true;
            }

            AppendTestConsoleText(token.Text);
            responseBuilder.Append(token.Text);
            return;
        }

        AppendTestConsoleText(token.Text);
        responseBuilder.Append(token.Text);
    }

    private void AppendTestConsoleText(string text)
    {
        _txtTestResponse.AppendText(text);
    }

    private void BtnTestCancel_Click(object? sender, EventArgs e)
    {
        if (_testSendCts is { IsCancellationRequested: false })
        {
            _lblTestStatus.Text = "Cancelling\u2026";
            _testSendCts.Cancel();
        }
    }

    private void HandleTestConsoleException(Exception ex)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debugger.BreakForUserUnhandledException(ex);

        System.Diagnostics.Debug.WriteLine($"[TestConsole] {ex}");

        _lblTestStatus.Text = $"Error: {ex.GetType().Name}: {ex.Message}";
        _txtTestResponse.AppendText($"\r\n\r\n[ERROR] {ex.GetType().FullName}: {ex.Message}\r\n{ex}");

        MessageBox.Show(
            $"{ex.GetType().FullName}: {ex.Message}\r\n\r\n{ex.StackTrace}",
            "Test Console Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>
    /// Streams tokens from the upstream /v1/chat/completions endpoint using SSE,
    /// yielding each content delta as it arrives.
    /// </summary>
    private async IAsyncEnumerable<TestConsoleToken> StreamChatAsync(
        string model,
        string? upstreamUrl,
        ModelMapping? mapping,
        string requestBodyJson,
        TestConsoleStreamDiagnostics diagnostics,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upstreamUrl))
        {
            yield return new TestConsoleToken("[ERROR: No upstream URL configured for this model]", IsThinking: false);
            yield break;
        }

        int timeout = mapping is { UpstreamTimeoutSeconds: > 0 } ? mapping.UpstreamTimeoutSeconds : 300;

        // Build the absolute request URI via the shared helper rather than HttpClient.BaseAddress.
        // A root-relative request URI ("/v1/chat/completions") combined with BaseAddress would
        // discard any path segment already present in upstreamUrl (e.g. ".../compatible-mode/v1"),
        // and naive concatenation can duplicate a trailing "/v1" segment - both cause a 404.
        Uri requestUri = UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/chat/completions");

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json"),
        };
        reqMsg.Headers.Accept.ParseAdd("text/event-stream");
        string? effectiveApiKey = mapping is null ? null : _settings.ResolveApiKey(mapping);
        if (!string.IsNullOrWhiteSpace(effectiveApiKey))
            reqMsg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", effectiveApiKey.Trim());

        // Overall request-level timeout so a stalled upstream cannot hang the UI forever.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        System.Diagnostics.Debug.WriteLine(
            $"[TestConsole] POST {requestUri} model={model}");

        HttpResponseMessage resp = await SendTestConsoleRequestAsync(_testConsoleClient, reqMsg, timeout, ct, requestCts.Token);

        using (resp)
        {
            string? contentType = resp.Content.Headers.ContentType?.MediaType;
            System.Diagnostics.Debug.WriteLine(
                $"[TestConsole] <- HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} content-type={contentType}");

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(requestCts.Token);
                throw new InvalidOperationException(
                    $"Upstream returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }

            bool isSse = contentType != null
                && contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase);

            if (!isSse)
            {
                // Upstream ignored stream=true (or returned an error/JSON body).
                // Parse it as a non-streaming chat completion if possible so the
                // visible response box shows the assistant message instead of raw JSON.
                string body = await resp.Content.ReadAsStringAsync(requestCts.Token);

                // The non-streaming body carries the same usage block as the terminal stream
                // chunk; capture it so token stats are populated on this path too.
                TryParseJsonDocument(body, out JsonDocument? nonStreamDoc);
                if (nonStreamDoc is not null)
                    using (nonStreamDoc)
                        diagnostics.RecordUsage(nonStreamDoc.RootElement);

                List<TestConsoleToken>? extracted = TryExtractNonStreamingTokens(body);
                if (extracted is { Count: > 0 })
                {
                    foreach (TestConsoleToken token in extracted)
                        yield return token;

                    yield break;
                }

                yield return new TestConsoleToken(
                    $"[Upstream returned non-streaming {contentType ?? "response"}]\r\n{body}",
                    IsThinking: false);
                yield break;
            }

            using var responseStream = await resp.Content.ReadAsStreamAsync(requestCts.Token);
            using var reader = new System.IO.StreamReader(responseStream);

            // Per-read inactivity timeout: if no bytes arrive for this long, fail.
            TimeSpan inactivityTimeout = TimeSpan.FromSeconds(Math.Max(30, timeout / 4));
            bool enableHeartbeats = _settings.EnableStreamingHeartbeats && (mapping?.EnableHeartbeats ?? true);
            TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(_settings.StreamingHeartbeatIntervalSeconds, 5, 300));

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                Task<string?> readTask = reader.ReadLineAsync(requestCts.Token).AsTask();
                DateTime readStartedUtc = DateTime.UtcNow;
                DateTime nextHeartbeatUtc = readStartedUtc.Add(heartbeatInterval);

                while (!readTask.IsCompleted)
                {
                    TimeSpan elapsed = DateTime.UtcNow - readStartedUtc;
                    TimeSpan untilTimeout = inactivityTimeout - elapsed;
                    if (untilTimeout <= TimeSpan.Zero)
                    {
                        throw new TimeoutException(
                            $"No data received from upstream for {inactivityTimeout.TotalSeconds:F0}s. Aborting.");
                    }

                    TimeSpan delay = untilTimeout;
                    if (enableHeartbeats)
                    {
                        TimeSpan untilHeartbeat = nextHeartbeatUtc - DateTime.UtcNow;
                        if (untilHeartbeat < TimeSpan.Zero)
                            untilHeartbeat = TimeSpan.Zero;

                        delay = untilHeartbeat < delay ? untilHeartbeat : delay;
                    }

                    Task completed = await Task.WhenAny(readTask, Task.Delay(delay, requestCts.Token));
                    if (completed == readTask)
                        break;

                    if (enableHeartbeats && DateTime.UtcNow >= nextHeartbeatUtc)
                    {
                        yield return new TestConsoleToken(TestConsoleHeartbeatMarker, IsThinking: false);
                        nextHeartbeatUtc = DateTime.UtcNow.Add(heartbeatInterval);
                    }
                }

                string? line = await readTask;

                if (line is null)
                {
                    System.Diagnostics.Debug.WriteLine("[TestConsole] stream ended without [DONE]");
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line["data:".Length..].Trim();
                diagnostics.RecordData(data);

                if (data == "[DONE]")
                {
                    diagnostics.MarkDone();
                    yield break;
                }

                if (!TryParseJsonDocument(data, out JsonDocument? doc))
                {
                    diagnostics.RecordParseFailure(data);
                    continue;
                }

                if (doc is null)
                    continue;

                using (JsonDocument parsed = doc)
                {
                    JsonElement root = parsed.RootElement;

                    diagnostics.RecordUsage(root);

                    if (TryExtractSseError(root, out string errorMessage))
                    {
                        yield return new TestConsoleToken(
                            $"[Upstream stream error]\r\n{errorMessage}",
                            IsThinking: false);
                        yield break;
                    }

                    if (!root.TryGetProperty("choices", out JsonElement choices))
                    {
                        foreach (TestConsoleToken token in ExtractTokensFromElement(root))
                            yield return token;

                        continue;
                    }

                    bool yieldedAnyChoiceToken = false;

                    foreach (JsonElement choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out JsonElement delta))
                        {
                            foreach (TestConsoleToken token in ExtractTokensFromElement(delta))
                            {
                                yieldedAnyChoiceToken = true;
                                yield return token;
                            }
                        }

                        if (choice.TryGetProperty("message", out JsonElement message))
                        {
                            foreach (TestConsoleToken token in ExtractTokensFromElement(message))
                            {
                                yieldedAnyChoiceToken = true;
                                yield return token;
                            }
                        }

                        foreach (TestConsoleToken token in ExtractTokensFromElement(choice))
                        {
                            yieldedAnyChoiceToken = true;
                            yield return token;
                        }
                    }

                    if (!yieldedAnyChoiceToken)
                        diagnostics.RecordIgnoredChunk(data);
                }
            }
        }
    }

    private static IEnumerable<TestConsoleToken> ExtractTokensFromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (string propertyName in new[] { "reasoning_content", "reasoning", "reasoning_text" })
        {
            if (TryGetStringProperty(element, propertyName, out string? thinking))
                yield return new TestConsoleToken(thinking, IsThinking: true);
        }

        foreach (string propertyName in new[] { "content", "text", "response", "output_text" })
        {
            if (TryGetStringProperty(element, propertyName, out string? answer))
                yield return new TestConsoleToken(answer, IsThinking: false);
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return false;

        if (property.ValueKind != JsonValueKind.String)
            return false;

        string? text = property.GetString();
        if (string.IsNullOrEmpty(text))
            return false;

        value = text;
        return true;
    }

    private static async Task<HttpResponseMessage> SendTestConsoleRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        int timeoutSeconds,
        CancellationToken userCancellationToken,
        CancellationToken requestCancellationToken)
    {
        try
        {
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellationToken);
        }
        catch (TaskCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Upstream did not respond within {timeoutSeconds}s while sending the request.");
        }
    }

    private static bool TryParseJsonDocument(string json, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static bool TryExtractSseError(JsonElement root, out string message)
    {
        message = string.Empty;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error))
            return false;

        if (error.ValueKind == JsonValueKind.String)
        {
            message = error.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(message);
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            message = error.ToString();
            return !string.IsNullOrWhiteSpace(message);
        }

        string? code = error.TryGetProperty("code", out JsonElement codeElement)
            ? codeElement.ToString()
            : null;
        string? type = error.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.ToString()
            : null;
        string? detail = error.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement.GetString()
            : error.ToString();

        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(code))
            parts.Add($"code={code}");
        if (!string.IsNullOrWhiteSpace(type))
            parts.Add($"type={type}");
        if (!string.IsNullOrWhiteSpace(detail))
            parts.Add(detail);

        message = parts.Count == 0 ? error.ToString() : string.Join("; ", parts);
        return !string.IsNullOrWhiteSpace(message);
    }

    private void BtnTestClear_Click(object? sender, EventArgs e)
    {
        _txtTestPrompt.Clear();
        _txtTestResponse.Clear();
        _lblTestStatus.Text = "Ready.";
    }

    /// <summary>
    /// Attempts to extract assistant thinking and answer text from a non-streaming
    /// /v1/chat/completions response body. Returns null if the JSON doesn't match that shape.
    /// </summary>
    private static List<TestConsoleToken>? TryExtractNonStreamingTokens(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            List<TestConsoleToken> tokens = [];
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out JsonElement message))
                    tokens.AddRange(ExtractTokensFromElement(message));

                tokens.AddRange(ExtractTokensFromElement(choice));
            }

            return tokens.Count > 0 ? tokens : null;
        }
    }

    private readonly record struct TestConsoleToken(string Text, bool IsThinking);

    private sealed class TestConsoleStreamDiagnostics
    {
        private const int MaxSampleLength = 1200;

        private string? _firstData;
        private string? _lastData;
        private string? _firstParseFailure;
        private string? _firstIgnoredChunk;
        private string? _firstThinkingToken;
        private StringBuilder? _rawData;

        public int DataLineCount { get; private set; }
        public bool SawDone { get; private set; }
        public bool HasDiagnostics => DataLineCount > 0 || _firstParseFailure is not null || _firstIgnoredChunk is not null;

        /// <summary>True once at least one raw upstream data line has been captured.</summary>
        public bool HasRawResponse => _rawData is not null;

        /// <summary>The first thinking-classified content, used to detect a model-emitted "[Thinking]" prefix.</summary>
        public string? FirstThinkingToken => _firstThinkingToken;

        /// <summary>
        /// The raw upstream JSON (each SSE data line as sent by the provider, newline-joined) for
        /// the request log's Response Body, kept unaltered so it matches what the model actually
        /// replied with.
        /// </summary>
        public string? RawResponse => _rawData?.ToString();

        /// <summary>True once a usage block was observed in the stream (token counts are valid).</summary>
        public bool HasUsage { get; private set; }
        public int PromptTokens { get; private set; }
        public int CompletionTokens { get; private set; }
        public int ReasoningTokens { get; private set; }
        public int CachedPromptTokens { get; private set; }

        public void RecordData(string data)
        {
            DataLineCount++;
            _firstData ??= TrimSample(data);
            _lastData = TrimSample(data);
        }

        /// <summary>
        /// Records a raw upstream JSON payload (a single SSE data line for streams, or the whole
        /// non-streaming body) so the request log can store the unaltered upstream response.
        /// </summary>
        public void RecordRawData(string data)
        {
            if (_rawData is null)
                _rawData = new StringBuilder();
            else if (_rawData.Length > 0)
                _rawData.Append('\n');
            _rawData.Append(data);
        }

        /// <summary>Remembers the first thinking-classified token for prefix detection.</summary>
        public void RecordFirstThinkingToken(string? token)
        {
            _firstThinkingToken ??= token;
        }

        /// <summary>
        /// Reads the OpenAI <c>usage</c> block from a stream chunk (when present) so token
        /// stats can be surfaced in the request log. Updates the last non-zero values seen,
        /// since usage is reported on the terminal chunk.
        /// </summary>
        public void RecordUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
                return;

            if (usage.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number)
                PromptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
                CompletionTokens = ct.GetInt32();

            if (usage.TryGetProperty("completion_tokens_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reasoning_tokens", out JsonElement rt)
                && rt.ValueKind == JsonValueKind.Number)
            {
                ReasoningTokens = rt.GetInt32();
            }

            if (usage.TryGetProperty("prompt_tokens_details", out JsonElement promptDetails)
                && promptDetails.ValueKind == JsonValueKind.Object
                && promptDetails.TryGetProperty("cached_tokens", out JsonElement cached)
                && cached.ValueKind == JsonValueKind.Number)
            {
                CachedPromptTokens = cached.GetInt32();
            }

            HasUsage = true;
        }

        public void MarkDone() => SawDone = true;

        public void RecordParseFailure(string data) => _firstParseFailure ??= TrimSample(data);

        public void RecordIgnoredChunk(string data) => _firstIgnoredChunk ??= TrimSample(data);

        public string BuildEmptyResponseMessage(int heartbeatCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[No visible assistant text was extracted from the upstream stream.]");
            sb.AppendLine($"SSE data lines: {DataLineCount:N0}; heartbeats while waiting: {heartbeatCount:N0}; saw [DONE]: {SawDone}");

            if (_firstParseFailure is not null)
                sb.AppendLine($"First unparsable data line: {_firstParseFailure}");

            if (_firstIgnoredChunk is not null)
                sb.AppendLine($"First parsed chunk without text fields: {_firstIgnoredChunk}");

            if (_firstData is not null)
                sb.AppendLine($"First data line: {_firstData}");

            if (_lastData is not null && !string.Equals(_lastData, _firstData, StringComparison.Ordinal))
                sb.AppendLine($"Last data line: {_lastData}");

            return sb.ToString();
        }

        private static string TrimSample(string value)
        {
            if (value.Length <= MaxSampleLength)
                return value;

            return value[..MaxSampleLength] + "…";
        }
    }
}
