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

    /// <summary>How much SSH activity is recorded into the host's MCP request log.</summary>
    public SshMcpLogLevel McpLogLevel { get; set; } = SshMcpLogLevel.Connectivity;

    /// <summary>
    /// Command guardrail for ssh_exec. Guarded (default) permits only allow-listed read-only
    /// commands with no shell metacharacters; FullAccess is an explicit opt-in to unrestricted use.
    /// </summary>
    public SshCommandPolicy CommandPolicy { get; set; } = SshCommandPolicy.Guarded;

    /// <summary>Newline/comma-separated read-only commands permitted in Guarded mode.</summary>
    public string AllowedCommands { get; set; } = SshCommandGuard.DefaultAllowedCommands;

    /// <summary>
    /// When true (default), a central-store credential may only be sent to the stored host it is
    /// bound to; ad-hoc host + credentialName is rejected unless a stored connection ties them
    /// together. Prevents phishing secrets to an attacker-controlled host. Opt out for full access.
    /// </summary>
    public bool BindCredentialsToStoredHosts { get; set; } = true;
}
