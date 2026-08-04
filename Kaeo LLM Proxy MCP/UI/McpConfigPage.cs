using Kaeo.LlmProxy.Mcp.Core.Models;

namespace Kaeo.LlmProxy.Mcp.UI;

/// <summary>
/// The module's configuration tab page injected into the host dashboard. Two sub-tabs:
/// <c>Server</c> (enable/disable, endpoint, authentication, status) and <c>Web Search</c>
/// (tool toggles, providers, domain rules, limits). All edits save immediately; endpoint and
/// authentication changes restart the running server on the fly.
/// </summary>
internal sealed class McpConfigPage : TabPage
{
    private readonly McpModule _module;
    private bool _loading;

    // Server sub-tab
    private CheckBox _chkEnabled = null!;
    private TextBox _txtListenAddress = null!;
    private TextBox _txtPort = null!;
    private ComboBox _cmbAuthCredential = null!;
    private Label _lblEndpoint = null!;
    private Label _lblScalar = null!;
    private Label _lblStatus = null!;
    private Button _btnStart = null!;
    private Button _btnStop = null!;

    // Web Search sub-tab
    private CheckBox _chkWebSearchTool = null!;
    private CheckBox _chkWebFetchTool = null!;
    private ListView _lstProviders = null!;
    private Button _btnToggleProvider = null!;
    private Button _btnConfigureProvider = null!;
    private ListView _lstDomainRules = null!;
    private Button _btnAddAllow = null!;
    private Button _btnAddDeny = null!;
    private Button _btnRemoveRule = null!;
    private NumericUpDown _nudMaxResults = null!;
    private NumericUpDown _nudTimeout = null!;
    private NumericUpDown _nudMaxBytes = null!;
    private CheckBox _chkAllowLocal = null!;

