using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Integration tests for context compaction features including compact model routing,
/// Copilot detection, and configuration settings.
/// </summary>
public class ContextCompactionTests
{
    // ── No global compaction default ──────────────────────────────────────

    [Fact]
    public void Compaction_HasNoGlobalDefault_AndMappingDefaultsToDisabled()
    {
        // There is deliberately no app-wide compaction toggle: a fresh mapping must default to
        // "do nothing", so requests pass to the model and the model handles its own context.
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080"
        };

        Assert.Equal(AutoCompactPaths.None, mapping.AutoCompactPaths);
        Assert.Equal(0, mapping.ProactiveOverflowPercent);
        Assert.Equal(0, mapping.ProactiveOverflowTokens);
        Assert.Null(mapping.ContextSummarizeModelId);
        Assert.False(mapping.RedirectManualCompaction);

        // Every gate is off, so auto-compaction is inactive on every path.
        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    // ── Auto-Compact Paths gating ─────────────────────────────────────────

    [Fact]
    public void AutoCompactPaths_None_NeverActivates()
    {
        ModelMapping mapping = new() { AutoCompactPaths = AutoCompactPaths.None };

        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void AutoCompactPaths_Ollama_ActivatesOnlyOllamaPath()
    {
        ModelMapping mapping = new() { AutoCompactPaths = AutoCompactPaths.Ollama };

        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void AutoCompactPaths_OpenAI_ActivatesOnlyOpenAiPath()
    {
        ModelMapping mapping = new() { AutoCompactPaths = AutoCompactPaths.OpenAI };

        Assert.False(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void AutoCompactPaths_Both_ActivatesBothPaths()
    {
        ModelMapping mapping = new() { AutoCompactPaths = AutoCompactPaths.Both };

        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void AutoCompactPaths_ProxyOnly_ActivatesEveryPath()
    {
        // ProxyOnly is the wildcard: the proxy summarizes on any path it handles.
        ModelMapping mapping = new() { AutoCompactPaths = AutoCompactPaths.ProxyOnly };

        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.Ollama));
        Assert.True(mapping.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void AutoCompactPaths_ProxyOnly_IsDistinctFromBoth()
    {
        Assert.NotEqual(AutoCompactPaths.Both, AutoCompactPaths.ProxyOnly);
        Assert.Equal(4, (int)AutoCompactPaths.ProxyOnly);
    }

    // ── Manual compaction target resolution ───────────────────────────────

    private static (AppSettings settings, ModelMapping main, ModelMapping compactTarget) BuildRedirectFixture(
        bool redirectEnabled, bool targetEnabled = true, string? targetUpstream = "http://localhost:8082")
    {
        AppSettings settings = new();

        ModelMapping target = new()
        {
            ProxyName = "compact-target",
            ModelName = "compact-target-upstream",
            UpstreamUrl = targetUpstream ?? string.Empty,
            IsEnabled = targetEnabled
        };
        target.EnsureId();

        ModelMapping main = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = target.Id,
            RedirectManualCompaction = redirectEnabled
        };
        main.EnsureId();

        settings.ModelMappings.Add(target);
        settings.ModelMappings.Add(main);

        return (settings, main, target);
    }

    [Fact]
    public void ManualCompact_RedirectDisabled_ForwardsToRequestedModel()
    {
        // Spec 1: redirect not checked => the request's own model handles the compaction.
        (AppSettings settings, ModelMapping main, _) = BuildRedirectFixture(redirectEnabled: false);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    [Fact]
    public void ManualCompact_NoTargetConfigured_ForwardsToRequestedModel()
    {
        // Spec 1: no compaction model selected => the request's own model handles the compaction.
        AppSettings settings = new();
        ModelMapping main = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true
        };
        main.EnsureId();
        settings.ModelMappings.Add(main);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    [Fact]
    public void ManualCompact_RedirectEnabledWithTarget_ForwardsToCompactionModel()
    {
        // Spec 2: redirect checked AND a compaction model selected => use the compaction model.
        (AppSettings settings, ModelMapping main, ModelMapping target) = BuildRedirectFixture(redirectEnabled: true);

        (ModelMapping resolved, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.True(redirected);
        Assert.Same(target, resolved);
    }

    [Fact]
    public void ManualCompact_TargetDisabled_FallsBackToRequestedModel()
    {
        (AppSettings settings, ModelMapping main, _) = BuildRedirectFixture(redirectEnabled: true, targetEnabled: false);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    [Fact]
    public void ManualCompact_TargetWithoutUpstream_FallsBackToRequestedModel()
    {
        (AppSettings settings, ModelMapping main, _) = BuildRedirectFixture(redirectEnabled: true, targetUpstream: null);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    [Fact]
    public void ManualCompact_TargetMissingById_FallsBackToRequestedModel()
    {
        AppSettings settings = new();
        ModelMapping main = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true,
            ContextSummarizeModelId = 9999 // no such mapping
        };
        main.EnsureId();
        settings.ModelMappings.Add(main);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    [Fact]
    public void ManualCompact_SelfReferentialTarget_IsNotARedirect()
    {
        AppSettings settings = new();
        ModelMapping main = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true
        };
        main.EnsureId();
        main.ContextSummarizeModelId = main.Id;
        settings.ModelMappings.Add(main);

        (ModelMapping target, bool redirected) = OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, target);
    }

    // ── Signature-based /compact redirect gating ──────────────────────────

    private const string CompactSignature =
        "Your task is to produce an authoritative, self-contained summary of this session.";

    [Fact]
    public void SignatureRedirect_WithoutRedirectEnabled_DoesNotRedirect()
    {
        (AppSettings settings, ModelMapping main, _) = BuildRedirectFixture(redirectEnabled: false);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, main.ProxyName, CompactSignature);

        Assert.Equal(main.ProxyName, effective);
    }

    [Fact]
    public void SignatureRedirect_WithRedirectAndTarget_UsesCompactionModel()
    {
        (AppSettings settings, ModelMapping main, ModelMapping target) = BuildRedirectFixture(redirectEnabled: true);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, main.ProxyName, CompactSignature);

        Assert.Equal(target.ProxyName, effective);
    }

    [Fact]
    public void SignatureRedirect_NonCompactRequest_NeverRedirects()
    {
        // Even with redirect enabled and a valid target, an ordinary request is untouched.
        (AppSettings settings, ModelMapping main, _) = BuildRedirectFixture(redirectEnabled: true);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, main.ProxyName, "hello, how are you?");

        Assert.Equal(main.ProxyName, effective);
    }

    [Fact]
    public void SignatureRedirect_UnknownModel_ReturnsOriginalName()
    {
        (AppSettings settings, _, _) = BuildRedirectFixture(redirectEnabled: true);

        string effective = OllamaProxyHandler.ResolveEffectiveModel(settings, "not-configured", CompactSignature);

        Assert.Equal("not-configured", effective);
    }

    // ── ContextSummarizeModelId Per-Mapping Setting ───────────────────────

    [Fact]
    public void ContextSummarizeModelId_DefaultsToNull()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080"
        };
        Assert.Null(mapping.ContextSummarizeModelId);
    }

    [Fact]
    public void ContextSummarizeModelId_CanBeSet()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = 42
        };
        Assert.Equal(42, mapping.ContextSummarizeModelId);
    }

    // ── RedirectManualCompaction Per-Mapping Setting ──────────────────────

    [Fact]
    public void RedirectManualCompaction_DefaultsToFalse()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080"
        };
        Assert.False(mapping.RedirectManualCompaction);
    }

    [Fact]
    public void RedirectManualCompaction_CanBeSet()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true
        };
        Assert.True(mapping.RedirectManualCompaction);
    }

    [Fact]
    public void RedirectManualCompaction_IsPreservedOnClone()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "test-model",
            ModelName = "test-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true
        };
        mapping.EnsureId();

        ModelMapping clone = mapping.Clone();

        Assert.True(clone.RedirectManualCompaction);
        Assert.Equal(mapping.Id, clone.Id);
    }

    // ── Compact Model Routing Logic ───────────────────────────────────────

    [Fact]
    public void CompactModelRouting_ResolvesPerMappingTarget()
    {
        AppSettings settings = new();

        ModelMapping perMappingCompact = new()
        {
            ProxyName = "per-mapping-compact",
            ModelName = "per-mapping-compact-upstream",
            UpstreamUrl = "http://localhost:8082"
        };
        perMappingCompact.EnsureId();

        ModelMapping mainMapping = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = perMappingCompact.Id
        };
        mainMapping.EnsureId();

        settings.ModelMappings.Add(perMappingCompact);
        settings.ModelMappings.Add(mainMapping);

        // The per-mapping compaction target resolves to the configured model.
        ModelMapping? found = settings.FindModelMappingById(mainMapping.ContextSummarizeModelId!.Value);
        Assert.NotNull(found);
        Assert.Equal("per-mapping-compact", found.ProxyName);
    }

    [Fact]
    public void CompactModelRouting_NoTargetWhenNoneConfigured()
    {
        AppSettings settings = new();

        ModelMapping mainMapping = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080"
        };
        mainMapping.EnsureId();

        settings.ModelMappings.Add(mainMapping);

        // With no compaction target configured, there is nothing to resolve.
        Assert.Null(mainMapping.ContextSummarizeModelId);
    }

    // ── Copilot Detection Integration ─────────────────────────────────────

    [Fact]
    public void CopilotDetection_IntegratesWithCompactSignature()
    {
        // Verify that Copilot detection works with compact signatures
        string compactPrompt = "Your task is to **produce an authoritative, self-contained summary** of the current session.";

        // Should detect as Copilot request
        Assert.True(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", compactPrompt));

        // Should also detect via User-Agent
        Assert.True(OllamaProxyHandler.IsCopilotRequest("GitHub Copilot CLI/1.0", "normal content"));

        // Should not detect non-Copilot requests
        Assert.False(OllamaProxyHandler.IsCopilotRequest("Mozilla/5.0", "normal content"));
    }

    [Fact]
    public void CopilotDetection_CompactSignatureDetection()
    {
        // Test various compact signature patterns
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest("authoritative, self-contained summary"));
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest("<ConversationSummary>"));
        Assert.True(OllamaProxyHandler.IsContextSummarizeRequest("<ReasoningScratchpad>"));

        // Should not match normal content
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest("normal conversation"));
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest(null));
        Assert.False(OllamaProxyHandler.IsContextSummarizeRequest(""));
    }

    // ── Configuration Validation ──────────────────────────────────────────

    [Fact]
    public void Configuration_AllSettingsCanBeSerialized()
    {
        AppSettings settings = new();

        // Verify settings can be serialized and deserialized.
        string json = JsonSerializer.Serialize(settings);
        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        // There is no global compaction toggle: a deserialized instance keeps the safe default
        // of "no mappings", and compaction behavior lives entirely on each ModelMapping.
        Assert.Empty(deserialized!.ModelMappings);
    }

    [Fact]
    public void Configuration_PerMappingCompactionRoundTrips()
    {
        ModelMapping mapping = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            AutoCompactPaths = AutoCompactPaths.ProxyOnly,
            RedirectManualCompaction = true,
            ContextSummarizeModelId = 42,
            ProactiveOverflowPercent = 85
        };

        string json = JsonSerializer.Serialize(mapping);
        ModelMapping? deserialized = JsonSerializer.Deserialize<ModelMapping>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(AutoCompactPaths.ProxyOnly, deserialized!.AutoCompactPaths);
        Assert.True(deserialized.RedirectManualCompaction);
        Assert.Equal(42, deserialized.ContextSummarizeModelId);
        Assert.Equal(85, deserialized.ProactiveOverflowPercent);
        Assert.True(deserialized.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }
}
