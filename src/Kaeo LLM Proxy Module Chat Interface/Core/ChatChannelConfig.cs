namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// One configured chat channel, persisted in the <c>mcp_chat_channels</c> table. A channel
/// binds a provider (Discord, MS Teams, Slack, Google Chat, Zoom) to a target (channel/space/
/// chat id or webhook) plus the credential and provider-specific configuration it needs.
/// </summary>
internal sealed class ChatChannelConfig
{
    /// <summary>Database identity; 0 for a channel that has not been inserted yet.</summary>
    public int Id { get; set; }

    /// <summary>Unique display name the AI client uses to address the channel.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Provider discriminator: discord | teams | slack | googlechat | zoom.</summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Platform target: channel id (Discord/Slack/Zoom), chat id (Teams) or space path (Google Chat).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Name of a credential in the host's central store supplying the bot token/secret.</summary>
    public string? CredentialName { get; set; }

    /// <summary>Incoming-webhook URL for webhook-based sends (Discord, Teams, Google Chat). Contains a secret token.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Provider-specific non-secret configuration as JSON (e.g. Teams tenantId/clientId/chatId, Zoom accountId).</summary>
    public string? ConfigJson { get; set; }

    /// <summary>Whether the channel is available to the tools and the polling loop.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between receive polls for this channel (clamped 5..300).</summary>
    public int PollIntervalSeconds { get; set; } = 15;
}