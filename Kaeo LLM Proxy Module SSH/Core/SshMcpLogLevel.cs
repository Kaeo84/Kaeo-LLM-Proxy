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

/// <summary>How much SSH activity the module records into the host's MCP request log.</summary>
internal enum SshMcpLogLevel
{
    /// <summary>Connection lifecycle events (open/reuse/close) and any tool errors.</summary>
    Connectivity,

    /// <summary>Additionally every tool call with its arguments and full result, including command output.</summary>
    Full,
}
