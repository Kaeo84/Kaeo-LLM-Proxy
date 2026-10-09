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
