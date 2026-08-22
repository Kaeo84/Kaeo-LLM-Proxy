using Kaeo.LlmProxy.Core.Modules;

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

    public void Write(McpActivityEntry entry) => _target?.Write(entry);
}
