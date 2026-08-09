namespace Kaeo.LlmProxy.Modules;

/// <summary>
/// A snapshot of the MCP session that tool targets are being created for. The host passes this
/// to <see cref="IMcpToolModule.CreateMcpToolTargets"/> on every session initialization so
/// modules can attribute activity (e.g. which client opened a connection) to the calling session.
/// </summary>
/// <param name="SessionId">The MCP session identifier assigned by the host.</param>
/// <param name="ClientAddress">
/// IP address of the MCP client that opened the session, or null when it could not be
/// determined. IPv4 is preferred; IPv4-mapped IPv6 addresses are reported as IPv4.
/// </param>
public sealed record McpSessionInfo(string SessionId, string? ClientAddress);
