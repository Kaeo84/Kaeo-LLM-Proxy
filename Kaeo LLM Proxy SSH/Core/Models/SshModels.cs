namespace Kaeo.LlmProxy.Ssh.Core.Models;

/// <summary>
/// A named SSH connection persisted by the module. The AI client can open it by name; the
/// authentication material is resolved from the host's central credential store through
/// <see cref="CredentialName"/>.
/// </summary>
internal sealed class SshStoredConnection
{
    /// <summary>Database identity.</summary>
    public int Id { get; set; }

    /// <summary>Unique friendly name the model uses to refer to this connection.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Host name or IP address to connect to.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SSH port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>Username to log in with (a credential may override it).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional name of a credential in the host's central credential store.</summary>
    public string? CredentialName { get; set; }

    /// <summary>Idle timeout override in seconds; 0 uses the module-wide default.</summary>
    public int IdleTimeoutSeconds { get; set; }
}

/// <summary>Feature settings for the SSH module, persisted in the <c>mcp_ssh_settings</c> table.</summary>
internal sealed class SshSettings
{
    /// <summary>Whether the ssh_connect tool is offered.</summary>
    public bool ConnectToolEnabled { get; set; } = true;

    /// <summary>Whether the ssh_exec tool is offered.</summary>
    public bool ExecToolEnabled { get; set; } = true;

    /// <summary>Whether the ssh_disconnect tool is offered.</summary>
    public bool DisconnectToolEnabled { get; set; } = true;

    /// <summary>Whether the ssh_list tool is offered.</summary>
    public bool ListToolEnabled { get; set; } = true;

    /// <summary>
    /// Default number of seconds an SSH connection may stay idle before it is closed
    /// automatically. 0 disables automatic closing.
    /// </summary>
    public int DefaultIdleTimeoutSeconds { get; set; } = 600;

    /// <summary>Maximum seconds a single command may run before it is abandoned.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum characters of command output returned to the model.</summary>
    public int MaxOutputChars { get; set; } = 20_000;
}

/// <summary>
/// Everything needed to open one SSH connection: target endpoint, authentication material,
/// and the idle timeout to apply. Built by the tools layer from stored connections, the host
/// credential store, or inline ad-hoc parameters.
/// </summary>
internal sealed class SshConnectionRequest
{
    /// <summary>Connection key: the stored connection name or <c>user@host:port</c> for ad-hoc ones.</summary>
    public required string Key { get; init; }

    /// <summary>Host name or IP address to connect to.</summary>
    public required string Host { get; init; }

    /// <summary>SSH port.</summary>
    public int Port { get; init; } = 22;

    /// <summary>Login username.</summary>
    public required string Username { get; init; }

    /// <summary>Optional password (also used as the key passphrase when a private key is set).</summary>
    public string? Password { get; init; }

    /// <summary>Optional SSH private key (PEM or OpenSSH format).</summary>
    public string? PrivateKey { get; init; }

    /// <summary>Optional SSH certificate paired with the private key.</summary>
    public string? Certificate { get; init; }

    /// <summary>Idle timeout in seconds for this connection; 0 uses the module-wide default.</summary>
    public int IdleTimeoutSeconds { get; init; }
}

/// <summary>The outcome of one executed SSH command.</summary>
internal sealed class SshCommandResult
{
    /// <summary>Process exit code reported by the remote host.</summary>
    public int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Captured standard error.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Whether the output had to be truncated to the configured limit.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Immutable snapshot of one open SSH connection for display in the module's configuration UI
/// and reporting through the ssh_list tool.
/// </summary>
internal sealed class OpenSshConnectionInfo
{
    /// <summary>Connection key: the stored connection name or <c>user@host:port</c> for ad-hoc ones.</summary>
    public required string Key { get; init; }

    /// <summary>Remote host the session is connected to.</summary>
    public required string Host { get; init; }

    /// <summary>Remote SSH port.</summary>
    public required int Port { get; init; }

    /// <summary>Login username.</summary>
    public required string Username { get; init; }

    /// <summary>Identifier of the MCP session that opened the connection, when known.</summary>
    public string? McpSessionId { get; init; }

    /// <summary>IP address of the MCP client that opened the connection, when known.</summary>
    public string? OpenedByClientAddress { get; init; }

    /// <summary>When the connection was opened (UTC).</summary>
    public required DateTime OpenedUtc { get; init; }

    /// <summary>When the connection was last used (UTC).</summary>
    public required DateTime LastActivityUtc { get; init; }

    /// <summary>Effective idle timeout in seconds; 0 means the connection never idles out.</summary>
    public required int IdleTimeoutSeconds { get; init; }

    /// <summary>Whether the underlying transport is still connected.</summary>
    public required bool IsConnected { get; init; }
}
