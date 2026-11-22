namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>Feature settings for the Chat Interface module, persisted in <c>mcp_chat_settings</c>.</summary>
internal sealed class ChatSettings
{
    /// <summary>Whether the chat_send tool is offered.</summary>
    public bool SendToolEnabled { get; set; } = true;

    /// <summary>Whether the chat_ask tool is offered.</summary>
    public bool AskToolEnabled { get; set; } = true;

    /// <summary>Whether the chat_read tool is offered.</summary>
    public bool ReadToolEnabled { get; set; } = true;

    /// <summary>Whether the chat_channels tool is offered.</summary>
    public bool ChannelsToolEnabled { get; set; } = true;

    /// <summary>Default seconds chat_ask waits for a reply (clamped 10..3600).</summary>
    public int DefaultAskTimeoutSeconds { get; set; } = 300;

    /// <summary>Maximum characters of a single incoming message returned to the model.</summary>
    public int MaxMessageChars { get; set; } = 4_000;

    /// <summary>Default seconds between receive polls for newly added channels (clamped 5..300).</summary>
    public int DefaultPollIntervalSeconds { get; set; } = 15;

    /// <summary>How much chat activity is recorded into the host's MCP request log.</summary>
    public ChatMcpLogLevel McpLogLevel { get; set; } = ChatMcpLogLevel.Connectivity;
}