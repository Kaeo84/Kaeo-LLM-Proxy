using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies how the per-model <c>reasoning_effort</c> priority is applied by the proxy when
/// normalizing OpenAI-compatible request bodies on the <c>/v1/*</c> passthrough path.
/// </summary>
public class ReasoningEffortNormalizationTests
{
    private static AppSettings CreateSettings(
        SamplingPriority priority,
        string? reasoningEffort,
        ReasoningEffortFormat format = ReasoningEffortFormat.Legacy)
    {
        AppSettings settings = new();
        settings.ModelMappings.Add(new ModelMapping
        {
            ProxyName = "test-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:8080",
            ReasoningEffortPriority = priority,
            ReasoningEffort = reasoningEffort,
            ReasoningEffortValues = reasoningEffort is null ? [] : [reasoningEffort],
            ReasoningEffortFormat = format,
        });
        return settings;
    }

    private static JsonElement Normalize(string json, AppSettings settings)
    {
        RequestLog log = new();
        string result = OllamaProxyHandler.NormalizeRequestBody(json, settings, log);
        return JsonDocument.Parse(result).RootElement.Clone();
    }

    [Fact]
    public void ProxyPriorityInjectsReasoningEffortWhenClientSendsNone()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "high");

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.True(root.TryGetProperty("reasoning_effort", out JsonElement effort));
        Assert.Equal("high", effort.GetString());
    }

    [Fact]
    public void ProxyPriorityOverridesClientReasoningEffort()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "max");

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"low"}""",
            settings);

        Assert.Equal("max", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ProviderPriorityDropsClientReasoningEffort()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Provider, null);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"low"}""",
            settings);

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ClientAppPriorityPassesClientReasoningEffortThrough()
    {
        AppSettings settings = CreateSettings(SamplingPriority.ClientApp, null);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"medium"}""",
            settings);

        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ClientAppPriorityOmitsReasoningEffortWhenClientSendsNone()
    {
        AppSettings settings = CreateSettings(SamplingPriority.ClientApp, null);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ProxyPriorityModernFormatInjectsNestedReasoningObject()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "high", ReasoningEffortFormat.Modern);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void ProxyPriorityMultiSelectInjectsLegacyAndModern()
    {
        AppSettings settings = CreateSettings(
            SamplingPriority.Proxy,
            "medium",
            ReasoningEffortFormat.Legacy | ReasoningEffortFormat.Modern);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void ProxyPriorityQwenCloudFormatInjectsExtraBodyWrapper()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "medium", ReasoningEffortFormat.QwenCloud);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        JsonElement extraBody = root.GetProperty("extra_body");
        Assert.True(extraBody.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("medium", extraBody.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void ProxyPriorityLowercasesConfiguredValue()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "High", ReasoningEffortFormat.Legacy);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ProxyPriorityModernFormatReplacesClientReasoningShapes()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "high", ReasoningEffortFormat.Modern);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"reasoning_effort":"low","reasoning":{"effort":"low"}}""",
            settings);

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void ProxyPriorityQwenCloudFormatOverridesClientExtraBody()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "medium", ReasoningEffortFormat.QwenCloud);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"extra_body":{"enable_thinking":false,"reasoning_effort":"low"}}""",
            settings);

        JsonElement extraBody = root.GetProperty("extra_body");
        Assert.True(extraBody.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("medium", extraBody.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ClientAppPriorityPassesClientReasoningObjectThrough()
    {
        AppSettings settings = CreateSettings(SamplingPriority.ClientApp, null);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"reasoning":{"effort":"high"}}""",
            settings);

        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void ProxyPriorityChatTemplateKwargsFormatInjectsTemplateKwargs()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "medium", ReasoningEffortFormat.ChatTemplateKwargs);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.Equal("medium", root.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ProxyPriorityChatTemplateKwargsFormatReplacesClientTemplateKwargs()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "high", ReasoningEffortFormat.ChatTemplateKwargs);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"chat_template_kwargs":{"reasoning_effort":"low","enable_thinking":true}}""",
            settings);

        JsonElement kwargs = root.GetProperty("chat_template_kwargs");
        Assert.Equal("high", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.False(kwargs.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void ClientAppPriorityPassesClientTemplateKwargsThrough()
    {
        AppSettings settings = CreateSettings(SamplingPriority.ClientApp, null);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"chat_template_kwargs":{"reasoning_effort":"medium"}}""",
            settings);

        Assert.Equal("medium", root.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ProxyPriorityMultiSelectInjectsAllSelectedShapes()
    {
        AppSettings settings = CreateSettings(
            SamplingPriority.Proxy,
            "high",
            ReasoningEffortFormat.QwenCloud | ReasoningEffortFormat.ChatTemplateKwargs);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            settings);

        Assert.Equal("high", root.GetProperty("extra_body").GetProperty("reasoning_effort").GetString());
        Assert.Equal("high", root.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void ProxyPriorityDropsUnselectedClientReasoningShapes()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "high", ReasoningEffortFormat.Legacy);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"extra_body":{"reasoning_effort":"low"},"chat_template_kwargs":{"reasoning_effort":"low"}}""",
            settings);

        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("extra_body", out _));
        Assert.False(root.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void ProxyPriorityQwenCloudFormatPassesClientTopLevelEnableThinkingThrough()
    {
        AppSettings settings = CreateSettings(SamplingPriority.Proxy, "medium", ReasoningEffortFormat.QwenCloud);

        JsonElement root = Normalize(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"enable_thinking":false}""",
            settings);

        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.True(root.GetProperty("extra_body").GetProperty("enable_thinking").GetBoolean());
    }
}
