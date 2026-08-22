namespace Kaeo.LlmProxy.Core.Modules;

/// <summary>
/// Implemented by modules that contribute tools to the host's built-in MCP server. The host
/// reflects over the returned target objects for methods annotated with the ModelContextProtocol
/// <c>[McpServerTool]</c> attribute and registers them with each MCP session. This contracts
/// assembly deliberately does not reference the MCP SDK: targets are plain objects and the
/// attribute types unify with the host's SDK copy at runtime.
/// </summary>
public interface IMcpToolModule
{
    /// <summary>
    /// Creates the tool target instances this module contributes. Called on every MCP session
    /// initialization, so implementations should be cheap and the targets should read their own
    /// enabled/disabled state live to support on-the-fly toggling.
    /// </summary>
    /// <param name="session">
    /// Information about the MCP session the targets are created for, including the calling
    /// client's address. Targets may capture it to attribute activity to the session.
    /// </param>
    IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session);
}
