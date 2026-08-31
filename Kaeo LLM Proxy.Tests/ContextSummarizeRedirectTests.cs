using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the per-model context-summarize (/compact) redirect: detection of Copilot
/// session-summary requests and routing them to a smaller/faster compact model configured on
/// the mapping, on both the OpenAI passthrough path and the Ollama chat path.
/// </summary>
public class ContextSummarizeRedirectTests
{
    // The distinctive head of the Copilot /compact system prompt.
    private const string CompactPrompt =
        "Your task is to **produce an authoritative, self-contained summary** of the current " +
        "session as a project checkpoint. <ConversationSummary> <ReasoningScratchpad>";

    /// <summary>Settings with a "main" model that redirects /compact to a "compact" model.</summary>
    private static AppSettings CreateSettings(string? compactModelName = "compact")
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelName = compactModelName,
        });
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "compact",
            ModelName = "compact-upstream",
            UpstreamUrl = "http://localhost:8081",
        });
        return settings;
    }

    // ── IsContextSummarizeRequest ──────────────────────────────────────────

    [Theory]
    [InlineData("authoritative, self-contained summary")]
    [InlineData("<ConversationSummary>")]
    [InlineData("ReasoningScratchpad")]
    public void DetectsCompactSignatureInHead(string marker)
    {
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest($"Some preamble. {marker} rest."));
    }

    [Fact]
    public void DetectsRealCompactPrompt()
    {
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest(CompactPrompt));
    }

    [Fact]
    public void DoesNotDetectNormalContent()
    {
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest("Please help me write a C# class."));
    }

    [Fact]
    public void DoesNotDetectNullOrEmpty()
    {
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest(null));
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest(string.Empty));
    }

    [Fact]
    public void DoesNotDetectSignatureBeyondHead()
    {
        // A normal prefix longer than the 512-char head followed by the signature must not match.
        string content = new string('x', 600) + "authoritative, self-contained summary";
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest(content));
    }

    // ── ResolveEffectiveModel ──────────────────────────────────────────────

    [Fact]
    public void RedirectsToCompactModelWhenConfiguredAndDetected()
    {
        AppSettings settings = CreateSettings();
        Assert.Equal("compact", OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactPrompt));
    }

    [Fact]
    public void DoesNotRedirectForNonSummarizeRequest()
    {
        AppSettings settings = CreateSettings();
        Assert.Equal("main", OllamaProxyHandler.ResolveEffectiveModel(settings, "main", "Hello, world."));
    }

    [Fact]
    public void DoesNotRedirectWhenNoCompactModelConfigured()
    {
        AppSettings settings = CreateSettings(compactModelName: null);
        Assert.Equal("main", OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactPrompt));
    }

    [Fact]
    public void DoesNotRedirectWhenCompactModelIsNotAValidProxy()
    {
        AppSettings settings = CreateSettings(compactModelName: "does-not-exist");
        Assert.Equal("main", OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactPrompt));
    }

    [Fact]
    public void DoesNotRedirectWhenOriginalModelHasNoMapping()
    {
        AppSettings settings = CreateSettings();
        Assert.Equal("unknown", OllamaProxyHandler.ResolveEffectiveModel(settings, "unknown", CompactPrompt));
    }

    // ── NormalizeRequestBody (OpenAI passthrough path) ────────────────────

    [Fact]
    public void RewritesModelToCompactUpstreamOnCompactRequest()
    {
        AppSettings settings = CreateSettings();
        RequestLog log = new();

        string json = $$"""{"model":"main","messages":[{"role":"system","content":"{{CompactPrompt}}"},{"role":"user","content":"# context"}]}""";
        string result = OllamaProxyHandler.NormalizeRequestBody(json, settings, log);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        Assert.Equal("compact-upstream", root.GetProperty("model").GetString());
        // log.Model reflects the effective (compact) model so upstream resolution follows it.
        Assert.Equal("compact", log.Model);
    }

    [Fact]
    public void DoesNotRewriteModelOnNonCompactRequest()
    {
        AppSettings settings = CreateSettings();
        RequestLog log = new();

        string json = """{"model":"main","messages":[{"role":"user","content":"Hello"}]}""";
        string result = OllamaProxyHandler.NormalizeRequestBody(json, settings, log);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        Assert.Equal("main-upstream", root.GetProperty("model").GetString());
        Assert.Equal("main", log.Model);
    }
}
