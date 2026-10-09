using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the global Copilot compaction path: a Copilot-detected summarization is routed to the
/// single installation-wide <see cref="AppSettings.CopilotCompactionModelName"/> for every model,
/// because Copilot picks the model for its compaction turn itself and cannot be steered per model.
/// When routing is off or no usable global target exists, resolution falls back to the per-mapping
/// redirect.
/// </summary>
public class CopilotCompactionTests
{
    // The distinctive head of the Copilot /compact system prompt.
    private const string CompactSignature =
        "Your task is to produce an authoritative, self-contained summary of this session.";

    /// <summary>
    /// Settings with a "main" model and two possible compaction targets: a per-mapping "per-model"
    /// target and a global "global-compact" target. Global routing is <b>on by default</b> for every
    /// model, so the global target wins; pass <c>copilotRouting: false</c> to exercise the
    /// per-mapping redirect instead.
    /// </summary>
    private static AppSettings CreateSettings(
        bool copilotRouting = true,
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
        };
        mainMapping.EnsureId();

        settings.ModelMappings.Add(perModelTarget);
        settings.ModelMappings.Add(globalTarget);
        settings.ModelMappings.Add(mainMapping);
        settings.CopilotCompactionModelName = globalModelName;
        settings.EnableCopilotCompactionRouting = copilotRouting;

        return settings;
    }

    // ── AppSettings.EnableCopilotCompactionRouting ─────────────────────────

    [Fact]
    public void CopilotCompactionRouting_DefaultsToTrue()
    {
        AppSettings settings = new();

        Assert.True(settings.EnableCopilotCompactionRouting);
    }

    [Fact]
    public void FindCopilotCompactionTarget_ReturnsNullWhenRoutingIsOff()
    {
        // The switch has to be honored by the finder rather than each call site, so the redirect,
        // the diagnostic reason and the manual resolver cannot disagree about whether a global
        // target applies.
        AppSettings settings = CreateSettings(copilotRouting: false);

        Assert.Null(settings.FindCopilotCompactionTarget());
    }

    [Fact]
    public void RoutingOff_FallsBackToThePerMappingRedirect()
    {
        AppSettings settings = CreateSettings(copilotRouting: false);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(
            settings, "main", CompactSignature);

        Assert.Equal("per-model", effective);
    }

    [Fact]
    public void RoutingOn_AppliesToAModelThatConfiguredNothingPerMapping()
    {
        // The point of acting globally: Copilot picks the model itself, so a model with no
        // per-mapping compaction configuration at all must still be routed to the global target.
        AppSettings settings = CreateSettings();

        string effective = OllamaProxyHandler.ResolveEffectiveModel(
            settings, "global-compact", CompactSignature);

        Assert.Equal("global-compact", effective);
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
    public void SignatureRedirect_RoutingOff_UsesPerMappingRedirect()
    {
        // Global routing off: the mapping's own compaction target applies, so other tools still work.
        AppSettings settings = CreateSettings(copilotRouting: false, redirectEnabled: true);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "main", CompactSignature);

        Assert.Equal("per-model", effective);
    }

    [Fact]
    public void SignatureRedirect_RoutingOffWithoutRedirect_DoesNotRedirect()
    {
        AppSettings settings = CreateSettings(copilotRouting: false, redirectEnabled: false);

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