using Kaeo.LlmProxy.Core.Modules;
using Kaeo.LlmProxy.Module.ChatInterface;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies Discord webhook sends: the wait=true execute URL builder, send-only receive
/// gating, and honest configuration guards that fire before any network call.
/// </summary>
public class DiscordWebhookTests
{
    private const string WebhookUrl = "https://discord.com/api/webhooks/1234567890/secret-token";

    private sealed class FakeSecrets : ISecretProvider
    {
        public IReadOnlyList<string> ListCredentialNames() => [];
        public string? ResolveSecret(string credentialName) => null;
        public CredentialMaterial? ResolveCredential(string credentialName) => null;
    }

    private static DiscordChatProvider CreateProvider() => new(new FakeSecrets());

    private static ChatChannelConfig Channel(string? webhookUrl = null, string target = "", string? credentialName = null) =>
        new()
        {
            Id = 1,
            Name = "Test Channel",
            ProviderType = "discord",
            Target = target,
            WebhookUrl = webhookUrl,
            CredentialName = credentialName,
        };

    [Theory]
    [InlineData(WebhookUrl, WebhookUrl + "?wait=true")]
    [InlineData(WebhookUrl + "?thread_id=42", WebhookUrl + "?thread_id=42&wait=true")]
    [InlineData(WebhookUrl + "?", WebhookUrl + "?wait=true")]
    [InlineData(WebhookUrl + "?thread_id=42&", WebhookUrl + "?thread_id=42&wait=true")]
    [InlineData("  " + WebhookUrl + "  ", WebhookUrl + "?wait=true")]
    public void BuildWebhookExecuteUrl_AppendsWaitTruePreservingQuery(string input, string expected)
    {
        Assert.Equal(expected, DiscordChatProvider.BuildWebhookExecuteUrl(input));
    }

    [Fact]
    public void CanReceive_WebhookOnlyChannel_IsSendOnly()
    {
        Assert.False(CreateProvider().CanReceive(Channel(webhookUrl: WebhookUrl)));
    }

    [Fact]
    public void CanReceive_BotCredentialAndTarget_CanReceive()
    {
        Assert.True(CreateProvider().CanReceive(Channel(target: "999", credentialName: "bot")));
    }

    [Fact]
    public void CanReceive_WebhookPlusBotConfig_CanReceive()
    {
        Assert.True(CreateProvider().CanReceive(Channel(WebhookUrl, "999", "bot")));
    }

    [Fact]
    public async Task SendAsync_NoWebhookAndNoTarget_ThrowsHonestConfigError()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider().SendAsync(Channel(), "hello", CancellationToken.None));
        Assert.Contains("webhook URL or target channel id", error.Message);
    }

    [Fact]
    public async Task SendAsync_BotTargetWithoutCredential_ThrowsCredentialError()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider().SendAsync(Channel(target: "999"), "hello", CancellationToken.None));
        Assert.Contains("no credential configured", error.Message);
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/1/token")]
    [InlineData("/api/webhooks/1/token")]
    public async Task SendAsync_NonHttpsWebhook_ThrowsBeforeAnyNetworkCall(string webhookUrl)
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider().SendAsync(Channel(webhookUrl), "hello", CancellationToken.None));
        Assert.Contains("https", error.Message);
    }
}

/// <summary>
/// Verifies URL redaction keeps Discord webhook tokens (which live in the path, not the
/// query) out of logs and error messages while preserving diagnosable URL structure.
/// </summary>
public class ChatHttpRedactionTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/1234567890/secret-token",
        "https://discord.com/api/webhooks/1234567890/***")]
    [InlineData("https://discord.com/api/webhooks/1234567890/secret-token?wait=true",
        "https://discord.com/api/webhooks/1234567890/***")]
    [InlineData("https://discordapp.com/api/webhooks/1234567890/secret-token",
        "https://discordapp.com/api/webhooks/1234567890/***")]
    [InlineData("https://discord.com/API/WEBHOOKS/1234567890/secret-token",
        "https://discord.com/API/WEBHOOKS/1234567890/***")]
    public void RedactUrl_DiscordWebhook_MasksPathToken(string url, string expected)
    {
        string redacted = ChatHttp.RedactUrl(url);
        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("secret-token", redacted);
    }

    [Fact]
    public void RedactUrl_OrdinaryUrl_StripsQueryButKeepsPath()
    {
        Assert.Equal("https://discord.com/api/v10/channels/999/messages",
            ChatHttp.RedactUrl("https://discord.com/api/v10/channels/999/messages?after=42"));
    }

    [Fact]
    public void RedactUrl_MalformedWebhookUrl_StillMasksToken()
    {
        string redacted = ChatHttp.RedactUrl("/api/webhooks/1234567890/secret-token?wait=true");
        Assert.Equal("/api/webhooks/1234567890/***", redacted);
    }

    [Fact]
    public void RedactUrl_WebhookPathWithoutTokenSegment_IsUnchanged()
    {
        Assert.Equal("https://discord.com/api/webhooks/1234567890",
            ChatHttp.RedactUrl("https://discord.com/api/webhooks/1234567890"));
    }

    [Fact]
    public void RedactUrl_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ChatHttp.RedactUrl(string.Empty));
    }
}