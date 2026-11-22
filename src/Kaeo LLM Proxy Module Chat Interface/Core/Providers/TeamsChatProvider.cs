using Kaeo.LlmProxy.Core.Modules;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Microsoft Teams integration. Sending uses an incoming webhook (Workflows) URL. Receiving
/// is optional and uses Microsoft Graph with an app registration: ConfigJson carries
/// tenantId/clientId/chatId and the credential's secret is the client secret. Without that
/// configuration the channel is send-only. Application-level chat-message permissions may
/// require tenant admin approval.
/// </summary>
internal sealed class TeamsChatProvider(ISecretProvider secrets) : IChatProvider
{
    private readonly ISecretProvider _secrets = secrets;
    private readonly ChatTokenCache _tokens = new();

    public string Type => "teams";

    public bool CanReceive(ChatChannelConfig channel) =>
        ChatProviderSupport.GetConfigValue(channel, "tenantId") is not null &&
        ChatProviderSupport.GetConfigValue(channel, "clientId") is not null &&
        ChatProviderSupport.GetConfigValue(channel, "chatId") is not null &&
        !string.IsNullOrWhiteSpace(channel.CredentialName);

    public async Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel.WebhookUrl))
            throw new InvalidOperationException(
                $"Channel '{channel.Name}' has no Teams incoming-webhook URL configured.");

        string json = JsonSerializer.Serialize(new { text });
        string body = await ChatHttp.PostJsonAsync(channel.WebhookUrl.Trim(), json, configure: null, cancellationToken)
            .ConfigureAwait(false);

        // Workflows webhooks usually return an empty 202 body; older connectors return JSON.
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ChatProviderSupport.GetStringOrNull(document.RootElement, "id") ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken)
    {
        string chatId = ChatProviderSupport.GetConfigValue(channel, "chatId")
            ?? throw new InvalidOperationException($"Channel '{channel.Name}' has no Teams chatId configured.");
        string token = await GetAccessTokenAsync(channel, cancellationToken).ConfigureAwait(false);

        string url = $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages?$top=20";
        string body = await ChatHttp.GetAsync(url,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("value", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        (DateTime cursorTime, string? cursorId) = ParseCursor(cursor);

        List<ChatIncomingMessage> messages = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            string? id = ChatProviderSupport.GetStringOrNull(element, "id");
            if (id is null)
                continue;

            DateTime created = ChatProviderSupport.ParseUtc(ChatProviderSupport.GetStringOrNull(element, "createdDateTime"));

            // Graph has no "after id" query for chat messages; filter client-side against the
            // opaque time|id cursor. The page is newest-first.
            if (created <= cursorTime || id == cursorId)
                continue;

            // from.application != null marks app/bot traffic; humans have from.user only.
            string author = "unknown";
            if (element.TryGetProperty("from", out JsonElement from) && from.ValueKind == JsonValueKind.Object)
            {
                if (from.TryGetProperty("application", out JsonElement application) &&
                    application.ValueKind != JsonValueKind.Null)
                {
                    continue;
                }

                if (from.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object)
                    author = ChatProviderSupport.GetStringOrNull(user, "displayName") ?? "unknown";
            }

            string text = element.TryGetProperty("body", out JsonElement messageBody) &&
                messageBody.ValueKind == JsonValueKind.Object
                    ? ChatProviderSupport.GetStringOrNull(messageBody, "content") ?? string.Empty
                    : string.Empty;

            messages.Add(new ChatIncomingMessage(id, author, AuthorIsBot: false, text, created, ReplyToId: null));
        }

        messages.Reverse(); // oldest-first
        return messages;
    }

    public string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor) =>
        messages.Count > 0
            ? $"{messages[^1].TimestampUtc:o}|{messages[^1].Id}"
            : currentCursor;

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

        string tenantId = ChatProviderSupport.GetConfigValue(channel, "tenantId")
            ?? throw new InvalidOperationException($"Channel '{channel.Name}' has no Teams tenantId configured.");
        string clientId = ChatProviderSupport.GetConfigValue(channel, "clientId")
            ?? throw new InvalidOperationException($"Channel '{channel.Name}' has no Teams clientId configured.");
        CredentialMaterial credential = ChatProviderSupport.RequireCredential(_secrets, channel);
        if (string.IsNullOrWhiteSpace(credential.Secret))
            throw new InvalidOperationException($"Credential '{channel.CredentialName}' has no secret (Teams client secret).");

        string url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";
        string body = await ChatHttp.PostFormAsync(url,
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = credential.Secret,
                ["scope"] = "https://graph.microsoft.com/.default",
            },
            configure: null, cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        string token = ChatProviderSupport.GetStringOrNull(document.RootElement, "access_token")
            ?? throw new InvalidOperationException("The Microsoft identity platform returned no access token.");

        _tokens.Set(channel.Id, token, ChatProviderSupport.ReadExpiresIn(document.RootElement));
        return token;
    }
}