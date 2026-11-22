using Kaeo.LlmProxy.Core.Modules;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>Shared helpers for the chat providers: credential/config access and JSON parsing.</summary>
internal static class ChatProviderSupport
{
    /// <summary>Resolves the channel's credential or throws a user-presentable error.</summary>
    public static CredentialMaterial RequireCredential(ISecretProvider secrets, ChatChannelConfig channel)
    {
        if (string.IsNullOrWhiteSpace(channel.CredentialName))
            throw new InvalidOperationException(
                $"Channel '{channel.Name}' has no credential configured; select one in the Chat Interface settings.");

        return secrets.ResolveCredential(channel.CredentialName)
            ?? throw new InvalidOperationException(
                $"Credential '{channel.CredentialName}' for channel '{channel.Name}' could not be resolved.");
    }

    /// <summary>Reads a string field from the channel's provider-specific ConfigJson, or null.</summary>
    public static string? GetConfigValue(ChatChannelConfig channel, string key)
    {
        if (string.IsNullOrWhiteSpace(channel.ConfigJson))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(channel.ConfigJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(key, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (JsonException)
        {
            // Malformed provider config behaves like missing config.
        }

        return null;
    }

    /// <summary>Reads an OAuth expires_in value (seconds) from a token response, defaulting to 3600.</summary>
    public static int ReadExpiresIn(JsonElement root) =>
        root.TryGetProperty("expires_in", out JsonElement value) && value.TryGetInt32(out int seconds) && seconds > 0
            ? seconds
            : 3600;

    /// <summary>Parses a platform timestamp as UTC; unparseable values become DateTime.MinValue.</summary>
    public static DateTime ParseUtc(string? timestamp) =>
        DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed
            : DateTime.MinValue;

    /// <summary>Base64url encoding (RFC 7515) used for JWT segments.</summary>
    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads an optional string property from a JSON object, or null.</summary>
    public static string? GetStringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Per-channel OAuth token cache. Keyed by channel id so one channel's token is never reused
/// for another; entries expire 60 seconds early to avoid using a token mid-expiry.
/// </summary>
internal sealed class ChatTokenCache
{
    private readonly ConcurrentDictionary<int, (string Token, DateTime ExpiresUtc)> _tokens = new();

    public bool TryGet(int channelId, out string token)
    {
        if (_tokens.TryGetValue(channelId, out (string Token, DateTime ExpiresUtc) entry) &&
            DateTime.UtcNow < entry.ExpiresUtc)
        {
            token = entry.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    public void Set(int channelId, string token, int expiresInSeconds) =>
        _tokens[channelId] = (token, DateTime.UtcNow.AddSeconds(Math.Max(expiresInSeconds - 60, 30)));

    public void Invalidate(int channelId) => _tokens.TryRemove(channelId, out _);
}