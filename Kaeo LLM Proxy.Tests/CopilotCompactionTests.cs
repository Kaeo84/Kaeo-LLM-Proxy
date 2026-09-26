using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the Copilot-compatible compaction path: when a mapping opts in via
/// <see cref="ModelMapping.CopilotCompatibleCompaction"/>, a Copilot-detected summarization is
/// routed to the single global <see cref="AppSettings.CopilotCompactionModelName"/> instead of
/// the mapping's own compaction target, while opted-out mappings keep the per-mapping redirect.
/// </summary>
public class CopilotCompactionTests
{
    // The distinctive head of the Copilot /compact system prompt.
    private const string CompactSignature =
        "Your task is to produce an authoritative, self-contained summary of this session.";

    /// <summary>
    /// Settings with a "main" model and two possible compaction targets: a per-mapping "per-model"
    /// target and a global "global-compact" target. The main mapping is Copilot-compatible by
    /// default; pass <c>copilotCompatible: false</c> to exercise the per-mapping redirect instead.
    /// </summary>
    private static AppSettings CreateSettings(
        bool copilotCompatible = true,
        bool redirectEnabled = true,
        string? globalModelName = "global-compact")
    {
        AppSettings settings = new();

        ModelMapping perModelTarget = new()
        {
            ProxyName = "per-model",
            ModelName = "per-model-upstream",
            UpstreamUrl = "http://localhost:8082",
        };
        perModelTarget.EnsureId();

        ModelMapping globalTarget = new()
        {
            ProxyName = "global-compact",
            ModelName = "global-compact-upstream",
            UpstreamUrl = "http://localhost:8083",
        };
        globalTarget.EnsureId();

        ModelMapping mainMapping = new()
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = perModelTarget.Id,
            RedirectManualCompaction = redirectEnabled,
            CopilotCompatibleCompaction = copilotCompatible,
        };
        mainMapping.EnsureId();

        settings.ModelMappings.Add(perModelTarget);
        settings.ModelMappings.Add(globalTarget);
        settings.ModelMappings.Add(mainMapping);
        settings.CopilotCompactionModelName = globalModelName;

        return settings;
    }

    // ── ModelMapping.CopilotCompatibleCompaction ───────────────────────────

    [Fact]
    public void CopilotCompatibleCompaction_DefaultsToTrue()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080"
        };

        Assert.True(mapping.CopilotCompatibleCompaction);
    }

    [Fact]
    public void CopilotCompatibleCompaction_IsPreservedOnClone()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080",
            CopilotCompatibleCompaction = false
        };
        mapping.EnsureId();

        ModelMapping clone = mapping.Clone();

        Assert.False(clone.CopilotCompatibleCompaction);
        Assert.Equal(mapping.Id, clone.Id);
    }

    // ── AppSettings.FindCopilotCompactionTarget ────────────────────────────

    [Fact]
    public void FindCopilotCompactionTarget_ReturnsNullWhenUnset()
    {
        AppSettings settings = CreateSettings(globalModelName: null);

        Assert.Null(settings.FindCopilotCompactionTarget());
    }

    [Fact]
    public void FindCopilotCompactionTarget_ResolvesEnabledMappingByName()
    {
        AppSettings settings = CreateSettings();

        ModelMapping? target = settings.FindCopilotCompactionTarget();

        Assert.NotNull(target);
        Assert.Equal("global-compact", target!.ProxyName);
    }

    [Fact]
    public void FindCopilotCompactionTarget_ReturnsNullWhenNameUnresolved()
    {
        AppSettings settings = CreateSettings(globalModelName: "does-not-exist");

        Assert.Null(settings.FindCopilotCompactionTarget());
    }

    [Fact]
    public void FindCopilotCompactionTarget_ReturnsNullWhenTargetDisabled()
    {
        AppSettings settings = CreateSettings();
        settings.FindModelMapping("global-compact")!.IsEnabled = false;

        Assert.Null(settings.FindCopilotCompactionTarget());
    }

    [Fact]
    public void FindCopilotCompactionTarget_ReturnsNullWhenNoUpstream()
    {
        AppSettings settings = CreateSettings();
        settings.FindModelMapping("global-compact")!.UpstreamUrl = string.Empty;

        Assert.Null(settings.FindCopilotCompactionTarget());
    }

    // ── ResolveEffectiveModel ──────────────────────────────────────────────

    [Fact]
    public void SignatureRedirect_CopilotCompatible_RoutesToGlobalTarget()
    {
        AppSettings settings = CreateSettings();

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("global-compact", effective);
    }

    [Fact]
    public void SignatureRedirect_CopilotCompatible_GlobalTargetWinsOverPerMappingTarget()
    {
        // Both a per-mapping target and a global target exist; the global one must win for a
        // Copilot-compatible mapping, so the per-mapping choice only serves other tools.
        AppSettings settings = CreateSettings(redirectEnabled: true, globalModelName: "global-compact");

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("global-compact", effective);
        Assert.NotEqual("per-model", effective);
    }

    [Fact]
    public void SignatureRedirect_CopilotCompatible_NoGlobalTarget_FallsBackToRequestedModel()
    {
        // Copilot-compatible is on but no global target is selected: the request goes to the model
        // the client asked for rather than silently using the per-mapping target.
        AppSettings settings = CreateSettings(globalModelName: null);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("main", effective);
    }

    [Fact]
    public void SignatureRedirect_CopilotCompatible_Off_UsesPerMappingRedirect()
    {
        // Opted out: the mapping's own compaction target applies, so other tools still work.
        AppSettings settings = CreateSettings(copilotCompatible: false, redirectEnabled: true);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("per-model", effective);
    }

    [Fact]
    public void SignatureRedirect_CopilotCompatible_OffWithoutRedirect_DoesNotRedirect()
    {
        AppSettings settings = CreateSettings(copilotCompatible: false, redirectEnabled: false);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("main", effective);
    }

    [Fact]
    public void CopilotCompatible_NonCompactRequest_IsUnaffected()
    {
        AppSettings settings = CreateSettings();

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", "hello, how are you?");

        Assert.Equal("main", effective);
    }

    [Fact]
    public void CopilotCompatible_UnknownModel_ReturnsOriginalName()
    {
        AppSettings settings = CreateSettings();

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "not-configured", CompactSignature);

        Assert.Equal("not-configured", effective);
    }

    // ── DescribeCompactSkipReason ──────────────────────────────────────────

    [Fact]
    public void DescribeCompactSkipReason_CopilotCompatibleWithoutGlobalTarget_ReportsGlobalGate()
    {
        AppSettings settings = CreateSettings(globalModelName: null);

        string reason = OllamaProxyHandler.DescribeCompactSkipReason(settings, "main", CompactSignature);

        Assert.Contains("global Copilot compaction model", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Runtime settings round-trip ────────────────────────────────────────

    [Fact]
    public void RuntimeSettings_CopilotCompactionModelName_RoundTrips()
    {
        AppSettings source = new() { CopilotCompactionModelName = "global-compact" };

        RuntimeSettings runtime = source.CreateRuntimeSettings();
        Assert.Equal("global-compact", runtime.CopilotCompactionModelName);

        AppSettings restored = new();
        restored.ApplyRuntimeSettings(runtime);

        Assert.Equal("global-compact", restored.CopilotCompactionModelName);
    }
}