using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// The module's configuration tab page injected into the host dashboard: tool toggles and
/// limits (debounced saves), the configured channels table with add/edit/delete, and the
/// polling start/stop control. Cross-thread events are marshaled with SafeBeginInvoke; every
/// database call degrades gracefully on sharing violations (multiple app instances).
/// </summary>
internal sealed class ChatConfigPage : TabPage
{
    private readonly ChatInterfaceModule _module;
    private readonly System.Windows.Forms.Timer _saveDebounce;
    private bool _loading;

    // Tools & limits controls
    private CheckBox _chkSendTool = null!;
    private CheckBox _chkAskTool = null!;
    private CheckBox _chkReadTool = null!;
    private CheckBox _chkChannelsTool = null!;
    private NumericUpDown _nudAskTimeout = null!;
    private NumericUpDown _nudMaxMessageChars = null!;
    private NumericUpDown _nudPollInterval = null!;
    private ComboBox _cmbLogLevel = null!;

    // Channels controls
    private ListView _lstChannels = null!;
    private Button _btnAddChannel = null!;
    private Button _btnEditChannel = null!;
    private Button _btnRemoveChannel = null!;
    private Button _btnRefresh = null!;

    // Polling controls
    private Button _btnStartStop = null!;
    private Label _lblStatus = null!;

