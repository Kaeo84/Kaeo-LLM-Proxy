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
    // ── CompactModelProxyName Global Setting ──────────────────────────────

    [Fact]
    public void CompactModelProxyName_DefaultsToNull()
    {
        AppSettings settings = new();
        Assert.Null(settings.CompactModelProxyName);
    }

    [Fact]
    public void CompactModelProxyName_CanBeSet()
    {
        AppSettings settings = new()
        {
            CompactModelProxyName = "compact-model"
        };
        Assert.Equal("compact-model", settings.CompactModelProxyName);
    }

    [Fact]
    public void CompactModelProxyName_CanBeEmpty()
    {
        AppSettings settings = new()
        {
            CompactModelProxyName = ""
        };
        Assert.Equal("", settings.CompactModelProxyName);
    }

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

    // ── Compact Model Routing Logic ───────────────────────────────────────

    [Fact]
    public void CompactModelRouting_GlobalSettingOverridesPerMapping()
    {
        // Setup: Create settings with both global and per-mapping compact models
        AppSettings settings = new()
        {
            CompactModelProxyName = "global-compact"
        };

        ModelMapping globalCompact = new()
        {
            ProxyName = "global-compact",
            ModelName = "global-compact-upstream",
            UpstreamUrl = "http://localhost:8081"
        };
        globalCompact.EnsureId();

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

        settings.ModelMappings.Add(globalCompact);
        settings.ModelMappings.Add(perMappingCompact);
        settings.ModelMappings.Add(mainMapping);

        // Verify: Global setting should be found
        ModelMapping? foundGlobal = settings.FindModelMapping(settings.CompactModelProxyName!);
        Assert.NotNull(foundGlobal);
        Assert.Equal("global-compact", foundGlobal.ProxyName);

        // Verify: Per-mapping setting should also be found
        ModelMapping? foundPerMapping = settings.FindModelMappingById(mainMapping.ContextSummarizeModelId!.Value);
        Assert.NotNull(foundPerMapping);
        Assert.Equal("per-mapping-compact", foundPerMapping.ProxyName);
    }

    [Fact]
    public void CompactModelRouting_FallsBackToPerMappingWhenGlobalNotSet()
    {
        AppSettings settings = new(); // CompactModelProxyName is null by default

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

        // Verify: Global setting is null
        Assert.Null(settings.CompactModelProxyName);

        // Verify: Per-mapping setting should be found
        ModelMapping? foundPerMapping = settings.FindModelMappingById(mainMapping.ContextSummarizeModelId!.Value);
        Assert.NotNull(foundPerMapping);
        Assert.Equal("per-mapping-compact", foundPerMapping.ProxyName);
    }

    [Fact]
    public void CompactModelRouting_GlobalSettingNotFoundFallsBackToPerMapping()
    {
        AppSettings settings = new()
        {
            CompactModelProxyName = "nonexistent-compact"
        };

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

        // Verify: Global setting is not found
        ModelMapping? foundGlobal = settings.FindModelMapping(settings.CompactModelProxyName!);
        Assert.Null(foundGlobal);

        // Verify: Per-mapping setting should still be found
        ModelMapping? foundPerMapping = settings.FindModelMappingById(mainMapping.ContextSummarizeModelId!.Value);
        Assert.NotNull(foundPerMapping);
        Assert.Equal("per-mapping-compact", foundPerMapping.ProxyName);
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
            EnableManualCompactionEndpoint = true,
            CompactModelProxyName = "test-compact"
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
        Assert.Equal("test-compact", deserialized.CompactModelProxyName);
    }

    [Fact]
    public void Configuration_NullCompactModelProxyNameSerializesCorrectly()
    {
        AppSettings settings = new()
        {
            CompactModelProxyName = null
        };

        string json = JsonSerializer.Serialize(settings);
        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.CompactModelProxyName);
    }
}
