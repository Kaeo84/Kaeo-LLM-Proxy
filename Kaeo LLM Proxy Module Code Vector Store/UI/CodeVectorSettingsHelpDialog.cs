namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// Modal window explaining what every Code Vector Store setting does. Opened from the
/// config page Help link. Follows the <see cref="ModelInfoDialog"/> pattern.
/// </summary>
internal sealed class CodeVectorSettingsHelpDialog : Form
{
    public CodeVectorSettingsHelpDialog()
    {
        Text = "Code Vector Store — Settings Reference";
        Size = new Size(720, 620);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Text = CodeVectorSettingsHelp.BuildText(),
            BackColor = SystemColors.Window,
        };
        layout.Controls.Add(textBox, 0, 0);

        var closeButton = new Button { Text = "Close", Anchor = AnchorStyles.Right };
        closeButton.Click += (_, _) => Close();
        layout.Controls.Add(closeButton, 0, 1);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.Add(layout);
    }
}
