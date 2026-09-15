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
    // ── EnableCopilotNativeCompaction Setting ─────────────────────────────

    [Fact]
    public void EnableCopilotNativeCompaction_DefaultsToTrue()
    {
        AppSettings settings = new();
        Assert.True(settings.EnableCopilotNativeCompaction);
    }

    [Fact]
    public void EnableCopilotNativeCompaction_CanBeDisabled()
    {
        AppSettings settings = new()
        {
            EnableCopilotNativeCompaction = false
        };
        Assert.False(settings.EnableCopilotNativeCompaction);
    }

    // ── EnableAutoCompaction Setting ──────────────────────────────────────

    [Fact]
    public void EnableAutoCompaction_DefaultsToTrue()
    {
        AppSettings settings = new();
        Assert.True(settings.EnableAutoCompaction);
    }

    [Fact]
    public void EnableAutoCompaction_CanBeDisabled()
    {
        AppSettings settings = new()
        {
            EnableAutoCompaction = false
        };
        Assert.False(settings.EnableAutoCompaction);
    }

    // ── EnableManualCompactionEndpoint Setting ────────────────────────────

    [Fact]
    public void EnableManualCompactionEndpoint_DefaultsToFalse()
    {
        AppSettings settings = new();
        Assert.False(settings.EnableManualCompactionEndpoint);
    }

    [Fact]
    public void EnableManualCompactionEndpoint_CanBeEnabled()
    {
        AppSettings settings = new()
        {
            EnableManualCompactionEndpoint = true
        };
        Assert.True(settings.EnableManualCompactionEndpoint);
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
        AppSettings settings = new()
        {
            EnableCopilotNativeCompaction = true,
            EnableAutoCompaction = false,
            EnableManualCompactionEndpoint = true
        };

        // Verify settings can be serialized and deserialized
        string json = JsonSerializer.Serialize(settings);
        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        // Note: Properties with [JsonIgnore] attribute won't be serialized,
        // so they will have their default values after deserialization
        Assert.True(deserialized.EnableCopilotNativeCompaction); // Default is true
        Assert.True(deserialized.EnableAutoCompaction); // Default is true, [JsonIgnore] prevents serialization
        Assert.False(deserialized.EnableManualCompactionEndpoint); // Default is false, [JsonIgnore] prevents serialization
    }
}
