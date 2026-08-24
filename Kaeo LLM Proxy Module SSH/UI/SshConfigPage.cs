using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Renci.SshNet.Common;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// The module's configuration tab page injected into the host dashboard. Everything SSH
/// specific lives here: tool toggles and limits, stored connection profiles, and the table
/// of currently open connections (including which client opened each one) with refresh and
/// disconnect controls. All edits save immediately.
/// </summary>
internal sealed class SshConfigPage : TabPage
{
    private readonly SshModule _module;
    private bool _loading;

    // Tools & limits controls
    private CheckBox _chkConnectTool = null!;
    private CheckBox _chkExecTool = null!;
    private CheckBox _chkDisconnectTool = null!;
    private CheckBox _chkListTool = null!;
    private NumericUpDown _nudIdleTimeout = null!;
    private NumericUpDown _nudCommandTimeout = null!;
    private NumericUpDown _nudMaxOutput = null!;
    private ComboBox _cmbLogLevel = null!;

    // Stored connections controls
    private ListView _lstConnections = null!;
    private Button _btnAddConnection = null!;
    private Button _btnEditConnection = null!;
    private Button _btnRemoveConnection = null!;

    // Open connections controls
    private ListView _lstOpen = null!;
    private Button _btnRefreshOpen = null!;
    private Button _btnDisconnect = null!;
    private Button _btnDisconnectAll = null!;

