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
