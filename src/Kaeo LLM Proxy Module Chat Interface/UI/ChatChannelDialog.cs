using Kaeo.LlmProxy.Core.Modules;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Modal dialog for adding or editing a chat channel: name, provider, target, credential
/// from the host's central store, webhook URL, provider-specific fields (shown per provider),
/// poll interval, and enabled flag. A Test button sends a test message through the provider.
/// </summary>
internal sealed class ChatChannelDialog : Form
{
    private const string NoCredentialItem = "(none)";

    private static readonly (string Display, string Type)[] Providers =
    [
        ("Discord", "discord"),
        ("MS Teams", "teams"),
        ("Slack", "slack"),
        ("Google Chat", "googlechat"),
        ("Zoom", "zoom"),
    ];

    private readonly ISecretProvider _secrets;
    private readonly ChatProviderFactory _factory;

    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblName = new();
    private readonly TextBox _txtName = new();
    private readonly Label _lblProvider = new();
    private readonly ComboBox _cmbProvider = new();
    private readonly Label _lblTarget = new();
    private readonly TextBox _txtTarget = new();
    private readonly Label _lblCredential = new();
    private readonly ComboBox _cmbCredential = new();
    private readonly Label _lblWebhook = new();
    private readonly TextBox _txtWebhook = new();
    private readonly Label _lblTenantId = new();
    private readonly TextBox _txtTenantId = new();
    private readonly Label _lblClientId = new();
    private readonly TextBox _txtClientId = new();
    private readonly Label _lblChatId = new();
    private readonly TextBox _txtChatId = new();
    private readonly Label _lblAccountId = new();
    private readonly TextBox _txtAccountId = new();
    private readonly Label _lblGoogleNote = new();
    private readonly Label _lblPollInterval = new();
    private readonly NumericUpDown _nudPollInterval = new();
    private readonly CheckBox _chkEnabled = new();
    private readonly Label _lblHint = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnTest = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    /// <summary>
    /// Builds the dialog offering the credential names in <paramref name="credentialNames"/>
    /// (from the host's central credential store) for authentication.
    /// </summary>
    public ChatChannelDialog(ISecretProvider secrets, IReadOnlyList<string> credentialNames)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        ArgumentNullException.ThrowIfNull(credentialNames);
        _factory = new ChatProviderFactory(secrets);

        SuspendLayout();

