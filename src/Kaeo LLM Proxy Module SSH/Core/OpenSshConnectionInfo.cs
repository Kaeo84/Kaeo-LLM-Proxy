using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Renci.SshNet.Common;

namespace Kaeo.LlmProxy.Module.Ssh;

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
