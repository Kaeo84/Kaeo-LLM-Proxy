

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class ModelInfoDialog : Form
{
    public ModelInfoDialog(string modelId, string content)
    {
        Text = $"Model Info â€” {modelId}";
        Size = new Size(700, 550);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(400, 300);
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
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = content,
            Font = new Font("Consolas", 9.5f),
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
