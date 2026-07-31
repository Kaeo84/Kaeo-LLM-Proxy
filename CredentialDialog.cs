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
    private readonly Label _lblSecret = new();
    private readonly TextBox _txtSecret = new();
    private readonly CheckBox _chkShowSecret = new();
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
    public string Secret
    {
        get => _txtSecret.Text.Trim();
        set => _txtSecret.Text = value;
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
        _tlpMain.Controls.Add(_lblSecret, 0, 1);
        _tlpMain.Controls.Add(_txtSecret, 1, 1);
        _tlpMain.Controls.Add(_chkShowSecret, 2, 1);
        _tlpMain.Controls.Add(_lblDescription, 0, 2);
        _tlpMain.SetColumnSpan(_txtDescription, 2);
        _tlpMain.Controls.Add(_txtDescription, 1, 2);
        _tlpMain.SetColumnSpan(_flpButtons, 3);
        _tlpMain.Controls.Add(_flpButtons, 0, 3);
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);
        _tlpMain.RowCount = 4;
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _lblName
        _lblName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblName.AutoSize = true;
        _lblName.Margin = new Padding(0, 4, 8, 4);
        _lblName.Text = "Name:";

        // _txtName
        _txtName.Dock = DockStyle.Fill;
        _txtName.Margin = new Padding(0, 4, 0, 4);

        // _lblSecret
        _lblSecret.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblSecret.AutoSize = true;
        _lblSecret.Margin = new Padding(0, 4, 8, 4);
        _lblSecret.Text = "Secret:";

        // _txtSecret
        _txtSecret.Dock = DockStyle.Fill;
        _txtSecret.Margin = new Padding(0, 4, 0, 4);
        _txtSecret.UseSystemPasswordChar = true;
        _txtSecret.PlaceholderText = "API key / bearer token";

        // _chkShowSecret
        _chkShowSecret.Anchor = AnchorStyles.Left;
        _chkShowSecret.AutoSize = true;
        _chkShowSecret.Margin = new Padding(8, 4, 0, 4);
        _chkShowSecret.Text = "Show";
        _chkShowSecret.CheckedChanged += (_, _) => _txtSecret.UseSystemPasswordChar = !_chkShowSecret.Checked;

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
        ClientSize = new Size(520, 180);
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
            dlg.Secret = existing.Secret;
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
        if (string.IsNullOrWhiteSpace(secret))
        {
            MessageBox.Show(owner, "Secret is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return new StoredCredential
        {
            Name = name,
            Secret = secret,
            Description = string.IsNullOrWhiteSpace(dlg.Description) ? null : dlg.Description,
        };
    }
}
