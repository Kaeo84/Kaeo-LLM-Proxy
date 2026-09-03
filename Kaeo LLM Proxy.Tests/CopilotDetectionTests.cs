using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies Copilot request detection and proactive auto-compaction skip logic.
/// </summary>
public class CopilotDetectionTests
{
    // ── IsCopilotRequest: User-Agent detection ────────────────────────────

    [Fact]
    public void DetectsCopilotByUserAgentContainingCopilot()
    {
        Assert.True(OllamaProxyHandler.IsCopilotRequest("GitHub Copilot CLI/1.0"));
    }

    [Fact]
    public void DetectsCopilotByUserAgentContainingGithub()
    {
        Assert.True(OllamaProxyHandler.IsCopilotRequest("github-copilot-sdk/2.0"));
    }

    [Fact]
    public void DoesNotDetectNonCopilotUserAgent()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0"));
    }

    [Fact]
    public void DoesNotDetectEmptyUserAgent()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest(string.Empty));
    }

    [Fact]
    public void DoesNotDetectNullUserAgent()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest((string?)null));
    }

    // ── IsCopilotRequest: Compact signature detection ─────────────────────

    [Fact]
    public void DetectsCopilotByCompactSignature()
    {
        string compactPrompt = "Your task is to **produce an authoritative, self-contained summary** of the current session.";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", compactPrompt));
    }

    [Fact]
    public void DetectsCopilotByConversationSummaryTag()
    {
        string content = "Some text <ConversationSummary> more text";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", content));
    }

    [Fact]
    public void DetectsCopilotByReasoningScratchpadTag()
    {
        string content = "Some text <ReasoningScratchpad> more text";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", content));
    }

    [Fact]
    public void DoesNotDetectNonCompactContent()
    {
        string content = "Please help me write a C# class.";
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", content));
    }

    [Fact]
    public void DoesNotDetectNullContent()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", (string?)null));
    }

    [Fact]
    public void DoesNotDetectEmptyContent()
    {
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", string.Empty));
    }

    // ── IsCopilotRequest: Combined detection ──────────────────────────────

    [Fact]
    public void DetectsCopilotByBothUserAgentAndContent()
    {
        string compactPrompt = "Your task is to **produce an authoritative, self-contained summary**.";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("GitHub Copilot CLI/1.0", compactPrompt));
    }

    [Fact]
    public void DetectsCopilotByUserAgentEvenWithNonCompactContent()
    {
        string normalContent = "Please help me write a C# class.";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("GitHub Copilot CLI/1.0", normalContent));
    }

    [Fact]
    public void DetectsCopilotByContentEvenWithNonCopilotUserAgent()
    {
        string compactPrompt = "Your task is to **produce an authoritative, self-contained summary**.";
        Assert.True(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", compactPrompt));
    }

    [Fact]
    public void DoesNotDetectNonCopilotRequest()
    {
        string normalContent = "Please help me write a C# class.";
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", normalContent));
    }
}
