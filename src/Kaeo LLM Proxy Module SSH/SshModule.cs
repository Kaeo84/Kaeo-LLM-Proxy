using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// The SSH Command module entry point discovered by the host via <see cref="IKaeoModule"/>.
/// Contributes the ssh_connect/ssh_exec/ssh_disconnect/ssh_list tools to the host's built-in
/// MCP server, maintains the SSH connections they use (closing them after their idle timeout),
/// and persists stored connections plus feature settings in the shared application database.
/// </summary>
public sealed class SshModule : IKaeoModule, IMcpToolModule, IRunnableModule, IHelpModule
{
    public const string Version = "1.0.0";

    private ModuleContext? _context;
    private SshRepository? _repository;
    private SshConnectionManager? _manager;
    private SshActivityLogger? _activity;

    public string Id => "kaeo.ssh";

    public string Name => "SSH Command";

    string IKaeoModule.Version => Version;

    public string Description =>
        "SSH tools (ssh_connect/ssh_exec/ssh_disconnect/ssh_list) for the built-in MCP server " +
        "with maintained connections, stored connection profiles, and idle timeouts.";

    internal SshRepository Repository =>
        _repository ?? throw new InvalidOperationException("Module not initialized.");

    internal SshConnectionManager Manager =>
        _manager ?? throw new InvalidOperationException("Module not initialized.");

    internal SshActivityLogger Activity =>
        _activity ?? throw new InvalidOperationException("Module not initialized.");

    internal ISecretProvider Secrets =>
        _context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");

    public void Initialize(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        ApplySchema(context.Database);

        _repository = new SshRepository(context.Database);
        _activity = new SshActivityLogger(context.ActivityLog, () => _repository.LoadSettings().McpLogLevel);
        _manager = new SshConnectionManager(_repository, _activity);
    }

    public System.Windows.Forms.TabPage CreateConfigPage() => new SshConfigPage(this);

    /// <summary>Tool targets for the host's MCP server; the session info carries the client address.</summary>
    public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) =>
        [new SshTools(Manager, Repository, Secrets, session, Activity)];

    // ── IRunnableModule ─────────────────────────────────────────────────────
    // "Running" means the idle sweep is active; open connections are tracked either way.
    // The host stops the module on disable/remove/shutdown, which closes every connection.

    public bool IsRunning => _manager?.IsRunning == true;

    public event EventHandler<string>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null)
            throw new InvalidOperationException("Module not initialized.");

        if (_manager.IsRunning)
            return Task.CompletedTask;

        _manager.Start();
        StatusChanged?.Invoke(this, "Running");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_manager is null || !_manager.IsRunning)
            return;

        await _manager.StopAsync();
        StatusChanged?.Invoke(this, "Stopped");
    }

    // ── IHelpModule ─────────────────────────────────────────────────────────

    /// <summary>Help page injected into the host Help tab.</summary>
    public System.Windows.Forms.TabPage CreateHelpPage()
    {
        System.Windows.Forms.TabPage page = new() { Text = "SSH Command", Padding = new System.Windows.Forms.Padding(8) };
        System.Windows.Forms.TextBox body = new()
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill,
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            BackColor = System.Drawing.SystemColors.Window,
            Text = HelpText,
        };
        page.Controls.Add(body);
        return page;
    }

    internal const string HelpText =
        """
        SSH COMMAND MODULE

        This module adds SSH tools to the built-in MCP server so AI clients can connect to
        computers over SSH, run commands, and read the results.

        TOOLS
        - ssh_connect: opens a persistent connection and returns its connection key.
        - ssh_exec: runs a command on an open connection (opening it first when a stored
          connection name or host details with authentication are supplied). Returns the exit
          code, standard output, and standard error.
        - ssh_disconnect: closes one connection by key, or all of them.
        - ssh_list: lists open connections, including the address of the client that opened
          each one.

        MAINTAINED CONNECTIONS AND IDLE TIMEOUT
        Connections stay open across tool calls so a model can do a series of tasks over one
        session. Each connection closes automatically once it has been idle longer than its
        timeout: a per-connection override (stored connections) or otherwise the module-wide
        default idle timeout. Set the default to 0 to never close idle connections.

        STORED CONNECTIONS
        Named connections (host, port, username, credential, optional idle override) are stored
        in the application database and can be opened by name. Ad-hoc connections remain fully
        supported: the model supplies the host (or IP), username, and authentication directly.

        AUTHENTICATION
        Credentials come from the host's central credential store and may contain a password,
        an SSH private key (passkeys are stored encrypted in the database), an optional SSH
        certificate paired with the key, and a username. When both a private key and a secret
        are present, the secret is used as the key passphrase. Inline password/key parameters
        work but are discouraged because parameter values may appear in logs.

        MCP LOGGING
        SSH activity can be recorded into the host's MCP request log (Logs tab, MCP sub-tab).
        "Connectivity & errors" (the default) records connection opens, reuses, and closes
        plus any tool error or timeout. "Full (verbose)" additionally records every tool call
        with its arguments and complete result, including full command output. Set the level
        under Tools & Limits; changes apply to subsequent tool calls without a restart.

        SECURITY NOTES
        - Remote host keys are accepted as presented; verify you trust the target host.
        - Command output is treated as untrusted data and framed as such for the model.
        - Every open connection records which MCP session and client address opened it; see
          the Open Connections table on this module's settings page.
        """;

    /// <summary>
    /// Baseline schema for the module's tables, applied during initialization. Idempotent:
    /// safe to run on every startup.
    /// </summary>
    private const string SchemaScript = """
-- Kaeo LLM Proxy Module SSH module baseline schema.
-- Idempotent: safe to run on every startup.

-- Named SSH connections the AI client can open by name.
-- credential_name references the host's central credential store, which supplies the
-- username/password or private key/certificate used to authenticate.
-- idle_timeout_seconds: per-connection override; 0 = use the module-wide default.
CREATE TABLE IF NOT EXISTS mcp_ssh_connections (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    host TEXT NOT NULL,
    port INTEGER NOT NULL DEFAULT 22,
    username TEXT NOT NULL,
    credential_name TEXT NULL,
    idle_timeout_seconds INTEGER NOT NULL DEFAULT 0
);

-- Key/value settings for the SSH feature (tool toggles, idle timeout, command timeout,
-- output size cap).
CREATE TABLE IF NOT EXISTS mcp_ssh_settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
""";

    private static void ApplySchema(IModuleDatabase database) => database.ExecuteSchemaScript(SchemaScript);
}
