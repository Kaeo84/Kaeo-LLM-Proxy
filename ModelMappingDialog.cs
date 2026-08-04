using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Kaeo.LlmProxy.Infrastructure;

namespace Kaeo.LlmProxy;

/// <summary>
/// Modal dialog for editing advanced per-model configuration that is not
/// displayed in the main mappings grid. The dialog blocks the main window
/// while open (ShowDialog).
/// </summary>
internal sealed class ModelMappingDialog : Form
{
    private const string NoneLabel = "(None)";

    // Shared client for upstream model discovery. Avoids socket churn when model lists are
    // fetched repeatedly; the fixed 10 s timeout applies to discovery calls only.
    private static readonly HttpClient _modelFetchClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Panel _pnlScroll = new();
    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblProxyName = new();
    private readonly TextBox _txtProxyName = new();
    private readonly Label _lblUpstreamUrl = new();
    private readonly ComboBox _cmbUpstreamUrl = new();
    private readonly Label _lblUpstreamType = new();
    private readonly ComboBox _cmbUpstreamType = new();
    private readonly Label _lblCredential = new();
    private readonly ComboBox _cmbCredential = new();
    private readonly Label _lblModelName = new();
    private readonly ComboBox _cmbModelName = new();
    private readonly Button _btnFetchModels = new();
    private readonly Label _lblInstructionSet = new();
    private readonly ComboBox _cmbInstructionSet = new();
    private readonly Label _lblUpstreamTimeout = new();
    private readonly TextBox _txtUpstreamTimeout = new();
    private readonly Label _lblContextWindow = new();
    private readonly TextBox _txtContextWindow = new();
    private readonly Label _lblTemperature = new();
    private readonly NumericUpDown _nudTemperature = new();
    private readonly Label _lblRepeatPenalty = new();
    private readonly NumericUpDown _nudRepeatPenalty = new();
    private readonly CheckBox _chkIsEnabled = new();
    private readonly Label _lblTempPriority = new();
    private readonly ComboBox _cmbTempPriority = new();
    private readonly Label _lblRepeatPenaltyPriority = new();
    private readonly ComboBox _cmbRepeatPenaltyPriority = new();
    private readonly CheckBox _chkEnableThinkingCompatibility = new();
    private readonly Label _lblThinkingHandling = new();
    private readonly ComboBox _cmbThinkingHandling = new();
    private readonly CheckBox _chkSupportsVision = new();
    private readonly CheckBox _chkEnableHeartbeats = new();
    private readonly CheckBox _chkSynthesizeOpenAiMetadata = new();
    private readonly CheckBox _chkEnableAutoSummarization = new();
    private readonly Label _lblPreserveRecentCount = new();
    private readonly NumericUpDown _nudPreserveRecentCount = new();
    private readonly Label _lblMaxSummarizationRetries = new();
    private readonly NumericUpDown _nudMaxSummarizationRetries = new();
    private readonly CheckBox _chkRedactRequestBodies = new();
    private readonly CheckBox _chkRedactResponseBodies = new();
    private readonly CheckBox _chkRedactSensitiveJsonFields = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();
    private readonly ToolTip _toolTip = new();

    private string _upstreamUrl = string.Empty;
    private List<StoredCredential> _credentials = [];
    private AppSettings? _settings;
    private StatisticsService? _stats;

