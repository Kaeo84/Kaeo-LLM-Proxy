using Kaeo.LlmProxy.Core.Modules;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Zoom Team Chat integration via a Server-to-Server OAuth app: ConfigJson carries accountId,
/// the credential's username is the client id and its secret the client secret. Sends and
/// polls /chat/users/me/messages for the configured channel id. Messages this module sent
/// itself are tracked per channel and skipped when polling.
/// </summary>
internal sealed class ZoomChatProvider(ISecretProvider secrets) : IChatProvider
{
    private const int SentIdCap = 500;

    private readonly ISecretProvider _secrets = secrets;
    private readonly ChatTokenCache _tokens = new();

    // Ids of messages sent by this module, per channel, so our own traffic is not echoed back.
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _sentIds = new();

    public string Type => "zoom";

    public bool CanReceive(ChatChannelConfig channel) =>
        ChatProviderSupport.GetConfigValue(channel, "accountId") is not null &&
        !string.IsNullOrWhiteSpace(channel.CredentialName) &&
        !string.IsNullOrWhiteSpace(channel.Target);

    public async Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(channel, cancellationToken).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(new { message = text, to_channel_id = channel.Target.Trim() });

        string body = await ChatHttp.PostJsonAsync("https://api.zoom.us/v2/chat/users/me/messages", json,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        string id = ChatProviderSupport.GetStringOrNull(document.RootElement, "id") ?? string.Empty;
        if (id.Length > 0)
            RememberSentId(channel.Id, id);

        return id;
    }

    public async Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(channel, cancellationToken).ConfigureAwait(false);
        string url = $"https://api.zoom.us/v2/chat/users/me/messages?to_channel_id={Uri.EscapeDataString(channel.Target.Trim())}&page_size=50";

        string body = await ChatHttp.GetAsync(url,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("messages", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        (DateTime cursorTime, string? cursorId) = ParseCursor(cursor);
        _sentIds.TryGetValue(channel.Id, out ConcurrentDictionary<string, byte>? sentIds);

        List<ChatIncomingMessage> messages = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            string? id = ChatProviderSupport.GetStringOrNull(element, "id");
            if (id is null)
                continue;

            DateTime created = ChatProviderSupport.ParseUtc(ChatProviderSupport.GetStringOrNull(element, "date_time"));

            // The page is newest-first; filter client-side against the opaque time|id cursor.
            if (created < cursorTime || (created == cursorTime && id == cursorId))
                continue;

            // Skip our own sends (the API echoes the app's messages back as the bot user).
            if (sentIds is not null && sentIds.ContainsKey(id))
                continue;

            messages.Add(new ChatIncomingMessage(
                id,
                ChatProviderSupport.GetStringOrNull(element, "sender_display_name")
                    ?? ChatProviderSupport.GetStringOrNull(element, "sender")
                    ?? "unknown",
                AuthorIsBot: false,
                ChatProviderSupport.GetStringOrNull(element, "message") ?? string.Empty,
                created,
                ChatProviderSupport.GetStringOrNull(element, "reply_main_message_id")));
        }

        messages.Reverse(); // oldest-first
        return messages;
    }

    public string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor) =>
        messages.Count > 0
            ? $"{messages[^1].TimestampUtc:o}|{messages[^1].Id}"
            : currentCursor;

    private void RememberSentId(int channelId, string id)
    {
        ConcurrentDictionary<string, byte> ids = _sentIds.GetOrAdd(channelId, _ => new ConcurrentDictionary<string, byte>());
        if (ids.Count >= SentIdCap)
            ids.Clear(); // bounded; a rare re-echo after a clear is deduplicated by the bridge buffer
        ids[id] = 0;
    }

    private static (DateTime Time, string? Id) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return (DateTime.MinValue, null);

        int separator = cursor.IndexOf('|');
        if (separator < 0)
            return (ChatProviderSupport.ParseUtc(cursor), null);

        return (ChatProviderSupport.ParseUtc(cursor[..separator]), cursor[(separator + 1)..]);
    }

    private async Task<string> GetAccessTokenAsync(ChatChannelConfig channel, CancellationToken cancellationToken)
    {
        if (_tokens.TryGet(channel.Id, out string cached))
            return cached;

        string accountId = ChatProviderSupport.GetConfigValue(channel, "accountId")
            ?? throw new InvalidOperationException($"Channel '{channel.Name}' has no Zoom accountId configured.");
        CredentialMaterial credential = ChatProviderSupport.RequireCredential(_secrets, channel);
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrWhiteSpace(credential.Secret))
            throw new InvalidOperationException(
                $"Credential '{channel.CredentialName}' must hold the Zoom client id (username) and client secret (secret).");

        string url = $"https://zoom.us/oauth/token?grant_type=account_credentials&account_id={Uri.EscapeDataString(accountId)}";
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}"));

        string body = await ChatHttp.PostFormAsync(url,
            new Dictionary<string, string>
            {
                ["grant_type"] = "account_credentials",
                ["account_id"] = accountId,
            },
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic),
            cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        string token = ChatProviderSupport.GetStringOrNull(document.RootElement, "access_token")
            ?? throw new InvalidOperationException("The Zoom OAuth endpoint returned no access token.");

        _tokens.Set(channel.Id, token, ChatProviderSupport.ReadExpiresIn(document.RootElement));
        return token;
    }
}