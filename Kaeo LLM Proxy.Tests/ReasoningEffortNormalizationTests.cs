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
    private static AppSettings CreateSettings(SamplingPriority priority, string? reasoningEffort)
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
}
