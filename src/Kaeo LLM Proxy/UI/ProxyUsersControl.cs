using System.Data.Common;
using System.Runtime.InteropServices;
using System.Threading;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy;

internal sealed class ProxyUsersControl : UserControl
{
    private readonly AppDatabase _database;
    private readonly ProxyAccessService _access;
    private readonly AppSettings _settings;
    private readonly Func<bool, bool> _saveRequireApiKey;
    private readonly CheckBox _requireApiKey = new() { AutoSize = true, Text = "Require API key for proxy requests" };
    private readonly ListView _users = new() { Dock = DockStyle.Fill, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false, View = View.Details };
    private readonly ListView _keys = new() { Dock = DockStyle.Fill, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false, View = View.Details };
    private bool _loadingSecuritySetting;

    public ProxyUsersControl(AppDatabase database, ProxyAccessService access, AppSettings settings, Func<bool, bool> saveRequireApiKey)
    {
        _database = database;
        _access = access;
        _settings = settings;
        _saveRequireApiKey = saveRequireApiKey;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        _users.Columns.Add("Name", 155);
        _users.Columns.Add("Email", 190);
        _users.Columns.Add("Status", 75);
        _users.Columns.Add("Dashboard", 150);
        _keys.Columns.Add("Label", 115);
        _keys.Columns.Add("Prefix", 105);
        _keys.Columns.Add("Models", 210);
        _keys.Columns.Add("MCP", 75);
        _keys.Columns.Add("Last used", 125);

        Button addUser = MakeButton("Add user", (_, _) => AddUser());
        Button editUser = MakeButton("Edit", (_, _) => EditSelectedUser());
        Button toggleUser = MakeButton("Enable / disable", (_, _) => ToggleSelectedUser());
        Button deleteUser = MakeButton("Delete user", (_, _) => DeleteSelectedUser());
        Button refresh = MakeButton("Refresh", (_, _) => RefreshUsers());
        Button createKey = MakeButton("Create key", (_, _) => CreateKey());
        Button editKey = MakeButton("Edit key access", (_, _) => EditSelectedKey());
        Button revealKey = MakeButton("Reveal key", (_, _) => RevealSelectedKey());
        Button regenerateKey = MakeButton("Regenerate key", (_, _) => RegenerateSelectedKey());
        Button deleteKey = MakeButton("Delete key", (_, _) => DeleteSelectedKey());

        FlowLayoutPanel userActions = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
        userActions.Controls.AddRange([addUser, editUser, toggleUser, deleteUser, refresh]);
        FlowLayoutPanel keyActions = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
        keyActions.Controls.AddRange([createKey, editKey, revealKey, regenerateKey, deleteKey]);

        Panel left = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 6, 0) };
        left.Controls.Add(_users);
        left.Controls.Add(userActions);
        Panel right = new() { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0) };
        right.Controls.Add(_keys);
        right.Controls.Add(keyActions);

        SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 480, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        Label hint = new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 6, 0, 8),
            Text = "Create users and keys, and grant Read Only users access to selected models and the MCP server. No selected models means no model access. Admin users always have full access.",
        };
        FlowLayoutPanel security = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        security.Controls.Add(_requireApiKey);
        security.Controls.Add(new Label { AutoSize = true, Padding = new Padding(12, 4, 0, 0), Text = "When off, requests without a key remain unrestricted." });

        Controls.Add(split);
        Controls.Add(hint);
        Controls.Add(security);

        _users.SelectedIndexChanged += (_, _) => RefreshKeys();
        _requireApiKey.CheckedChanged += RequireApiKey_CheckedChanged;
        Load += (_, _) => RefreshUsers();
        LoadSecuritySetting();
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        Button button = new() { AutoSize = true, Text = text, Margin = new Padding(2) };
        button.Click += click;
        return button;
    }

    private void LoadSecuritySetting()
    {
        _loadingSecuritySetting = true;
        _requireApiKey.Checked = _settings.ProxySecurity.RequireApiKey;
        _loadingSecuritySetting = false;
    }

    private void RequireApiKey_CheckedChanged(object? sender, EventArgs e)
    {
        if (_loadingSecuritySetting)
            return;

        bool requested = _requireApiKey.Checked;
        if (!_saveRequireApiKey(requested))
        {
            _loadingSecuritySetting = true;
            _requireApiKey.Checked = _settings.ProxySecurity.RequireApiKey;
            _loadingSecuritySetting = false;
        }
    }

    private void RefreshUsers()
    {
        int? selectedId = SelectedUser()?.Id;
        _loadingSecuritySetting = true;
        _requireApiKey.Checked = _settings.ProxySecurity.RequireApiKey;
        _loadingSecuritySetting = false;
        try
        {
            IReadOnlyList<ProxyUser> records = _database.LoadProxyUsers();
            _users.BeginUpdate();
            _users.Items.Clear();
            foreach (ProxyUser user in records)
            {
                ListViewItem item = new(user.Name) { Tag = user };
                item.SubItems.Add(user.Email);
                item.SubItems.Add(user.IsEnabled ? "Enabled" : "Disabled");
                item.SubItems.Add(user.DashboardLoginEnabled
                    ? $"{(user.DashboardRole == "admin" ? "Admin" : "Read only")} ({user.DashboardUsername})"
                    : "None");
                _users.Items.Add(item);
                if (user.Id == selectedId)
                    item.Selected = true;
            }
            _users.EndUpdate();

            if (_users.SelectedItems.Count == 0 && _users.Items.Count > 0)
                _users.Items[0].Selected = true;
            RefreshKeys();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void RefreshKeys()
    {
        _keys.Items.Clear();
        ProxyUser? user = SelectedUser();
        if (user is null)
            return;

        try
        {
            foreach (ProxyApiKey key in _database.LoadProxyApiKeys().Where(k => k.UserId == user.Id))
            {
                string models = key.AllowedModels.Count == 0 ? "No models" : string.Join(", ", key.AllowedModels);
                ListViewItem item = new(key.Label.Length == 0 ? "(unlabeled)" : key.Label) { Tag = key };
                item.SubItems.Add(key.KeyPrefix);
                item.SubItems.Add(models);
                item.SubItems.Add(key.McpAccess ? "Allowed" : "Denied");
                item.SubItems.Add(key.LastUsedUtc?.ToLocalTime().ToString("g") ?? "Never");
                _keys.Items.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private ProxyUser? SelectedUser() => _users.SelectedItems.Count == 0 ? null : _users.SelectedItems[0].Tag as ProxyUser;
    private ProxyApiKey? SelectedKey() => _keys.SelectedItems.Count == 0 ? null : _keys.SelectedItems[0].Tag as ProxyApiKey;

    private void AddUser()
    {
        Func<string, bool>? usernameTaken = TryBuildUsernameCheck(null);
        if (usernameTaken is null)
            return;

        using UserEditorDialog dialog = new("Add user", new ProxyUser(), usernameTaken);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            ProxyUser user = dialog.User;
            user.CreatedUtc = DateTime.UtcNow;
            _database.InsertProxyUser(user);
            _access.InvalidateCache();
            RefreshUsers();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            MessageBox.Show(this, "That dashboard username is already in use.", "Users", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void EditSelectedUser()
    {
        ProxyUser? selected = SelectedUser();
        if (selected is null)
        {
            ShowSelectUser();
            return;
        }

        Func<string, bool>? usernameTaken = TryBuildUsernameCheck(selected.Id);
        if (usernameTaken is null)
            return;

        using UserEditorDialog dialog = new("Edit user", selected, usernameTaken);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            ProxyUser updated = dialog.User;
            updated.Id = selected.Id;
            updated.CreatedUtc = selected.CreatedUtc;
            _database.UpdateProxyUser(updated);
            _access.InvalidateCache();
            RefreshUsers();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            MessageBox.Show(this, "That dashboard username is already in use.", "Users", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private Func<string, bool>? TryBuildUsernameCheck(int? excludeUserId)
    {
        try
        {
            HashSet<string> taken = new(
                _database.LoadProxyUsers()
                    .Where(u => u.Id != excludeUserId && u.DashboardLoginEnabled && u.DashboardUsername.Length > 0)
                    .Select(u => u.DashboardUsername),
                StringComparer.OrdinalIgnoreCase);
            return username => taken.Contains(username);
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
            return null;
        }
    }

    private void ToggleSelectedUser()
    {
        ProxyUser? user = SelectedUser();
        if (user is null)
        {
            ShowSelectUser();
            return;
        }

        if (user.IsEnabled && user.DashboardLoginEnabled)
        {
            DialogResult confirm = MessageBox.Show(this,
                "This user can sign in to the Web GUI. Disabling the account will also lock them out of dashboard login. Continue?",
                "Disable user", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return;
        }

        try
        {
            user.IsEnabled = !user.IsEnabled;
            _database.UpdateProxyUser(user);
            _access.InvalidateCache();
            RefreshUsers();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void DeleteSelectedUser()
    {
        ProxyUser? user = SelectedUser();
        if (user is null)
        {
            ShowSelectUser();
            return;
        }

        if (MessageBox.Show(this,
                $"Delete {user.Name} and all of their API keys? This cannot be undone.",
                "Delete user", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            _database.DeleteProxyUser(user.Id);
            _access.InvalidateCache();
            RefreshUsers();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void CreateKey()
    {
        ProxyUser? user = SelectedUser();
        if (user is null)
        {
            ShowSelectUser();
            return;
        }

        IReadOnlyList<ModelMapping> mappings;
        try
        {
            mappings = _database.LoadModelMappings().Where(m => m.IsEnabled).ToList();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
            return;
        }

        using KeyEditorDialog dialog = new("Create API key", mappings, [], string.Empty, mcpAccess: false);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            (string plainText, string hash, string prefix, string? secret) = _access.GenerateKeyMaterial();
            ProxyApiKey key = new()
            {
                UserId = user.Id,
                Label = dialog.Label,
                KeyHash = hash,
                KeyPrefix = prefix,
                KeySecret = secret,
                CreatedUtc = DateTime.UtcNow,
                AllowedModels = dialog.AllowedModels,
                McpAccess = dialog.McpAccess,
            };
            _database.InsertProxyApiKey(key);
            _access.InvalidateCache();
            RefreshKeys();
            using OneTimeKeyDialog keyDialog = new(plainText);
            keyDialog.ShowDialog(this);
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void EditSelectedKey()
    {
        ProxyApiKey? key = SelectedKey();
        if (key is null)
        {
            MessageBox.Show(this, "Select an API key first.", "API keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<ModelMapping> mappings;
        try
        {
            mappings = _database.LoadModelMappings().Where(m => m.IsEnabled).ToList();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
            return;
        }

        using KeyEditorDialog dialog = new("Edit API key access", mappings, key.AllowedModels, key.Label, key.McpAccess);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _database.UpdateProxyApiKeyAccess(key.Id, dialog.Label, dialog.AllowedModels, dialog.McpAccess);
            _access.InvalidateCache();
            RefreshKeys();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void RevealSelectedKey()
    {
        ProxyApiKey? key = SelectedKey();
        if (key is null)
        {
            MessageBox.Show(this, "Select an API key first.", "API keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? plainText = _access.RevealKey(key);
        if (plainText is null)
        {
            if (MessageBox.Show(this,
                    "This key cannot be shown: it was created before key recovery was available, or its protection key file changed.\n\nRegenerate it now to get a copyable key?",
                    "Reveal API key", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                RegenerateSelectedKey();
            }
            return;
        }

        using OneTimeKeyDialog dialog = new(plainText,
            title: "API key",
            note: $"Key '{key.Label}' ({key.KeyPrefix}). Anyone holding it can use this user's models, so share it carefully.",
            startMasked: true);
        dialog.ShowDialog(this);
    }

    private void RegenerateSelectedKey()
    {
        ProxyApiKey? key = SelectedKey();
        if (key is null)
        {
            MessageBox.Show(this, "Select an API key first.", "API keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Replace the key '{key.Label}' ({key.KeyPrefix}) with a new one?\n\nThe current key stops working immediately. The label, model grants, and MCP access stay the same.",
                "Regenerate API key", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            (string plainText, string hash, string prefix, string? secret) = _access.GenerateKeyMaterial();
            DateTime createdUtc = DateTime.UtcNow;

            _database.UpdateProxyApiKeyMaterial(key.Id, hash, prefix, secret, createdUtc);
            _access.InvalidateCache();

            key.KeyHash = hash;
            key.KeyPrefix = prefix;
            key.KeySecret = secret;
            key.CreatedUtc = createdUtc;
            key.LastUsedUtc = null;
            RefreshKeys();

            using OneTimeKeyDialog dialog = new(plainText, title: "Copy regenerated API key");
            dialog.ShowDialog(this);
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void DeleteSelectedKey()
    {
        ProxyApiKey? key = SelectedKey();
        if (key is null)
        {
            MessageBox.Show(this, "Select an API key first.", "API keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"Delete API key '{key.Label}' ({key.KeyPrefix})?", "Delete API key",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            _database.DeleteProxyApiKey(key.Id);
            _access.InvalidateCache();
            RefreshKeys();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
        }
    }

    private void ShowSelectUser() =>
        MessageBox.Show(this, "Select a user first.", "Users", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowDatabaseError(Exception ex) =>
        MessageBox.Show(this, $"The user/key change could not be completed.\n\n{ex.Message}",
            "Database error", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal sealed class UserEditorDialog : Form
{
    private readonly ProxyUser _original;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _email = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _enabled = new() { AutoSize = true, Text = "Account enabled" };
    private readonly CheckBox _loginEnabled = new() { AutoSize = true, Text = "Enable dashboard login (Web GUI)" };
    private readonly TextBox _loginUsername = new() { Dock = DockStyle.Fill };
    private readonly TextBox _loginPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ComboBox _loginRole = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    public ProxyUser User => new()
    {
        Name = _name.Text.Trim(),
        Email = _email.Text.Trim(),
        IsEnabled = _enabled.Checked,
        DashboardLoginEnabled = _loginEnabled.Checked,
        DashboardUsername = _loginUsername.Text.Trim(),
        DashboardRole = ((RoleChoice)_loginRole.SelectedItem!).Value,
        DashboardPasswordHash = _loginPassword.Text.Length > 0
            ? DashboardPasswords.Hash(_original, _loginPassword.Text)
            : _original.DashboardPasswordHash,
    };

    public UserEditorDialog(string title, ProxyUser user, Func<string, bool> usernameTaken)
    {
        _original = user;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 360);

        _name.Text = user.Name;
        _email.Text = user.Email;
        _enabled.Checked = user.IsEnabled;
        _loginEnabled.Checked = user.DashboardLoginEnabled;
        _loginUsername.Text = user.DashboardUsername;
        _loginRole.Items.Add(new RoleChoice("Admin (full dashboard)", "admin"));
        _loginRole.Items.Add(new RoleChoice("Read only (logs only)", "readOnly"));
        _loginRole.SelectedIndex = user.DashboardRole == "admin" ? 0 : 1;
        _loginEnabled.CheckedChanged += (_, _) => UpdateLoginFieldState();
        UpdateLoginFieldState();

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Name", Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Email", Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_email, 1, 1);
        layout.Controls.Add(_enabled, 0, 2);
        layout.SetColumnSpan(_enabled, 2);
        layout.Controls.Add(_loginEnabled, 0, 3);
        layout.SetColumnSpan(_loginEnabled, 2);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Username", Anchor = AnchorStyles.Left }, 0, 4);
        layout.Controls.Add(_loginUsername, 1, 4);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Password", Anchor = AnchorStyles.Left }, 0, 5);
        layout.Controls.Add(_loginPassword, 1, 5);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Role", Anchor = AnchorStyles.Left }, 0, 6);
        layout.Controls.Add(_loginRole, 1, 6);
        Label hint = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "Passwords need 12+ characters. Leave blank to keep the current password.\nDashboard logins sign in to the Web GUI; API keys stay model-only.",
        };
        layout.Controls.Add(hint, 0, 7);
        layout.SetColumnSpan(hint, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button save = new() { AutoSize = true, Text = "Save" };
        Button cancel = new() { AutoSize = true, Text = "Cancel", DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, "A user name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_email.Text) && !_email.Text.Contains('@'))
            {
                MessageBox.Show(this, "Enter a valid email address or leave it blank.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_loginEnabled.Checked)
            {
                string username = _loginUsername.Text.Trim();
                if (username.Length is < 3 or > 128)
                {
                    MessageBox.Show(this, "Dashboard usernames must be 3-128 characters.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (usernameTaken(username))
                {
                    MessageBox.Show(this, "That dashboard username is already in use.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_loginPassword.Text.Length == 0 && _original.DashboardPasswordHash.Length == 0)
                {
                    MessageBox.Show(this, "A password is required to enable dashboard login.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            if (_loginPassword.Text.Length > 0 && _loginPassword.Text.Length is < 12 or > 1024)
            {
                MessageBox.Show(this, "Dashboard passwords must be 12-1024 characters.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void UpdateLoginFieldState()
    {
        _loginUsername.Enabled = _loginEnabled.Checked;
        _loginPassword.Enabled = _loginEnabled.Checked;
        _loginRole.Enabled = _loginEnabled.Checked;
    }

    private sealed record RoleChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}

internal sealed class KeyEditorDialog : Form
{
    private readonly TextBox _label = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox _models = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox _mcpAccess = new() { AutoSize = true, Text = "Allow MCP server access" };

    public string Label => _label.Text.Trim();
    public List<string> AllowedModels => _models.CheckedItems.Cast<ModelChoice>().Select(m => m.Name).ToList();
    public bool McpAccess => _mcpAccess.Checked;

    public KeyEditorDialog(string title, IReadOnlyList<ModelMapping> mappings, IReadOnlyList<string> allowedModels, string label, bool mcpAccess)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 470);
        _label.Text = label;
        _mcpAccess.Checked = mcpAccess;

        HashSet<string> allowed = new(allowedModels, StringComparer.OrdinalIgnoreCase);
        foreach (ModelMapping mapping in mappings.OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(mapping.ProxyName))
                continue;
            int index = _models.Items.Add(new ModelChoice(mapping.ProxyName));
            if (allowed.Contains(mapping.ProxyName))
                _models.SetItemChecked(index, true);
        }

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Label", Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_label, 1, 0);
        layout.Controls.Add(_models, 0, 1);
        layout.SetColumnSpan(_models, 2);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Leave every model unchecked to grant no model access.", Anchor = AnchorStyles.Left }, 0, 2);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 2)!, 2);
        layout.Controls.Add(_mcpAccess, 0, 3);
        layout.SetColumnSpan(_mcpAccess, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button save = new() { AutoSize = true, Text = "Save", DialogResult = DialogResult.OK };
        Button cancel = new() { AutoSize = true, Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private sealed record ModelChoice(string Name)
    {
        public override string ToString() => Name;
    }
}

internal sealed class OneTimeKeyDialog : Form
{
    private readonly TextBox _key;

    public OneTimeKeyDialog(
        string key,
        string title = "Copy new API key",
        string note = "Copy this key now. You can reveal or regenerate it later from this tab.",
        bool startMasked = false)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 160);

        _key = new TextBox { Dock = DockStyle.Top, ReadOnly = true, Text = key, UseSystemPasswordChar = startMasked };
        Label warning = new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 8, 0, 8),
            Text = note,
        };
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38 };
        Button close = new() { AutoSize = true, Text = "Close", DialogResult = DialogResult.OK };
        Button copy = new() { AutoSize = true, Text = "Copy" };
        CheckBox hide = new() { AutoSize = true, Text = "Hide", Checked = startMasked, Padding = new Padding(0, 6, 12, 0) };
        hide.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = hide.Checked;
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(key);
                MessageBox.Show(this, "API key copied to clipboard.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException)
            {
                MessageBox.Show(this, $"Could not copy the API key. Select and copy it manually.\n\n{ex.Message}",
                    "Clipboard unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        buttons.Controls.Add(hide);
        Controls.Add(buttons);
        Controls.Add(warning);
        Controls.Add(_key);
        AcceptButton = close;
        CancelButton = close;
    }
}
