using Kaeo.LlmProxy.Core.Modules;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Slack bot integration. The credential's secret is the bot token (xoxb-...); the target is
/// the channel id. Sends via chat.postMessage and polls conversations.history with the
/// <c>oldest</c> ts cursor. Bot messages (bot_id / subtype bot_message) are filtered out.
/// </summary>
internal sealed class SlackChatProvider(ISecretProvider secrets) : IChatProvider
{
    private readonly ISecretProvider _secrets = secrets;

    // Newest raw ts seen per channel (including filtered bot messages) so the cursor advances
    // even when a poll page contains only bot traffic.
    private readonly ConcurrentDictionary<int, string> _lastRawTs = new();

    public string Type => "slack";

    public bool CanReceive(ChatChannelConfig channel) =>
        !string.IsNullOrWhiteSpace(channel.CredentialName) && !string.IsNullOrWhiteSpace(channel.Target);

    public async Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken)
    {
        string token = RequireBotToken(channel);
        string json = JsonSerializer.Serialize(new { channel = channel.Target.Trim(), text });

        string body = await ChatHttp.PostJsonAsync("https://slack.com/api/chat.postMessage", json,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        EnsureOk(document.RootElement);
        return ChatProviderSupport.GetStringOrNull(document.RootElement, "ts") ?? string.Empty;
    }

    public async Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken)
    {
        string token = RequireBotToken(channel);
        string url = $"https://slack.com/api/conversations.history?channel={Uri.EscapeDataString(channel.Target.Trim())}&limit=50";
        if (!string.IsNullOrWhiteSpace(cursor))
            url += $"&oldest={Uri.EscapeDataString(cursor)}";

        string body = await ChatHttp.GetAsync(url,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        EnsureOk(document.RootElement);

        if (!document.RootElement.TryGetProperty("messages", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ChatIncomingMessage> messages = [];
        string? newestRawTs = null;
        foreach (JsonElement element in array.EnumerateArray())
        {
            string? ts = ChatProviderSupport.GetStringOrNull(element, "ts");
            if (ts is null)
                continue;

            // The API returns newest-first; the first element is the newest raw message.
            newestRawTs ??= ts;

            bool isBot = element.TryGetProperty("bot_id", out _) ||
                ChatProviderSupport.GetStringOrNull(element, "subtype") == "bot_message";
            if (isBot)
                continue;

            string? threadTs = ChatProviderSupport.GetStringOrNull(element, "thread_ts");
            messages.Add(new ChatIncomingMessage(
                ts,
                ChatProviderSupport.GetStringOrNull(element, "user") ?? "unknown",
                AuthorIsBot: false,
                ChatProviderSupport.GetStringOrNull(element, "text") ?? string.Empty,
                ParseSlackTs(ts),
                threadTs != ts ? threadTs : null));
        }

        if (newestRawTs is not null)
            _lastRawTs[channel.Id] = newestRawTs;

        messages.Reverse(); // oldest-first
        return messages;
    }

    public string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor) =>
        _lastRawTs.TryGetValue(channel.Id, out string? rawTs) ? rawTs
        : messages.Count > 0 ? messages[^1].Id
        : currentCursor;

    /// <summary>Slack ts values are epoch seconds with microsecond precision ("1712345678.123456").</summary>
    private static DateTime ParseSlackTs(string ts)
    {
        int dot = ts.IndexOf('.');
        string secondsPart = dot >= 0 ? ts[..dot] : ts;
        if (!long.TryParse(secondsPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            return DateTime.MinValue;

        DateTime timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        if (dot >= 0 && long.TryParse(ts[(dot + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros))
            timestamp = timestamp.AddTicks(micros * 10);

        return timestamp;
    }

    private static void EnsureOk(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("ok", out JsonElement ok) &&
            ok.ValueKind == JsonValueKind.True)
        {
            return;
        }

        string error = ChatProviderSupport.GetStringOrNull(root, "error") ?? "unknown error";
        throw new InvalidOperationException($"Slack API error: {error}");
    }

    private string RequireBotToken(ChatChannelConfig channel)
    {
        CredentialMaterial credential = ChatProviderSupport.RequireCredential(_secrets, channel);
        return string.IsNullOrWhiteSpace(credential.Secret)
            ? throw new InvalidOperationException($"Credential '{channel.CredentialName}' has no secret (Slack bot token).")
            : credential.Secret;
    }
}