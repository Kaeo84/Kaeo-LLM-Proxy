

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class RepoDialog : Form
{
    private readonly TextBox _collectionBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly TextBox _urlBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly TextBox _branchBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3), Text = "main" };
    private readonly TextBox _mirrorPathBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly TextBox _pathPrefixBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly ComboBox _credentialCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3) };
    private readonly TextBox _dirBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };

    public string CollectionName => _collectionBox.Text.Trim();
    public string RemoteUrl => _urlBox.Text.Trim();
    public string Branch => string.IsNullOrWhiteSpace(_branchBox.Text) ? "main" : _branchBox.Text.Trim();
    public string? MirrorPath => string.IsNullOrWhiteSpace(_mirrorPathBox.Text) ? null : _mirrorPathBox.Text.Trim();
    public string? PathPrefix => string.IsNullOrWhiteSpace(_pathPrefixBox.Text) ? null : _pathPrefixBox.Text.Trim();
    public string? CredentialName => string.IsNullOrWhiteSpace(_credentialCombo.Text) ? null : _credentialCombo.Text.Trim();
    /// <summary>Local directory or file-share path to watch. When set, the mirror is a local-directory mirror (git fields are ignored).</summary>
    public string? DirectoryPath => string.IsNullOrWhiteSpace(_dirBox.Text) ? null : _dirBox.Text.Trim();
    public bool IsLocalDirectory => DirectoryPath is not null;

    public RepoDialog(MirrorRegistration? existing)
    {
        Text = existing is null ? "Add Git Repo" : "Edit Git Repo";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 295);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Collection:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        layout.Controls.Add(_collectionBox, 1, 0);
        layout.Controls.Add(new Label { Text = "Remote URL:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 1);
        layout.Controls.Add(_urlBox, 1, 1);
        layout.Controls.Add(new Label { Text = "Branch:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 2);
        layout.Controls.Add(_branchBox, 1, 2);
        layout.Controls.Add(new Label { Text = "Mirror Path:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 3);
        layout.Controls.Add(_mirrorPathBox, 1, 3);
        layout.Controls.Add(new Label { Text = "Path Prefix:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 4);
        layout.Controls.Add(_pathPrefixBox, 1, 4);
        layout.Controls.Add(new Label { Text = "Credential:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 5);
        layout.Controls.Add(_credentialCombo, 1, 5);
        layout.Controls.Add(new Label { Text = "Directory (local):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 6);
        layout.Controls.Add(_dirBox, 1, 6);

        var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var okBtn = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(3) };
        var cancelBtn = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(3) };
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(okBtn);
        layout.Controls.Add(btnPanel, 0, 7);
        layout.SetColumnSpan(btnPanel, 2);

        Controls.Add(layout);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        if (existing is not null)
        {
            _collectionBox.Text = existing.CollectionName;
            _urlBox.Text = existing.RemoteUrl;
            _branchBox.Text = existing.Branch;
            _mirrorPathBox.Text = existing.MirrorPath ?? "";
            _pathPrefixBox.Text = existing.PathPrefix ?? "";
            _credentialCombo.Text = existing.CredentialName ?? "";
            _dirBox.Text = existing.SourcePath ?? "";
        }
    }
}