    public McpConfigPage(McpModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));

        Text = "MCP Config";
        Padding = new Padding(8);

        TabControl tabs = new() { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildServerTab());
        tabs.TabPages.Add(BuildWebSearchTab());
        Controls.Add(tabs);

        _module.StatusChanged += OnModuleStatusChanged;
        LoadSettingsToUi();
        RefreshStatusLabels();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _module.StatusChanged -= OnModuleStatusChanged;

        base.Dispose(disposing);
    }

    // ── Server sub-tab ──────────────────────────────────────────────────────

    private TabPage BuildServerTab()
    {
        TabPage page = new() { Text = "Server", Padding = new Padding(8) };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _chkEnabled = new CheckBox
        {
            Text = "Enable MCP server (starts automatically with the application)",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8),
        };
        _chkEnabled.CheckedChanged += ChkEnabled_CheckedChanged;
        layout.Controls.Add(_chkEnabled, 0, 0);
        layout.SetColumnSpan(_chkEnabled, 2);

        layout.Controls.Add(MakeCaption("Listen address:"), 0, 1);
        _txtListenAddress = new TextBox { Width = 180, Margin = new Padding(0, 2, 0, 6) };
        _txtListenAddress.Validated += ServerEndpoint_Validated;
        _txtListenAddress.KeyDown += TextBoxEnterApplies;
        layout.Controls.Add(_txtListenAddress, 1, 1);

        layout.Controls.Add(MakeCaption("Port:"), 0, 2);
        _txtPort = new TextBox { Width = 90, Margin = new Padding(0, 2, 0, 6) };
        _txtPort.Validated += ServerEndpoint_Validated;
        _txtPort.KeyDown += TextBoxEnterApplies;
        layout.Controls.Add(_txtPort, 1, 2);

        layout.Controls.Add(MakeCaption("Auth credential:"), 0, 3);
        _cmbAuthCredential = new ComboBox
        {
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 2, 0, 6),
        };
        _cmbAuthCredential.SelectedIndexChanged += CmbAuthCredential_SelectedIndexChanged;
        layout.Controls.Add(_cmbAuthCredential, 1, 3);

        _lblEndpoint = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
        layout.Controls.Add(_lblEndpoint, 0, 4);
        layout.SetColumnSpan(_lblEndpoint, 2);

        _lblScalar = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 2), ForeColor = SystemColors.GrayText };
        layout.Controls.Add(_lblScalar, 0, 5);
        layout.SetColumnSpan(_lblScalar, 2);

        _lblStatus = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Font = new Font(Font, FontStyle.Bold),
        };
        layout.Controls.Add(_lblStatus, 0, 6);
        layout.SetColumnSpan(_lblStatus, 2);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnStart = new Button { Text = "Start", Width = 90 };
        _btnStart.Click += BtnStart_Click;
        _btnStop = new Button { Text = "Stop", Width = 90 };
        _btnStop.Click += BtnStop_Click;
        buttons.Controls.Add(_btnStart);
        buttons.Controls.Add(_btnStop);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 2);

        page.Controls.Add(layout);
        return page;
    }

    // ── Web Search sub-tab ──────────────────────────────────────────────────

    private TabPage BuildWebSearchTab()
    {
        TabPage page = new() { Text = "Web Search", Padding = new Padding(8) };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel toggles = new() { AutoSize = true, ColumnCount = 1, RowCount = 2 };
        toggles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _chkWebSearchTool = new CheckBox { Text = "Enable the web_search tool", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        _chkWebSearchTool.CheckedChanged += WebSetting_Changed;
        _chkWebFetchTool = new CheckBox { Text = "Enable the web_fetch tool", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
        _chkWebFetchTool.CheckedChanged += WebSetting_Changed;
        toggles.Controls.Add(_chkWebSearchTool, 0, 0);
        toggles.Controls.Add(_chkWebFetchTool, 0, 1);
        layout.Controls.Add(toggles, 0, 0);

        layout.Controls.Add(BuildProvidersGroup(), 0, 1);
        layout.Controls.Add(BuildDomainRulesGroup(), 0, 2);
        layout.Controls.Add(BuildLimitsGroup(), 0, 3);

        Label note = new()
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Settings save immediately. When an allow rule exists, only matching domains are reachable.",
        };
        layout.Controls.Add(note, 0, 4);

        page.Controls.Add(layout);
        return page;
    }

    private GroupBox BuildProvidersGroup()
    {
        GroupBox group = new() { Text = "Search providers", Dock = DockStyle.Fill, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstProviders = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _lstProviders.Columns.Add("Name", 110);
        _lstProviders.Columns.Add("Enabled", 70);
        _lstProviders.Columns.Add("Endpoint", 230);
        _lstProviders.Columns.Add("Credential", 110);
        _lstProviders.SelectedIndexChanged += LstProviders_SelectedIndexChanged;
        inner.Controls.Add(_lstProviders, 0, 0);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnToggleProvider = new Button { Text = "Enable", Width = 90, Enabled = false };
        _btnToggleProvider.Click += BtnToggleProvider_Click;
        _btnConfigureProvider = new Button { Text = "Configure...", Enabled = false };
        _btnConfigureProvider.Click += BtnConfigureProvider_Click;
        buttons.Controls.Add(_btnToggleProvider);
        buttons.Controls.Add(_btnConfigureProvider);
        inner.Controls.Add(buttons, 0, 1);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildDomainRulesGroup()
    {
        GroupBox group = new() { Text = "Domain rules", Dock = DockStyle.Fill, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lstDomainRules = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _lstDomainRules.Columns.Add("Type", 70);
        _lstDomainRules.Columns.Add("Pattern", 320);
        _lstDomainRules.SelectedIndexChanged += LstDomainRules_SelectedIndexChanged;
        inner.Controls.Add(_lstDomainRules, 0, 0);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _btnAddAllow = new Button { Text = "Add Allow..." };
        _btnAddAllow.Click += (s, e) => AddDomainRule(DomainRuleType.Allow);
        _btnAddDeny = new Button { Text = "Add Deny..." };
        _btnAddDeny.Click += (s, e) => AddDomainRule(DomainRuleType.Deny);
        _btnRemoveRule = new Button { Text = "Remove", Enabled = false };
        _btnRemoveRule.Click += BtnRemoveRule_Click;
        buttons.Controls.Add(_btnAddAllow);
        buttons.Controls.Add(_btnAddDeny);
        buttons.Controls.Add(_btnRemoveRule);
        inner.Controls.Add(buttons, 0, 1);

        group.Controls.Add(inner);
        return group;
    }

    private GroupBox BuildLimitsGroup()
    {
        GroupBox group = new() { Text = "Limits", AutoSize = true, Padding = new Padding(6) };

        TableLayoutPanel inner = new() { AutoSize = true, ColumnCount = 4, RowCount = 2 };
        for (int i = 0; i < 4; i++)
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _nudMaxResults = MakeNud(1, 20, 1);
        _nudTimeout = MakeNud(5, 120, 5);
        _nudMaxBytes = MakeNud(10_000, 2_000_000, 10_000);
        _nudMaxResults.ValueChanged += WebSetting_Changed;
        _nudTimeout.ValueChanged += WebSetting_Changed;
        _nudMaxBytes.ValueChanged += WebSetting_Changed;

        _chkAllowLocal = new CheckBox { Text = "Allow local/private networks", AutoSize = true, Margin = new Padding(8, 4, 0, 4) };
        _chkAllowLocal.CheckedChanged += WebSetting_Changed;

        inner.Controls.Add(MakeCaption("Max results:"), 0, 0);
        inner.Controls.Add(_nudMaxResults, 1, 0);
        inner.Controls.Add(MakeCaption("Timeout (seconds):"), 2, 0);
        inner.Controls.Add(_nudTimeout, 3, 0);
        inner.Controls.Add(MakeCaption("Max page size (bytes):"), 0, 1);
        inner.Controls.Add(_nudMaxBytes, 1, 1);
        inner.Controls.Add(_chkAllowLocal, 2, 1);
        inner.SetColumnSpan(_chkAllowLocal, 2);

        group.Controls.Add(inner);
        return group;
    }

    // ── Load / save ─────────────────────────────────────────────────────────

    private void LoadSettingsToUi()
    {
        _loading = true;

        try
        {
            McpServerSettings server = _module.Repository.LoadServerSettings();
            _chkEnabled.Checked = server.Enabled;
            _txtListenAddress.Text = server.ListenAddress;
            _txtPort.Text = server.ListenPort.ToString();

            _cmbAuthCredential.Items.Clear();
            _cmbAuthCredential.Items.Add("(None)");
            int selectedIndex = 0;
            foreach (string credential in _module.Secrets.ListCredentialNames())
            {
                _cmbAuthCredential.Items.Add(credential);
                if (string.Equals(credential, server.AuthCredentialName, StringComparison.OrdinalIgnoreCase))
                    selectedIndex = _cmbAuthCredential.Items.Count - 1;
            }
            _cmbAuthCredential.SelectedIndex = selectedIndex;

            WebSearchSettings web = _module.Repository.LoadWebSearchSettings();
            _chkWebSearchTool.Checked = web.WebSearchToolEnabled;
            _chkWebFetchTool.Checked = web.WebFetchToolEnabled;
            _nudMaxResults.Value = web.MaxResults;
            _nudTimeout.Value = web.TimeoutSeconds;
            _nudMaxBytes.Value = web.MaxResponseBytes;
            _chkAllowLocal.Checked = web.AllowLocalNetworks;
        }
        finally
        {
            _loading = false;
        }

        RefreshProviders();
        RefreshDomainRules();
    }

    private void RefreshProviders()
    {
        _lstProviders.BeginUpdate();
        try
        {
            _lstProviders.Items.Clear();
            foreach (SearchProviderConfig provider in _module.Repository.LoadProviders())
            {
                ListViewItem item = new(provider.Name);
                item.SubItems.Add(provider.IsEnabled ? "Yes" : "No");
                item.SubItems.Add(provider.Endpoint);
                item.SubItems.Add(provider.CredentialName ?? string.Empty);
                item.Tag = provider;
                _lstProviders.Items.Add(item);
            }
        }
        finally
        {
            _lstProviders.EndUpdate();
        }

        UpdateProviderButtons();
    }

    private void RefreshDomainRules()
    {
        _lstDomainRules.BeginUpdate();
        try
        {
            _lstDomainRules.Items.Clear();
            foreach (DomainRule rule in _module.Repository.LoadDomainRules())
            {
                ListViewItem item = new(rule.RuleType == DomainRuleType.Allow ? "Allow" : "Deny");
                item.SubItems.Add(rule.Pattern);
                item.Tag = rule;
                _lstDomainRules.Items.Add(item);
            }
        }
        finally
        {
            _lstDomainRules.EndUpdate();
        }

        _btnRemoveRule.Enabled = _lstDomainRules.SelectedItems.Count > 0;
    }

    private void SaveWebSettings()
    {
        if (_loading)
            return;

        _module.Repository.SaveWebSearchSettings(new WebSearchSettings
        {
            WebSearchToolEnabled = _chkWebSearchTool.Checked,
            WebFetchToolEnabled = _chkWebFetchTool.Checked,
            MaxResults = (int)_nudMaxResults.Value,
            TimeoutSeconds = (int)_nudTimeout.Value,
            MaxResponseBytes = (int)_nudMaxBytes.Value,
            AllowLocalNetworks = _chkAllowLocal.Checked,
        });
    }

    private void SaveServerSettings()
    {
        string address = _txtListenAddress.Text.Trim();
        int port = int.TryParse(_txtPort.Text.Trim(), out int parsed) ? parsed : McpServerSettings.DefaultPort;

        _module.Repository.SaveServerSettings(new McpServerSettings
        {
            Enabled = _chkEnabled.Checked,
            ListenAddress = address.Length == 0 ? "localhost" : address,
            ListenPort = Math.Clamp(port, McpServerSettings.MinPort, McpServerSettings.MaxPort),
            AuthCredentialName = _cmbAuthCredential.SelectedIndex <= 0
                ? null
                : _cmbAuthCredential.SelectedItem as string,
        });

        _txtPort.Text = Math.Clamp(port, McpServerSettings.MinPort, McpServerSettings.MaxPort).ToString();
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private async void ChkEnabled_CheckedChanged(object? sender, EventArgs e)
    {
        if (_loading)
            return;

        SaveServerSettings();
        await ApplyServerSettingsSafeAsync();
    }

    private async void ServerEndpoint_Validated(object? sender, EventArgs e)
    {
        if (_loading)
            return;

        SaveServerSettings();
        await ApplyServerSettingsSafeAsync();
    }

    private void TextBoxEnterApplies(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
            return;

        e.SuppressKeyPress = true;
        ((Control)sender!).Focus(); // triggers Validated
        _chkEnabled.Focus();
    }

    private async void CmbAuthCredential_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_loading)
            return;

        SaveServerSettings();
        await ApplyServerSettingsSafeAsync();
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (!_chkEnabled.Checked)
            _chkEnabled.Checked = true; // handler applies
        else
            _ = ApplyServerSettingsSafeAsync();
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        if (_chkEnabled.Checked)
            _chkEnabled.Checked = false; // handler applies
    }

    private async Task ApplyServerSettingsSafeAsync()
    {
        try
        {
            await _module.ApplyServerSettingsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to apply MCP server settings:\n\n{ex.Message}",
                "MCP Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            RefreshStatusLabels();
        }
    }

    private void WebSetting_Changed(object? sender, EventArgs e) => SaveWebSettings();

    private void LstProviders_SelectedIndexChanged(object? sender, EventArgs e) => UpdateProviderButtons();

    private void UpdateProviderButtons()
    {
        bool selected = _lstProviders.SelectedItems.Count > 0;
        _btnConfigureProvider.Enabled = selected;

        if (selected && _lstProviders.SelectedItems[0].Tag is SearchProviderConfig provider)
        {
            _btnToggleProvider.Enabled = true;
            _btnToggleProvider.Text = provider.IsEnabled ? "Disable" : "Enable";
        }
        else
        {
            _btnToggleProvider.Enabled = false;
            _btnToggleProvider.Text = "Enable";
        }
    }

    private void BtnToggleProvider_Click(object? sender, EventArgs e)
    {
        if (_lstProviders.SelectedItems.Count == 0
            || _lstProviders.SelectedItems[0].Tag is not SearchProviderConfig provider)
        {
            return;
        }

        provider.IsEnabled = !provider.IsEnabled;
        _module.Repository.UpsertProvider(provider);
        RefreshProviders();
    }

    private void BtnConfigureProvider_Click(object? sender, EventArgs e)
    {
        if (_lstProviders.SelectedItems.Count == 0
            || _lstProviders.SelectedItems[0].Tag is not SearchProviderConfig provider)
        {
            return;
        }

        bool requiresKey = provider.Name is "Brave" or "Bing";

        using ProviderConfigDialog dialog = new(provider, _module.Secrets.ListCredentialNames(), requiresKey);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _module.Repository.UpsertProvider(provider);
            RefreshProviders();
        }
    }

    private void LstDomainRules_SelectedIndexChanged(object? sender, EventArgs e) =>
        _btnRemoveRule.Enabled = _lstDomainRules.SelectedItems.Count > 0;

    private void AddDomainRule(DomainRuleType ruleType)
    {
        string title = ruleType == DomainRuleType.Allow ? "Add Allow Rule" : "Add Deny Rule";

        using TextPromptDialog dialog = new(title, "Domain pattern (e.g. example.com or *.example.com):");
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string pattern = dialog.Value.Trim();
        if (pattern.Length == 0)
            return;

        try
        {
            _module.Repository.AddDomainRule(ruleType, pattern);
            RefreshDomainRules();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to add domain rule:\n\n{ex.Message}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BtnRemoveRule_Click(object? sender, EventArgs e)
    {
        if (_lstDomainRules.SelectedItems.Count == 0
            || _lstDomainRules.SelectedItems[0].Tag is not DomainRule rule)
        {
            return;
        }

        _module.Repository.RemoveDomainRule(rule.Id);
        RefreshDomainRules();
    }

    private void OnModuleStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        BeginInvoke(RefreshStatusLabels);
    }

    private void RefreshStatusLabels()
    {
        _lblStatus.Text = $"Status: {_module.Status}";
        _lblEndpoint.Text = $"MCP endpoint: {_module.EndpointUrl ?? "(stopped)"}";
        _lblScalar.Text = $"API explorer: {_module.ScalarUrl ?? "(stopped)"}";
    }

    // ── Small helpers ───────────────────────────────────────────────────────

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 6, 8, 6),
    };

    private static NumericUpDown MakeNud(int min, int max, int increment) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = increment,
        Width = 110,
        Margin = new Padding(0, 2, 16, 6),
        ThousandsSeparator = true,
    };
}

