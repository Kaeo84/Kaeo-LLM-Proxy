using System.Data.Common;
using System.Runtime.InteropServices;
using System.Threading;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Infrastructure.Modules;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Web;
using Microsoft.Data.Sqlite;

namespace Kaeo.LlmProxy;

internal sealed class ProxyUsersControl : UserControl
{
    private readonly AppDatabase _database;
    private readonly ProxyAccessService _access;
    private readonly AppSettings _settings;
    private readonly ModuleHost _moduleHost;
    private readonly Func<bool, bool> _saveRequireApiKey;
    private readonly CheckBox _requireApiKey = new() { AutoSize = true, Text = "Require API key for proxy requests" };
    private readonly ListView _users = new() { Dock = DockStyle.Fill, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false, View = View.Details };
    private readonly ListView _keys = new() { Dock = DockStyle.Fill, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false, View = View.Details };
    private bool _loadingSecuritySetting;

    public ProxyUsersControl(AppDatabase database, ProxyAccessService access, AppSettings settings, ModuleHost moduleHost, Func<bool, bool> saveRequireApiKey)
    {
        _database = database;
        _access = access;
        _settings = settings;
        _moduleHost = moduleHost;
        _saveRequireApiKey = saveRequireApiKey;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        _users.Columns.Add("Name", 155);
        _users.Columns.Add("Email", 190);
        _users.Columns.Add("Status", 75);
        _users.Columns.Add("Dashboard", 210);
        _users.Columns.Add("Model access", 190);
        _keys.Columns.Add("Label", 150);
        _keys.Columns.Add("Prefix", 105);
        _keys.Columns.Add("Last used", 140);

        Button addUser = MakeButton("Add user", (_, _) => AddUser());
        Button editUser = MakeButton("Edit", (_, _) => EditSelectedUser());
        Button toggleUser = MakeButton("Enable / disable", (_, _) => ToggleSelectedUser());
        Button deleteUser = MakeButton("Delete user", (_, _) => DeleteSelectedUser());
        Button refresh = MakeButton("Refresh", (_, _) => RefreshUsers());
        Button createKey = MakeButton("Create key", (_, _) => CreateKey());
        Button editKey = MakeButton("Edit key label", (_, _) => EditSelectedKey());
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
            Text = "Create users and keys, and grant each user access to selected models, the MCP server, and specific MCP modules. No selected models means no model access, and no selected modules means every module. Admin users always have full access. Model and MCP access belong to the user, so every key they hold shares it.",
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
                    ? $"{DashboardRoles.Describe(user.DashboardRole)} ({user.DashboardUsername})"
                    : "None");
                item.SubItems.Add(DescribeModelAccess(user));
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
                ListViewItem item = new(key.Label.Length == 0 ? "(unlabeled)" : key.Label) { Tag = key };
                item.SubItems.Add(key.KeyPrefix);
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

    // Admin implies every model, so say that instead of listing them all.
    private static string DescribeModelAccess(ProxyUser user)
    {
        if (DashboardRoles.HasRole(user.DashboardRole, DashboardRoles.Admin))
            return "All models (admin)";
        string models = user.AllowedModels.Count == 0 ? "No models" : string.Join(", ", user.AllowedModels);
        if (!user.McpAccess)
            return models;
        return models + (user.AllowedMcpModules.Count == 0
            ? " + MCP (all modules)"
            : $" + MCP ({user.AllowedMcpModules.Count} module{(user.AllowedMcpModules.Count == 1 ? string.Empty : "s")})");
    }

    /// <summary>Enabled model mappings for the grant pickers, or null when the database read failed.</summary>
    private IReadOnlyList<ModelMapping>? LoadEnabledMappings()
    {
        try
        {
            return _database.LoadModelMappings().Where(m => m.IsEnabled).ToList();
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            ShowDatabaseError(ex);
            return null;
        }
    }

