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
