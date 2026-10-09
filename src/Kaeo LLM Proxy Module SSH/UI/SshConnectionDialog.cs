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
/// Modal dialog for adding or editing a stored SSH connection (name, host, port, username,
/// credential from the host's central store, and an optional idle timeout override).
/// </summary>
internal sealed class SshConnectionDialog : Form
{
    private const string NoCredentialItem = "(none)";

    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblName = new();
    private readonly TextBox _txtName = new();
    private readonly Label _lblHost = new();
    private readonly TextBox _txtHost = new();
    private readonly Label _lblPort = new();
    private readonly NumericUpDown _nudPort = new();
    private readonly Label _lblUsername = new();
    private readonly TextBox _txtUsername = new();
    private readonly Label _lblCredential = new();
    private readonly ComboBox _cmbCredential = new();
    private readonly Label _lblIdleTimeout = new();
    private readonly NumericUpDown _nudIdleTimeout = new();
    private readonly Label _lblIdleHint = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string ConnectionName
    {
        get => _txtName.Text.Trim();
        set => _txtName.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Host
    {
        get => _txtHost.Text.Trim();
        set => _txtHost.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Port
    {
        get => (int)_nudPort.Value;
        set => _nudPort.Value = Math.Clamp(value, (int)_nudPort.Minimum, (int)_nudPort.Maximum);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Username
    {
        get => _txtUsername.Text.Trim();
        set => _txtUsername.Text = value;
    }

    /// <summary>Selected credential name, or null for "(none)".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? CredentialName
    {
        get => _cmbCredential.SelectedItem is string item && item != NoCredentialItem ? item : null;
        set => _cmbCredential.SelectedItem = value ?? NoCredentialItem;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int IdleTimeoutSeconds
    {
        get => (int)_nudIdleTimeout.Value;
        set => _nudIdleTimeout.Value = Math.Clamp(value, (int)_nudIdleTimeout.Minimum, (int)_nudIdleTimeout.Maximum);
    }

    /// <summary>
    /// Builds the dialog offering the credential names in <paramref name="credentialNames"/>
    /// (from the host's central credential store) for authentication.
    /// </summary>
    public SshConnectionDialog(IReadOnlyList<string> credentialNames)
    {
        ArgumentNullException.ThrowIfNull(credentialNames);

        SuspendLayout();

        // _tlpMain
        _tlpMain.ColumnCount = 2;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.Controls.Add(_lblName, 0, 0);
        _tlpMain.Controls.Add(_txtName, 1, 0);
        _tlpMain.Controls.Add(_lblHost, 0, 1);
        _tlpMain.Controls.Add(_txtHost, 1, 1);
        _tlpMain.Controls.Add(_lblPort, 0, 2);
        _tlpMain.Controls.Add(_nudPort, 1, 2);
        _tlpMain.Controls.Add(_lblUsername, 0, 3);
        _tlpMain.Controls.Add(_txtUsername, 1, 3);
        _tlpMain.Controls.Add(_lblCredential, 0, 4);
        _tlpMain.Controls.Add(_cmbCredential, 1, 4);
        _tlpMain.Controls.Add(_lblIdleTimeout, 0, 5);
        _tlpMain.Controls.Add(_nudIdleTimeout, 1, 5);
        _tlpMain.Controls.Add(_lblIdleHint, 1, 6);
        _tlpMain.SetColumnSpan(_flpButtons, 2);
        _tlpMain.Controls.Add(_flpButtons, 0, 7);
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);
        _tlpMain.RowCount = 8;
        for (int i = 0; i < 8; i++)
            _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Labels
        _lblName.Anchor = AnchorStyles.Left;
        _lblName.AutoSize = true;
        _lblName.Margin = new Padding(0, 4, 8, 4);
        _lblName.Text = "Name:";

        _lblHost.Anchor = AnchorStyles.Left;
        _lblHost.AutoSize = true;
        _lblHost.Margin = new Padding(0, 4, 8, 4);
        _lblHost.Text = "Host:";

        _lblPort.Anchor = AnchorStyles.Left;
        _lblPort.AutoSize = true;
        _lblPort.Margin = new Padding(0, 4, 8, 4);
        _lblPort.Text = "Port:";

        _lblUsername.Anchor = AnchorStyles.Left;
        _lblUsername.AutoSize = true;
        _lblUsername.Margin = new Padding(0, 4, 8, 4);
        _lblUsername.Text = "Username:";

        _lblCredential.Anchor = AnchorStyles.Left;
        _lblCredential.AutoSize = true;
        _lblCredential.Margin = new Padding(0, 4, 8, 4);
        _lblCredential.Text = "Credential:";

        _lblIdleTimeout.Anchor = AnchorStyles.Left;
        _lblIdleTimeout.AutoSize = true;
        _lblIdleTimeout.Margin = new Padding(0, 4, 8, 4);
        _lblIdleTimeout.Text = "Idle timeout (s):";

        // Inputs
        _txtName.Dock = DockStyle.Fill;
        _txtName.Margin = new Padding(0, 4, 0, 4);
        _txtName.PlaceholderText = "e.g. build-server";

        _txtHost.Dock = DockStyle.Fill;
        _txtHost.Margin = new Padding(0, 4, 0, 4);
        _txtHost.PlaceholderText = "Host name or IP address";

        _nudPort.Dock = DockStyle.Fill;
        _nudPort.Margin = new Padding(0, 4, 0, 4);
        _nudPort.Maximum = 65535;
        _nudPort.Minimum = 1;
        _nudPort.Value = 22;

        _txtUsername.Dock = DockStyle.Fill;
        _txtUsername.Margin = new Padding(0, 4, 0, 4);
        _txtUsername.PlaceholderText = "SSH login user";

        _cmbCredential.Dock = DockStyle.Fill;
        _cmbCredential.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbCredential.Margin = new Padding(0, 4, 0, 4);
        _cmbCredential.Items.Add(NoCredentialItem);
        foreach (string credentialName in credentialNames)
            _cmbCredential.Items.Add(credentialName);
        _cmbCredential.SelectedIndex = 0;

        _nudIdleTimeout.Dock = DockStyle.Fill;
        _nudIdleTimeout.Margin = new Padding(0, 4, 0, 4);
        _nudIdleTimeout.Maximum = 86_400;
        _nudIdleTimeout.Minimum = 0;
        _nudIdleTimeout.Value = 0;

        _lblIdleHint.AutoSize = true;
        _lblIdleHint.ForeColor = SystemColors.GrayText;
        _lblIdleHint.Margin = new Padding(0, 0, 0, 4);
        _lblIdleHint.Text = "0 = use the module-wide default idle timeout.";

        // Buttons
        _flpButtons.AutoSize = true;
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0, 8, 0, 0);

        _btnOk.AutoSize = true;
        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.MinimumSize = new Size(80, 28);
        _btnOk.Text = "OK";

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
        ClientSize = new Size(460, 320);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Stored SSH Connection";

        ResumeLayout(false);
    }

    /// <summary>
    /// Shows the modal add/edit dialog. Returns the edited connection on OK, or null when
    /// cancelled or validation fails. Duplicate-name handling is done by the caller.
    /// </summary>
    public static SshStoredConnection? ShowAddEditDialog(
        IWin32Window owner, IReadOnlyList<string> credentialNames, SshStoredConnection? existing = null)
    {
        using SshConnectionDialog dlg = new(credentialNames);

        if (existing is not null)
        {
            dlg.Text = "Edit Stored Connection";
            dlg.ConnectionName = existing.Name;
            dlg.Host = existing.Host;
            dlg.Port = existing.Port;
            dlg.Username = existing.Username;
            dlg.CredentialName = existing.CredentialName;
            dlg.IdleTimeoutSeconds = existing.IdleTimeoutSeconds;
        }
        else
        {
            dlg.Text = "Add Stored Connection";
        }

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return null;

        if (string.IsNullOrWhiteSpace(dlg.ConnectionName))
        {
            MessageBox.Show(owner, "Name is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(dlg.Host))
        {
            MessageBox.Show(owner, "Host is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(dlg.Username))
        {
            MessageBox.Show(owner, "Username is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return new SshStoredConnection
        {
            Id = existing?.Id ?? 0,
            Name = dlg.ConnectionName,
            Host = dlg.Host,
            Port = dlg.Port,
            Username = dlg.Username,
            CredentialName = dlg.CredentialName,
            IdleTimeoutSeconds = dlg.IdleTimeoutSeconds,
        };
    }
}
