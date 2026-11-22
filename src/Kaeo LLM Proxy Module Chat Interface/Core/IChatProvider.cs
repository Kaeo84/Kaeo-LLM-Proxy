namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// One chat platform integration. Implementations are stateless singletons except for token
/// caches, which must be keyed per channel id so channels never share another channel's token.
/// All HTTP goes through <see cref="ChatHttp"/> (shared client, capped reads, per-request
/// timeouts, per-request auth headers).
/// </summary>
internal interface IChatProvider
{
    /// <summary>Provider discriminator matching <see cref="ChatChannelConfig.ProviderType"/>.</summary>
    string Type { get; }

    /// <summary>Whether the channel is configured so replies can be polled (not just sent).</summary>
    bool CanReceive(ChatChannelConfig channel);

    /// <summary>Sends a message and returns the platform message id, or "" when the platform gives none.</summary>
    Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Returns NEW messages newer than <paramref name="cursor"/>, oldest-first. The cursor
    /// is opaque per-channel state produced by <see cref="GetNextCursor"/>; null means
    /// "start from the most recent page".
    /// </summary>
    Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Derives the next opaque cursor after a poll. <paramref name="messages"/> is the
    /// oldest-first batch just returned (may be empty); the implementation returns the cursor
    /// that excludes everything up to and including the newest returned message.
    /// </summary>
    string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor);
}