using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Covers the logic extracted out of the Model Mapping dialog into Core and Infrastructure.
/// </summary>
/// <remarks>
/// These members moved without behaviour changes, and they had no direct tests where they lived —
/// they were only ever exercised through the WinForms dialog. Now that the proxy and the dialog
/// share one copy of each rule, a regression here would be invisible in the UI but would change
/// what the proxy actually does, so they are pinned directly.
/// </remarks>
public class ModelMappingExtractionTests
{
    // ── ModelMapping.ResolveProactiveOverflowThreshold ──────────────────────

    [Fact]
    public void Threshold_AbsoluteTokensWinOverPercent()
    {
        // The dialog's status label and the proxy's gate must agree that an explicit token count
        // beats a percentage; this is the precedence the move exists to protect.
        Assert.Equal(12000, ModelMapping.ResolveProactiveOverflowThreshold(12000, 85, 65536));
    }

    [Fact]
    public void Threshold_PercentResolvesAgainstContextWindow()
    {
        Assert.Equal(32768, ModelMapping.ResolveProactiveOverflowThreshold(0, 50, 65536));
    }

    [Fact]
    public void Threshold_IsZeroWhenNeitherInputIsSet()
    {
        Assert.Equal(0, ModelMapping.ResolveProactiveOverflowThreshold(0, 0, 131072));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public void Threshold_NonPositiveInputsMeanNoThreshold(int absoluteTokens, int percent)
    {
        Assert.Equal(0, ModelMapping.ResolveProactiveOverflowThreshold(absoluteTokens, percent, 131072));
    }

    [Fact]
    public void Threshold_InstanceMethodDelegatesToTheSharedRule()
    {
        // The instance member must agree with the static rule for the same inputs, or the gate and
        // the dialog can drift apart again the moment one of them is edited.
        ModelMapping mapping = new()
        {
            ProactiveOverflowTokens = 8000,
            ProactiveOverflowPercent = 90,
            ContextWindowTokens = 32768,
        };

        Assert.Equal(
            ModelMapping.ResolveProactiveOverflowThreshold(8000, 90, 32768),
            mapping.GetProactiveOverflowThreshold());
    }

    [Fact]
    public void Threshold_InstanceMethodUsesEffectiveContextWindowWhenUnset()
    {
        // ContextWindowTokens = 0 resolves to the default, so a percentage is applied to that.
        ModelMapping mapping = new() { ProactiveOverflowPercent = 50 };

        Assert.Equal(
            ModelMapping.ResolveProactiveOverflowThreshold(0, 50, ModelMapping.DefaultContextWindowTokens),
            mapping.GetProactiveOverflowThreshold());
    }

    // ── AutoCompactPathsExtensions.ToDisplayName ───────────────────────────

    [Theory]
    [InlineData(AutoCompactPaths.Ollama, "Ollama /api/chat")]
    [InlineData(AutoCompactPaths.OpenAI, "OpenAI /v1/chat/completions")]
    [InlineData(AutoCompactPaths.Both, "both Ollama and OpenAI paths")]
    [InlineData(AutoCompactPaths.ProxyOnly, "all proxy-handled paths")]
    [InlineData(AutoCompactPaths.None, "no paths")]
    internal void DisplayName_CoversEverySelection(AutoCompactPaths paths, string expected)
    {
        Assert.Equal(expected, paths.ToDisplayName());
    }

    // ── UpstreamModelMetadata.TryExtractContextWindow ──────────────────────

    [Fact]
    public void ContextWindow_ReadsRootLevelField()
    {
        Assert.True(UpstreamModelMetadata.TryExtractContextWindow(
            """{ "context_length": 32768 }""", out int? window));
        Assert.Equal(32768, window);
    }

    [Fact]
    public void ContextWindow_ReadsNestedDetailsObject()
    {
        // Ollama /api/tags advertises the window under "details".
        Assert.True(UpstreamModelMetadata.TryExtractContextWindow(
            """{ "details": { "context_length": 8192 } }""", out int? window));
        Assert.Equal(8192, window);
    }

    [Fact]
    public void ContextWindow_ReadsOllamaModelInfoDottedKeys()
    {
        // Ollama /api/show uses "<family>.context_length" keys inside "model_info".
        Assert.True(UpstreamModelMetadata.TryExtractContextWindow(
            """{ "model_info": { "llama.context_length": 131072 } }""", out int? window));
        Assert.Equal(131072, window);
    }

    [Fact]
    public void ContextWindow_ReadsLlamaFamilyObject()
    {
        Assert.True(UpstreamModelMetadata.TryExtractContextWindow(
            """{ "llama": { "context_length": 4096 } }""", out int? window));
        Assert.Equal(4096, window);
    }

    [Fact]
    public void ContextWindow_RootLevelWinsOverNested()
    {
        Assert.True(UpstreamModelMetadata.TryExtractContextWindow(
            """{ "context_length": 1, "details": { "context_length": 2 } }""", out int? window));
        Assert.Equal(1, window);
    }

    [Theory]
    [InlineData("""{ "context_length": 0 }""")]
    [InlineData("""{ "context_length": -5 }""")]
    [InlineData("""{ "context_length": "32768" }""")]
    [InlineData("""{ "unrelated": 1 }""")]
    public void ContextWindow_RejectsNonPositiveAndNonNumeric(string json)
    {
        // A provider reporting 0 or a string must read as "not stated" rather than clamping the
        // proxy's budget to an unusable figure.
        Assert.False(UpstreamModelMetadata.TryExtractContextWindow(json, out int? window));
        Assert.Null(window);
    }

    [Fact]
    public void ContextWindow_ReturnsFalseOnUnparseableJson()
    {
        Assert.False(UpstreamModelMetadata.TryExtractContextWindow("not json", out int? window));
        Assert.Null(window);
    }
}