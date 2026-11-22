using Kaeo.LlmProxy.Core.Modules;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Google Chat integration. Sending uses a space's incoming-webhook URL. Receiving is
/// optional: the credential's secret holds the full service-account JSON (client_email,
/// private_key, token_uri); an RS256 JWT is exchanged for an access token with the
/// chat.messages.readonly scope and messages are polled from the Chat REST API. The target
/// is the space path (spaces/AAAA). The Chat API must be enabled and the service account
/// added to the space.
/// </summary>
internal sealed class GoogleChatChatProvider(ISecretProvider secrets) : IChatProvider
{
    private const string ReadScope = "https://www.googleapis.com/auth/chat.messages.readonly";

    private readonly ISecretProvider _secrets = secrets;
    private readonly ChatTokenCache _tokens = new();

    public string Type => "googlechat";

    public bool CanReceive(ChatChannelConfig channel) =>
        !string.IsNullOrWhiteSpace(channel.CredentialName) &&
        channel.Target.Trim().StartsWith("spaces/", StringComparison.OrdinalIgnoreCase);

    public async Task<string> SendAsync(ChatChannelConfig channel, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel.WebhookUrl))
            throw new InvalidOperationException(
                $"Channel '{channel.Name}' has no Google Chat webhook URL configured.");

        string json = JsonSerializer.Serialize(new { text });
        string body = await ChatHttp.PostJsonAsync(channel.WebhookUrl.Trim(), json, configure: null, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ChatProviderSupport.GetStringOrNull(document.RootElement, "name") ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<ChatIncomingMessage>> PollAsync(ChatChannelConfig channel, string? cursor, CancellationToken cancellationToken)
    {
        string spacePath = ValidateSpaceTarget(channel);
        string token = await GetAccessTokenAsync(channel, cancellationToken).ConfigureAwait(false);

        string url = $"https://chat.googleapis.com/v1/{spacePath}/messages" +
            $"?pageSize=25&orderBy={Uri.EscapeDataString("createTime desc")}";
        string body = await ChatHttp.GetAsync(url,
            request => ChatHttp.SetBearer(request, token), cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("messages", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        DateTime cursorTime = ChatProviderSupport.ParseUtc(cursor);

        List<ChatIncomingMessage> messages = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            string? name = ChatProviderSupport.GetStringOrNull(element, "name");
            if (name is null)
                continue;

            DateTime created = ChatProviderSupport.ParseUtc(ChatProviderSupport.GetStringOrNull(element, "createTime"));

            // The page is newest-first; filter client-side against the createTime cursor.
            if (created <= cursorTime)
                continue;

            if (!element.TryGetProperty("sender", out JsonElement sender) ||
                sender.ValueKind != JsonValueKind.Object ||
                ChatProviderSupport.GetStringOrNull(sender, "type") != "HUMAN")
            {
                continue;
            }

            string text = ChatProviderSupport.GetStringOrNull(element, "text")
                ?? ChatProviderSupport.GetStringOrNull(element, "argumentText")
                ?? string.Empty;

            messages.Add(new ChatIncomingMessage(
                name,
                ChatProviderSupport.GetStringOrNull(sender, "displayName") ?? "unknown",
                AuthorIsBot: false,
                text,
                created,
                ReplyToId: null));
        }

        messages.Reverse(); // oldest-first
        return messages;
    }

    public string? GetNextCursor(ChatChannelConfig channel, IReadOnlyList<ChatIncomingMessage> messages, string? currentCursor) =>
        messages.Count > 0
            ? messages[^1].TimestampUtc.ToString("o")
            : currentCursor;

    /// <summary>Validates the spaces/AAAA target and URL-encodes the id segment (no path traversal).</summary>
    private static string ValidateSpaceTarget(ChatChannelConfig channel)
    {
        string target = channel.Target.Trim();
        if (!target.StartsWith("spaces/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Google Chat target '{target}' must look like 'spaces/AAAA'.");

        string spaceId = target["spaces/".Length..];
        if (spaceId.Length == 0 || spaceId.Contains('/') || spaceId.Contains(".."))
            throw new InvalidOperationException(
                $"Google Chat target '{target}' must be a single space id after 'spaces/'.");

        return $"spaces/{Uri.EscapeDataString(spaceId)}";
    }

    private async Task<string> GetAccessTokenAsync(ChatChannelConfig channel, CancellationToken cancellationToken)
    {
        if (_tokens.TryGet(channel.Id, out string cached))
            return cached;

        CredentialMaterial credential = ChatProviderSupport.RequireCredential(_secrets, channel);
        if (string.IsNullOrWhiteSpace(credential.Secret))
            throw new InvalidOperationException(
                $"Credential '{channel.CredentialName}' has no secret (Google service-account JSON).");

        string clientEmail;
        string privateKeyPem;
        string tokenUri;
        try
        {
            using JsonDocument account = JsonDocument.Parse(credential.Secret);
            clientEmail = ChatProviderSupport.GetStringOrNull(account.RootElement, "client_email")
                ?? throw new InvalidOperationException("The service-account JSON has no client_email.");
            privateKeyPem = ChatProviderSupport.GetStringOrNull(account.RootElement, "private_key")
                ?? throw new InvalidOperationException("The service-account JSON has no private_key.");
            tokenUri = ChatProviderSupport.GetStringOrNull(account.RootElement, "token_uri")
                ?? "https://oauth2.googleapis.com/token";
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Credential '{channel.CredentialName}' is not valid service-account JSON: {ex.Message}");
        }

        string assertion = BuildJwtAssertion(clientEmail, privateKeyPem, tokenUri);
        string body = await ChatHttp.PostFormAsync(tokenUri,
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            },
            configure: null, cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(body);
        string token = ChatProviderSupport.GetStringOrNull(document.RootElement, "access_token")
            ?? throw new InvalidOperationException("The Google token endpoint returned no access token.");

        _tokens.Set(channel.Id, token, ChatProviderSupport.ReadExpiresIn(document.RootElement));
        return token;
    }

    /// <summary>Builds the RS256-signed JWT used for the service-account token exchange.</summary>
    private static string BuildJwtAssertion(string clientEmail, string privateKeyPem, string tokenUri)
    {
        long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string header = ChatProviderSupport.Base64UrlEncode(
            Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        string claims = ChatProviderSupport.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = clientEmail,
            scope = ReadScope,
            aud = tokenUri,
            iat = issuedAt,
            exp = issuedAt + 3600,
        })));

        string signingInput = $"{header}.{claims}";
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        byte[] signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{ChatProviderSupport.Base64UrlEncode(signature)}";
    }
}