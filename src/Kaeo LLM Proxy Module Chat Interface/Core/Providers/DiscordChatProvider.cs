using Kaeo.LlmProxy.Core.Modules;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Discord bot integration. The credential's secret is the bot token; the target is the
/// channel id. Sends via POST /channels/{id}/messages and polls with the snowflake
/// <c>after</c> parameter. Bot-authored messages are filtered out.
/// </summary>
internal sealed class DiscordChatProvider(ISecretProvider secrets) : IChatProvider
{
    private const string ApiBase = "https://discord.com/api/v10";

    private readonly ISecretProvider _secrets = secrets;

    // Newest raw id seen per channel (including filtered bot messages) so the cursor advances
    // even when a poll page contains only bot traffic.
    private readonly ConcurrentDictionary<int, string> _lastRawId = new();

    public string Type => "discord";

    public bool CanReceive(ChatChannelConfig channel) =>
        !string.IsNullOrWhiteSpace(channel.CredentialName) && !string.IsNullOrWhiteSpace(channel.Target);

    public async Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken)
    {
        string token = RequireBotToken(channel);
        string url = $"{ApiBase}/channels/{Uri.EscapeDataString(channel.Target.Trim())}/messages";
        string json = JsonSerializer.Serialize(new { content = text });

        string body = await ChatHttp.PostJsonAsync(url, json,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token),
            cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        return ChatProviderSupport.GetStringOrNull(document.RootElement, "id") ?? string.Empty;
    }

    public async Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken)
    {
        string token = RequireBotToken(channel);
        string url = $"{ApiBase}/channels/{Uri.EscapeDataString(channel.Target.Trim())}/messages?limit=50";
        if (!string.IsNullOrWhiteSpace(cursor))
            url += $"&after={Uri.EscapeDataString(cursor)}";

        string body = await ChatHttp.GetAsync(url,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token),
            cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        List<ChatIncomingMessage> messages = [];
        string? newestRawId = null;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            string? id = ChatProviderSupport.GetStringOrNull(element, "id");
            if (id is null)
                continue;

            // The API returns newest-first; the first element is the newest raw message.
            newestRawId ??= id;

            bool isBot = element.TryGetProperty("author", out JsonElement author) &&
                author.TryGetProperty("bot", out JsonElement botFlag) &&
                botFlag.ValueKind == JsonValueKind.True;
            if (isBot)
                continue;

            string authorName = author.ValueKind == JsonValueKind.Object
                ? ChatProviderSupport.GetStringOrNull(author, "username") ?? "unknown"
                : "unknown";

            string? replyTo = element.TryGetProperty("message_reference", out JsonElement reference) &&
                reference.ValueKind == JsonValueKind.Object
                    ? ChatProviderSupport.GetStringOrNull(reference, "message_id")
                    : null;

            messages.Add(new ChatIncomingMessage(
                id,
                authorName,
                AuthorIsBot: false,
                ChatProviderSupport.GetStringOrNull(element, "content") ?? string.Empty,
                ChatProviderSupport.ParseUtc(ChatProviderSupport.GetStringOrNull(element, "timestamp")),
                replyTo));
        }

        if (newestRawId is not null)
            _lastRawId[channel.Id] = newestRawId;

        messages.Reverse(); // oldest-first
        return messages;
    }

    public string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor) =>
        _lastRawId.TryGetValue(channel.Id, out string? rawId) ? rawId
        : messages.Count > 0 ? messages[^1].Id
        : currentCursor;

    private string RequireBotToken(ChatChannelConfig channel)
    {
        CredentialMaterial credential = ChatProviderSupport.RequireCredential(_secrets, channel);
        return string.IsNullOrWhiteSpace(credential.Secret)
            ? throw new InvalidOperationException($"Credential '{channel.CredentialName}' has no secret (Discord bot token).")
            : credential.Secret;
    }
}