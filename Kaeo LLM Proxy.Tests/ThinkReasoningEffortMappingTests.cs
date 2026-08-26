using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies how Ollama's <c>think</c> request field (a boolean or a "low"/"medium"/"high"/"max"
/// level) is translated to an OpenAI-compatible <c>reasoning_effort</c> value and how the
/// per-model <c>ReasoningEffortPriority</c> routes that mapped value on the Ollama
/// translation path.
/// </summary>
public class ThinkReasoningEffortMappingTests
{
    private static ModelMapping CreateMapping(SamplingPriority priority, string? reasoningEffort) =>
        new()
        {
            ProxyName = "test-model",
            ModelName = "upstream-model",
            UpstreamUrl = "http://localhost:8080",
            ReasoningEffortPriority = priority,
            ReasoningEffort = reasoningEffort,
        };

    [Theory]
    [InlineData(true, "high")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("max", "high")]
    public void MapThinkToReasoningEffortMapsKnownValues(object think, string? expected)
    {
        JsonElement element = JsonDocument.Parse(JsonSerializer.Serialize(think)).RootElement.Clone();
        Assert.Equal(expected, OllamaProxyHandler.MapThinkToReasoningEffort(element));
    }

    [Theory]
    [InlineData("LOW", "low")]
    [InlineData("Medium", "medium")]
    [InlineData("HIGH", "high")]
    [InlineData("MAX", "high")]
    public void MapThinkToReasoningEffortIsCaseInsensitive(string think, string expected)
    {
        JsonElement element = JsonDocument.Parse($"\"{think}\"").RootElement.Clone();
        Assert.Equal(expected, OllamaProxyHandler.MapThinkToReasoningEffort(element));
    }

    [Theory]
    [InlineData("  high  ", "high")]
    [InlineData(" Medium ", "medium")]
    public void MapThinkToReasoningEffortTrimsWhitespace(string think, string expected)
    {
        JsonElement element = JsonDocument.Parse($"\"{think}\"").RootElement.Clone();
        Assert.Equal(expected, OllamaProxyHandler.MapThinkToReasoningEffort(element));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData(42)]
    public void MapThinkToReasoningEffortOmitsUnsupportedValues(object? think)
    {
        JsonElement element = JsonDocument.Parse(JsonSerializer.Serialize(think)).RootElement.Clone();
        Assert.Null(OllamaProxyHandler.MapThinkToReasoningEffort(element));
    }

    [Fact]
    public void MapThinkToReasoningEffortHandlesNullAndTypedInputs()
    {
        Assert.Null(OllamaProxyHandler.MapThinkToReasoningEffort(null));
        Assert.Equal("high", OllamaProxyHandler.MapThinkToReasoningEffort(true));
        Assert.Null(OllamaProxyHandler.MapThinkToReasoningEffort(false));
        Assert.Equal("low", OllamaProxyHandler.MapThinkToReasoningEffort("low"));
        Assert.Null(OllamaProxyHandler.MapThinkToReasoningEffort(3.14));
    }

    [Fact]
    public void ClientAppPriorityForwardsMappedClientThink()
    {
        ModelMapping mapping = CreateMapping(SamplingPriority.ClientApp, null);

        Assert.Equal("high", OllamaProxyHandler.ResolveReasoningEffort(mapping, "high"));
        Assert.Null(OllamaProxyHandler.ResolveReasoningEffort(mapping, null));
    }

    [Fact]
    public void ProxyPriorityOverridesClientThink()
    {
        ModelMapping mapping = CreateMapping(SamplingPriority.Proxy, "medium");

        Assert.Equal("medium", OllamaProxyHandler.ResolveReasoningEffort(mapping, "high"));
    }

    [Fact]
    public void ProxyPriorityOmitsWhenConfiguredValueIsBlank()
    {
        ModelMapping mapping = CreateMapping(SamplingPriority.Proxy, "  ");

        Assert.Null(OllamaProxyHandler.ResolveReasoningEffort(mapping, "high"));
    }

    [Fact]
    public void ProxyPriorityLowercasesConfiguredValue()
    {
        ModelMapping mapping = CreateMapping(SamplingPriority.Proxy, "High");

        Assert.Equal("high", OllamaProxyHandler.ResolveReasoningEffort(mapping, "low"));
    }

    [Fact]
    public void ProviderPriorityOmitsMappedClientThink()
    {
        ModelMapping mapping = CreateMapping(SamplingPriority.Provider, null);

        Assert.Null(OllamaProxyHandler.ResolveReasoningEffort(mapping, "high"));
    }

    [Fact]
    public void ResolveReasoningEffortDefaultsToClientEffortForNullMapping()
    {
        Assert.Equal("medium", OllamaProxyHandler.ResolveReasoningEffort(null, "medium"));
        Assert.Null(OllamaProxyHandler.ResolveReasoningEffort(null, null));
    }

    [Fact]
    public void MapThinkToReasoningEffortHandlesPipedThinkValues()
    {
        // "low" | "medium" | "high" | "max" per Ollama's documented think levels.
        foreach (string level in new[] { "low", "medium", "high", "max" })
        {
            JsonElement element = JsonDocument.Parse($"\"{level}\"").RootElement.Clone();
            string? mapped = OllamaProxyHandler.MapThinkToReasoningEffort(element);
            Assert.Contains(mapped, new[] { "low", "medium", "high" });
        }
    }
}
