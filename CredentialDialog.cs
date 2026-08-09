using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy;

/// <summary>
/// Modal dialog for adding or editing a centrally stored <see cref="StoredCredential"/>
/// (a named secret such as an upstream API key). The secret is masked by default and can
/// be revealed with a "Show" toggle.
/// </summary>
internal sealed class CredentialDialog : Form
{
    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblName = new();
    private readonly TextBox _txtName = new();
    private readonly Label _lblUsername = new();
    private readonly TextBox _txtUsername = new();
    private readonly Label _lblSecret = new();
    private readonly TextBox _txtSecret = new();
    private readonly CheckBox _chkShowSecret = new();
    private readonly Label _lblPrivateKey = new();
    private readonly TextBox _txtPrivateKey = new();
    private readonly Button _btnImportKey = new();
    private readonly Label _lblCertificate = new();
    private readonly TextBox _txtCertificate = new();
    private readonly Button _btnImportCertificate = new();
    private readonly Label _lblDescription = new();
    private readonly TextBox _txtDescription = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CredentialName
    {
        get => _txtName.Text.Trim();
        set => _txtName.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Username
    {
        get => _txtUsername.Text.Trim();
        set => _txtUsername.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Secret
    {
        get => _txtSecret.Text.Trim();
        set => _txtSecret.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string PrivateKey
    {
        get => _txtPrivateKey.Text.Trim();
        set => _txtPrivateKey.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Certificate
    {
        get => _txtCertificate.Text.Trim();
        set => _txtCertificate.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get => _txtDescription.Text.Trim();
        set => _txtDescription.Text = value;
    }

    public CredentialDialog()
    {
        SuspendLayout();

        // _tlpMain
        _tlpMain.ColumnCount = 3;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.Controls.Add(_lblName, 0, 0);
        _tlpMain.SetColumnSpan(_txtName, 2);
        _tlpMain.Controls.Add(_txtName, 1, 0);
        _tlpMain.Controls.Add(_lblUsername, 0, 1);
        _tlpMain.SetColumnSpan(_txtUsername, 2);
        _tlpMain.Controls.Add(_txtUsername, 1, 1);
        _tlpMain.Controls.Add(_lblSecret, 0, 2);
        _tlpMain.Controls.Add(_txtSecret, 1, 2);
        _tlpMain.Controls.Add(_chkShowSecret, 2, 2);
        _tlpMain.Controls.Add(_lblPrivateKey, 0, 3);
        _tlpMain.Controls.Add(_txtPrivateKey, 1, 3);
        _tlpMain.Controls.Add(_btnImportKey, 2, 3);
        _tlpMain.Controls.Add(_lblCertificate, 0, 4);
        _tlpMain.Controls.Add(_txtCertificate, 1, 4);
        _tlpMain.Controls.Add(_btnImportCertificate, 2, 4);
        _tlpMain.Controls.Add(_lblDescription, 0, 5);
        _tlpMain.SetColumnSpan(_txtDescription, 2);
        _tlpMain.Controls.Add(_txtDescription, 1, 5);
        _tlpMain.SetColumnSpan(_flpButtons, 3);
        _tlpMain.Controls.Add(_flpButtons, 0, 6);
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);
        _tlpMain.RowCount = 7;
        for (int i = 0; i < 7; i++)
            _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _lblName
        _lblName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblName.AutoSize = true;
        _lblName.Margin = new Padding(0, 4, 8, 4);
        _lblName.Text = "Name:";

        // _txtName
        _txtName.Dock = DockStyle.Fill;
        _txtName.Margin = new Padding(0, 4, 0, 4);

        // _lblUsername
        _lblUsername.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblUsername.AutoSize = true;
        _lblUsername.Margin = new Padding(0, 4, 8, 4);
        _lblUsername.Text = "Username:";

        // _txtUsername
        _txtUsername.Dock = DockStyle.Fill;
        _txtUsername.Margin = new Padding(0, 4, 0, 4);
        _txtUsername.PlaceholderText = "Optional; e.g. SSH login user";

        // _lblSecret
        _lblSecret.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblSecret.AutoSize = true;
        _lblSecret.Margin = new Padding(0, 4, 8, 4);
        _lblSecret.Text = "Secret:";

        // _txtSecret
        _txtSecret.Dock = DockStyle.Fill;
        _txtSecret.Margin = new Padding(0, 4, 0, 4);
        _txtSecret.UseSystemPasswordChar = true;
        _txtSecret.PlaceholderText = "API key / bearer token / SSH password";

        // _chkShowSecret
        _chkShowSecret.Anchor = AnchorStyles.Left;
        _chkShowSecret.AutoSize = true;
        _chkShowSecret.Margin = new Padding(8, 4, 0, 4);
        _chkShowSecret.Text = "Show";
        _chkShowSecret.CheckedChanged += (_, _) => _txtSecret.UseSystemPasswordChar = !_chkShowSecret.Checked;

        // _lblPrivateKey
        _lblPrivateKey.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblPrivateKey.AutoSize = true;
        _lblPrivateKey.Margin = new Padding(0, 4, 8, 4);
        _lblPrivateKey.Text = "Private key:";

        // _txtPrivateKey
        _txtPrivateKey.AcceptsReturn = true;
        _txtPrivateKey.Dock = DockStyle.Fill;
        _txtPrivateKey.Font = new Font(FontFamily.GenericMonospace, _txtPrivateKey.Font.Size);
        _txtPrivateKey.Height = 72;
        _txtPrivateKey.Margin = new Padding(0, 4, 0, 4);
        _txtPrivateKey.Multiline = true;
        _txtPrivateKey.PlaceholderText = "Optional; paste or import an SSH private key (PEM / OpenSSH)";
        _txtPrivateKey.ScrollBars = ScrollBars.Vertical;

        // _btnImportKey
        _btnImportKey.Anchor = AnchorStyles.Left;
        _btnImportKey.AutoSize = true;
        _btnImportKey.Margin = new Padding(8, 4, 0, 4);
        _btnImportKey.Text = "Import…";
        _btnImportKey.UseVisualStyleBackColor = true;
        _btnImportKey.Click += (_, _) => ImportFileInto(_txtPrivateKey, "SSH Private Key");

        // _lblCertificate
        _lblCertificate.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblCertificate.AutoSize = true;
        _lblCertificate.Margin = new Padding(0, 4, 8, 4);
        _lblCertificate.Text = "Certificate:";

        // _txtCertificate
        _txtCertificate.AcceptsReturn = true;
        _txtCertificate.Dock = DockStyle.Fill;
        _txtCertificate.Font = new Font(FontFamily.GenericMonospace, _txtCertificate.Font.Size);
        _txtCertificate.Height = 72;
        _txtCertificate.Margin = new Padding(0, 4, 0, 4);
        _txtCertificate.Multiline = true;
        _txtCertificate.PlaceholderText = "Optional; paste or import an SSH certificate paired with the key";
        _txtCertificate.ScrollBars = ScrollBars.Vertical;

        // _btnImportCertificate
        _btnImportCertificate.Anchor = AnchorStyles.Left;
        _btnImportCertificate.AutoSize = true;
        _btnImportCertificate.Margin = new Padding(8, 4, 0, 4);
        _btnImportCertificate.Text = "Import…";
        _btnImportCertificate.UseVisualStyleBackColor = true;
        _btnImportCertificate.Click += (_, _) => ImportFileInto(_txtCertificate, "SSH Certificate");

        // _lblDescription
        _lblDescription.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblDescription.AutoSize = true;
        _lblDescription.Margin = new Padding(0, 4, 8, 4);
        _lblDescription.Text = "Description:";

        // _txtDescription
        _txtDescription.Dock = DockStyle.Fill;
        _txtDescription.Margin = new Padding(0, 4, 0, 4);

        // _flpButtons
        _flpButtons.AutoSize = true;
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0, 8, 0, 0);

        // _btnOk
        _btnOk.AutoSize = true;
        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.MinimumSize = new Size(80, 28);
        _btnOk.Text = "OK";

        // _btnCancel
        _btnCancel.AutoSize = true;
        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Margin = new Padding(0, 0, 8, 0);
        _btnCancel.MinimumSize = new Size(80, 28);
        _btnCancel.Text = "Cancel";

        // Form
        AcceptButton = _btnOk;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        CancelButton = _btnCancel;
        ClientSize = new Size(560, 480);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Credential";

        ResumeLayout(false);
    }

    /// <summary>
    /// Reads a key/certificate file chosen by the user into <paramref name="target"/>. Import
    /// replaces the current contents; the user is asked before overwriting non-empty text.
    /// </summary>
    private void ImportFileInto(TextBox target, string title)
    {
        using OpenFileDialog dialog = new()
        {
            Title = $"Import {title}",
            Filter = "Key and certificate files (*.pem;*.key;*.pub;*.crt;*.cer;*.ppk)|*.pem;*.key;*.pub;*.crt;*.cer;*.ppk|All files (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (target.TextLength > 0
            && MessageBox.Show(this, "Replace the current contents with the imported file?",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            target.Text = File.ReadAllText(dialog.FileName).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The file could not be read:\n{ex.Message}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Shows the modal add/edit dialog. When <paramref name="existing"/> is supplied its values
    /// seed the fields. Returns the edited credential on OK, or null when cancelled or validation
    /// fails. Renaming (and propagation of the rename) is handled by the caller.
    /// </summary>
    public static StoredCredential? ShowAddEditDialog(IWin32Window owner, StoredCredential? existing = null)
    {
        using CredentialDialog dlg = new();

        if (existing is not null)
        {
            dlg.Text = "Edit Credential";
            dlg.CredentialName = existing.Name;
            dlg.Username = existing.Username ?? string.Empty;
            dlg.Secret = existing.Secret;
            dlg.PrivateKey = existing.PrivateKey ?? string.Empty;
            dlg.Certificate = existing.Certificate ?? string.Empty;
            dlg.Description = existing.Description ?? string.Empty;
        }
        else
        {
            dlg.Text = "Add Credential";
        }

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return null;

        string name = dlg.CredentialName;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(owner, "Name is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        string secret = dlg.Secret;
        string privateKey = dlg.PrivateKey;
        if (string.IsNullOrWhiteSpace(secret) && string.IsNullOrWhiteSpace(privateKey))
        {
            MessageBox.Show(owner,
                "At least one of Secret (API key / password) or Private key is required.",
                "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        string certificate = dlg.Certificate;
        if (!string.IsNullOrWhiteSpace(certificate) && string.IsNullOrWhiteSpace(privateKey))
        {
            MessageBox.Show(owner,
                "A certificate requires a private key to authenticate with.",
                "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return new StoredCredential
        {
            Name = name,
            Username = string.IsNullOrWhiteSpace(dlg.Username) ? null : dlg.Username,
            Secret = secret,
            PrivateKey = string.IsNullOrWhiteSpace(privateKey) ? null : privateKey,
            Certificate = string.IsNullOrWhiteSpace(certificate) ? null : certificate,
            Description = string.IsNullOrWhiteSpace(dlg.Description) ? null : dlg.Description,
        };
    }
}