    public SshConfigPage(SshModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));

        Text = "SSH Command";
        Padding = new Padding(8);
        AutoScroll = true;

        Controls.Add(BuildContent());

        LoadSettingsToUi();
        RefreshStoredConnections();
        RefreshOpenConnections();

        // Keep the open-connections table current as connections open, close, or idle out.
        _module.Manager.ConnectionsChanged += OnConnectionsChanged;
        HandleDestroyed += (_, _) => _module.Manager.ConnectionsChanged -= OnConnectionsChanged;
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
        layout.Controls.Add(BuildStoredConnectionsGroup(), 0, 1);
        layout.Controls.Add(BuildOpenConnectionsGroup(), 0, 2);

        Label note = new()
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Settings save immediately. Connections close automatically after their idle timeout; " +
                "0 means never. Prefer stored credentials over passing literal passwords to the tools.",
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

        _chkConnectTool = MakeToolCheckBox("Enable ssh_connect");
        _chkExecTool = MakeToolCheckBox("Enable ssh_exec");
        _chkDisconnectTool = MakeToolCheckBox("Enable ssh_disconnect");
        _chkListTool = MakeToolCheckBox("Enable ssh_list");

        _nudIdleTimeout = MakeNud(0, 86_400, 60);
        _nudCommandTimeout = MakeNud(5, 3_600, 5);
        _nudMaxOutput = MakeNud(1_000, 200_000, 1_000);
        _nudIdleTimeout.ValueChanged += SshSetting_Changed;
        _nudCommandTimeout.ValueChanged += SshSetting_Changed;
        _nudMaxOutput.ValueChanged += SshSetting_Changed;

        _cmbLogLevel = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 2, 12, 2),
        };
        _cmbLogLevel.Items.AddRange(["Connectivity & errors", "Full (verbose)"]);
        _cmbLogLevel.SelectedIndexChanged += SshSetting_Changed;

        inner.Controls.Add(_chkConnectTool, 0, 0);
        inner.Controls.Add(_chkExecTool, 1, 0);
        inner.Controls.Add(_chkDisconnectTool, 2, 0);
        inner.Controls.Add(_chkListTool, 3, 0);

        inner.Controls.Add(MakeCaption("Default idle timeout (s):"), 0, 1);
        inner.Controls.Add(_nudIdleTimeout, 1, 1);
        inner.Controls.Add(MakeCaption("Command timeout (s):"), 2, 1);
        inner.Controls.Add(_nudCommandTimeout, 3, 1);
        inner.Controls.Add(MakeCaption("Max output (chars):"), 0, 2);
        inner.Controls.Add(_nudMaxOutput, 1, 2);
        inner.Controls.Add(MakeCaption("MCP log detail:"), 2, 2);
        inner.Controls.Add(_cmbLogLevel, 3, 2);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildStoredConnectionsGroup()
    {
        GroupBox group = new() { Text = "Stored Connections", Dock = DockStyle.Fill, Height = 180, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstConnections = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _lstConnections.Columns.Add("Name", 130);
        _lstConnections.Columns.Add("Host", 160);
        _lstConnections.Columns.Add("Port", 50);
        _lstConnections.Columns.Add("Username", 100);
        _lstConnections.Columns.Add("Credential", 130);
        _lstConnections.Columns.Add("Idle (s)", 60);
        _lstConnections.SelectedIndexChanged += LstConnections_SelectedIndexChanged;
        _lstConnections.DoubleClick += (_, _) => BtnEditConnection_Click(this, EventArgs.Empty);
        inner.Controls.Add(_lstConnections, 0, 0);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnAddConnection = new Button { Text = "Add..." };
        _btnAddConnection.Click += BtnAddConnection_Click;
        _btnEditConnection = new Button { Text = "Edit...", Enabled = false };
        _btnEditConnection.Click += BtnEditConnection_Click;
        _btnRemoveConnection = new Button { Text = "Remove", Enabled = false };
        _btnRemoveConnection.Click += BtnRemoveConnection_Click;
        buttons.Controls.Add(_btnAddConnection);
        buttons.Controls.Add(_btnEditConnection);
        buttons.Controls.Add(_btnRemoveConnection);
        inner.Controls.Add(buttons, 0, 1);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildOpenConnectionsGroup()
    {
        GroupBox group = new() { Text = "Open Connections", Dock = DockStyle.Fill, Height = 200, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstOpen = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _lstOpen.Columns.Add("Connection", 140);
        _lstOpen.Columns.Add("Target", 170);
        _lstOpen.Columns.Add("Opened By", 120);
        _lstOpen.Columns.Add("MCP Session", 110);
        _lstOpen.Columns.Add("Opened (UTC)", 140);
        _lstOpen.Columns.Add("Idle (s)", 60);
        _lstOpen.SelectedIndexChanged += LstOpen_SelectedIndexChanged;
        inner.Controls.Add(_lstOpen, 0, 0);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnRefreshOpen = new Button { Text = "Refresh" };
        _btnRefreshOpen.Click += (_, _) => RefreshOpenConnections();
        _btnDisconnect = new Button { Text = "Disconnect", Enabled = false };
        _btnDisconnect.Click += BtnDisconnect_Click;
        _btnDisconnectAll = new Button { Text = "Disconnect All", Enabled = false };
        _btnDisconnectAll.Click += BtnDisconnectAll_Click;
        buttons.Controls.Add(_btnRefreshOpen);
        buttons.Controls.Add(_btnDisconnect);
        buttons.Controls.Add(_btnDisconnectAll);
        inner.Controls.Add(buttons, 0, 1);

        group.Controls.Add(inner);
        return group;
    }

    // ── Load / save ─────────────────────────────────────────────────────────

    private void LoadSettingsToUi()
    {
        _loading = true;

        try
        {
            SshSettings settings = _module.Repository.LoadSettings();
            _chkConnectTool.Checked = settings.ConnectToolEnabled;
            _chkExecTool.Checked = settings.ExecToolEnabled;
            _chkDisconnectTool.Checked = settings.DisconnectToolEnabled;
            _chkListTool.Checked = settings.ListToolEnabled;
            _nudIdleTimeout.Value = settings.DefaultIdleTimeoutSeconds;
            _nudCommandTimeout.Value = settings.CommandTimeoutSeconds;
            _nudMaxOutput.Value = settings.MaxOutputChars;
            _cmbLogLevel.SelectedIndex = settings.McpLogLevel == SshMcpLogLevel.Full ? 1 : 0;
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

        _module.Repository.SaveSettings(new SshSettings
        {
            ConnectToolEnabled = _chkConnectTool.Checked,
            ExecToolEnabled = _chkExecTool.Checked,
            DisconnectToolEnabled = _chkDisconnectTool.Checked,
            ListToolEnabled = _chkListTool.Checked,
            DefaultIdleTimeoutSeconds = (int)_nudIdleTimeout.Value,
            CommandTimeoutSeconds = (int)_nudCommandTimeout.Value,
            MaxOutputChars = (int)_nudMaxOutput.Value,
            McpLogLevel = _cmbLogLevel.SelectedIndex == 1 ? SshMcpLogLevel.Full : SshMcpLogLevel.Connectivity,
        });
    }

    private void RefreshStoredConnections()
    {
        _lstConnections.BeginUpdate();
        try
        {
            _lstConnections.Items.Clear();
            foreach (SshStoredConnection connection in _module.Repository.LoadConnections())
            {
                ListViewItem item = new(connection.Name);
                item.SubItems.Add(connection.Host);
                item.SubItems.Add(connection.Port.ToString());
                item.SubItems.Add(connection.Username);
                item.SubItems.Add(connection.CredentialName ?? string.Empty);
                item.SubItems.Add(connection.IdleTimeoutSeconds > 0 ? connection.IdleTimeoutSeconds.ToString() : "default");
                item.Tag = connection;
                _lstConnections.Items.Add(item);
            }
        }
        finally
        {
            _lstConnections.EndUpdate();
        }

        UpdateConnectionButtons();
    }

    private void RefreshOpenConnections()
    {
        _lstOpen.BeginUpdate();
        try
        {
            _lstOpen.Items.Clear();
            foreach (OpenSshConnectionInfo info in _module.Manager.GetSnapshot())
            {
                TimeSpan idle = DateTime.UtcNow - info.LastActivityUtc;

                ListViewItem item = new(info.Key);
                item.SubItems.Add($"{info.Username}@{info.Host}:{info.Port}");
                item.SubItems.Add(info.OpenedByClientAddress ?? "unknown");
                item.SubItems.Add(info.McpSessionId ?? string.Empty);
                item.SubItems.Add(info.OpenedUtc.ToString("u"));
                item.SubItems.Add($"{idle.TotalSeconds:F0}");
                item.Tag = info;
                _lstOpen.Items.Add(item);
            }
        }
        finally
        {
            _lstOpen.EndUpdate();
        }

        _btnDisconnectAll.Enabled = _lstOpen.Items.Count > 0;
        _btnDisconnect.Enabled = _lstOpen.SelectedItems.Count > 0;
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private void OnConnectionsChanged(object? sender, EventArgs e)
    {
        // Raised from tool/sweep threads; marshal to the UI thread when the page is alive.
        if (IsDisposed || !IsHandleCreated)
            return;

        BeginInvoke(RefreshOpenConnections);
    }

    private void SshSetting_Changed(object? sender, EventArgs e) => SaveSettings();

    private void LstConnections_SelectedIndexChanged(object? sender, EventArgs e) => UpdateConnectionButtons();

    private void LstOpen_SelectedIndexChanged(object? sender, EventArgs e) =>
        _btnDisconnect.Enabled = _lstOpen.SelectedItems.Count > 0;

    private void UpdateConnectionButtons()
    {
        bool selected = _lstConnections.SelectedItems.Count > 0;
        _btnEditConnection.Enabled = selected;
        _btnRemoveConnection.Enabled = selected;
    }

    private void BtnAddConnection_Click(object? sender, EventArgs e)
    {
        SshStoredConnection? connection = SshConnectionDialog.ShowAddEditDialog(
            this, _module.Secrets.ListCredentialNames());

        if (connection is null)
            return;

        if (_module.Repository.FindConnectionByName(connection.Name) is not null)
        {
            MessageBox.Show(this, $"A stored connection named '{connection.Name}' already exists.",
                "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _module.Repository.InsertConnection(connection);
        RefreshStoredConnections();
    }

    private void BtnEditConnection_Click(object? sender, EventArgs e)
    {
        if (_lstConnections.SelectedItems.Count == 0 || _lstConnections.SelectedItems[0].Tag is not SshStoredConnection existing)
            return;

        SshStoredConnection? edited = SshConnectionDialog.ShowAddEditDialog(
            this, _module.Secrets.ListCredentialNames(), existing);

        if (edited is null)
            return;

        SshStoredConnection? duplicate = _module.Repository.FindConnectionByName(edited.Name);
        if (duplicate is not null && duplicate.Id != existing.Id)
        {
            MessageBox.Show(this, $"A stored connection named '{edited.Name}' already exists.",
                "Duplicate Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _module.Repository.UpdateConnection(edited);
        RefreshStoredConnections();
    }

    private void BtnRemoveConnection_Click(object? sender, EventArgs e)
    {
        if (_lstConnections.SelectedItems.Count == 0 || _lstConnections.SelectedItems[0].Tag is not SshStoredConnection connection)
            return;

        if (MessageBox.Show(this,
                $"Remove the stored connection '{connection.Name}'?\nOpen sessions using it are not affected.",
                "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _module.Repository.DeleteConnection(connection.Id);
        RefreshStoredConnections();
    }

    private void BtnDisconnect_Click(object? sender, EventArgs e)
    {
        if (_lstOpen.SelectedItems.Count == 0 || _lstOpen.SelectedItems[0].Tag is not OpenSshConnectionInfo info)
            return;

        _module.Manager.Disconnect(info.Key);
        RefreshOpenConnections();
    }

    private void BtnDisconnectAll_Click(object? sender, EventArgs e)
    {
        if (_lstOpen.Items.Count == 0)
            return;

        if (MessageBox.Show(this,
                $"Disconnect all {_lstOpen.Items.Count} open SSH connection(s)?",
                "Confirm Disconnect All", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _module.Manager.DisconnectAll();
        RefreshOpenConnections();
    }

    // ── Control factories ───────────────────────────────────────────────────

    private CheckBox MakeToolCheckBox(string text)
    {
        CheckBox checkBox = new() { Text = text, AutoSize = true, Margin = new Padding(0, 2, 12, 2) };
        checkBox.CheckedChanged += SshSetting_Changed;
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
