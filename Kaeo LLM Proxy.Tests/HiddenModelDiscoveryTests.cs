using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies that the per-mapping <see cref="ModelMapping.Hidden"/> flag is discovery-only: it
/// removes a model from the list endpoints that clients auto-discover through, without touching
/// request routing or the model's eligibility to serve as a compaction target.
/// </summary>
public class HiddenModelDiscoveryTests
{
    /// <summary>
    /// A settings object holding a visible "main" model and an enabled-but-hidden "embedder",
    /// with "main" configured to redirect its compaction to "embedder".
    /// </summary>
    private static AppSettings CreateSettings(out ModelMapping main, out ModelMapping embedder)
    {
        AppSettings settings = new();

        embedder = new ModelMapping
        {
            ProxyName = "embedder",
            ModelName = "embedder-upstream",
            UpstreamUrl = "http://localhost:8081",
            Hidden = true,
        };
        embedder.EnsureId();

        main = new ModelMapping
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = embedder.Id,
            RedirectManualCompaction = true,
        };
        main.EnsureId();

        settings.ModelMappings.Add(main);
        settings.ModelMappings.Add(embedder);

        return settings;
    }

    // ── Defaults ───────────────────────────────────────────────────────────

    [Fact]
    public void Hidden_DefaultsToFalse()
    {
        ModelMapping mapping = new() { ProxyName = "main" };

        Assert.False(mapping.Hidden);
    }

    // ── Routing still resolves a hidden mapping ────────────────────────────

    [Fact]
    public void FindModelMapping_ResolvesHiddenMappingByName()
    {
        AppSettings settings = CreateSettings(out _, out ModelMapping embedder);

        ModelMapping? resolved = settings.FindModelMapping("embedder");

        Assert.Same(embedder, resolved);
    }

    [Fact]
    public void ResolveModelName_MapsHiddenMappingToItsUpstreamName()
    {
        AppSettings settings = CreateSettings(out _, out _);

        Assert.Equal("embedder-upstream", settings.ResolveModelName("embedder"));
    }

    // ── A hidden mapping remains a valid compaction target ─────────────────

    [Fact]
    public void ResolveManualCompactTarget_AcceptsHiddenTarget()
    {
        // Hiding a model from /v1/models must not disqualify it as the compact model Copilot is
        // redirected to — that is the whole point of the flag.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping embedder);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.True(redirected);
        Assert.Same(embedder, target);
    }

    [Fact]
    public void ResolveManualCompactTarget_StillRejectsDisabledTarget()
    {
        // Disabled (as opposed to hidden) must keep disqualifying a target: disabling means the
        // mapping does not route at all.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping embedder);
        embedder.IsEnabled = false;

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    // ── Compaction chunk budgeting ─────────────────────────────────────────

    [Fact]
    public void GetCompactionContextWindow_UsesExplicitWindowWhenSet()
    {
        ModelMapping mapping = new() { ProxyName = "main", ContextWindowTokens = 32768 };

        Assert.Equal(32768, mapping.GetCompactionContextWindow(8192));
    }

    [Fact]
    public void GetCompactionContextWindow_FallsBackToCapNotAdvertisedDefault()
    {
        // The bug this pins: with no explicit window the compaction path used the advertised
        // 131072 default, so a small compact model was fed ~98k-token chunks and every attempt
        // overflowed. The fallback must be the conservative global cap instead.
        ModelMapping mapping = new() { ProxyName = "embedder" };

        int budget = mapping.GetCompactionContextWindow(8192);

        Assert.Equal(8192, budget);
        Assert.NotEqual(ModelMapping.DefaultContextWindowTokens, budget);
    }

    [Fact]
    public void GetEffectiveContextWindow_StillAdvertisesDefaultWhenUnset()
    {
        // Advertised surfaces (ContextLength on /v1/models, /api/show) must keep reporting the
        // full default; only chunk sizing may use the conservative cap.
        ModelMapping mapping = new() { ProxyName = "embedder" };

        Assert.Equal(ModelMapping.DefaultContextWindowTokens, mapping.GetEffectiveContextWindow());
    }

    [Fact]
    public void GetSummaryPromptBudget_ReservesRoomForTheModelsOwnReply()
    {
        // Prompt + completion must fit the window, so the summary output is subtracted from the
        // budget before chunking rather than being left to overrun it.
        int budget = AutoCompactionService.GetSummaryPromptBudget(8192);

        Assert.True(budget > 0);
        Assert.True(budget + AutoCompactionService.SummaryMaxTokens <= 8192);
    }

    [Fact]
    public void GetSummaryPromptBudget_LeavesRoomForTheCombineStepToo()
    {
        int budget = AutoCompactionService.GetSummaryPromptBudget(
            8192, AutoCompactionService.CombineMaxTokens);

        Assert.True(budget + AutoCompactionService.CombineMaxTokens <= 8192);
    }

    [Fact]
    public void GetSummaryPromptBudget_NeverCollapsesBelowTheFloor()
    {
        // A window smaller than the reserved output would otherwise produce a zero or negative
        // budget, and the sub-chunk splitter would spin without ever fitting a message.
        int budget = AutoCompactionService.GetSummaryPromptBudget(256);

        Assert.Equal(AutoCompactionService.MinSummaryPromptTokens, budget);
    }

    // ── Global fallback cap ────────────────────────────────────────────────

    [Fact]
    public void CompactionFallbackContextTokens_DefaultsConservatively()
    {
        AppSettings settings = new();

        Assert.Equal(8192, settings.CompactionFallbackContextTokens);
        Assert.True(settings.CompactionFallbackContextTokens < ModelMapping.DefaultContextWindowTokens);
    }

    [Theory]
    [InlineData(0, AppSettings.MinCompactionFallbackContextTokens)]
    [InlineData(-5, AppSettings.MinCompactionFallbackContextTokens)]
    [InlineData(99999999, AppSettings.MaxCompactionFallbackContextTokens)]
    [InlineData(16384, 16384)]
    public void Normalize_ClampsCompactionFallbackContextTokens(int input, int expected)
    {
        AppSettings settings = new() { CompactionFallbackContextTokens = input };

        settings.Normalize();

        Assert.Equal(expected, settings.CompactionFallbackContextTokens);
    }

    [Fact]
    public void RuntimeSettingsRoundTrip_CarriesCompactionFallbackContextTokens()
    {
        // The cap lives in the database rather than settings.jsonc, so dropping the field in
        // either direction would silently reset it to the default on the next launch.
        AppSettings settings = new() { CompactionFallbackContextTokens = 4096 };

        RuntimeSettings runtime = settings.CreateRuntimeSettings();
        AppSettings restored = new();
        restored.ApplyRuntimeSettings(runtime);

        Assert.Equal(4096, restored.CompactionFallbackContextTokens);
    }
}
