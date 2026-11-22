namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>How much Chat Interface activity the module records into the host's MCP request log.</summary>
internal enum ChatMcpLogLevel
{
    /// <summary>Only tool errors, cancellations, and poll failures.</summary>
    Errors = 0,

    /// <summary>Additionally send/ask/reply lifecycle events (default).</summary>
    Connectivity = 1,

    /// <summary>Additionally every tool call with its arguments and full result, including message text.</summary>
    Full = 2,
}