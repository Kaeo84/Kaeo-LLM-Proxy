namespace Kaeo.LlmProxy.Mcp.Core.Models;

/// <summary>
/// Persisted settings for the MCP server endpoint (mcp_server_settings table).
/// </summary>
internal sealed class McpServerSettings
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int DefaultPort = 8388;

    /// <summary>Whether the MCP server should run (applied at app start and via on-the-fly toggle).</summary>
    public bool Enabled { get; set; }

    /// <summary>Address to bind ("localhost", "0.0.0.0", or a specific IP).</summary>
    public string ListenAddress { get; set; } = "localhost";

    /// <summary>Port to listen on.</summary>
    public int ListenPort { get; set; } = DefaultPort;

    /// <summary>
    /// Name of a host credential store entry whose secret is required as a bearer token.
    /// Null/empty disables authentication.
    /// </summary>
    public string? AuthCredentialName { get; set; }
}
