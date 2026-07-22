using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Infrastructure;

namespace Kaeo.LlmProxy;

internal partial class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly StatisticsService _stats;
    private readonly ProxyServer _server;
    private readonly OllamaProxyHandler _handler;
    private readonly PerformanceService _perfService;
    private readonly AppDatabase _database;

    // Cached snapshot of log summaries backing the virtual-mode ListView. Only visible rows
    // (plus a small buffer) are materialized as ListViewItem objects via RetrieveVirtualItem.
    private IReadOnlyList<RequestLog> _logCache = [];

    internal event EventHandler? MinimizedToTray;

    private const string TestConsoleHeartbeatMarker = "__kaeo_test_console_heartbeat__";

    private static readonly JsonSerializerOptions _indentedJsonOptions = new() { WriteIndented = true };

    public MainForm(AppSettings settings, StatisticsService stats, ProxyServer server, OllamaProxyHandler handler, PerformanceService perfService, AppDatabase database)
    {
        _settings = settings;
        _stats = stats;
        _server = server;
        _handler = handler;
        _perfService = perfService;
        _database = database;

        InitializeComponent();
        Icon = Program.GetApplicationIcon();

        // Virtual mode materializes only the visible rows (plus a small buffer) instead of
        // creating a ListViewItem for every log entry on each refresh.
        _lstLogs.VirtualMode = true;
        _lstLogs.RetrieveVirtualItem += LstLogs_RetrieveVirtualItem;

        _stats.StatsChanged += OnStatsChanged;
        _server.StatusChanged += OnServerStatusChanged;
        _perfService.Sampled += OnPerfSampled;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadSettingsToForm();
        RefreshStatus();
        RefreshStats();
        RefreshLogs();
        RefreshHeartbeats();
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
        _stats.HeartbeatsChanged -= OnHeartbeatsChanged;
        _server.StatusChanged -= OnServerStatusChanged;
        _perfService.Sampled -= OnPerfSampled;
        base.OnFormClosed(e);
    }

    // ── Status ──────────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        bool running = _server.IsRunning;
        _lblStatusValue.Text = running ? $"Running ({_settings.ListenAddress}:{_settings.ListenPort})" : "Stopped";
        _lblStatusValue.ForeColor = running ? Color.Green : Color.Red;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _btnRestart.Enabled = running;
        _btnDashStart.Enabled = !running;
        _btnDashStop.Enabled = running;
        _btnDashRestart.Enabled = running;
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

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        try
        {
            _server.Start(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        try
        {
            await _server.StopAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error stopping: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void BtnRestart_Click(object? sender, EventArgs e)
    {
        try
        {
            await _server.RestartAsync(_settings.ListenPort, _settings.ListenAddress, _settings.MaxConcurrentRequests);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error restarting: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void BtnResetStats_Click(object? sender, EventArgs e)
    {
        _stats.Reset();
        RefreshStats();
        RefreshLogs();
    }

    // ── Logs ─────────────────────────────────────────────────────────────────

    private void RefreshLogs()
    {
        _logCache = _stats.GetRecentLogs();
        _lstLogs.VirtualListSize = _logCache.Count;
        _lstLogs.Invalidate();
    }

    private void LstLogs_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _logCache.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        RequestLog log = _logCache[e.ItemIndex];
        var item = new ListViewItem(log.Timestamp.ToString("HH:mm:ss"));
        item.SubItems.Add(log.Method);
        item.SubItems.Add(log.OllamaPath);
        item.SubItems.Add(log.Model);
        item.SubItems.Add(log.Status.ToString());
        item.SubItems.Add($"{log.DurationMs:F0}");
        item.SubItems.Add($"{log.PromptTokens}+{log.CompletionTokens}");
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

    private void BtnClearLogs_Click(object? sender, EventArgs e)
    {
        _stats.Reset();
        _logCache = [];
        _lstLogs.VirtualListSize = 0;
        _lstLogs.Invalidate();
    }

    private void BtnRefreshLogs_Click(object? sender, EventArgs e) => RefreshLogs();

    private void BtnLogDetails_Click(object? sender, EventArgs e)
    {
        if (_lstLogs.SelectedIndices.Count == 0)
        {
            MessageBox.Show("Select a log entry first.", "No selection",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int index = _lstLogs.SelectedIndices[0];
        if (index >= 0 && index < _logCache.Count)
            ShowLogDetails(_logCache[index]);
    }

    private void LstLogs_DoubleClick(object? sender, EventArgs e)
    {
        if (_lstLogs.SelectedIndices.Count == 0)
            return;

        int index = _lstLogs.SelectedIndices[0];
        if (index >= 0 && index < _logCache.Count)
            ShowLogDetails(_logCache[index]);
    }

    private void ShowLogDetails(RequestLog log)
    {
        // The in-memory entry is a lightweight summary without request/response bodies.
        // Load the full entry from SQLite on demand so large bodies stay out of memory.
        RequestLog? full = _database.LoadFullLogEntry(log.Timestamp);
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
        sb.AppendLine($"Upstream  : {log.UpstreamPath}");
        sb.AppendLine($"Model     : {log.Model}");
        sb.AppendLine($"Status    : {log.Status} ({log.StatusCode})");
        sb.AppendLine($"Streaming : {log.Streaming}");
        sb.AppendLine($"Duration  : {log.DurationMs:F1} ms");
        sb.AppendLine($"Tokens    : {log.PromptTokens} prompt + {log.CompletionTokens} completion");
        sb.AppendLine($"Bytes     : {FormatBytes(log.RequestBytes, log.ResponseBytes)} (request / response)");

        if (!string.IsNullOrEmpty(log.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("── Error ──────────────────────────────────────────────────────");
            sb.AppendLine(log.ErrorMessage);
        }

        if (log.RequestBody is not null)
        {
            sb.AppendLine();
            sb.AppendLine("── Request Body ───────────────────────────────────────────────");
            AppendBody(sb, log.RequestBody);
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

        if (log.ResponseBody is not null)
        {
            TabPage responseTab = new()
            {
                Name = "_tabLogResponseBody",
                Padding = new Padding(8),
                Text = "Response Body",
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

    private static void AppendBody(StringBuilder sb, string body)
    {
        sb.AppendLine(FormatBody(body));
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

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_tabControl.SelectedTab == _tabLogs && _chkAutoRefresh.Checked)
            RefreshLogs();
    }

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
                mapping.EnableHeartbeats && _settings.EnableStreamingHeartbeats,
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

    private void BtnSaveHeartbeats_Click(object? sender, EventArgs e)
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
    }

    private void BtnResetHeartbeats_Click(object? sender, EventArgs e)
    {
        _stats.ResetHeartbeats();
        RefreshHeartbeats();
    }

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

    private void LoadSettingsToForm()
    {
        _txtListenPort.Text = _settings.ListenPort.ToString();
        _txtMaxLogs.Text = _settings.MaxLogEntries.ToString();
        _chkAutoStart.Checked = _settings.AutoStartProxy;
        _chkStartWithDashboard.Checked = _settings.StartWithDashboardOpen;
        _chkCollectDetails.Checked = _settings.CollectRequestDetails;
        _chkCollectResponseDetails.Checked = _settings.CollectResponseDetails;
        _chkStreamingHeartbeats.Checked = _settings.EnableStreamingHeartbeats;
        _txtHeartbeatInterval.Text = _settings.StreamingHeartbeatIntervalSeconds.ToString();

        _dgvMappings.Rows.Clear();
        foreach (ModelMapping mapping in _settings.ModelMappings)
        {
            int idx = _dgvMappings.Rows.Add(
                mapping.IsEnabled ? "Yes" : "No",
                mapping.ProxyName,
                mapping.ModelName,
                mapping.UpstreamUrl,
                mapping.UpstreamType.ToDisplayName());

            DataGridViewRow row = _dgvMappings.Rows[idx];

            // Carry per-row advanced configuration (instruction set + redaction)
            // on the row Tag — these fields are edited in the modal Configure dialog.
            row.Tag = new ModelMapping
            {
                IsEnabled = mapping.IsEnabled,
                ProxyName = mapping.ProxyName,
                ModelName = mapping.ModelName,
                EnableThinkingCompatibility = mapping.EnableThinkingCompatibility,
                EnableHeartbeats = mapping.EnableHeartbeats,
                ApiKey = mapping.ApiKey,
                UpstreamUrl = mapping.UpstreamUrl,
                UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds,
                RepeatPenalty = mapping.RepeatPenalty,
                Temperature = mapping.Temperature,
                UpstreamType = mapping.UpstreamType,
                EnableAutoSummarization = mapping.EnableAutoSummarization,
                PreserveRecentMessageCount = mapping.PreserveRecentMessageCount,
                MaxSummarizationRetries = mapping.MaxSummarizationRetries,
                InstructionSetName = mapping.InstructionSetName,
                RedactRequestBodies = mapping.RedactRequestBodies,
                RedactResponseBodies = mapping.RedactResponseBodies,
                RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields,
            };

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
    }

    private void BtnSaveSettings_Click(object? sender, EventArgs e)
    {
        if (!int.TryParse(_txtListenPort.Text, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Listen port must be a number between 1 and 65535.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtMaxLogs.Text, out int maxLogs) || maxLogs < 1)
        {
            MessageBox.Show("Max log entries must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_txtLogDir.Text))
        {
            MessageBox.Show("Log directory cannot be empty.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_txtRequestDbPath.Text))
        {
            MessageBox.Show("Application database file path cannot be empty.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string requestDbPath = _txtRequestDbPath.Text.Trim();
        string? requestDbDirectory = Path.GetDirectoryName(requestDbPath);
        if (string.IsNullOrWhiteSpace(requestDbDirectory))
        {
            MessageBox.Show("Application database file path must include a directory.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtAppLogSize.Text, out int appLogSize) || appLogSize < 1)
        {
            MessageBox.Show("App log file size limit must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtAppLogRetain.Text, out int appLogRetain) || appLogRetain < 1)
        {
            MessageBox.Show("App log files to keep must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtReqLogSize.Text, out int reqLogSize) || reqLogSize < 1)
        {
            MessageBox.Show("Application database file size limit must be a positive number.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!int.TryParse(_txtLogRetention.Text, out int logRetentionHours) || logRetentionHours < 0)
        {
            MessageBox.Show("Log retention must be 0 (keep forever) or a positive number of hours.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.ListenPort = port;
        _settings.MaxLogEntries = maxLogs;
        _settings.AutoStartProxy = _chkAutoStart.Checked;
        _settings.StartWithDashboardOpen = _chkStartWithDashboard.Checked;
        _settings.CollectRequestDetails = _chkCollectDetails.Checked;
        _settings.CollectResponseDetails = _chkCollectResponseDetails.Checked;

        _settings.Logging.LogDirectory = _txtLogDir.Text.Trim();
        _settings.Logging.MinimumLevel = _cmbMinLevel.SelectedItem?.ToString() ?? "Information";
        _settings.Logging.AppLogFileSizeLimitMb = appLogSize;
        _settings.Logging.AppLogRetainedFileCount = appLogRetain;
        _settings.Logging.RequestLogFileSizeLimitMb = reqLogSize;
        _settings.Logging.ApplicationDatabasePath = requestDbPath;
        _settings.Logging.LogRetentionHours = logRetentionHours;

        _settings.ModelMappings.Clear();
        HashSet<string> seenProxyNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _dgvMappings.Rows)
        {
            string? proxyName  = row.Cells[_colProxyName.Name].Value?.ToString();
            string? modelName  = row.Cells[_colModelName.Name].Value?.ToString();
            string? upstreamUrl = row.Cells[_colUpstreamUrl.Name].Value?.ToString() ?? string.Empty;
            string? upstreamStr = row.Cells[_colUpstreamType.Name].Value?.ToString();

            // Advanced per-model settings live on the row Tag and are edited via the Configure dialog.
            ModelMapping? advanced = row.Tag as ModelMapping;

            if (!string.IsNullOrWhiteSpace(proxyName) && !string.IsNullOrWhiteSpace(modelName))
            {
                string trimmedProxy = proxyName.Trim();

                if (!seenProxyNames.Add(trimmedProxy))
                {
                    MessageBox.Show(
                        $"Duplicate proxy model name '{trimmedProxy}'. Proxy names must be unique.",
                        "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Validate upstream URL is required
                if (string.IsNullOrWhiteSpace(upstreamUrl) ||
                    !Uri.TryCreate(upstreamUrl, UriKind.Absolute, out _))
                {
                    MessageBox.Show($"Model mapping '{trimmedProxy}' requires a valid upstream URL.", "Validation",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                UpstreamType upstream = UpstreamTypeExtensions.FromDisplayName(upstreamStr);

                _settings.ModelMappings.Add(new ModelMapping
                {
                    ProxyName              = trimmedProxy,
                    ModelName              = modelName.Trim(),
                    IsEnabled              = advanced?.IsEnabled ?? true,
                    EnableThinkingCompatibility = advanced?.EnableThinkingCompatibility ?? true,
                    EnableHeartbeats       = advanced?.EnableHeartbeats ?? true,
                    ApiKey                 = advanced?.ApiKey,
                    UpstreamUrl            = upstreamUrl.Trim(),
                    UpstreamTimeoutSeconds = advanced?.UpstreamTimeoutSeconds ?? 300,
                    RepeatPenalty          = advanced?.RepeatPenalty ?? 1.0,
                    Temperature            = advanced?.Temperature ?? 0.7,
                    UpstreamType           = upstream,
                    InstructionSetName     = advanced?.InstructionSetName,
                    RedactRequestBodies    = advanced?.RedactRequestBodies ?? true,
                    RedactResponseBodies   = advanced?.RedactResponseBodies ?? true,
                    RedactSensitiveJsonFields = advanced?.RedactSensitiveJsonFields ?? true,
                });
            }
        }

        _database.SaveModelMappings(_settings.ModelMappings);
        _database.SaveInstructionSets(_settings.InstructionSets);
        _database.SaveRuntimeSettings(_settings.CreateRuntimeSettings());
        _settings.Save();
        _stats.UpdateMaxEntries(maxLogs);
        _stats.UpdateRetentionHours(logRetentionHours);
        _handler.UpdateSettings(_settings);

        // Re-apply logging config immediately so the new level/size/dir is active.
        AppLogger.Initialize(_settings.Logging);

        MessageBox.Show("Settings saved. Restart the app for application database file path changes to take effect. Restart the proxy for port changes to take effect.",
            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);

        RefreshStatus();
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

        if (!ModelMappingDialog.ShowConfigureDialog(this, mapping, _settings.InstructionSets, [], out _))
            return;

        int idx = _dgvMappings.Rows.Add(
            mapping.IsEnabled ? "Yes" : "No",
            mapping.ProxyName,
            mapping.ModelName,
            mapping.UpstreamUrl,
            mapping.UpstreamType.ToDisplayName());

        DataGridViewRow row = _dgvMappings.Rows[idx];
        row.Tag = mapping;

        _dgvMappings.ClearSelection();
        row.Selected = true;
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
        // models for this specific upstream.
        mapping.ProxyName = row.Cells[_colProxyName.Name].Value?.ToString() ?? string.Empty;
        mapping.UpstreamUrl = row.Cells[_colUpstreamUrl.Name].Value?.ToString() ?? string.Empty;
        mapping.ModelName = row.Cells[_colModelName.Name].Value?.ToString() ?? string.Empty;

        List<string> existingItems = string.IsNullOrWhiteSpace(mapping.ModelName)
            ? []
            : [mapping.ModelName];

        if (ModelMappingDialog.ShowConfigureDialog(this, mapping, _settings.InstructionSets, existingItems, out _))
        {
            // Write user-edited values back into the grid cells. The grid is read-only;
            // these values come exclusively from the modal.
            row.Cells[_colMappingEnabled.Name].Value = mapping.IsEnabled ? "Yes" : "No";
            row.Cells[_colProxyName.Name].Value = mapping.ProxyName;
            row.Cells[_colModelName.Name].Value = mapping.ModelName;
            row.Cells[_colUpstreamUrl.Name].Value = mapping.UpstreamUrl;
            row.Cells[_colUpstreamType.Name].Value = mapping.UpstreamType.ToDisplayName();
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

    private void BtnRemoveMapping_Click(object? sender, EventArgs e)
    {
        foreach (DataGridViewRow row in _dgvMappings.SelectedRows)
        {
            if (!row.IsNewRow)
                _dgvMappings.Rows.Remove(row);
        }
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

        int idx = _dgvMappings.Rows.Add(
            duplicatedMapping.IsEnabled ? "Yes" : "No",
            duplicatedMapping.ProxyName,
            duplicatedMapping.ModelName,
            duplicatedMapping.UpstreamUrl,
            duplicatedMapping.UpstreamType.ToDisplayName());

        DataGridViewRow newRow = _dgvMappings.Rows[idx];
        newRow.Tag = duplicatedMapping;

        _dgvMappings.ClearSelection();
        newRow.Selected = true;
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

        // Inject configured instruction set (if any) as a leading system message.
        InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);

        var messages = new List<object>();
        if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            messages.Add(new { role = "system", content = instructionSet.Instructions });
        messages.Add(new { role = "user", content = prompt });

        double temperature = (double)_nudTestTemp.Value;
        double repeatPenalty = (double)_nudTestRepeatPenalty.Value;

        var payload = new
        {
            model = upstreamModel,
            stream = true,
            temperature,
            repeat_penalty = repeatPenalty,
            messages,
        };
        string requestBody = JsonSerializer.Serialize(payload, _indentedJsonOptions);

        var log = new RequestLog
        {
            Method = "POST",
            OllamaPath = "(test console)",
            UpstreamPath = "/v1/chat/completions",
            Model = proxyName,
            Streaming = true,
            Status = RequestStatus.Success,
            RequestBody = _settings.CollectRequestDetails ? requestBody : null,
            RequestBytes = Encoding.UTF8.GetByteCount(requestBody),
        };

        var responseBuilder = new StringBuilder();
        bool hasThinkingOutput = false;
        bool hasAnswerOutput = false;
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
                AppendTestConsoleToken(token, responseBuilder, ref hasThinkingOutput, ref hasAnswerOutput);
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
            log.CompletionTokens = tokenCount;
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
        _nudTestRepeatPenalty.Value = ClampDecimal(
            mapping.RepeatPenalty,
            _nudTestRepeatPenalty.Minimum,
            _nudTestRepeatPenalty.Maximum,
            _nudTestRepeatPenalty.Value);
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
        ref bool hasThinkingOutput,
        ref bool hasAnswerOutput)
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

        if (hasThinkingOutput && !hasAnswerOutput)
        {
            AppendTestConsoleText("\r\n\r\n[Answer]\r\n");
            responseBuilder.Append("\r\n\r\n[Answer]\r\n");
        }

        hasAnswerOutput = true;
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

        // Use Timeout.InfiniteTimeSpan so HttpClient doesn't pre-empt our own per-read
        // cancellation; we manage timeouts ourselves below.
        using var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBodyJson, Encoding.UTF8, "application/json"),
        };
        reqMsg.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrWhiteSpace(mapping?.ApiKey))
            reqMsg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mapping.ApiKey.Trim());

        // Overall request-level timeout so a stalled upstream cannot hang the UI forever.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        System.Diagnostics.Debug.WriteLine(
            $"[TestConsole] POST {requestUri} model={model}");

        HttpResponseMessage resp = await SendTestConsoleRequestAsync(client, reqMsg, timeout, ct, requestCts.Token);

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

        public int DataLineCount { get; private set; }
        public bool SawDone { get; private set; }
        public bool HasDiagnostics => DataLineCount > 0 || _firstParseFailure is not null || _firstIgnoredChunk is not null;

        public void RecordData(string data)
        {
            DataLineCount++;
            _firstData ??= TrimSample(data);
            _lastData = TrimSample(data);
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