        _tlpMain.ColumnCount = 2;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);
        _tlpMain.RowCount = 14;
        for (int i = 0; i < 14; i++)
            _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddRow(0, _lblName, "Name:", _txtName);
        AddRow(1, _lblProvider, "Provider:", _cmbProvider);
        AddRow(2, _lblTarget, "Target:", _txtTarget);
        AddRow(3, _lblCredential, "Credential:", _cmbCredential);
        AddRow(4, _lblWebhook, "Webhook URL:", _txtWebhook);
        AddRow(5, _lblTenantId, "Tenant id:", _txtTenantId);
        AddRow(6, _lblClientId, "Client id:", _txtClientId);
        AddRow(7, _lblChatId, "Chat id:", _txtChatId);
        AddRow(8, _lblAccountId, "Account id:", _txtAccountId);

        _lblGoogleNote.AutoSize = true;
        _lblGoogleNote.ForeColor = SystemColors.GrayText;
        _lblGoogleNote.Margin = new Padding(0, 0, 0, 4);
        _lblGoogleNote.MaximumSize = new Size(420, 0);
        _lblGoogleNote.Text = "For receiving, the credential's Secret must hold the full " +
            "service-account JSON key (client_email, private_key, token_uri).";
        _tlpMain.SetColumnSpan(_lblGoogleNote, 2);
        _tlpMain.Controls.Add(_lblGoogleNote, 0, 9);

        AddRow(10, _lblPollInterval, "Poll interval (s):", _nudPollInterval);

        _chkEnabled.AutoSize = true;
        _chkEnabled.Checked = true;
        _chkEnabled.Margin = new Padding(0, 4, 0, 4);
        _chkEnabled.Text = "Enabled";
        _tlpMain.SetColumnSpan(_chkEnabled, 2);
        _tlpMain.Controls.Add(_chkEnabled, 0, 11);

        _lblHint.AutoSize = true;
        _lblHint.ForeColor = SystemColors.GrayText;
        _lblHint.Margin = new Padding(0, 4, 0, 4);
        _lblHint.MaximumSize = new Size(420, 0);
        _tlpMain.SetColumnSpan(_lblHint, 2);
        _tlpMain.Controls.Add(_lblHint, 0, 12);

        _txtName.PlaceholderText = "e.g. team-alerts";
        _txtTarget.PlaceholderText = "Channel / chat id, or spaces/AAAA for Google Chat";
        _txtWebhook.PlaceholderText = "https://... (incoming webhook; contains a secret token)";
        _txtTenantId.PlaceholderText = "Entra tenant id (receive only)";
        _txtClientId.PlaceholderText = "App registration client id (receive only)";
        _txtChatId.PlaceholderText = "Graph chat id, e.g. 19:...@thread.v2 (receive only)";
        _txtAccountId.PlaceholderText = "Zoom account id (Server-to-Server OAuth app)";

        _cmbProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach ((string display, _) in Providers)
            _cmbProvider.Items.Add(display);
        _cmbProvider.SelectedIndex = 0;
        _cmbProvider.SelectedIndexChanged += (_, _) => UpdateProviderFields();

        _cmbCredential.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbCredential.Items.Add(NoCredentialItem);
        foreach (string credentialName in credentialNames)
            _cmbCredential.Items.Add(credentialName);
        _cmbCredential.SelectedIndex = 0;

        _nudPollInterval.Minimum = 5;
        _nudPollInterval.Maximum = 300;
        _nudPollInterval.Value = 15;

        _flpButtons.AutoSize = true;
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0, 8, 0, 0);
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Controls.Add(_btnTest);
        _tlpMain.SetColumnSpan(_flpButtons, 2);
        _tlpMain.Controls.Add(_flpButtons, 0, 13);

        _btnOk.AutoSize = true;
        _btnOk.MinimumSize = new Size(80, 28);
        _btnOk.Text = "OK";
        _btnOk.Click += BtnOk_Click;

        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Margin = new Padding(0, 0, 8, 0);
        _btnCancel.MinimumSize = new Size(80, 28);
        _btnCancel.Text = "Cancel";

        _btnTest.AutoSize = true;
        _btnTest.Margin = new Padding(0, 0, 8, 0);
        _btnTest.MinimumSize = new Size(110, 28);
        _btnTest.Text = "Send Test Message";
        _btnTest.Click += BtnTest_Click;

        AcceptButton = _btnOk;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _btnCancel;
        ClientSize = new Size(540, 480);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Chat Channel";

        UpdateProviderFields();
        ResumeLayout(false);
    }

    private void AddRow(int row, Label label, string caption, Control input)
    {
        label.Anchor = AnchorStyles.Left;
        label.AutoSize = true;
        label.Margin = new Padding(0, 4, 8, 4);
        label.Text = caption;

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 4);

        _tlpMain.Controls.Add(label, 0, row);
        _tlpMain.Controls.Add(input, 1, row);
    }

    private string SelectedProviderType => Providers[Math.Max(_cmbProvider.SelectedIndex, 0)].Type;

    private string? SelectedCredentialName =>
        _cmbCredential.SelectedItem is string item && item != NoCredentialItem ? item : null;

    /// <summary>Shows/hides the provider-specific rows and updates the hint for the selection.</summary>
    private void UpdateProviderFields()
    {
        string type = SelectedProviderType;
        bool teams = type == "teams";
        bool zoom = type == "zoom";
        bool google = type == "googlechat";
        bool webhook = teams || google;

        SetRowVisible(_lblTenantId, _txtTenantId, teams);
        SetRowVisible(_lblClientId, _txtClientId, teams);
        SetRowVisible(_lblChatId, _txtChatId, teams);
        SetRowVisible(_lblAccountId, _txtAccountId, zoom);
        _lblGoogleNote.Visible = google;
        SetRowVisible(_lblWebhook, _txtWebhook, webhook);
        SetRowVisible(_lblTarget, _txtTarget, true);

        _lblHint.Text = type switch
        {
            "discord" => "Credential Secret = bot token. Target = channel id. The bot needs " +
                "Send Messages and Read Message History in that channel.",
            "teams" => "Webhook URL (Workflows incoming webhook) is used to send. For receiving, " +
                "fill tenant/client/chat ids and select a credential holding the client secret.",
            "slack" => "Credential Secret = bot token (xoxb-...). Target = channel id. The bot " +
                "needs chat:write and channels:history and must be invited to the channel.",
            "googlechat" => "Webhook URL sends. Target = spaces/AAAA. For receiving, select a " +
                "credential whose Secret is the service-account JSON.",
            "zoom" => "Credential Username = client id, Secret = client secret of a " +
                "Server-to-Server OAuth app. Target = channel id.",
            _ => string.Empty,
        };
    }

    private static void SetRowVisible(Label label, Control input, bool visible)
    {
        label.Visible = visible;
        input.Visible = visible;
    }

    /// <summary>Builds a channel config from the current field values (without validation).</summary>
    private ChatChannelConfig BuildConfig(int id)
    {
        string type = SelectedProviderType;
        string? configJson = null;
        if (type == "teams")
        {
            Dictionary<string, string> config = [];
            if (_txtTenantId.Text.Trim().Length > 0) config["tenantId"] = _txtTenantId.Text.Trim();
            if (_txtClientId.Text.Trim().Length > 0) config["clientId"] = _txtClientId.Text.Trim();
            if (_txtChatId.Text.Trim().Length > 0) config["chatId"] = _txtChatId.Text.Trim();
            if (config.Count > 0)
                configJson = JsonSerializer.Serialize(config);
        }
        else if (type == "zoom" && _txtAccountId.Text.Trim().Length > 0)
        {
            configJson = JsonSerializer.Serialize(new { accountId = _txtAccountId.Text.Trim() });
        }

        return new ChatChannelConfig
        {
            Id = id,
            Name = _txtName.Text.Trim(),
            ProviderType = type,
            Target = _txtTarget.Text.Trim(),
            CredentialName = SelectedCredentialName,
            WebhookUrl = _txtWebhook.Text.Trim().Length > 0 ? _txtWebhook.Text.Trim() : null,
            ConfigJson = configJson,
            Enabled = _chkEnabled.Checked,
            PollIntervalSeconds = (int)_nudPollInterval.Value,
        };
    }

    private void LoadConfig(ChatChannelConfig config)
    {
        _txtName.Text = config.Name;
        int providerIndex = Array.FindIndex(Providers, p => p.Type == config.ProviderType);
        _cmbProvider.SelectedIndex = providerIndex >= 0 ? providerIndex : 0;
        _txtTarget.Text = config.Target;
        _cmbCredential.SelectedItem = config.CredentialName ?? NoCredentialItem;
        if (_cmbCredential.SelectedIndex < 0)
            _cmbCredential.SelectedIndex = 0;
        _txtWebhook.Text = config.WebhookUrl ?? string.Empty;
        _nudPollInterval.Value = Math.Clamp(config.PollIntervalSeconds, (int)_nudPollInterval.Minimum, (int)_nudPollInterval.Maximum);
        _chkEnabled.Checked = config.Enabled;

        if (!string.IsNullOrWhiteSpace(config.ConfigJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(config.ConfigJson);
                _txtTenantId.Text = ChatProviderSupport.GetStringOrNull(document.RootElement, "tenantId") ?? string.Empty;
                _txtClientId.Text = ChatProviderSupport.GetStringOrNull(document.RootElement, "clientId") ?? string.Empty;
                _txtChatId.Text = ChatProviderSupport.GetStringOrNull(document.RootElement, "chatId") ?? string.Empty;
                _txtAccountId.Text = ChatProviderSupport.GetStringOrNull(document.RootElement, "accountId") ?? string.Empty;
            }
            catch (JsonException)
            {
                // Malformed stored config: leave the fields empty.
            }
        }

        UpdateProviderFields();
    }

    /// <summary>Validates the fields; shows a message and returns false when invalid.</summary>
    private bool ValidateFields()
    {
        ChatChannelConfig config = BuildConfig(0);

        if (config.Name.Length == 0)
            return Fail("Name is required.");

        bool webhookProvider = config.ProviderType is "teams" or "googlechat";
        if (!webhookProvider && config.Target.Length == 0)
            return Fail("Target (channel id) is required for this provider.");

        if (webhookProvider && string.IsNullOrWhiteSpace(config.WebhookUrl))
            return Fail("A webhook URL is required to send on this provider.");

        if (!string.IsNullOrWhiteSpace(config.WebhookUrl) &&
            (!Uri.TryCreate(config.WebhookUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            return Fail("The webhook URL must be an absolute https URL.");
        }

        if (config.ProviderType == "googlechat" && config.Target.Length > 0 &&
            !config.Target.StartsWith("spaces/", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("The Google Chat target must look like 'spaces/AAAA'.");
        }

        if (config.ProviderType == "zoom" &&
            ChatProviderSupport.GetConfigValue(config, "accountId") is null)
        {
            return Fail("Account id is required for Zoom (Server-to-Server OAuth app).");
        }

        return true;

        bool Fail(string message)
        {
            MessageBox.Show(this, message, "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (ValidateFields())
            DialogResult = DialogResult.OK;
    }

    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        if (!ValidateFields())
            return;

        ChatChannelConfig config = BuildConfig(0);
        IChatProvider provider = _factory.GetProvider(config);

        _btnTest.Enabled = false;
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            string sentId = await provider.SendAsync(config,
                $"Test message from the Kaeo LLM Proxy Chat Interface module ({DateTime.UtcNow:u}).",
                cts.Token);

            MessageBox.Show(this,
                "Test message sent successfully." + (sentId.Length > 0 ? $"\nMessage id: {sentId}" : string.Empty),
                "Test Succeeded", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "The test message timed out.", "Test Failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            MessageBox.Show(this, $"Test failed: {ex.Message}", "Test Failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnTest.Enabled = true;
        }
    }

    /// <summary>
    /// Shows the modal add/edit dialog. Returns the edited channel on OK, or null when
    /// cancelled. Duplicate-name handling is done by the caller.
    /// </summary>
    public static ChatChannelConfig? ShowAddEditDialog(
        IWin32Window owner, ISecretProvider secrets, IReadOnlyList<string> credentialNames, ChatChannelConfig? existing = null)
    {
        using ChatChannelDialog dlg = new(secrets, credentialNames);

        if (existing is not null)
        {
            dlg.Text = "Edit Chat Channel";
            dlg.LoadConfig(existing);
        }
        else
        {
            dlg.Text = "Add Chat Channel";
        }

        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.BuildConfig(existing?.Id ?? 0) : null;
    }
}