    public ChatConfigPage(ChatInterfaceModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));

        Text = "Chat Interface";
        Padding = new Padding(8);
        AutoScroll = true;

        _saveDebounce = new System.Windows.Forms.Timer { Interval = 500 };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveSettings();
        };

        Controls.Add(BuildContent());

        LoadSettingsToUi();
        RefreshChannels();
        UpdatePollingStatus();

        _module.StatusChanged += OnStatusChanged;
        HandleDestroyed += (_, _) =>
        {
            _module.StatusChanged -= OnStatusChanged;
            _saveDebounce.Dispose();
        };
    }

    private TableLayoutPanel BuildContent()
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 4; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(BuildLimitsGroup(), 0, 0);
        layout.Controls.Add(BuildChannelsGroup(), 0, 1);
        layout.Controls.Add(BuildPollingGroup(), 0, 2);

        Label note = new()
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Settings save automatically. Polling must be running for chat_ask/chat_read to receive " +
                "replies; chat_send works either way. Webhook URLs and tokens are secrets and are never logged.",
        };
        layout.Controls.Add(note, 0, 3);

        return layout;
    }

    private GroupBox BuildLimitsGroup()
    {
        GroupBox group = new() { Text = "Tools && Limits", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { AutoSize = true, ColumnCount = 4, RowCount = 3 };
        for (int i = 0; i < 4; i++)
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 3; i++)
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _chkSendTool = MakeToolCheckBox("Enable chat_send");
        _chkAskTool = MakeToolCheckBox("Enable chat_ask");
        _chkReadTool = MakeToolCheckBox("Enable chat_read");
        _chkChannelsTool = MakeToolCheckBox("Enable chat_channels");

        _nudAskTimeout = MakeNud(10, 3_600, 10);
        _nudMaxMessageChars = MakeNud(500, 50_000, 500);
        _nudPollInterval = MakeNud(5, 300, 5);
        _nudAskTimeout.ValueChanged += ChatSetting_Changed;
        _nudMaxMessageChars.ValueChanged += ChatSetting_Changed;
        _nudPollInterval.ValueChanged += ChatSetting_Changed;

        _cmbLogLevel = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 2, 12, 2),
        };
        _cmbLogLevel.Items.AddRange(["Errors only", "Connectivity & errors", "Full (verbose)"]);
        _cmbLogLevel.SelectedIndexChanged += ChatSetting_Changed;

        inner.Controls.Add(_chkSendTool, 0, 0);
        inner.Controls.Add(_chkAskTool, 1, 0);
        inner.Controls.Add(_chkReadTool, 2, 0);
        inner.Controls.Add(_chkChannelsTool, 3, 0);

        inner.Controls.Add(MakeCaption("Ask timeout (s):"), 0, 1);
        inner.Controls.Add(_nudAskTimeout, 1, 1);
        inner.Controls.Add(MakeCaption("Max message (chars):"), 2, 1);
        inner.Controls.Add(_nudMaxMessageChars, 3, 1);
        inner.Controls.Add(MakeCaption("Default poll interval (s):"), 0, 2);
        inner.Controls.Add(_nudPollInterval, 1, 2);
        inner.Controls.Add(MakeCaption("MCP log detail:"), 2, 2);
        inner.Controls.Add(_cmbLogLevel, 3, 2);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildChannelsGroup()
    {
        GroupBox group = new() { Text = "Channels", Dock = DockStyle.Fill, Height = 220, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstChannels = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _lstChannels.Columns.Add("Name", 130);
        _lstChannels.Columns.Add("Provider", 90);
        _lstChannels.Columns.Add("Target", 180);
        _lstChannels.Columns.Add("Enabled", 60);
        _lstChannels.Columns.Add("Status", 220);
        _lstChannels.SelectedIndexChanged += (_, _) => UpdateChannelButtons();
        _lstChannels.DoubleClick += (_, _) => BtnEditChannel_Click(this, EventArgs.Empty);
        inner.Controls.Add(_lstChannels, 0, 0);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnAddChannel = new Button { Text = "Add..." };
        _btnAddChannel.Click += BtnAddChannel_Click;
        _btnEditChannel = new Button { Text = "Edit...", Enabled = false };
        _btnEditChannel.Click += BtnEditChannel_Click;
        _btnRemoveChannel = new Button { Text = "Remove", Enabled = false };
        _btnRemoveChannel.Click += BtnRemoveChannel_Click;
        _btnRefresh = new Button { Text = "Refresh" };
        _btnRefresh.Click += (_, _) => RefreshChannels();
        buttons.Controls.Add(_btnAddChannel);
        buttons.Controls.Add(_btnEditChannel);
        buttons.Controls.Add(_btnRemoveChannel);
        buttons.Controls.Add(_btnRefresh);
        inner.Controls.Add(buttons, 0, 1);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildPollingGroup()
    {
        GroupBox group = new() { Text = "Reply Polling", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };

        FlowLayoutPanel inner = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnStartStop = new Button { Text = "Start Polling", MinimumSize = new Size(110, 28) };
        _btnStartStop.Click += BtnStartStop_Click;
        _lblStatus = new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 7, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Stopped",
        };
        inner.Controls.Add(_btnStartStop);
        inner.Controls.Add(_lblStatus);

        group.Controls.Add(inner);
        return group;
    }

    // ── Load / save ─────────────────────────────────────────────────────────

    private void LoadSettingsToUi()
    {
        _loading = true;

        try
        {
            ChatSettings settings = _module.Repository.LoadSettings();
            _chkSendTool.Checked = settings.SendToolEnabled;
            _chkAskTool.Checked = settings.AskToolEnabled;
            _chkReadTool.Checked = settings.ReadToolEnabled;
            _chkChannelsTool.Checked = settings.ChannelsToolEnabled;
            _nudAskTimeout.Value = settings.DefaultAskTimeoutSeconds;
            _nudMaxMessageChars.Value = settings.MaxMessageChars;
            _nudPollInterval.Value = settings.DefaultPollIntervalSeconds;
            _cmbLogLevel.SelectedIndex = (int)settings.McpLogLevel;
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat Interface settings could not be loaded");
            _lblStatus.Text = "Database unavailable; settings could not be loaded.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        if (_loading)
            return;

        try
        {
            _module.Repository.SaveSettings(new ChatSettings
            {
                SendToolEnabled = _chkSendTool.Checked,
                AskToolEnabled = _chkAskTool.Checked,
                ReadToolEnabled = _chkReadTool.Checked,
                ChannelsToolEnabled = _chkChannelsTool.Checked,
                DefaultAskTimeoutSeconds = (int)_nudAskTimeout.Value,
                MaxMessageChars = (int)_nudMaxMessageChars.Value,
                DefaultPollIntervalSeconds = (int)_nudPollInterval.Value,
                McpLogLevel = (ChatMcpLogLevel)Math.Max(_cmbLogLevel.SelectedIndex, 0),
            });
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat Interface settings could not be saved");
            _lblStatus.Text = "Database unavailable; settings were not saved.";
        }
    }

    private void RefreshChannels()
    {
        IReadOnlyList<ChatChannelStatus> statuses;
        try
        {
            statuses = _module.Bridge.ChannelsStatus();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat Interface channels could not be loaded");
            _lblStatus.Text = "Database unavailable; channels could not be loaded.";
            return;
        }

        _lstChannels.BeginUpdate();
        try
        {
            _lstChannels.Items.Clear();
            foreach (ChatChannelStatus status in statuses)
            {
                ListViewItem item = new(status.Name);
                item.SubItems.Add(status.ProviderType);
                item.SubItems.Add(status.Target);
                item.SubItems.Add(status.Enabled ? "yes" : "no");
                item.SubItems.Add(DescribeStatus(status));
                item.Tag = status;
                _lstChannels.Items.Add(item);
            }
        }
        finally
        {
            _lstChannels.EndUpdate();
        }

        UpdateChannelButtons();
    }

    private static string DescribeStatus(ChatChannelStatus status)
    {
        if (!status.Enabled)
            return "disabled";
        if (status.LastError is not null)
            return $"error: {status.LastError}";
        if (!status.CanReceive)
            return "send-only";
        return status.LastPollUtc is not null
            ? $"receive ok (last poll {status.LastPollUtc:HH:mm:ss} UTC)"
            : "receive ready (not polled yet)";
    }

    private void UpdatePollingStatus()
    {
        bool running = _module.IsRunning;
        _btnStartStop.Text = running ? "Stop Polling" : "Start Polling";
        _lblStatus.Text = running ? "Polling running" : "Polling stopped";
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private void OnStatusChanged(object? sender, string status) =>
        SafeBeginInvoke(() =>
        {
            UpdatePollingStatus();
            RefreshChannels();
        });

    /// <summary>Marshals an action to the UI thread, tolerating a disposed/destroyed page.</summary>
    private void SafeBeginInvoke(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // The page's handle was destroyed concurrently.
        }
        catch (InvalidOperationException)
        {
            // The page was disposed between the check and the invoke.
        }
    }

    private void ChatSetting_Changed(object? sender, EventArgs e)
    {
        if (_loading)
            return;

        // Debounce saves so typing/scrolling a numeric field does not hit the DB per keystroke.
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void UpdateChannelButtons()
    {
        bool selected = _lstChannels.SelectedItems.Count > 0;
        _btnEditChannel.Enabled = selected;
        _btnRemoveChannel.Enabled = selected;
    }

    private void BtnAddChannel_Click(object? sender, EventArgs e)
    {
        ChatChannelConfig? channel = ShowChannelDialog(existing: null);
        if (channel is null)
            return;

        try
        {
            if (_module.Repository.FindChannelByName(channel.Name) is not null)
            {
                MessageBox.Show(this, $"A chat channel named '{channel.Name}' already exists.",
                    "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _module.Repository.InsertChannel(channel);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat channel {Channel} could not be added", channel.Name);
            _lblStatus.Text = "Database unavailable; the channel was not saved.";
        }

        RefreshChannels();
    }

    private void BtnEditChannel_Click(object? sender, EventArgs e)
    {
        if (_lstChannels.SelectedItems.Count == 0 || _lstChannels.SelectedItems[0].Tag is not ChatChannelStatus status)
            return;

        try
        {
            ChatChannelConfig? existing = _module.Repository.FindChannelByName(status.Name);
            if (existing is null)
            {
                RefreshChannels();
                return;
            }

            ChatChannelConfig? edited = ShowChannelDialog(existing);
            if (edited is null)
                return;

            ChatChannelConfig? duplicate = _module.Repository.FindChannelByName(edited.Name);
            if (duplicate is not null && duplicate.Id != existing.Id)
            {
                MessageBox.Show(this, $"A chat channel named '{edited.Name}' already exists.",
                    "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _module.Repository.UpdateChannel(edited);
            _module.Bridge.ResetChannelState(existing.Id);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat channel {Channel} could not be edited", status.Name);
            _lblStatus.Text = "Database unavailable; the channel was not saved.";
        }

        RefreshChannels();
    }

    private void BtnRemoveChannel_Click(object? sender, EventArgs e)
    {
        if (_lstChannels.SelectedItems.Count == 0 || _lstChannels.SelectedItems[0].Tag is not ChatChannelStatus status)
            return;

        if (MessageBox.Show(this,
                $"Remove the chat channel '{status.Name}'?\nPending asks on it are cancelled.",
                "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ChatChannelConfig? existing = _module.Repository.FindChannelByName(status.Name);
            if (existing is not null)
            {
                _module.Repository.DeleteChannel(existing.Id);
                _module.Bridge.ResetChannelState(existing.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Chat channel {Channel} could not be removed", status.Name);
            _lblStatus.Text = "Database unavailable; the channel was not removed.";
        }

        RefreshChannels();
    }

    private ChatChannelConfig? ShowChannelDialog(ChatChannelConfig? existing)
    {
        IReadOnlyList<string> credentialNames;
        try
        {
            credentialNames = _module.Secrets.ListCredentialNames();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            Log.Warning(ex, "Credential names could not be listed for the chat channel dialog");
            credentialNames = [];
        }

        return ChatChannelDialog.ShowAddEditDialog(this, _module.Secrets, credentialNames, existing);
    }

    private async void BtnStartStop_Click(object? sender, EventArgs e)
    {
        _btnStartStop.Enabled = false;
        try
        {
            if (_module.IsRunning)
                await _module.StopAsync();
            else
                await _module.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Chat polling start/stop failed");
            _lblStatus.Text = $"Start/stop failed: {ex.Message}";
        }
        finally
        {
            _btnStartStop.Enabled = true;
            UpdatePollingStatus();
            RefreshChannels();
        }
    }

    // ── Control factories ───────────────────────────────────────────────────

    private CheckBox MakeToolCheckBox(string text)
    {
        CheckBox checkBox = new() { Text = text, AutoSize = true, Margin = new Padding(0, 2, 12, 2) };
        checkBox.CheckedChanged += ChatSetting_Changed;
        return checkBox;
    }

    private static Label MakeCaption(string text) => new()
    {
        AutoSize = true,
        Margin = new Padding(0, 6, 6, 4),
        Text = text,
    };

    private static NumericUpDown MakeNud(int min, int max, int increment) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = increment,
        Margin = new Padding(0, 4, 16, 4),
        Width = 100,
    };
}