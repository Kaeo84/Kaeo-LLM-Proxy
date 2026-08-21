namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Persisted settings for the built-in MCP server endpoint (mcp_server_settings table).
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
    /// When true, the MCP server serves the Scalar API explorer at /scalar and its OpenAPI
    /// specification at /openapi/v1/openapi.json. Default: false.
    /// </summary>
    public bool EnableApiExplorer { get; set; }

    /// <summary>
    /// Name of a credential store entry whose secret is required as a bearer token.
    /// Null/empty disables authentication.
    /// </summary>
    public string? AuthCredentialName { get; set; }

    /// <summary>
    /// When true, the raw JSON-RPC request body is captured in the MCP log entry.
    /// </summary>
    public bool CollectRequestDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// When true, the SSE/JSON-RPC response body is captured in the MCP log entry.
    /// </summary>
    public bool CollectResponseDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif
}