    /// <summary>
    /// The MCP module keys a per-user grant can name. A registry read failure yields an empty list,
    /// which the editor treats as "leave the grant alone" rather than "clear it".
    /// </summary>
    private IReadOnlyList<McpModuleChoice> LoadMcpModules()
    {
        try
        {
            List<McpModuleChoice> modules = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (ModuleRegistryEntry entry in _moduleHost.GetRegistryEntries())
            {
                string? key = ModuleHost.ModuleKeyFor(entry);
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    continue;
                modules.Add(new McpModuleChoice(key, entry.Name ?? key, entry.IsEnabled));
            }

            return [.. modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or DbException)
        {
            // Sharing violation with another instance: degrade to "no module list" instead of
            // blocking user administration.
            return [];
        }
    }

    private void AddUser()
    {
        Func<string, bool>? usernameTaken = TryBuildUsernameCheck(null);
        if (usernameTaken is null)
            return;
        IReadOnlyList<ModelMapping>? mappings = LoadEnabledMappings();
        if (mappings is null)
            return;

        using UserEditorDialog dialog = new("Add user", new ProxyUser(), usernameTaken, mappings, LoadMcpModules());
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
        IReadOnlyList<ModelMapping>? mappings = LoadEnabledMappings();
        if (mappings is null)
            return;

        using UserEditorDialog dialog = new("Edit user", selected, usernameTaken, mappings, LoadMcpModules());
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

        using KeyEditorDialog dialog = new("Create API key", string.Empty);
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

        using KeyEditorDialog dialog = new("Edit API key label", key.Label);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _database.UpdateProxyApiKeyAccess(key.Id, dialog.Label, key.AllowedModels, key.McpAccess);
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

/// <summary>One selectable MCP module for a user's per-module grant.</summary>
internal sealed record McpModuleChoice(string Key, string Name, bool IsEnabled)
{
    public override string ToString() => IsEnabled ? Name : Name + " (disabled)";
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
    private readonly CheckedListBox _roles = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckedListBox _models = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckedListBox _mcpModules = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox _mcpAccess = new() { AutoSize = true, Text = "Allow MCP server access" };

    public ProxyUser User => new()
    {
        Name = _name.Text.Trim(),
        Email = _email.Text.Trim(),
        IsEnabled = _enabled.Checked,
        DashboardLoginEnabled = _loginEnabled.Checked,
        DashboardUsername = _loginUsername.Text.Trim(),
        DashboardRole = string.Join(',', SelectedRoles()),
        AllowedModels = _models.CheckedItems.Cast<ModelChoice>().Select(m => m.Name).ToList(),
        McpAccess = _mcpAccess.Checked,
        // An unreadable module registry leaves the list empty; keep whatever the user already had
        // instead of silently widening their access to every module.
        AllowedMcpModules = _mcpModules.Items.Count == 0
            ? _original.AllowedMcpModules
            : [.. _mcpModules.CheckedItems.Cast<McpModuleChoice>().Select(m => m.Key)],
        DashboardPasswordHash = _loginPassword.Text.Length > 0
            ? DashboardPasswords.Hash(_original, _loginPassword.Text)
            : _original.DashboardPasswordHash,
    };

    public UserEditorDialog(
        string title,
        ProxyUser user,
        Func<string, bool> usernameTaken,
        IReadOnlyList<ModelMapping> mappings,
        IReadOnlyList<McpModuleChoice> mcpModules)
    {
        _original = user;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 720);

        _name.Text = user.Name;
        _email.Text = user.Email;
        _enabled.Checked = user.IsEnabled;
        _loginEnabled.Checked = user.DashboardLoginEnabled;
        _loginUsername.Text = user.DashboardUsername;
        _mcpAccess.Checked = user.McpAccess;

        // A user may hold several roles at once and the effective permissions are the union, so the
        // editor is a checklist rather than the single-select combo this used to be.
        IReadOnlyList<string> held = DashboardRoles.Parse(user.DashboardRole);
        foreach (DashboardRoleDefinition role in DashboardRoles.All)
        {
            int index = _roles.Items.Add(new RoleChoice(role.Label, role.Description, role.Key));
            _roles.SetItemChecked(index, held.Contains(role.Key, StringComparer.OrdinalIgnoreCase));
        }

        HashSet<string> allowed = new(user.AllowedModels, StringComparer.OrdinalIgnoreCase);
        foreach (ModelMapping mapping in mappings.OrderBy(m => m.ProxyName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(mapping.ProxyName))
                continue;
            int index = _models.Items.Add(new ModelChoice(mapping.ProxyName));
            if (allowed.Contains(mapping.ProxyName))
                _models.SetItemChecked(index, true);
        }

        HashSet<string> grantedModules = new(user.AllowedMcpModules, StringComparer.OrdinalIgnoreCase);
        foreach (McpModuleChoice module in mcpModules)
        {
            int index = _mcpModules.Items.Add(module);
            if (grantedModules.Contains(module.Key))
                _mcpModules.SetItemChecked(index, true);
        }

        _loginEnabled.CheckedChanged += (_, _) => UpdateLoginFieldState();
        UpdateLoginFieldState();

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 12 };
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
        layout.Controls.Add(new Label { AutoSize = true, Text = "Roles", Anchor = AnchorStyles.Left }, 0, 6);
        layout.Controls.Add(_roles, 1, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Model access", Anchor = AnchorStyles.Left }, 0, 7);
        layout.Controls.Add(_models, 1, 7);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.Controls.Add(new Label { AutoSize = true, Text = "MCP modules", Anchor = AnchorStyles.Left }, 0, 8);
        layout.Controls.Add(_mcpModules, 1, 8);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.Controls.Add(_mcpAccess, 0, 9);
        layout.SetColumnSpan(_mcpAccess, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label hint = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "Passwords need 12+ characters. Leave blank to keep the current password.\n"
                + "Check every role that applies; permissions are combined and Admin already includes everything.\n"
                + "Model and MCP access belong to the user, so every API key they hold shares it. Leave every\n"
                + "model unchecked to grant no model access. Leave every MCP module unchecked to allow all of them.",
        };
        layout.Controls.Add(hint, 0, 10);
        layout.SetColumnSpan(hint, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
                if (SelectedRoles().Count == 0)
                {
                    MessageBox.Show(this, "Pick at least one dashboard role.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        layout.Controls.Add(buttons, 0, 11);
        layout.SetColumnSpan(buttons, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private List<string> SelectedRoles() =>
        _roles.CheckedItems.Cast<RoleChoice>().Select(r => r.Value).ToList();

    private void UpdateLoginFieldState()
    {
        _loginUsername.Enabled = _loginEnabled.Checked;
        _loginPassword.Enabled = _loginEnabled.Checked;
        _roles.Enabled = _loginEnabled.Checked;
    }

    private sealed record RoleChoice(string Label, string Description, string Value)
    {
        public override string ToString() => $"{Label} — {Description}";
    }

    private sealed record ModelChoice(string Name)
    {
        public override string ToString() => Name;
    }
}

/// <summary>Label-only editor: a key carries no grants of its own, the owning user does.</summary>
internal sealed class KeyEditorDialog : Form
{
    private readonly TextBox _label = new() { Dock = DockStyle.Fill };

    public string Label => _label.Text.Trim();

    public KeyEditorDialog(string title, string label)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 190);
        _label.Text = label;

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Label", Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_label, 1, 0);
        Label hint = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "What this key is for, e.g. \"laptop\" or \"CI pipeline\".\n"
                + "Model and MCP access belong to the user, not the key; change it with \"Edit user\".",
        };
        layout.Controls.Add(hint, 0, 1);
        layout.SetColumnSpan(hint, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button save = new() { AutoSize = true, Text = "Save", DialogResult = DialogResult.OK };
        Button cancel = new() { AutoSize = true, Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
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