/// <summary>Edits a search provider's endpoint, credential, and enabled flag.</summary>
internal sealed class ProviderConfigDialog : Form
{
    private readonly SearchProviderConfig _provider;
    private readonly TextBox _txtEndpoint;
    private readonly ComboBox _cmbCredential;
    private readonly CheckBox _chkEnabled;

    public ProviderConfigDialog(SearchProviderConfig provider, IReadOnlyList<string> credentialNames, bool requiresKey)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        Text = $"Configure {_provider.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 190);

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 4; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _chkEnabled = new CheckBox { Text = "Provider enabled", AutoSize = true, Checked = _provider.IsEnabled };
        layout.Controls.Add(_chkEnabled, 0, 0);
        layout.SetColumnSpan(_chkEnabled, 2);

        layout.Controls.Add(new Label { Text = "Endpoint:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 1);
        _txtEndpoint = new TextBox { Dock = DockStyle.Fill, Text = _provider.Endpoint, Margin = new Padding(0, 6, 0, 6) };
        layout.Controls.Add(_txtEndpoint, 1, 1);

        Label credentialCaption = new()
        {
            Text = requiresKey ? "API key credential:" : "API key credential (optional):",
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 0),
        };
        layout.Controls.Add(credentialCaption, 0, 2);
        _cmbCredential = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 0, 8) };
        _cmbCredential.Items.Add("(None)");
        foreach (string name in credentialNames)
            _cmbCredential.Items.Add(name);
        _cmbCredential.SelectedIndex = 0;
        for (int i = 1; i < _cmbCredential.Items.Count; i++)
        {
            if (string.Equals(_cmbCredential.Items[i] as string, _provider.CredentialName, StringComparison.OrdinalIgnoreCase))
            {
                _cmbCredential.SelectedIndex = i;
                break;
            }
        }
        layout.Controls.Add(_cmbCredential, 1, 2);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 84 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 84 };
        ok.Click += Ok_Click;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Ok_Click(object? sender, EventArgs e)
    {
        string endpoint = _txtEndpoint.Text.Trim();
        if (endpoint.Length == 0)
        {
            MessageBox.Show(this, "The endpoint must not be empty.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        _provider.IsEnabled = _chkEnabled.Checked;
        _provider.Endpoint = endpoint;
        _provider.CredentialName = _cmbCredential.SelectedIndex <= 0 ? null : _cmbCredential.SelectedItem as string;
    }
}

/// <summary>Minimal single-text input dialog used for domain rule patterns.</summary>
internal sealed class TextPromptDialog : Form
{
    private readonly TextBox _txtValue;

    public string Value => _txtValue.Text;

    public TextPromptDialog(string title, string caption)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 120);

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        _txtValue = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_txtValue, 0, 1);

        FlowLayoutPanel buttons = new() { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 84 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 84 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