    public ModelMappingDialog()
    {
        InitializeUi();
        _cmbUpstreamUrl.TextChanged += (_, _) => _upstreamUrl = _cmbUpstreamUrl.Text.Trim();
        _toolTip.SetToolTip(
            _cmbUpstreamUrl,
            "Base URL of the OpenAI-compatible upstream, e.g. http://localhost:11434 or\n"
            + "https://provider.example/compatible-mode/v1. A trailing \"/v1\" is handled\n"
            + "automatically and won't be duplicated in requests.");
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private string? InstructionSetName
    {
        get
        {
            string? value = _cmbInstructionSet.SelectedItem?.ToString();
            return string.Equals(value, NoneLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }
        set
        {
            string target = string.IsNullOrWhiteSpace(value) ? NoneLabel : value!;
            int idx = _cmbInstructionSet.FindStringExact(target);
            _cmbInstructionSet.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private string? CredentialName
    {
        get
        {
            string? value = _cmbCredential.SelectedItem?.ToString();
            return string.Equals(value, NoneLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }
        set
        {
            string target = string.IsNullOrWhiteSpace(value) ? NoneLabel : value!;
            int idx = _cmbCredential.FindStringExact(target);
            _cmbCredential.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactRequestBodies
    {
        get => _chkRedactRequestBodies.Checked;
        set => _chkRedactRequestBodies.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactResponseBodies
    {
        get => _chkRedactResponseBodies.Checked;
        set => _chkRedactResponseBodies.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool RedactSensitiveJsonFields
    {
        get => _chkRedactSensitiveJsonFields.Checked;
        set => _chkRedactSensitiveJsonFields.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private SamplingPriority TemperaturePriority
    {
        get => (_cmbTempPriority.SelectedItem as SamplingPriorityOption)?.Priority ?? SamplingPriority.ClientApp;
        set => SelectSamplingPriority(_cmbTempPriority, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private SamplingPriority RepeatPenaltyPriority
    {
        get => (_cmbRepeatPenaltyPriority.SelectedItem as SamplingPriorityOption)?.Priority ?? SamplingPriority.ClientApp;
        set => SelectSamplingPriority(_cmbRepeatPenaltyPriority, value);
    }

    private static void SelectSamplingPriority(ComboBox combo, SamplingPriority value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((SamplingPriorityOption)combo.Items[i]!).Priority == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool EnableThinkingCompatibility
    {
        get => _chkEnableThinkingCompatibility.Checked;
        set => _chkEnableThinkingCompatibility.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool EnableHeartbeats
    {
        get => _chkEnableHeartbeats.Checked;
        set => _chkEnableHeartbeats.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool SupportsVision
    {
        get => _chkSupportsVision.Checked;
        set => _chkSupportsVision.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int UpstreamTimeoutSeconds
    {
        get => int.TryParse(_txtUpstreamTimeout.Text, out int v) && v > 0 ? v : 300;
        set => _txtUpstreamTimeout.Text = value <= 0 ? "300" : value.ToString();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int ContextWindowTokens
    {
        get => int.TryParse(_txtContextWindow.Text, out int v) && v >= 0 ? v : 0;
        set => _txtContextWindow.Text = value <= 0 ? string.Empty : value.ToString();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private ThinkingMode ThinkingMode
    {
        get => (_cmbThinkingHandling.SelectedItem as ThinkingModeOption)?.Mode ?? ThinkingMode.LeaveInline;
        set
        {
            for (int i = 0; i < _cmbThinkingHandling.Items.Count; i++)
            {
                if (((ThinkingModeOption)_cmbThinkingHandling.Items[i]!).Mode == value)
                {
                    _cmbThinkingHandling.SelectedIndex = i;
                    return;
                }
            }

            _cmbThinkingHandling.SelectedIndex = 0;
        }
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private double Temperature
    {
        get => (double)_nudTemperature.Value;
        set => _nudTemperature.Value = ClampDecimal(value, _nudTemperature.Minimum, _nudTemperature.Maximum, 0.7M);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private double RepeatPenalty
    {
        get => (double)_nudRepeatPenalty.Value;
        set => _nudRepeatPenalty.Value = ClampDecimal(value, _nudRepeatPenalty.Minimum, _nudRepeatPenalty.Maximum, 1.0M);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private bool EnableAutoSummarization
    {
        get => _chkEnableAutoSummarization.Checked;
        set => _chkEnableAutoSummarization.Checked = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int PreserveRecentMessageCount
    {
        get => (int)_nudPreserveRecentCount.Value;
        set => _nudPreserveRecentCount.Value = Math.Clamp(value, (int)_nudPreserveRecentCount.Minimum, (int)_nudPreserveRecentCount.Maximum);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int MaxSummarizationRetries
    {
        get => (int)_nudMaxSummarizationRetries.Value;
        set => _nudMaxSummarizationRetries.Value = Math.Clamp(value, (int)_nudMaxSummarizationRetries.Minimum, (int)_nudMaxSummarizationRetries.Maximum);
    }

    private void PopulateInstructionSets(IEnumerable<InstructionSet> instructionSets)
    {
        _cmbInstructionSet.Items.Clear();
        _cmbInstructionSet.Items.Add(NoneLabel);
        foreach (InstructionSet set in instructionSets)
        {
            _cmbInstructionSet.Items.Add(set.Name);
        }
        _cmbInstructionSet.SelectedIndex = 0;
    }

    private void PopulateCredentials(IEnumerable<StoredCredential> credentials)
    {
        _credentials = [.. credentials];
        _cmbCredential.Items.Clear();
        _cmbCredential.Items.Add(NoneLabel);
        foreach (StoredCredential credential in credentials)
        {
            if (!string.IsNullOrWhiteSpace(credential.Name))
                _cmbCredential.Items.Add(credential.Name);
        }
        _cmbCredential.SelectedIndex = 0;
    }

    private void PopulateUpstreamUrls(IEnumerable<string> urls, string? selected)
    {
        _cmbUpstreamUrl.Items.Clear();
        foreach (string url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(url))
                _cmbUpstreamUrl.Items.Add(url);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            _cmbUpstreamUrl.Text = selected;
            _upstreamUrl = selected;
        }
    }

    private void PopulateModelItems(IEnumerable<string> models, string? selected)
    {
        _cmbModelName.Items.Clear();
        foreach (string m in models)
        {
            if (!string.IsNullOrWhiteSpace(m) && !_cmbModelName.Items.Contains(m))
                _cmbModelName.Items.Add(m);
        }

        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (!_cmbModelName.Items.Contains(selected))
                _cmbModelName.Items.Add(selected);

            _cmbModelName.SelectedItem = selected;
        }
    }

    private void InitializeUi()
    {
        SuspendLayout();

        _tlpMain.ColumnCount = 3;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Every row sizes to its content. The table lives inside a scrollable panel so all
        // settings stay reachable when the content is taller than the dialog.
        _tlpMain.RowCount = 24;
        for (int i = 0; i < _tlpMain.RowCount; i++)
            _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.AutoSize = true;
        _tlpMain.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpMain.Dock = DockStyle.Top;
        _tlpMain.Padding = new Padding(8);

        _tlpMain.Controls.Add(_lblProxyName, 0, 0);
        _tlpMain.SetColumnSpan(_txtProxyName, 2);
        _tlpMain.Controls.Add(_txtProxyName, 1, 0);

        _tlpMain.Controls.Add(_lblUpstreamUrl, 0, 1);
        _tlpMain.SetColumnSpan(_cmbUpstreamUrl, 2);
        _tlpMain.Controls.Add(_cmbUpstreamUrl, 1, 1);

        _tlpMain.Controls.Add(_lblUpstreamType, 0, 2);
        _tlpMain.SetColumnSpan(_cmbUpstreamType, 2);
        _tlpMain.Controls.Add(_cmbUpstreamType, 1, 2);

        _tlpMain.Controls.Add(_lblCredential, 0, 3);
        _tlpMain.SetColumnSpan(_cmbCredential, 2);
        _tlpMain.Controls.Add(_cmbCredential, 1, 3);

        _tlpMain.Controls.Add(_lblModelName, 0, 4);
        _tlpMain.Controls.Add(_cmbModelName, 1, 4);
        _tlpMain.Controls.Add(_btnFetchModels, 2, 4);

        _tlpMain.Controls.Add(_lblInstructionSet, 0, 5);
        _tlpMain.SetColumnSpan(_cmbInstructionSet, 2);
        _tlpMain.Controls.Add(_cmbInstructionSet, 1, 5);

        _tlpMain.Controls.Add(_lblUpstreamTimeout, 0, 6);
        _tlpMain.SetColumnSpan(_txtUpstreamTimeout, 2);
        _tlpMain.Controls.Add(_txtUpstreamTimeout, 1, 6);

        _tlpMain.Controls.Add(_lblContextWindow, 0, 7);
        _tlpMain.SetColumnSpan(_txtContextWindow, 2);
        _tlpMain.Controls.Add(_txtContextWindow, 1, 7);

        _tlpMain.Controls.Add(_lblTempPriority, 0, 8);
        _tlpMain.SetColumnSpan(_cmbTempPriority, 2);
        _tlpMain.Controls.Add(_cmbTempPriority, 1, 8);

        _tlpMain.Controls.Add(_lblTemperature, 0, 9);
        _tlpMain.SetColumnSpan(_nudTemperature, 2);
        _tlpMain.Controls.Add(_nudTemperature, 1, 9);

        _tlpMain.Controls.Add(_lblRepeatPenaltyPriority, 0, 10);
        _tlpMain.SetColumnSpan(_cmbRepeatPenaltyPriority, 2);
        _tlpMain.Controls.Add(_cmbRepeatPenaltyPriority, 1, 10);

        _tlpMain.Controls.Add(_lblRepeatPenalty, 0, 11);
        _tlpMain.SetColumnSpan(_nudRepeatPenalty, 2);
        _tlpMain.Controls.Add(_nudRepeatPenalty, 1, 11);
        _tlpMain.SetColumnSpan(_chkIsEnabled, 3);
        _tlpMain.Controls.Add(_chkIsEnabled, 0, 12);
        _tlpMain.SetColumnSpan(_chkEnableThinkingCompatibility, 3);
        _tlpMain.Controls.Add(_chkEnableThinkingCompatibility, 0, 13);
        _tlpMain.Controls.Add(_lblThinkingHandling, 0, 14);
        _tlpMain.SetColumnSpan(_cmbThinkingHandling, 2);
        _tlpMain.Controls.Add(_cmbThinkingHandling, 1, 14);
        _tlpMain.SetColumnSpan(_chkSupportsVision, 3);
        _tlpMain.Controls.Add(_chkSupportsVision, 0, 15);
        _tlpMain.SetColumnSpan(_chkEnableHeartbeats, 3);
        _tlpMain.Controls.Add(_chkEnableHeartbeats, 0, 16);
        _tlpMain.SetColumnSpan(_chkSynthesizeOpenAiMetadata, 3);
        _tlpMain.Controls.Add(_chkSynthesizeOpenAiMetadata, 0, 17);
        _tlpMain.SetColumnSpan(_chkEnableAutoSummarization, 3);
        _tlpMain.Controls.Add(_chkEnableAutoSummarization, 0, 18);
        _tlpMain.Controls.Add(_lblPreserveRecentCount, 0, 19);
        _tlpMain.SetColumnSpan(_nudPreserveRecentCount, 2);
        _tlpMain.Controls.Add(_nudPreserveRecentCount, 1, 19);
        _tlpMain.Controls.Add(_lblMaxSummarizationRetries, 0, 20);
        _tlpMain.SetColumnSpan(_nudMaxSummarizationRetries, 2);
        _tlpMain.Controls.Add(_nudMaxSummarizationRetries, 1, 20);
        _tlpMain.SetColumnSpan(_chkRedactRequestBodies, 3);
        _tlpMain.Controls.Add(_chkRedactRequestBodies, 0, 21);
        _tlpMain.SetColumnSpan(_chkRedactResponseBodies, 3);
        _tlpMain.Controls.Add(_chkRedactResponseBodies, 0, 22);
        _tlpMain.SetColumnSpan(_chkRedactSensitiveJsonFields, 3);
        _tlpMain.Controls.Add(_chkRedactSensitiveJsonFields, 0, 23);

        _lblProxyName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblProxyName.AutoSize = true;
        _lblProxyName.Margin = new Padding(0, 4, 8, 4);
        _lblProxyName.Text = "Proxy Name:";

        _txtProxyName.Dock = DockStyle.Fill;
        _txtProxyName.Margin = new Padding(0, 4, 0, 4);

        _lblUpstreamUrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamUrl.AutoSize = true;
        _lblUpstreamUrl.Margin = new Padding(0, 4, 8, 4);
        _lblUpstreamUrl.Text = "Upstream URL:";

        _cmbUpstreamUrl.Dock = DockStyle.Fill;
        _cmbUpstreamUrl.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbUpstreamUrl.Margin = new Padding(0, 4, 0, 4);

        _lblUpstreamType.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamType.AutoSize = true;
        _lblUpstreamType.Margin = new Padding(0, 4, 8, 4);
        _lblUpstreamType.Text = "Upstream Type:";

        _cmbUpstreamType.Dock = DockStyle.Fill;
        _cmbUpstreamType.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbUpstreamType.Margin = new Padding(0, 4, 0, 4);

        _lblCredential.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblCredential.AutoSize = true;
        _lblCredential.Margin = new Padding(0, 8, 8, 4);
        _lblCredential.Text = "Credential:";

        _cmbCredential.Dock = DockStyle.Fill;
        _cmbCredential.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbCredential.Margin = new Padding(0, 4, 0, 4);
        _toolTip.SetToolTip(
            _cmbCredential,
            "Use a centrally stored credential (API key) for upstream authentication.\n"
            + "Manage credentials on the Credentials tab.");

        _lblModelName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblModelName.AutoSize = true;
        _lblModelName.Margin = new Padding(0, 8, 8, 4);
        _lblModelName.Text = "Model Name:";

        _cmbModelName.Dock = DockStyle.Fill;
        _cmbModelName.Margin = new Padding(0, 4, 4, 4);

        _btnFetchModels.Anchor = AnchorStyles.Right;
        _btnFetchModels.AutoSize = true;
        _btnFetchModels.Margin = new Padding(0, 4, 0, 4);
        _btnFetchModels.MinimumSize = new Size(110, 24);
        _btnFetchModels.Text = "Fetch Models \u2193";
        _btnFetchModels.Click += BtnFetchModels_Click;

        _lblInstructionSet.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblInstructionSet.AutoSize = true;
        _lblInstructionSet.Margin = new Padding(0, 8, 8, 4);
        _lblInstructionSet.Text = "Instruction Set:";

        _cmbInstructionSet.Dock = DockStyle.Fill;
        _cmbInstructionSet.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbInstructionSet.Margin = new Padding(0, 4, 0, 4);

        _lblUpstreamTimeout.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUpstreamTimeout.AutoSize = true;
        _lblUpstreamTimeout.Margin = new Padding(0, 8, 8, 4);
        _lblUpstreamTimeout.Text = "Upstream Timeout (s):";

        _txtUpstreamTimeout.Dock = DockStyle.Fill;
        _txtUpstreamTimeout.Margin = new Padding(0, 4, 0, 4);

        _lblContextWindow.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblContextWindow.AutoSize = true;
        _lblContextWindow.Margin = new Padding(0, 8, 8, 4);
        _lblContextWindow.Text = "Context Window (tokens):";

        _txtContextWindow.Dock = DockStyle.Fill;
        _txtContextWindow.Margin = new Padding(0, 4, 0, 4);
        _txtContextWindow.PlaceholderText = $"Auto ({ModelMapping.DefaultContextWindowTokens:N0})";
        _toolTip.SetToolTip(
            _txtContextWindow,
            $"Model context window size in tokens. Leave empty to use the default ({ModelMapping.DefaultContextWindowTokens:N0}).\n"
            + "Override per-model if the auto-default is incorrect (e.g., qwen-max is 32K, qwen-long is 10M).");

        _lblTemperature.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblTemperature.AutoSize = true;
        _lblTemperature.Margin = new Padding(0, 8, 8, 4);
        _lblTemperature.Text = "Temperature:";

        _nudTemperature.DecimalPlaces = 2;
        _nudTemperature.Dock = DockStyle.Left;
        _nudTemperature.Increment = 0.05M;
        _nudTemperature.Margin = new Padding(0, 4, 0, 4);
        _nudTemperature.Maximum = 2.0M;
        _nudTemperature.Minimum = 0.0M;
        _nudTemperature.Size = new Size(90, 25);
        _nudTemperature.Value = 0.7M;

        _lblRepeatPenalty.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRepeatPenalty.AutoSize = true;
        _lblRepeatPenalty.Margin = new Padding(0, 8, 8, 4);
        _lblRepeatPenalty.Text = "Repeat Penalty:";

        _nudRepeatPenalty.DecimalPlaces = 2;
        _nudRepeatPenalty.Dock = DockStyle.Left;
        _nudRepeatPenalty.Increment = 0.05M;
        _nudRepeatPenalty.Margin = new Padding(0, 4, 0, 4);
        _nudRepeatPenalty.Maximum = 2.0M;
        _nudRepeatPenalty.Minimum = 0.5M;
        _nudRepeatPenalty.Size = new Size(90, 25);
        _nudRepeatPenalty.Value = 1.0M;

        _lblTempPriority.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblTempPriority.AutoSize = true;
        _lblTempPriority.Margin = new Padding(0, 8, 8, 4);
        _lblTempPriority.Text = "Temperature Priority:";

        _cmbTempPriority.Dock = DockStyle.Fill;
        _cmbTempPriority.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTempPriority.Margin = new Padding(0, 4, 0, 4);
        _cmbTempPriority.Items.AddRange([.. SamplingPriorityOptions()]);
        _cmbTempPriority.SelectedIndex = 0;
        _cmbTempPriority.SelectedIndexChanged += (_, _) =>
            _nudTemperature.Enabled = TemperaturePriority != SamplingPriority.Provider;
        _toolTip.SetToolTip(
            _cmbTempPriority,
            "Client App Priority passes the client's temperature through; Proxy Priority always\n"
            + "sends the configured Temperature (overriding the client); Provider Priority omits the\n"
            + "field so the provider's platform setting wins.");

        _lblRepeatPenaltyPriority.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblRepeatPenaltyPriority.AutoSize = true;
        _lblRepeatPenaltyPriority.Margin = new Padding(0, 8, 8, 4);
        _lblRepeatPenaltyPriority.Text = "Repeat Penalty Priority:";

        _cmbRepeatPenaltyPriority.Dock = DockStyle.Fill;
        _cmbRepeatPenaltyPriority.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRepeatPenaltyPriority.Margin = new Padding(0, 4, 0, 4);
        _cmbRepeatPenaltyPriority.Items.AddRange([.. SamplingPriorityOptions()]);
        _cmbRepeatPenaltyPriority.SelectedIndex = 0;
        _cmbRepeatPenaltyPriority.SelectedIndexChanged += (_, _) =>
            _nudRepeatPenalty.Enabled = RepeatPenaltyPriority != SamplingPriority.Provider;
        _toolTip.SetToolTip(
            _cmbRepeatPenaltyPriority,
            "Client App Priority passes the client's repeat penalty through; Proxy Priority always\n"
            + "sends the configured Repeat Penalty (overriding the client); Provider Priority omits\n"
            + "the field so the provider's platform setting wins.");

        _chkIsEnabled.AutoSize = true;
        _chkIsEnabled.Margin = new Padding(0, 8, 0, 2);
        _chkIsEnabled.Text = "Enable this proxy model";
        _chkIsEnabled.Checked = true;

        _chkEnableThinkingCompatibility.AutoSize = true;
        _chkEnableThinkingCompatibility.Margin = new Padding(0, 2, 0, 2);
        _chkEnableThinkingCompatibility.Text = "Enable thinking compatibility (strip assistant response-prefill turns)";

        _lblThinkingHandling.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblThinkingHandling.AutoSize = true;
        _lblThinkingHandling.Margin = new Padding(0, 8, 8, 4);
        _lblThinkingHandling.Text = "Thinking Handling:";

        _cmbThinkingHandling.Dock = DockStyle.Fill;
        _cmbThinkingHandling.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbThinkingHandling.Margin = new Padding(0, 4, 0, 4);
        _cmbThinkingHandling.Items.AddRange(
        [
            new ThinkingModeOption(ThinkingMode.LeaveInline, "Leave thinking in the visible answer"),
            new ThinkingModeOption(ThinkingMode.MoveToReasoningContent, "Move thinking into reasoning_content (Qwen Cloud compatibility)"),
            new ThinkingModeOption(ThinkingMode.StripFromOutput, "Remove thinking from client output (kept in logs)"),
        ]);
        _cmbThinkingHandling.SelectedIndex = 0;
        _toolTip.SetToolTip(
            _cmbThinkingHandling,
            "Controls how upstream <think> reasoning is surfaced to clients.\n"
            + "Leave inline keeps it in the visible answer; moving it to reasoning_content lets\n"
            + "clients like VS render a collapsible thinking panel; removing it hides it from\n"
            + "clients entirely while captured logs still retain the original upstream body.");

        _chkSupportsVision.AutoSize = true;
        _chkSupportsVision.Margin = new Padding(0, 2, 0, 2);
        _chkSupportsVision.Text = "Model supports vision (image) input";

        _chkEnableHeartbeats.AutoSize = true;
        _chkEnableHeartbeats.Margin = new Padding(0, 2, 0, 2);
        _chkEnableHeartbeats.Text = "Enable streaming heartbeats for this model (keep-alive frames while waiting)";
        _chkEnableHeartbeats.Checked = true;

        _chkSynthesizeOpenAiMetadata.AutoSize = true;
        _chkSynthesizeOpenAiMetadata.Margin = new Padding(0, 2, 0, 2);
        _chkSynthesizeOpenAiMetadata.Text = "Synthesize OpenAI /v1/models metadata";
        _chkSynthesizeOpenAiMetadata.Checked = false;
        _toolTip.SetToolTip(
            _chkSynthesizeOpenAiMetadata,
            "When checked, the proxy synthesizes OpenAI /v1/models metadata with the\n"
            + "configured context window and reasoning capabilities instead of\n"
            + "passing through the upstream model list.");

        _chkEnableAutoSummarization.AutoSize = true;
        _chkEnableAutoSummarization.Margin = new Padding(0, 8, 0, 2);
        _chkEnableAutoSummarization.Text = "Enable automatic context summarization on overflow";
        _chkEnableAutoSummarization.Checked = true;
        _toolTip.SetToolTip(_chkEnableAutoSummarization, "When the model's context window is exceeded, summarize older history and retry.");

        _lblPreserveRecentCount.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblPreserveRecentCount.AutoSize = true;
        _lblPreserveRecentCount.Margin = new Padding(0, 4, 8, 4);
        _lblPreserveRecentCount.Text = "Preserve Recent Exchanges:";

        _nudPreserveRecentCount.Dock = DockStyle.Left;
        _nudPreserveRecentCount.Margin = new Padding(0, 4, 0, 4);
        _nudPreserveRecentCount.Maximum = 20;
        _nudPreserveRecentCount.Minimum = 2;
        _nudPreserveRecentCount.Size = new Size(90, 25);
        _nudPreserveRecentCount.Value = 4;
        _toolTip.SetToolTip(_nudPreserveRecentCount, "Number of recent user/assistant exchanges to keep verbatim (2-20).");

        _lblMaxSummarizationRetries.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblMaxSummarizationRetries.AutoSize = true;
        _lblMaxSummarizationRetries.Margin = new Padding(0, 4, 8, 4);
        _lblMaxSummarizationRetries.Text = "Max Summarization Retries:";

        _nudMaxSummarizationRetries.Dock = DockStyle.Left;
        _nudMaxSummarizationRetries.Margin = new Padding(0, 4, 0, 4);
        _nudMaxSummarizationRetries.Maximum = 3;
        _nudMaxSummarizationRetries.Minimum = 1;
        _nudMaxSummarizationRetries.Size = new Size(90, 25);
        _nudMaxSummarizationRetries.Value = 2;
        _toolTip.SetToolTip(_nudMaxSummarizationRetries, "Maximum summarization retry attempts on context overflow (1-3).");

        _chkRedactRequestBodies.AutoSize = true;
        _chkRedactRequestBodies.Margin = new Padding(0, 8, 0, 2);
        _chkRedactRequestBodies.Text = "Redact captured request bodies";

        _chkRedactResponseBodies.AutoSize = true;
        _chkRedactResponseBodies.Margin = new Padding(0, 2, 0, 2);
        _chkRedactResponseBodies.Text = "Redact captured response bodies";

        _chkRedactSensitiveJsonFields.AutoSize = true;
        _chkRedactSensitiveJsonFields.Margin = new Padding(0, 2, 0, 8);
        _chkRedactSensitiveJsonFields.Text = "Redact sensitive JSON fields (api keys, prompts, messages)";

        _flpButtons.AutoSize = true;
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Dock = DockStyle.Bottom;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Padding = new Padding(8);

        _btnOk.AutoSize = true;
        _btnOk.Click += BtnOk_Click;
        _btnOk.MinimumSize = new Size(80, 28);
        _btnOk.Text = "Save";

        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Margin = new Padding(0, 0, 8, 0);
        _btnCancel.MinimumSize = new Size(80, 28);
        _btnCancel.Text = "Cancel";

        AcceptButton = _btnOk;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _btnCancel;
        ClientSize = new Size(600, 640);
        _pnlScroll.AutoScroll = true;
        _pnlScroll.Dock = DockStyle.Fill;
        _pnlScroll.Controls.Add(_tlpMain);
        // Add order defines dock Z-order: the bottom-docked button row must be added after the
        // fill panel so it is laid out first and the scroll area stops above it.
        Controls.Add(_pnlScroll);
        Controls.Add(_flpButtons);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(480, 420);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Configure Model";

        ResumeLayout(false);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        // Validate the upstream URL before accepting. A non-empty URL must be a valid absolute
        // URI using http/https; otherwise reject with a clear message and keep the dialog open
        // (DialogResult stays None). Setting DialogResult.OK closes the modal and returns OK.
        string url = _cmbUpstreamUrl.Text.Trim();
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                MessageBox.Show(this,
                    "The upstream URL is not a valid absolute URL (e.g. http://192.168.1.10:8080).",
                    "Invalid Upstream URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    $"The upstream URL scheme '{uri.Scheme}' is not supported. Use an http:// or https:// URL.",
                    "Invalid Upstream URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        DialogResult = DialogResult.OK;
    }

    private async void BtnFetchModels_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_upstreamUrl) ||
            !Uri.TryCreate(_upstreamUrl, UriKind.Absolute, out _))
        {
            MessageBox.Show(this,
                "This model mapping does not have a valid upstream URL configured. " +
                "Set the upstream URL in the main mappings grid first.",
                "Fetch Models", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnFetchModels.Enabled = false;
        string originalText = _btnFetchModels.Text;
        _btnFetchModels.Text = "Fetching\u2026";

        try
        {
            // Resolve the API key from the selected credential
            string? apiKey = null;
            string? credentialName = CredentialName;
            if (!string.IsNullOrWhiteSpace(credentialName))
            {
                StoredCredential? credential = _credentials.FirstOrDefault(
                    c => string.Equals(c.Name, credentialName, StringComparison.OrdinalIgnoreCase));
                apiKey = credential?.Secret;
            }

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            UpstreamModelFetchResult result = await FetchUpstreamModelsAsync(_upstreamUrl, apiKey);
            sw.Stop();

            LogFetchModelsRequest(result, sw.Elapsed.TotalMilliseconds);

            if (result.Models.Count == 0)
            {
                MessageBox.Show(this,
                    $"Failed to fetch models from '{_upstreamUrl}'. Check that the server is reachable.",
                    "Fetch Models", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string? current = _cmbModelName.SelectedItem?.ToString() ?? _cmbModelName.Text;

            _cmbModelName.Items.Clear();
            _cmbModelName.Items.AddRange([.. result.Models.Cast<object>()]);

            if (!string.IsNullOrWhiteSpace(current) && result.Models.Contains(current))
                _cmbModelName.SelectedItem = current;
            else if (_cmbModelName.Items.Count > 0)
                _cmbModelName.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to fetch models from '{_upstreamUrl}': {ex.Message}",
                "Fetch Models", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnFetchModels.Enabled = true;
            _btnFetchModels.Text = originalText;
        }
    }

    /// <summary>
    /// Records the /v1/models discovery call in the request log, following the same body-capture
    /// gating and per-model redaction as proxied traffic. No-op when no statistics service was
    /// supplied to the dialog.
    /// </summary>
    private void LogFetchModelsRequest(UpstreamModelFetchResult result, double durationMs)
    {
        if (_stats is null)
            return;

        string model = _cmbModelName.SelectedItem?.ToString() ?? _cmbModelName.Text ?? string.Empty;
        ModelMapping? mapping = _settings?.FindModelMapping(model);

        RequestLog log = new()
        {
            Method = "GET",
            OllamaPath = "(fetch models)",
            UpstreamPath = "/v1/models",
            Model = model,
            DurationMs = durationMs,
            StatusCode = result.StatusCode,
        };

        if (result.ErrorMessage is null)
        {
            log.Status = RequestStatus.Success;
        }
        else
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = result.ErrorMessage;
        }

        if (result.ResponseBody is not null)
        {
            log.ResponseBytes = Encoding.UTF8.GetByteCount(result.ResponseBody);
            if (_settings?.CollectResponseDetails == true)
            {
                log.ResponseBody = mapping?.RedactResponseBodies ?? true
                    ? OllamaProxyHandler.RedactedBodyText
                    : mapping?.RedactSensitiveJsonFields ?? true
                        ? OllamaProxyHandler.RedactSensitiveJsonFields(result.ResponseBody)
                        : result.ResponseBody;
            }
        }
        else
        {
            log.ResponseBytes = -1;
        }

        _stats.AddLog(log);
    }

    /// <summary>
    /// Fetches the model list from the specified upstream URL. Returns the parsed model ids along
    /// with the HTTP status code, raw response body, and any error text so the caller can record
    /// the discovery call in the request log.
    /// </summary>
    internal static async Task<UpstreamModelFetchResult> FetchUpstreamModelsAsync(string upstreamUrl, string? apiKey = null)
    {
        try
        {
            Uri requestUri = UpstreamUriHelper.BuildRequestUri(upstreamUrl, "v1/models");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using HttpResponseMessage resp = await _modelFetchClient.SendAsync(request);
            string body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return new UpstreamModelFetchResult([], (int)resp.StatusCode, body, $"Upstream {(int)resp.StatusCode}: {body}");

            var models = new List<string>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data))
                    return new UpstreamModelFetchResult([], (int)resp.StatusCode, body, "Upstream response has no 'data' model array.");

                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement id))
                    {
                        string? name = id.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            models.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
                return new UpstreamModelFetchResult([], (int)resp.StatusCode, body, "Upstream returned an unparseable model list.");
            }

            return new UpstreamModelFetchResult(models, (int)resp.StatusCode, body, null);
        }
        catch (Exception ex)
        {
            return new UpstreamModelFetchResult([], 0, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Outcome of an upstream /v1/models discovery call, for request logging.</summary>
    internal sealed record UpstreamModelFetchResult(
        List<string> Models,
        int StatusCode,
        string? ResponseBody,
        string? ErrorMessage);

    /// <summary>
    /// Shows the modal dialog for the supplied <paramref name="mapping"/>. The dialog is
    /// modal — the owner cannot be activated until the user closes it. Returns true and
    /// writes the user's changes back to <paramref name="mapping"/> when accepted.
    /// </summary>
    /// <param name="existingModelItems">Models currently listed in the row's combo cell, used to seed the model picker.</param>
    /// <param name="existingUpstreamUrls">Upstream URLs from all mappings, used to populate the URL dropdown.</param>
    /// <param name="updatedModelItems">Receives the current list of model items after the dialog closes (whether OK or Cancel).</param>
    public static bool ShowConfigureDialog(
        IWin32Window owner,
        ModelMapping mapping,
        IEnumerable<InstructionSet> instructionSets,
        IEnumerable<StoredCredential> credentials,
        IEnumerable<string> existingModelItems,
        IEnumerable<string> existingUpstreamUrls,
        AppSettings settings,
        StatisticsService? stats,
        out List<string> updatedModelItems)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(instructionSets);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(existingModelItems);
        ArgumentNullException.ThrowIfNull(existingUpstreamUrls);
        ArgumentNullException.ThrowIfNull(settings);

        using ModelMappingDialog dlg = new();
        dlg._settings = settings;
        dlg._stats = stats;
        dlg.PopulateInstructionSets(instructionSets);
        dlg.PopulateCredentials(credentials);
        dlg.PopulateUpstreamTypes(mapping.UpstreamType);
        dlg.PopulateUpstreamUrls(existingUpstreamUrls, mapping.UpstreamUrl);
        dlg._txtProxyName.Text = mapping.ProxyName ?? string.Empty;
        dlg.CredentialName = mapping.CredentialName;
        dlg._upstreamUrl = mapping.UpstreamUrl ?? string.Empty;
        dlg.PopulateModelItems(existingModelItems, mapping.ModelName);
        dlg.InstructionSetName = mapping.InstructionSetName;
        dlg._chkIsEnabled.Checked = mapping.IsEnabled;
        dlg.TemperaturePriority = mapping.TemperaturePriority;
        dlg.RepeatPenaltyPriority = mapping.RepeatPenaltyPriority;
        dlg.EnableThinkingCompatibility = mapping.EnableThinkingCompatibility;
        dlg.ThinkingMode = mapping.ThinkingMode;
        dlg.SupportsVision = mapping.SupportsVision ?? false;
        dlg.EnableHeartbeats = mapping.EnableHeartbeats;
        dlg._chkSynthesizeOpenAiMetadata.Checked = mapping.SynthesizeOpenAiMetadata;
        dlg.UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds;
        dlg.ContextWindowTokens = mapping.ContextWindowTokens;
        dlg.Temperature = mapping.Temperature;
        dlg.RepeatPenalty = mapping.RepeatPenalty;
        dlg.EnableAutoSummarization = mapping.EnableAutoSummarization;
        dlg.PreserveRecentMessageCount = mapping.PreserveRecentMessageCount;
        dlg.MaxSummarizationRetries = mapping.MaxSummarizationRetries;
        dlg.RedactRequestBodies = mapping.RedactRequestBodies;
        dlg.RedactResponseBodies = mapping.RedactResponseBodies;
        dlg.RedactSensitiveJsonFields = mapping.RedactSensitiveJsonFields;

        DialogResult result = dlg.ShowDialog(owner);

        updatedModelItems = [.. dlg._cmbModelName.Items.Cast<object>().Select(o => o?.ToString() ?? string.Empty)];

        if (result != DialogResult.OK)
            return false;

        mapping.ProxyName = dlg._txtProxyName.Text.Trim();
        mapping.IsEnabled = dlg._chkIsEnabled.Checked;
        mapping.UpstreamUrl = dlg._cmbUpstreamUrl.Text.Trim();
        mapping.CredentialName = dlg.CredentialName;
        mapping.UpstreamType = UpstreamTypeExtensions.FromDisplayName(dlg._cmbUpstreamType.SelectedItem?.ToString());
        mapping.ModelName = (dlg._cmbModelName.SelectedItem?.ToString() ?? dlg._cmbModelName.Text ?? string.Empty).Trim();
        mapping.InstructionSetName = dlg.InstructionSetName;
        mapping.TemperaturePriority = dlg.TemperaturePriority;
        mapping.RepeatPenaltyPriority = dlg.RepeatPenaltyPriority;
        mapping.EnableThinkingCompatibility = dlg.EnableThinkingCompatibility;
        mapping.ThinkingMode = dlg.ThinkingMode;
        mapping.SupportsVision = dlg.SupportsVision;
        mapping.EnableHeartbeats = dlg.EnableHeartbeats;
        mapping.SynthesizeOpenAiMetadata = dlg._chkSynthesizeOpenAiMetadata.Checked;
        mapping.UpstreamTimeoutSeconds = dlg.UpstreamTimeoutSeconds;
        mapping.ContextWindowTokens = dlg.ContextWindowTokens;
        mapping.Temperature = dlg.Temperature;
        mapping.RepeatPenalty = dlg.RepeatPenalty;
        mapping.EnableAutoSummarization = dlg.EnableAutoSummarization;
        mapping.PreserveRecentMessageCount = dlg.PreserveRecentMessageCount;
        mapping.MaxSummarizationRetries = dlg.MaxSummarizationRetries;
        mapping.RedactRequestBodies = dlg.RedactRequestBodies;
        mapping.RedactResponseBodies = dlg.RedactResponseBodies;
        mapping.RedactSensitiveJsonFields = dlg.RedactSensitiveJsonFields;
        return true;
    }

    private void PopulateUpstreamTypes(UpstreamType selected)
    {
        _cmbUpstreamType.Items.Clear();
        _cmbUpstreamType.Items.Add(UpstreamType.OpenAI.ToDisplayName());
        _cmbUpstreamType.SelectedItem = selected.ToDisplayName();
    }

    /// <summary>Display wrapper binding a friendly label to a <see cref="ThinkingMode"/> value.</summary>
    private sealed record ThinkingModeOption(ThinkingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Display wrapper binding a friendly label to a <see cref="SamplingPriority"/> value.</summary>
    private sealed record SamplingPriorityOption(SamplingPriority Priority, string Label)
    {
        public override string ToString() => Label;
    }

    private static SamplingPriorityOption[] SamplingPriorityOptions() =>
    [
        new(SamplingPriority.ClientApp, "Client App Priority (client value wins)"),
        new(SamplingPriority.Proxy, "Proxy Priority (configured value overrides client)"),
        new(SamplingPriority.Provider, "Provider Priority (field omitted, platform setting wins)"),
    ];
}
