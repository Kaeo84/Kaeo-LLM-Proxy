using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies compaction-model selection: unset keeps the current model silently, an available
/// configured model is used with a notice, and an unavailable one falls back honestly.
/// </summary>
public class CompactionModelSelectorTests
{
    private static readonly string[] Available = ["qwen3:32b", "llama3.1:8b", "gemma3:4b"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unconfigured_KeepsCurrentModelSilently(string? configured)
    {
        var pick = CompactionModelSelector.Select(configured, "qwen3:32b", Available);
        Assert.Equal("qwen3:32b", pick.ModelName);
        Assert.Null(pick.Notice);
        Assert.False(pick.UsedFallback);
    }

    [Fact]
    public void ConfiguredSameAsCurrent_NoNotice()
    {
        var pick = CompactionModelSelector.Select("qwen3:32b", "qwen3:32b", Available);
        Assert.Equal("qwen3:32b", pick.ModelName);
        Assert.Null(pick.Notice);
        Assert.False(pick.UsedFallback);
    }

    [Fact]
    public void ConfiguredAvailable_UsesItWithNotice()
    {
        var pick = CompactionModelSelector.Select("gemma3:4b", "qwen3:32b", Available);
        Assert.Equal("gemma3:4b", pick.ModelName);
        Assert.False(pick.UsedFallback);
        Assert.NotNull(pick.Notice);
        Assert.Contains("gemma3:4b", pick.Notice);
    }

    [Fact]
    public void ConfiguredUnavailable_FallsBackWithHonestNotice()
    {
        var pick = CompactionModelSelector.Select("mistral:7b", "qwen3:32b", Available);
        Assert.Equal("qwen3:32b", pick.ModelName);
        Assert.True(pick.UsedFallback);
        Assert.NotNull(pick.Notice);
        Assert.Contains("mistral:7b", pick.Notice);
        Assert.Contains("qwen3:32b", pick.Notice);
    }

    [Fact]
    public void NoModelsAvailable_FallsBack()
    {
        var pick = CompactionModelSelector.Select("gemma3:4b", "qwen3:32b", []);
        Assert.Equal("qwen3:32b", pick.ModelName);
        Assert.True(pick.UsedFallback);
    }

    [Fact]
    public void ConfiguredName_IsTrimmed()
    {
        var pick = CompactionModelSelector.Select("  gemma3:4b  ", "qwen3:32b", Available);
        Assert.Equal("gemma3:4b", pick.ModelName);
        Assert.False(pick.UsedFallback);
    }

    [Fact]
    public void ModelNames_AreCaseSensitive()
    {
        var pick = CompactionModelSelector.Select("Gemma3:4b", "qwen3:32b", Available);
        Assert.Equal("qwen3:32b", pick.ModelName);
        Assert.True(pick.UsedFallback);
    }
}
