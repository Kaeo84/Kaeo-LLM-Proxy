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
