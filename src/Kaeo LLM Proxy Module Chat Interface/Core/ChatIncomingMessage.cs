namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// One message received from a chat platform. The text is user-supplied content and is always
/// framed as untrusted data before it reaches the model.
/// </summary>
/// <param name="Id">Platform message id (Discord snowflake, Slack ts, Graph/Zoom id, Google Chat name).</param>
/// <param name="Author">Display name of the sender.</param>
/// <param name="AuthorIsBot">True when the sender is a bot/application rather than a human.</param>
/// <param name="Text">Plain message text.</param>
/// <param name="TimestampUtc">When the message was posted (UTC).</param>
/// <param name="ReplyToId">Id of the message this one replies to, when the platform exposes it.</param>
internal sealed record ChatIncomingMessage(
    string Id,
    string Author,
    bool AuthorIsBot,
    string Text,
    DateTime TimestampUtc,
    string? ReplyToId);