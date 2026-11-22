using Kaeo.LlmProxy.Core.Modules;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Maps a channel's ProviderType string to its <see cref="IChatProvider"/> singleton. The
/// providers are stateless apart from token/sent-id caches, which are keyed per channel id
/// inside each provider, so sharing instances across channels is safe.
/// </summary>
internal sealed class ChatProviderFactory
{
    private readonly Dictionary<string, IChatProvider> _providers;

    public ChatProviderFactory(ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        IChatProvider[] providers =
        [
            new DiscordChatProvider(secrets),
            new SlackChatProvider(secrets),
            new TeamsChatProvider(secrets),
            new GoogleChatChatProvider(secrets),
            new ZoomChatProvider(secrets),
        ];

        _providers = providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves the provider for a channel, or throws a user-presentable error.</summary>
    public IChatProvider GetProvider(ChatChannelConfig channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return _providers.TryGetValue(channel.ProviderType ?? string.Empty, out IChatProvider? provider)
            ? provider
            : throw new InvalidOperationException(
                $"Unknown chat provider '{channel.ProviderType}' on channel '{channel.Name}'.");
    }

    /// <summary>Resolves the provider for a provider-type string, or null when unknown.</summary>
    public IChatProvider? FindProvider(string providerType) =>
        _providers.TryGetValue(providerType ?? string.Empty, out IChatProvider? provider) ? provider : null;
}