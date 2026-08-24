using Kaeo.LlmProxy.Core.Modules;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Data.Common;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Module.WebSearch;

/// <summary>
/// The module's configuration tab page injected into the host dashboard: web_search/web_fetch
/// tool toggles, search providers, domain rules, and limits. All edits save immediately and
/// apply to new MCP tool invocations without restarting the server.
/// </summary>
internal sealed class WebSearchConfigPage : TabPage
{
    private readonly WebSearchModule _module;
    private bool _loading;

    // Web Search controls
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
    private Button _btnSafetyInfo = null!;

    public WebSearchConfigPage(WebSearchModule module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));

        Text = "Web Search";
        Padding = new Padding(8);
        AutoScroll = true;

        Controls.Add(BuildWebSearchContent());

        LoadSettingsToUi();
    }

    private TableLayoutPanel BuildWebSearchContent()
    {
        // AutoSize + Dock.Top inside the AutoScroll page: the tab scrolls vertically whenever
        // the stacked content overflows instead of crushing the tables.
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 5; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel toggles = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        toggles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        toggles.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        toggles.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _chkWebSearchTool = new CheckBox { Text = "Enable the web_search tool", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        _chkWebSearchTool.CheckedChanged += WebSetting_Changed;
        _chkWebFetchTool = new CheckBox { Text = "Enable the web_fetch tool", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
        _chkWebFetchTool.CheckedChanged += WebSetting_Changed;
        toggles.Controls.Add(_chkWebSearchTool, 0, 0);
        toggles.Controls.Add(_chkWebFetchTool, 0, 1);

        // Opens the safety-precautions reference dialog; sits at the top-right of the page.
        _btnSafetyInfo = new Button
        {
            Text = "Module Information",
            AutoSize = true,
            AccessibleName = "Module Information",
            AccessibleDescription = "Opens a dialog explaining every precaution that protects web search.",
            Margin = new Padding(0, 2, 0, 2),
        };
        _btnSafetyInfo.Click += BtnSafetyInfo_Click;
        toggles.Controls.Add(_btnSafetyInfo, 1, 0);
        toggles.SetRowSpan(_btnSafetyInfo, 2);
        layout.Controls.Add(toggles, 0, 0);

        // Non-table settings first, then the two tables.
        layout.Controls.Add(BuildLimitsGroup(), 0, 1);
        layout.Controls.Add(BuildProvidersGroup(), 0, 2);
        layout.Controls.Add(BuildDomainRulesGroup(), 0, 3);

        Label note = new()
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Settings save immediately. When an allow rule exists, only matching domains are reachable.",
        };
        layout.Controls.Add(note, 0, 4);

        return layout;
    }

    private GroupBox BuildProvidersGroup()
    {
        GroupBox group = new() { Text = "Search providers", Dock = DockStyle.Fill, Height = 180, Padding = new Padding(6) };

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
        GroupBox group = new() { Text = "Domain rules", Dock = DockStyle.Fill, Height = 180, Padding = new Padding(6) };

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
        GroupBox group = new() { Text = "Limits", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };

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

    private void BtnSafetyInfo_Click(object? sender, EventArgs e)
    {
        using WebSearchSafetyDialog dialog = new();
        dialog.ShowDialog(FindForm());
    }

    // ── Load / save ─────────────────────────────────────────────────────────

    private void LoadSettingsToUi()
    {
        _loading = true;

        try
        {
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

    // ── Event handlers ──────────────────────────────────────────────────────

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
