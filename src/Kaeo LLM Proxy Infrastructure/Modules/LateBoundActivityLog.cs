using Kaeo.LlmProxy.Core.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Forwarding <see cref="IMcpActivityLog"/> handed to modules at initialization, before the
/// host's MCP log store exists. The real target is bound later by the host; entries arriving
/// before binding are dropped (module tool activity cannot occur before the MCP server starts).
/// </summary>
internal sealed class LateBoundActivityLog : IMcpActivityLog
{
    private volatile IMcpActivityLog? _target;

    /// <summary>Binds the real sink once the host's MCP log store exists.</summary>
    public void SetTarget(IMcpActivityLog target) => _target = target ?? throw new ArgumentNullException(nameof(target));

    public void Write(McpActivityEntry entry)
    {
        IMcpActivityLog? target = _target;
        if (target is not null)
        {
            target.Write(entry);
            return;
        }

        // Dropping is still correct here (tool activity cannot occur before the server binds
        // the target), but a drop must not be invisible if the host wiring ever changes.
        Log.Debug("LateBoundActivityLog dropped an MCP activity entry before binding: {Source} {Operation}",
            entry.Source, entry.Operation);
    }
}
