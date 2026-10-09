using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy;

internal sealed class InstructionSetDialog : Form
{
    private readonly TableLayoutPanel _tlpMain = new();
    private readonly Label _lblName = new();
    private readonly TextBox _txtName = new();
    private readonly Label _lblDescription = new();
    private readonly TextBox _txtDescription = new();
    private readonly Label _lblInstructions = new();
    private readonly TextBox _txtInstructions = new();
    private readonly FlowLayoutPanel _flpButtons = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string InstructionName
    {
        get => _txtName.Text.Trim();
        set => _txtName.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Description
    {
        get => _txtDescription.Text.Trim();
        set => _txtDescription.Text = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Instructions
    {
        get => _txtInstructions.Text.Trim();
        set => _txtInstructions.Text = value;
    }

    public InstructionSetDialog()
    {
        SuspendLayout();

        // _tlpMain
        _tlpMain.ColumnCount = 2;
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tlpMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _tlpMain.Controls.Add(_lblName, 0, 0);
        _tlpMain.Controls.Add(_txtName, 1, 0);
        _tlpMain.Controls.Add(_lblDescription, 0, 1);
        _tlpMain.Controls.Add(_txtDescription, 1, 1);
        _tlpMain.Controls.Add(_lblInstructions, 0, 2);
        _tlpMain.SetColumnSpan(_txtInstructions, 2);
        _tlpMain.Controls.Add(_txtInstructions, 0, 3);
        _tlpMain.SetColumnSpan(_flpButtons, 2);
        _tlpMain.Controls.Add(_flpButtons, 0, 4);
        _tlpMain.Dock = DockStyle.Fill;
        _tlpMain.Padding = new Padding(8);
        _tlpMain.RowCount = 5;
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tlpMain.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // _lblName
        _lblName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblName.AutoSize = true;
        _lblName.Margin = new Padding(0, 4, 8, 4);
        _lblName.Text = "Name:";

        // _txtName
        _txtName.Dock = DockStyle.Fill;
        _txtName.Margin = new Padding(0, 4, 0, 4);

        // _lblDescription
        _lblDescription.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblDescription.AutoSize = true;
        _lblDescription.Margin = new Padding(0, 4, 8, 4);
        _lblDescription.Text = "Description:";

        // _txtDescription
        _txtDescription.Dock = DockStyle.Fill;
        _txtDescription.Margin = new Padding(0, 4, 0, 4);

        // _lblInstructions
        _lblInstructions.AutoSize = true;
        _lblInstructions.Margin = new Padding(0, 8, 0, 4);
        _lblInstructions.Text = "Instructions:";

        // _txtInstructions
        _txtInstructions.Dock = DockStyle.Fill;
        _txtInstructions.Margin = new Padding(0, 0, 0, 8);
        _txtInstructions.Multiline = true;
        _txtInstructions.ScrollBars = ScrollBars.Vertical;
        _txtInstructions.AcceptsReturn = true;

        // _flpButtons
        _flpButtons.AutoSize = true;
        _flpButtons.Controls.Add(_btnCancel);
        _flpButtons.Controls.Add(_btnOk);
        _flpButtons.Dock = DockStyle.Fill;
        _flpButtons.FlowDirection = FlowDirection.RightToLeft;
        _flpButtons.Margin = new Padding(0);

        // _btnOk
        _btnOk.AutoSize = true;
        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.Margin = new Padding(0, 0, 0, 0);
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
        ClientSize = new Size(600, 450);
        Controls.Add(_tlpMain);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Instruction Set";

        ResumeLayout(false);
    }

    public static InstructionSet? ShowAddEditDialog(IWin32Window owner, InstructionSet? existingSet = null)
    {
        using InstructionSetDialog dlg = new();

        if (existingSet is not null)
        {
            dlg.Text = "Edit Instruction Set";
            dlg.InstructionName = existingSet.Name;
            dlg.Description = existingSet.Description ?? string.Empty;
            dlg.Instructions = existingSet.Instructions;
        }
        else
        {
            dlg.Text = "Add Instruction Set";
        }

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return null;

        string name = dlg.InstructionName;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(owner, "Name is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        string instructions = dlg.Instructions;
        if (string.IsNullOrWhiteSpace(instructions))
        {
            MessageBox.Show(owner, "Instructions text is required.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return new InstructionSet
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(dlg.Description) ? null : dlg.Description,
            Instructions = instructions
        };
    }
}
