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
