using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
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
    private readonly Button _btnModelInfo = new();
    private readonly FlowLayoutPanel _flpModelButtons = new();
    private readonly Label _lblModelInfoStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 2) };
    private readonly Label _lblInstructionSet = new();
    private readonly ComboBox _cmbInstructionSet = new();
    private readonly Label _lblContextSummarizeModel = new();
    private readonly ComboBox _cmbContextSummarizeModel = new();
    private readonly Label _lblUpstreamTimeout = new();
    private readonly TextBox _txtUpstreamTimeout = new();
    private readonly Label _lblContextWindow = new();
    private readonly TextBox _txtContextWindow = new();
    private readonly Label _lblProactiveOverflowPercent = new();
    private readonly NumericUpDown _nudProactiveOverflowPercent = new();
    private readonly Label _lblProactiveOverflowTokens = new();
    private readonly NumericUpDown _nudProactiveOverflowTokens = new();
    private readonly Label _lblTemperature = new();
    private readonly NumericUpDown _nudTemperature = new();
    private readonly Label _lblRepeatPenalty = new();
    private readonly NumericUpDown _nudRepeatPenalty = new();
    private readonly Label _lblReasoningEffortPriority = new();
    private readonly ComboBox _cmbReasoningEffortPriority = new();
    private readonly Label _lblReasoningEffortValues = new();
    private readonly TextBox _txtReasoningEffortValues = new();
    private readonly Label _lblReasoningEffort = new();
    private readonly ComboBox _cmbReasoningEffort = new();
    private readonly Label _lblReasoningEffortFormats = new();
    private readonly CheckedListBox _lstReasoningEffortFormats = new();
    private readonly CheckBox _chkIsEnabled = new();
    private readonly Label _lblTempPriority = new();
    private readonly ComboBox _cmbTempPriority = new();
    private readonly Label _lblRepeatPenaltyPriority = new();
    private readonly ComboBox _cmbRepeatPenaltyPriority = new();
    private readonly CheckBox _chkEnableThinkingCompatibility = new();
    private readonly GroupBox _grpThinkingReasoning = new();
    private readonly TableLayoutPanel _tlpThinkingReasoning = new();
    private readonly Label _lblThinkingHandling = new();
    private readonly ComboBox _cmbThinkingHandling = new();
    private readonly GroupBox _grpClientCapabilities = new();
    private readonly TableLayoutPanel _tlpCapGroup = new();
    private readonly FlowLayoutPanel _flpCapButtons = new();
    private readonly Button _btnCapAutoDetect = new();
    private readonly Button _btnCapDefaults = new();
    private readonly Button _btnCapAdd = new();
    private readonly Button _btnCapRemove = new();
    private readonly Label _lblCapStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4) };
    private readonly DataGridView _dgvCapabilities = new();
    private readonly CheckBox _chkEnableHeartbeats = new();
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
    private Dictionary<int, string> _compactModelIdToName = [];
    private Dictionary<string, int> _compactModelNameToId = [];

    // Set while ShowConfigureDialog populates controls so model-name change events do not
    // prefill reasoning effort values over the values being loaded.
    private bool _suppressReasoningPrefill;

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
    private int? ContextSummarizeModelId
    {
        get
        {
            string? value = _cmbContextSummarizeModel.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, NoneLabel, StringComparison.OrdinalIgnoreCase))
                return null;
            return _compactModelNameToId.TryGetValue(value!, out int id) ? id : null;
        }
        set
        {
            if (!value.HasValue || !_compactModelIdToName.TryGetValue(value.Value, out string? name))
            {
                _cmbContextSummarizeModel.SelectedIndex = 0;
                return;
            }
            int idx = _cmbContextSummarizeModel.FindStringExact(name);
            _cmbContextSummarizeModel.SelectedIndex = idx >= 0 ? idx : 0;
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

    /// <summary>
    /// The capability tokens currently enabled in the Model Capabilities table, returned in
    /// canonical order (known tokens first, then custom tokens in row order; deduped). Disabled
    /// rows and blank rows are excluded.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private List<string> Capabilities
    {
        get
        {
            List<string> tokens = [];
            foreach (DataGridViewRow row in _dgvCapabilities.Rows)
            {
                if (row.IsNewRow)
                    continue;
                bool enabled = row.Cells[1].Value is true;
                string? token = row.Cells[0].Value?.ToString();
                if (enabled && !string.IsNullOrWhiteSpace(token))
                    tokens.Add(token.Trim());
            }
            return ModelCapabilities.Normalize(tokens);
        }
        set
        {
            _dgvCapabilities.Rows.Clear();
            var capColumn = (DataGridViewComboBoxColumn)_dgvCapabilities.Columns["_colCapCapability"];
            foreach (string token in value ?? [])
            {
                if (!capColumn.Items.Contains(token))
                    capColumn.Items.Add(token);
                int idx = _dgvCapabilities.Rows.Add();
                _dgvCapabilities.Rows[idx].Cells[0].Value = token;
                _dgvCapabilities.Rows[idx].Cells[1].Value = true;
            }
        }
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
    private int ProactiveOverflowPercent
    {
        get => (int)_nudProactiveOverflowPercent.Value;
        set => _nudProactiveOverflowPercent.Value = Math.Clamp(value, 0, 100);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private int ProactiveOverflowTokens
    {
        get => (int)_nudProactiveOverflowTokens.Value;
        set => _nudProactiveOverflowTokens.Value = Math.Clamp(value, 0, (int)_nudProactiveOverflowTokens.Maximum);
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
    private SamplingPriority ReasoningEffortPriority
    {
        get => (_cmbReasoningEffortPriority.SelectedItem as SamplingPriorityOption)?.Priority ?? SamplingPriority.ClientApp;
        set => SelectSamplingPriority(_cmbReasoningEffortPriority, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private string? ReasoningEffort
    {
        get
        {
            string value = _cmbReasoningEffort.Text.Trim();
            return value.Length == 0 ? null : value;
        }
        set => _cmbReasoningEffort.Text = value ?? string.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private List<string> ReasoningEffortValues
    {
        get => ParseReasoningEffortValues(_txtReasoningEffortValues.Text);
        set => _txtReasoningEffortValues.Text = string.Join(", ", value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    private ReasoningEffortFormat ReasoningEffortFormat
    {
        get
        {
            ReasoningEffortFormat format = default;
            foreach (ReasoningEffortFormatOption option in _lstReasoningEffortFormats.CheckedItems)
                format |= option.Format;

            // Nothing selected degrades to Legacy so Proxy priority always injects a shape.
            return format == default ? ReasoningEffortFormat.Legacy : format;
        }
        set
        {
            for (int i = 0; i < _lstReasoningEffortFormats.Items.Count; i++)
            {
                ReasoningEffortFormatOption option = (ReasoningEffortFormatOption)_lstReasoningEffortFormats.Items[i]!;
                _lstReasoningEffortFormats.SetItemChecked(i, value.HasFlag(option.Format));
            }
        }
    }

    private static List<string> ParseReasoningEffortValues(string raw) =>
        [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Prefills the reasoning effort values and selection from a known model profile when the
    /// values field is empty and the model name matches a known family (e.g. glm-5.x,
    /// deepseek-v4, kimi-k3, qwen3.8-max).
    /// </summary>
    private void TryPrefillReasoningEffortProfile(string? modelName)
    {
        if (_suppressReasoningPrefill)
            return;

        if (_txtReasoningEffortValues.Text.Trim().Length > 0)
            return;

        if (!ReasoningEffortProfiles.TryGetProfile(modelName, out IReadOnlyList<string> values, out string defaultValue))
            return;

        _txtReasoningEffortValues.Text = string.Join(", ", values);
        _cmbReasoningEffort.Text = defaultValue;
    }

    /// <summary>
    /// Repopulates the reasoning effort selection dropdown with the values entered in the
    /// comma-separated list, preserving the current selection.
    /// </summary>
    private void PopulateReasoningEffortOptions()
    {
        string current = _cmbReasoningEffort.Text;
        _cmbReasoningEffort.Items.Clear();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string value in ParseReasoningEffortValues(_txtReasoningEffortValues.Text))
        {
            if (seen.Add(value))
                _cmbReasoningEffort.Items.Add(value);
        }

        _cmbReasoningEffort.Text = current;
    }

    private void UpdateReasoningEffortControlStates()
    {
        bool enabled = ReasoningEffortPriority != SamplingPriority.Provider;
        _txtReasoningEffortValues.Enabled = enabled;
        _cmbReasoningEffort.Enabled = enabled;
        // The payload formats only matter under Proxy Priority, the only mode that injects.
        _lstReasoningEffortFormats.Enabled = ReasoningEffortPriority == SamplingPriority.Proxy;
    }

    /// <summary>
    /// Enables or disables the entire thinking/reasoning options group based on the
    /// Enable thinking compatibility checkbox.
    /// </summary>
    private void UpdateThinkingReasoningGroupState() =>
        _grpThinkingReasoning.Enabled = EnableThinkingCompatibility;



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

    /// <summary>
    /// Populates the context-summarize (/compact) model dropdown with the proxy names of all
    /// configured model mappings. The compact model is the smaller/faster model that a mapping's
    /// /compact requests are transparently redirected to.
    /// </summary>
    private void PopulateContextSummarizeModels()
    {
        _cmbContextSummarizeModel.Items.Clear();
        _cmbContextSummarizeModel.Items.Add(NoneLabel);
        _compactModelIdToName.Clear();
        _compactModelNameToId.Clear();
        if (_settings is not null)
        {
            foreach (ModelMapping m in _settings.ModelMappings)
            {
                if (!string.IsNullOrWhiteSpace(m.ProxyName) && m.Id != 0)
                {
                    _cmbContextSummarizeModel.Items.Add(m.ProxyName);
                    _compactModelIdToName[m.Id] = m.ProxyName;
                    _compactModelNameToId[m.ProxyName] = m.Id;
                }
            }
        }
        _cmbContextSummarizeModel.SelectedIndex = 0;
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
        _tlpMain.RowCount = 23;
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
        _flpModelButtons.Controls.Add(_btnFetchModels);
        _flpModelButtons.Controls.Add(_btnModelInfo);
        _tlpMain.Controls.Add(_flpModelButtons, 2, 4);
        _tlpMain.Controls.Add(_lblModelInfoStatus, 2, 5);
        _tlpMain.SetColumnSpan(_lblModelInfoStatus, 3);

        _tlpMain.Controls.Add(_lblInstructionSet, 0, 5);
        _tlpMain.SetColumnSpan(_cmbInstructionSet, 2);
        _tlpMain.Controls.Add(_cmbInstructionSet, 1, 5);

        _tlpMain.Controls.Add(_lblContextSummarizeModel, 0, 6);
        _tlpMain.SetColumnSpan(_cmbContextSummarizeModel, 2);
        _tlpMain.Controls.Add(_cmbContextSummarizeModel, 1, 6);

        _tlpMain.Controls.Add(_lblUpstreamTimeout, 0, 7);
        _tlpMain.SetColumnSpan(_txtUpstreamTimeout, 2);
        _tlpMain.Controls.Add(_txtUpstreamTimeout, 1, 7);

        _tlpMain.Controls.Add(_lblContextWindow, 0, 8);
        _tlpMain.SetColumnSpan(_txtContextWindow, 2);
        _tlpMain.Controls.Add(_txtContextWindow, 1, 8);

        _tlpMain.Controls.Add(_lblProactiveOverflowPercent, 0, 9);
        _tlpMain.SetColumnSpan(_nudProactiveOverflowPercent, 2);
        _tlpMain.Controls.Add(_nudProactiveOverflowPercent, 1, 9);

        _tlpMain.Controls.Add(_lblProactiveOverflowTokens, 0, 10);
        _tlpMain.SetColumnSpan(_nudProactiveOverflowTokens, 2);
        _tlpMain.Controls.Add(_nudProactiveOverflowTokens, 1, 10);

        _tlpMain.Controls.Add(_lblTempPriority, 0, 11);
        _tlpMain.SetColumnSpan(_cmbTempPriority, 2);
        _tlpMain.Controls.Add(_cmbTempPriority, 1, 11);

        _tlpMain.Controls.Add(_lblTemperature, 0, 12);
        _tlpMain.SetColumnSpan(_nudTemperature, 2);
        _tlpMain.Controls.Add(_nudTemperature, 1, 12);

        _tlpMain.Controls.Add(_lblRepeatPenaltyPriority, 0, 13);
        _tlpMain.SetColumnSpan(_cmbRepeatPenaltyPriority, 2);
        _tlpMain.Controls.Add(_cmbRepeatPenaltyPriority, 1, 13);

        _tlpMain.Controls.Add(_lblRepeatPenalty, 0, 14);
        _tlpMain.SetColumnSpan(_nudRepeatPenalty, 2);
        _tlpMain.Controls.Add(_nudRepeatPenalty, 1, 14);
        _tlpMain.SetColumnSpan(_chkIsEnabled, 3);
        _tlpMain.Controls.Add(_chkIsEnabled, 0, 15);
        _tlpMain.SetColumnSpan(_chkEnableThinkingCompatibility, 3);
        _tlpMain.Controls.Add(_chkEnableThinkingCompatibility, 0, 16);
        _tlpMain.SetColumnSpan(_grpThinkingReasoning, 3);
        _tlpMain.Controls.Add(_grpThinkingReasoning, 0, 17);
        _tlpMain.SetColumnSpan(_grpClientCapabilities, 3);
        _tlpMain.Controls.Add(_grpClientCapabilities, 0, 18);
        _tlpMain.SetColumnSpan(_chkEnableHeartbeats, 3);
        _tlpMain.Controls.Add(_chkEnableHeartbeats, 0, 19);
        _tlpMain.SetColumnSpan(_chkRedactRequestBodies, 3);
        _tlpMain.Controls.Add(_chkRedactRequestBodies, 0, 20);
        _tlpMain.SetColumnSpan(_chkRedactResponseBodies, 3);
        _tlpMain.Controls.Add(_chkRedactResponseBodies, 0, 21);
        _tlpMain.SetColumnSpan(_chkRedactSensitiveJsonFields, 3);
        _tlpMain.Controls.Add(_chkRedactSensitiveJsonFields, 0, 22);

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
        _cmbModelName.TextChanged += (_, _) => TryPrefillReasoningEffortProfile(_cmbModelName.Text);

        _flpModelButtons.AutoSize = true;
        _flpModelButtons.AutoSizeMode = AutoSizeMode.GrowOnly;
        _flpModelButtons.FlowDirection = FlowDirection.TopDown;
        _flpModelButtons.WrapContents = false;
        _flpModelButtons.Anchor = AnchorStyles.Right;
        _flpModelButtons.Margin = new Padding(0, 4, 0, 4);

        _btnFetchModels.AutoSize = true;
        _btnFetchModels.Margin = new Padding(0, 0, 0, 2);
        _btnFetchModels.MinimumSize = new Size(110, 24);
        _btnFetchModels.Text = "Fetch Models \u2193";
        _btnFetchModels.Click += BtnFetchModels_Click;

        _btnModelInfo.AutoSize = true;
        _btnModelInfo.Margin = new Padding(0);
        _btnModelInfo.MinimumSize = new Size(110, 24);
        _btnModelInfo.Text = "Model Info";
        _btnModelInfo.Click += BtnModelInfo_Click;

        _lblInstructionSet.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblInstructionSet.AutoSize = true;
        _lblInstructionSet.Margin = new Padding(0, 8, 8, 4);
        _lblInstructionSet.Text = "Instruction Set:";

        _cmbInstructionSet.Dock = DockStyle.Fill;
        _cmbInstructionSet.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbInstructionSet.Margin = new Padding(0, 4, 0, 4);

        _lblContextSummarizeModel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblContextSummarizeModel.AutoSize = true;
        _lblContextSummarizeModel.Margin = new Padding(0, 8, 8, 4);
        _lblContextSummarizeModel.Text = "Compact Model:";

        _cmbContextSummarizeModel.Dock = DockStyle.Fill;
        _cmbContextSummarizeModel.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbContextSummarizeModel.Margin = new Padding(0, 4, 0, 4);
        _toolTip.SetToolTip(
            _cmbContextSummarizeModel,
            "Optional smaller/faster model to handle context-summarize (/compact) requests for this model.\n"
            + "When a request is detected as a Copilot /compact session-summary request, it is\n"
            + "transparently routed to the selected model instead. Leave (None) to use this model itself.");

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

        _lblProactiveOverflowPercent.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblProactiveOverflowPercent.AutoSize = true;
        _lblProactiveOverflowPercent.Margin = new Padding(0, 8, 8, 4);
        _lblProactiveOverflowPercent.Text = "Proactive 413 at (% of context):";

        _nudProactiveOverflowPercent.Dock = DockStyle.Left;
        _nudProactiveOverflowPercent.Margin = new Padding(0, 4, 0, 4);
        _nudProactiveOverflowPercent.Maximum = 100;
        _nudProactiveOverflowPercent.Minimum = 0;
        _nudProactiveOverflowPercent.Size = new Size(90, 25);
        _nudProactiveOverflowPercent.Value = 0;
        _toolTip.SetToolTip(_nudProactiveOverflowPercent,
            "When the estimated request size exceeds this percentage of the context window,\n"
            + "return 413 immediately without calling upstream. 0 disables.");

        _lblProactiveOverflowTokens.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblProactiveOverflowTokens.AutoSize = true;
        _lblProactiveOverflowTokens.Margin = new Padding(0, 4, 8, 4);
        _lblProactiveOverflowTokens.Text = "Proactive 413 at (tokens):";

        _nudProactiveOverflowTokens.Dock = DockStyle.Left;
        _nudProactiveOverflowTokens.Margin = new Padding(0, 4, 0, 4);
        _nudProactiveOverflowTokens.Maximum = 1_000_000;
        _nudProactiveOverflowTokens.Minimum = 0;
        _nudProactiveOverflowTokens.Size = new Size(120, 25);
        _nudProactiveOverflowTokens.Value = 0;
        _toolTip.SetToolTip(_nudProactiveOverflowTokens,
            "Absolute token threshold. Takes precedence over the percentage above.\n"
            + "0 disables. Estimated as ~4 chars/token of the serialized request.");

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

        _lblReasoningEffortPriority.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblReasoningEffortPriority.AutoSize = true;
        _lblReasoningEffortPriority.Margin = new Padding(0, 8, 8, 4);
        _lblReasoningEffortPriority.Text = "Reasoning Effort Priority:";

        _cmbReasoningEffortPriority.Dock = DockStyle.Fill;
        _cmbReasoningEffortPriority.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbReasoningEffortPriority.Margin = new Padding(0, 4, 0, 4);
        _cmbReasoningEffortPriority.Items.AddRange([.. SamplingPriorityOptions()]);
        _cmbReasoningEffortPriority.SelectedIndex = 0;
        _cmbReasoningEffortPriority.SelectedIndexChanged += (_, _) => UpdateReasoningEffortControlStates();
        _toolTip.SetToolTip(
            _cmbReasoningEffortPriority,
            "Client App Priority passes the client's reasoning_effort through unchanged;\n"
            + "Proxy Priority always sends the configured Reasoning Effort (overriding the client);\n"
            + "Provider Priority omits the field so the provider's platform default wins.");

        _lblReasoningEffortValues.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblReasoningEffortValues.AutoSize = true;
        _lblReasoningEffortValues.Margin = new Padding(0, 8, 8, 4);
        _lblReasoningEffortValues.Text = "Reasoning Effort Values:";

        _txtReasoningEffortValues.Dock = DockStyle.Fill;
        _txtReasoningEffortValues.Margin = new Padding(0, 4, 0, 4);
        _txtReasoningEffortValues.PlaceholderText =
            $"Standard: {string.Join(", ", ReasoningEffortProfiles.StandardValues)}";
        _txtReasoningEffortValues.TextChanged += (_, _) => PopulateReasoningEffortOptions();
        _toolTip.SetToolTip(
            _txtReasoningEffortValues,
            "Comma-separated reasoning effort values this model supports, in priority order\n"
            + "(highest priority first). Standard values: low, medium, high, xhigh, max,\n"
            + "minimal, none. Known models are prefilled automatically.");

        _lblReasoningEffort.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblReasoningEffort.AutoSize = true;
        _lblReasoningEffort.Margin = new Padding(0, 8, 8, 4);
        _lblReasoningEffort.Text = "Reasoning Effort:";

        _cmbReasoningEffort.Dock = DockStyle.Fill;
        _cmbReasoningEffort.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbReasoningEffort.Margin = new Padding(0, 4, 0, 4);
        _toolTip.SetToolTip(
            _cmbReasoningEffort,
            "The reasoning_effort value sent upstream when Reasoning Effort Priority is\n"
            + "Proxy Priority. Leave empty to send nothing.");

        _lblReasoningEffortFormats.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblReasoningEffortFormats.AutoSize = true;
        _lblReasoningEffortFormats.Margin = new Padding(0, 8, 8, 4);
        _lblReasoningEffortFormats.Text = "Reasoning Effort Formats:";

        _lstReasoningEffortFormats.CheckOnClick = true;
        _lstReasoningEffortFormats.Dock = DockStyle.Fill;
        _lstReasoningEffortFormats.Margin = new Padding(0, 4, 0, 4);
        _lstReasoningEffortFormats.Items.AddRange([.. ReasoningEffortFormatOptions()]);
        _toolTip.SetToolTip(
            _lstReasoningEffortFormats,
            "Wire shapes sent when Reasoning Effort Priority is Proxy Priority; select any\n"
            + "combination. Legacy sends top-level reasoning_effort; Modern sends the nested\n"
            + "reasoning.effort object; Qwen Cloud sends extra_body with enable_thinking and\n"
            + "reasoning_effort; llama.cpp/vLLM sends chat_template_kwargs. Ignored for the\n"
            + "other priorities.");

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
        _chkEnableThinkingCompatibility.CheckedChanged += (_, _) => UpdateThinkingReasoningGroupState();

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
            new ThinkingModeOption(ThinkingMode.QwenThinkingCompatible, "Qwen Thinking Compatible ([Thinking] → reasoning_content, [Answer] → answer)"),
        ]);
        _cmbThinkingHandling.SelectedIndex = 0;
        _toolTip.SetToolTip(
            _cmbThinkingHandling,
            "Controls how upstream <think> reasoning is surfaced to clients.\n"
            + "Leave inline keeps it in the visible answer; moving it to reasoning_content lets\n"
            + "clients like VS render a collapsible thinking panel; removing it hides it from\n"
            + "clients entirely while captured logs still retain the original upstream body.\n"
            + "Qwen Thinking Compatible handles models that emit literal [Thinking] and [Answer]\n"
            + "markers: the text between them becomes reasoning_content and the rest the answer.");

        _grpThinkingReasoning.AutoSize = true;
        _grpThinkingReasoning.AutoSizeMode = AutoSizeMode.GrowOnly;
        _grpThinkingReasoning.Controls.Add(_tlpThinkingReasoning);
        _grpThinkingReasoning.Dock = DockStyle.Fill;
        _grpThinkingReasoning.Margin = new Padding(0, 4, 0, 8);
        _grpThinkingReasoning.Text = "Thinking && Reasoning";

        _tlpThinkingReasoning.AutoSize = true;
        _tlpThinkingReasoning.AutoSizeMode = AutoSizeMode.GrowOnly;
        _tlpThinkingReasoning.ColumnCount = 2;
        _tlpThinkingReasoning.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpThinkingReasoning.Dock = DockStyle.Fill;
        _tlpThinkingReasoning.RowCount = 5;
        _tlpThinkingReasoning.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpThinkingReasoning.Controls.Add(_lblThinkingHandling, 0, 0);
        _tlpThinkingReasoning.Controls.Add(_cmbThinkingHandling, 1, 0);
        _tlpThinkingReasoning.Controls.Add(_lblReasoningEffortPriority, 0, 1);
        _tlpThinkingReasoning.Controls.Add(_cmbReasoningEffortPriority, 1, 1);
        _tlpThinkingReasoning.Controls.Add(_lblReasoningEffortValues, 0, 2);
        _tlpThinkingReasoning.Controls.Add(_txtReasoningEffortValues, 1, 2);
        _tlpThinkingReasoning.Controls.Add(_lblReasoningEffort, 0, 3);
        _tlpThinkingReasoning.Controls.Add(_cmbReasoningEffort, 1, 3);
        _tlpThinkingReasoning.Controls.Add(_lblReasoningEffortFormats, 0, 4);
        _tlpThinkingReasoning.Controls.Add(_lstReasoningEffortFormats, 1, 4);

        _tlpCapGroup.ColumnCount = 1;
        _tlpCapGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpCapGroup.RowCount = 3;
        _tlpCapGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpCapGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpCapGroup.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpCapGroup.Dock = DockStyle.Fill;
        _tlpCapGroup.Padding = new Padding(8);

        _flpCapButtons.AutoSize = true;
        _flpCapButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpCapButtons.FlowDirection = FlowDirection.LeftToRight;
        _flpCapButtons.WrapContents = false;
        _flpCapButtons.Margin = new Padding(0);
        _flpCapButtons.Controls.Add(_btnCapAutoDetect);
        _flpCapButtons.Controls.Add(_btnCapDefaults);
        _flpCapButtons.Controls.Add(_btnCapAdd);
        _flpCapButtons.Controls.Add(_btnCapRemove);

        _btnCapAutoDetect.AutoSize = true;
        _btnCapAutoDetect.Margin = new Padding(0, 0, 4, 0);
        _btnCapAutoDetect.Text = "Auto-Detect";
        _btnCapAutoDetect.Click += BtnCapAutoDetect_Click;
        _toolTip.SetToolTip(
            _btnCapAutoDetect,
            "Best-effort: queries the upstream's model metadata endpoints (data calls only,\n"
            + "no model invocations) and fills the table with the detected capabilities.\n"
            + "Leaves the table blank when nothing can be detected.");

        _btnCapDefaults.AutoSize = true;
        _btnCapDefaults.Margin = new Padding(0, 0, 4, 0);
        _btnCapDefaults.Text = "Set Defaults";
        _btnCapDefaults.Click += BtnCapDefaults_Click;
        _toolTip.SetToolTip(
            _btnCapDefaults,
            "Replace the table with the default capability set (text, chat, function calling),\n"
            + "which is what clients such as GitHub Copilot require.");

        _btnCapAdd.AutoSize = true;
        _btnCapAdd.Margin = new Padding(0, 0, 4, 0);
        _btnCapAdd.Text = "Add";
        _btnCapAdd.Click += BtnCapAdd_Click;
        _toolTip.SetToolTip(
            _btnCapAdd,
            "Add a row. Pick a known capability from the dropdown or type a custom one.");

        _btnCapRemove.AutoSize = true;
        _btnCapRemove.Margin = new Padding(4, 0, 0, 0);
        _btnCapRemove.Text = "Remove";
        _btnCapRemove.Click += BtnCapRemove_Click;
        _toolTip.SetToolTip(_btnCapRemove, "Remove the selected row.");

        _lblCapStatus.Text = "Auto-Detect from the model, Add a known capability from the dropdown, or type a custom one. Tick Enabled to advertise it.";

        DataGridViewComboBoxColumn colCapability = new()
        {
            HeaderText = "Capability",
            Name = "_colCapCapability",
            FillWeight = 70,
            FlatStyle = FlatStyle.System,
        };
        foreach (string token in ModelCapabilities.Tokens)
            colCapability.Items.Add(token);

        DataGridViewCheckBoxColumn colEnabled = new()
        {
            HeaderText = "Enabled",
            Name = "_colCapEnabled",
            FillWeight = 30,
        };

        _dgvCapabilities.AllowUserToAddRows = false;
        _dgvCapabilities.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _dgvCapabilities.RowHeadersVisible = false;
        _dgvCapabilities.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _dgvCapabilities.MultiSelect = false;
        _dgvCapabilities.Dock = DockStyle.Fill;
        _dgvCapabilities.MinimumSize = new Size(0, 100);
        _dgvCapabilities.Columns.Add(colCapability);
        _dgvCapabilities.Columns.Add(colEnabled);
        _dgvCapabilities.EditingControlShowing += DgvCapabilities_EditingControlShowing;
        _dgvCapabilities.CellValidating += DgvCapabilities_CellValidating;
        _dgvCapabilities.CellEndEdit += DgvCapabilities_CellEndEdit;

        _tlpCapGroup.Controls.Add(_flpCapButtons, 0, 0);
        _tlpCapGroup.Controls.Add(_lblCapStatus, 0, 1);
        _tlpCapGroup.Controls.Add(_dgvCapabilities, 0, 2);

        _grpClientCapabilities.AutoSize = false;
        _grpClientCapabilities.Size = new Size(560, 240);
        _grpClientCapabilities.Controls.Add(_tlpCapGroup);
        _grpClientCapabilities.Dock = DockStyle.Fill;
        _grpClientCapabilities.Margin = new Padding(0, 4, 0, 8);
        _grpClientCapabilities.Text = "Model Capabilities";

        _chkEnableHeartbeats.AutoSize = true;
        _chkEnableHeartbeats.Margin = new Padding(0, 2, 0, 2);
        _chkEnableHeartbeats.Text = "Enable streaming heartbeats for this model (keep-alive frames while waiting)";
        _chkEnableHeartbeats.Checked = true;

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
        // Commit any pending capability cell edit so a typed custom value is captured before
        // the dialog closes and the capabilities are read.
        _dgvCapabilities.EndEdit();

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
            // Resolve the API key from the selected credential.
            string? apiKey = ResolveApiKey();

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

    private async void BtnModelInfo_Click(object? sender, EventArgs e)
    {
        string modelId = _cmbModelName.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            MessageBox.Show(this, "Select a model first.", "No Model Selected",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_upstreamUrl) ||
            !Uri.TryCreate(_upstreamUrl, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Set the upstream URL first.", "No Upstream URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnModelInfo.Enabled = false;
        _lblModelInfoStatus.Text = "Fetching model info…";

        try
        {
            string? apiKey = ResolveApiKey();

            Uri modelUri = UpstreamUriHelper.BuildRequestUri(_upstreamUrl, "v1/models/" + Uri.EscapeDataString(modelId));
            using var request = new HttpRequestMessage(HttpMethod.Get, modelUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await _modelFetchClient.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Fall back to the list endpoint (Ollama and others don't support GET /v1/models/{id}).
                Uri listUri = UpstreamUriHelper.BuildRequestUri(_upstreamUrl, "v1/models");
                using var listRequest = new HttpRequestMessage(HttpMethod.Get, listUri);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    listRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

                using var listResponse = await _modelFetchClient.SendAsync(listRequest);
                string listBody = await listResponse.Content.ReadAsStringAsync();

                if (listResponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(listBody);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp)
                                && string.Equals(idProp.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                            {
                                body = item.GetRawText();
                                break;
                            }
                        }
                    }
                }
                else
                {
                    body = listBody;
                    response.StatusCode = listResponse.StatusCode;
                }
            }

            // Pretty-print for display.
            string displayText;
            try
            {
                using var doc = JsonDocument.Parse(body);
                displayText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                displayText = body;
            }

            _lblModelInfoStatus.ForeColor = SystemColors.GrayText;
            _lblModelInfoStatus.Text = response.IsSuccessStatusCode ? "Model info loaded." : $"HTTP {(int)response.StatusCode}";

            // Attempt to auto-fill context window from the response.
            if (TryExtractContextWindow(body, out int? contextWindow))
            {
                if (contextWindow is > 0)
                {
                    _txtContextWindow.Text = contextWindow.Value.ToString();
                    _lblModelInfoStatus.Text += $" Context window set to {contextWindow.Value:N0}.";
                    _lblModelInfoStatus.ForeColor = Color.Green;
                }
            }

            var dialog = new ModelInfoDialog(modelId, displayText);
            dialog.Show(this);
        }
        catch (Exception ex)
        {
            _lblModelInfoStatus.ForeColor = Color.Red;
            _lblModelInfoStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _btnModelInfo.Enabled = true;
        }
    }

    private async void BtnCapAutoDetect_Click(object? sender, EventArgs e)
    {
        string modelId = _cmbModelName.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            SetCapStatus("Select a model first.", Color.Red);
            return;
        }

        if (string.IsNullOrWhiteSpace(_upstreamUrl) ||
            !Uri.TryCreate(_upstreamUrl, UriKind.Absolute, out _))
        {
            SetCapStatus("Set the upstream URL first.", Color.Red);
            return;
        }

        _btnCapAutoDetect.Enabled = false;
        SetCapStatus("Detecting capabilities…", SystemColors.GrayText);

        try
        {
            CapabilityDetectionResult result = await CapabilityDetector.DetectAsync(
                _upstreamUrl, modelId, ResolveApiKey(), CancellationToken.None);

            // Replace the table contents entirely with the detected capabilities.
            Capabilities = result.Capabilities;
            SetCapStatus(result.Summary, result.Capabilities.Count > 0 ? Color.Green : SystemColors.GrayText);
        }
        catch (Exception ex)
        {
            SetCapStatus($"Error: {ex.Message}", Color.Red);
        }
        finally
        {
            _btnCapAutoDetect.Enabled = true;
        }
    }

    private void BtnCapDefaults_Click(object? sender, EventArgs e)
    {
        // Replace the table with the default capability set (text, chat, function calling).
        Capabilities = [.. ModelCapabilities.Defaults];
        SetCapStatus($"Set defaults: {string.Join(", ", ModelCapabilities.Defaults)}.", Color.Green);
    }

    private void BtnCapAdd_Click(object? sender, EventArgs e)
    {
        int idx = _dgvCapabilities.Rows.Add();
        _dgvCapabilities.Rows[idx].Cells[0].Value = string.Empty;
        _dgvCapabilities.Rows[idx].Cells[1].Value = true;
        _dgvCapabilities.CurrentCell = _dgvCapabilities.Rows[idx].Cells[0];
    }

    private void BtnCapRemove_Click(object? sender, EventArgs e)
    {
        int idx = _dgvCapabilities.SelectedRows.Count > 0
            ? _dgvCapabilities.SelectedRows[0].Index
            : _dgvCapabilities.CurrentRow?.Index ?? -1;
        if (idx >= 0 && idx < _dgvCapabilities.Rows.Count)
            _dgvCapabilities.Rows.RemoveAt(idx);
    }

    private void SetCapStatus(string text, Color color)
    {
        _lblCapStatus.Text = text;
        _lblCapStatus.ForeColor = color;
    }

    /// <summary>
    /// The built-in combo cell is select-only; make it editable so the user can pick a known
    /// capability from the dropdown OR type a custom one.
    /// </summary>
    private void DgvCapabilities_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        DataGridViewCell? cell = _dgvCapabilities.CurrentCell;
        if (cell is null || _dgvCapabilities.Columns[cell.ColumnIndex].Name != "_colCapCapability")
            return;
        if (e.Control is ComboBox combo)
            combo.DropDownStyle = ComboBoxStyle.DropDown;
    }

    // Holds the typed capability text captured in CellValidating (where the combo is still
    // active) so CellEndEdit can apply it. The grid commits SelectedItem (null for a custom
    // value) rather than the combo's Text, so the captured value is applied after the commit.
    private string? _pendingCapValue;

    /// <summary>
    /// Capture the typed text from the combo while it is still active, and register it in the
    /// column's Items so validation passes. The grid commits SelectedItem (null for a custom
    /// value) rather than the combo's Text, so the captured value is applied in CellEndEdit.
    /// </summary>
    private void DgvCapabilities_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_dgvCapabilities.Columns[e.ColumnIndex].Name != "_colCapCapability")
            return;

        string? value = _dgvCapabilities.EditingControl is ComboBox combo
            ? combo.Text?.Trim()
            : e.FormattedValue?.ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(value))
            return;

        var col = (DataGridViewComboBoxColumn)_dgvCapabilities.Columns[e.ColumnIndex];
        if (!col.Items.Contains(value))
            col.Items.Add(value);

        _pendingCapValue = value;
    }

    /// <summary>
    /// Apply the captured typed text to the cell. The grid has already committed SelectedItem
    /// (null for a custom value) by the time this fires, so set the cell value explicitly.
    /// </summary>
    private void DgvCapabilities_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_dgvCapabilities.Columns[e.ColumnIndex].Name != "_colCapCapability")
            return;
        if (_pendingCapValue is null)
            return;
        _dgvCapabilities.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = _pendingCapValue;
        _pendingCapValue = null;
    }

    /// <summary>Resolves the API key for the currently selected credential, if any.</summary>
    private string? ResolveApiKey()
    {
        string? credentialName = CredentialName;
        if (string.IsNullOrWhiteSpace(credentialName))
            return null;
        return _credentials.FirstOrDefault(
            c => string.Equals(c.Name, credentialName, StringComparison.OrdinalIgnoreCase))?.Secret;
    }

    private static bool TryExtractContextWindow(string json, out int? contextWindow)
    {
        contextWindow = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Search common field names for context window size.
            string[] candidateNames = ["context_window", "context_length", "max_context_length", "max_context", "context", "num_ctx", "max_tokens"];
            foreach (string name in candidateNames)
            {
                if (root.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Number
                    && val.TryGetInt32(out int v) && v > 0)
                {
                    contextWindow = v;
                    return true;
                }
            }

            // Check nested "details" or "parameters" objects (Ollama /api/show style).
            foreach (string nested in new[] { "details", "parameters", "config" })
            {
                if (root.TryGetProperty(nested, out var nestedObj) && nestedObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (string name in candidateNames)
                    {
                        if (nestedObj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Number
                            && val.TryGetInt32(out int v) && v > 0)
                        {
                            contextWindow = v;
                            return true;
                        }
                    }
                }
            }
        }
        catch (JsonException) { }

        return false;
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
        dlg._suppressReasoningPrefill = true;
        dlg._settings = settings;
        dlg._stats = stats;
        dlg.PopulateInstructionSets(instructionSets);
        dlg.PopulateContextSummarizeModels();
        dlg.PopulateCredentials(credentials);
        dlg.PopulateUpstreamTypes(mapping.UpstreamType);
        dlg.PopulateUpstreamUrls(existingUpstreamUrls, mapping.UpstreamUrl);
        dlg._txtProxyName.Text = mapping.ProxyName ?? string.Empty;
        dlg.CredentialName = mapping.CredentialName;
        dlg._upstreamUrl = mapping.UpstreamUrl ?? string.Empty;
        dlg.PopulateModelItems(existingModelItems, mapping.ModelName);
        dlg.InstructionSetName = mapping.InstructionSetName;
        dlg.ContextSummarizeModelId = mapping.ContextSummarizeModelId;
        dlg._chkIsEnabled.Checked = mapping.IsEnabled;
        dlg.TemperaturePriority = mapping.TemperaturePriority;
        dlg.RepeatPenaltyPriority = mapping.RepeatPenaltyPriority;
        dlg.EnableThinkingCompatibility = mapping.EnableThinkingCompatibility;
        dlg.ThinkingMode = mapping.ThinkingMode;
        dlg.Capabilities = mapping.Capabilities;

        dlg.EnableHeartbeats = mapping.EnableHeartbeats;
        dlg.UpstreamTimeoutSeconds = mapping.UpstreamTimeoutSeconds;
        dlg.ContextWindowTokens = mapping.ContextWindowTokens;
        dlg.ProactiveOverflowPercent = mapping.ProactiveOverflowPercent;
        dlg.ProactiveOverflowTokens = mapping.ProactiveOverflowTokens;
        dlg.Temperature = mapping.Temperature;
        dlg.RepeatPenalty = mapping.RepeatPenalty;
        dlg.ReasoningEffortPriority = mapping.ReasoningEffortPriority;
        dlg.ReasoningEffortValues = mapping.ReasoningEffortValues;
        dlg.ReasoningEffortFormat = mapping.ReasoningEffortFormat;
        if (!string.IsNullOrWhiteSpace(mapping.ReasoningEffort))
            dlg.ReasoningEffort = mapping.ReasoningEffort;
        dlg.UpdateReasoningEffortControlStates();
        dlg.UpdateThinkingReasoningGroupState();
        dlg._suppressReasoningPrefill = false;
        dlg.TryPrefillReasoningEffortProfile(mapping.ModelName);
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
        mapping.ContextSummarizeModelId = dlg.ContextSummarizeModelId;
        mapping.TemperaturePriority = dlg.TemperaturePriority;
        mapping.RepeatPenaltyPriority = dlg.RepeatPenaltyPriority;
        mapping.EnableThinkingCompatibility = dlg.EnableThinkingCompatibility;
        mapping.ThinkingMode = dlg.ThinkingMode;
        mapping.Capabilities = dlg.Capabilities;

        mapping.EnableHeartbeats = dlg.EnableHeartbeats;
        mapping.UpstreamTimeoutSeconds = dlg.UpstreamTimeoutSeconds;
        mapping.ContextWindowTokens = dlg.ContextWindowTokens;
        mapping.ProactiveOverflowPercent = dlg.ProactiveOverflowPercent;
        mapping.ProactiveOverflowTokens = dlg.ProactiveOverflowTokens;
        mapping.Temperature = dlg.Temperature;
        mapping.RepeatPenalty = dlg.RepeatPenalty;
        mapping.ReasoningEffortPriority = dlg.ReasoningEffortPriority;
        mapping.ReasoningEffort = dlg.ReasoningEffort;
        mapping.ReasoningEffortValues = dlg.ReasoningEffortValues;
        mapping.ReasoningEffortFormat = dlg.ReasoningEffortFormat;
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

    /// <summary>Display wrapper binding a friendly label to a <see cref="ReasoningEffortFormat"/> value.</summary>
    private sealed record ReasoningEffortFormatOption(ReasoningEffortFormat Format, string Label)
    {
        public override string ToString() => Label;
    }

    private static ReasoningEffortFormatOption[] ReasoningEffortFormatOptions() =>
    [
        new(ReasoningEffortFormat.Legacy, "Legacy (top-level reasoning_effort)"),
        new(ReasoningEffortFormat.Modern, "Modern (nested reasoning.effort)"),
        new(ReasoningEffortFormat.QwenCloud, "Qwen Cloud (extra_body with enable_thinking)"),
        new(ReasoningEffortFormat.ChatTemplateKwargs, "llama.cpp / vLLM (chat_template_kwargs)"),
    ];

    private static SamplingPriorityOption[] SamplingPriorityOptions() =>
    [
        new(SamplingPriority.ClientApp, "Client App Priority (client value wins)"),
        new(SamplingPriority.Proxy, "Proxy Priority (configured value overrides client)"),
        new(SamplingPriority.Provider, "Provider Priority (field omitted, platform setting wins)"),
    ];
}
