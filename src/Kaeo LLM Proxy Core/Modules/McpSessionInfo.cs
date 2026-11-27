namespace Kaeo.LlmProxy.Core.Modules;

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
public sealed record McpSessionInfo(string SessionId, string? ClientAddress)
{
    /// <summary>
    /// Module keys (the registry module id, or the module name when no id is reported) this session
    /// may use, or null when every module is allowed — the shared-credential and open-server cases
    /// and any user without a narrowing grant. Tool targets contributed by other modules are not
    /// exposed to the session, and each exposed tool re-checks the grant on every call.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedModules { get; init; }
}
