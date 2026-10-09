using Kaeo.LlmProxy.Core.Security;

namespace Kaeo.LlmProxy;

/// <summary>
/// Modal dialog that prompts the user for the passphrase used to encrypt/decrypt model-mapping
/// API keys. Offers an optional "remember" checkbox so the passphrase can be persisted to
/// settings.jsonc for automatic decryption on subsequent launches.
/// </summary>
internal sealed class PassphraseDialog : Form
{
    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblMessage = new();
    private readonly Label _lblPassphrase = new();
    private readonly TextBox _txtPassphrase = new();
    private readonly CheckBox _chkShow = new();
    private readonly CheckBox _chkRemember = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    /// <summary>The passphrase entered by the user. Only meaningful when <see cref="DialogResult"/> is OK.</summary>
    public string Passphrase => _txtPassphrase.Text;

    /// <summary>Whether the user opted to persist the passphrase to settings.jsonc.</summary>
    public bool RememberPassphrase => _chkRemember.Checked;

    public PassphraseDialog(string message, bool rememberByDefault = false)
    {
        BuildUi(message, rememberByDefault);
    }

    private void BuildUi(string message, bool rememberByDefault)
    {
        _lblMessage.AutoSize = true;
        _lblMessage.Dock = DockStyle.Fill;
        _lblMessage.Margin = new Padding(0, 0, 0, 8);
        _lblMessage.MaximumSize = new Size(360, 0);
        _lblMessage.Text = message;

        _lblPassphrase.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblPassphrase.AutoSize = true;
        _lblPassphrase.Margin = new Padding(0, 4, 8, 4);
        _lblPassphrase.Text = "Passphrase:";

        _txtPassphrase.Dock = DockStyle.Fill;
        _txtPassphrase.Margin = new Padding(0, 4, 0, 4);
        _txtPassphrase.UseSystemPasswordChar = true;

        _chkShow.Anchor = AnchorStyles.Left;
        _chkShow.AutoSize = true;
        _chkShow.Margin = new Padding(8, 4, 0, 4);
        _chkShow.Text = "Show";
        _chkShow.CheckedChanged += (_, _) => _txtPassphrase.UseSystemPasswordChar = !_chkShow.Checked;

        _chkRemember.AutoSize = true;
        _chkRemember.Checked = rememberByDefault;
        _chkRemember.Margin = new Padding(0, 8, 0, 4);
        _chkRemember.Text = "Remember passphrase in settings.jsonc";

        _btnOk.AutoSize = true;
        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.Margin = new Padding(0, 0, 6, 0);
        _btnOk.Text = "OK";

        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Text = "Cancel";

        _flpButtons.AutoSize = true;
        _flpButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0, 8, 0, 0);
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);

        _tlpMain.ColumnCount = 3;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(12);
        _tlpMain.RowCount = 4;
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tlpMain.SetColumnSpan(_lblMessage, 3);
        _tlpMain.Controls.Add(_lblMessage, 0, 0);
        _tlpMain.Controls.Add(_lblPassphrase, 0, 1);
        _tlpMain.Controls.Add(_txtPassphrase, 1, 1);
        _tlpMain.Controls.Add(_chkShow, 2, 1);
        _tlpMain.SetColumnSpan(_chkRemember, 3);
        _tlpMain.Controls.Add(_chkRemember, 0, 2);
        _tlpMain.SetColumnSpan(_flpButtons, 3);
        _tlpMain.Controls.Add(_flpButtons, 0, 3);

        AcceptButton = _btnOk;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _btnCancel;
        ClientSize = new Size(420, 180);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "PassphraseDialog";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Security Passphrase";
    }

    /// <summary>
    /// Shows the passphrase prompt. Returns true and outputs the passphrase when the user
    /// confirms a non-empty value; returns false when cancelled or left blank.
    /// </summary>
    public static bool Prompt(
        IWin32Window? owner,
        string message,
        out string passphrase,
        out bool remember,
        bool rememberByDefault = false)
    {
        using PassphraseDialog dialog = new(message, rememberByDefault);
        DialogResult result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        if (result != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.Passphrase))
        {
            passphrase = string.Empty;
            remember = false;
            return false;
        }

        passphrase = dialog.Passphrase;
        remember = dialog.RememberPassphrase;
        return true;
    }
}
