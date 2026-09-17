using Kaeo.LlmProxy.Core.Models;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies ModelMapping identity semantics: stable IDs survive clones (so cross-mapping
/// references such as ContextSummarizeModelId keep resolving across grid commits and form
/// reloads), while fresh IDs are only assigned where explicitly requested (duplicate).
/// </summary>
public class ModelMappingCloneTests
{
    [Fact]
    public void ClonePreservesIdAndCrossReferences()
    {
        ModelMapping original = new() { ProxyName = "main", ContextSummarizeModelId = 42 };
        original.EnsureId();

        ModelMapping clone = original.Clone();

        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(42, clone.ContextSummarizeModelId);
    }

    [Fact]
    public void ClonePreservesCompactionConfiguration()
    {
        // A clone that dropped any compaction field would silently change behavior when the
        // settings grid commits, so pin the whole compaction surface (including ProxyOnly).
        ModelMapping original = new()
        {
            ProxyName = "main",
            ContextSummarizeModelId = 42,
            AutoCompactPaths = AutoCompactPaths.ProxyOnly,
            RedirectManualCompaction = true,
            ProactiveOverflowPercent = 85,
            ProactiveOverflowTokens = 12000,
            ContextWindowTokens = 65536
        };
        original.EnsureId();

        ModelMapping clone = original.Clone();

        Assert.Equal(42, clone.ContextSummarizeModelId);
        Assert.Equal(AutoCompactPaths.ProxyOnly, clone.AutoCompactPaths);
        Assert.True(clone.RedirectManualCompaction);
        Assert.Equal(85, clone.ProactiveOverflowPercent);
        Assert.Equal(12000, clone.ProactiveOverflowTokens);
        Assert.Equal(65536, clone.ContextWindowTokens);
        Assert.True(clone.IsAutoCompactActiveFor(AutoCompactPaths.OpenAI));
    }

    [Fact]
    public void ClonePreservesCopilotCompatibility()
    {
        // The flag drives whether streaming responses are made well-formed for Microsoft.Extensions.AI
        // clients; a clone that dropped it would silently re-enable the hang when the settings grid
        // commits, so pin it explicitly (default is true, so flip it to false to catch a silent reset).
        ModelMapping original = new() { ProxyName = "main", EnableCopilotCompatibility = false };

        ModelMapping clone = original.Clone();

        Assert.False(clone.EnableCopilotCompatibility);
    }

    [Fact]
    public void CloneOfUnidentifiedMappingGetsFreshId()
    {
        ModelMapping original = new() { ProxyName = "main" };

        ModelMapping clone = original.Clone();

        Assert.NotEqual(0, clone.Id);
    }

    [Fact]
    public void AssignNewIdReplacesExistingId()
    {
        ModelMapping mapping = new() { ProxyName = "main" };
        mapping.EnsureId();
        int originalId = mapping.Id;

        mapping.AssignNewId();

        Assert.NotEqual(0, mapping.Id);
        Assert.NotEqual(originalId, mapping.Id);
    }

    [Fact]
    public void ContextSummarizeReferenceSurvivesCommitStyleCloning()
    {
        // Simulates MainForm.TryCommitMappings: every row's mapping is cloned on commit, so
        // the stored ContextSummarizeModelId must still resolve to the cloned target mapping.
        ModelMapping compact = new() { ProxyName = "compact" };
        compact.EnsureId();

        ModelMapping main = new() { ProxyName = "main", ContextSummarizeModelId = compact.Id };
        main.EnsureId();

        ModelMapping clonedCompact = compact.Clone();
        ModelMapping clonedMain = main.Clone();

        AppSettings settings = new();
        settings.ModelMappings.Add(clonedMain);
        settings.ModelMappings.Add(clonedCompact);

        ModelMapping? resolved = settings.FindModelMappingById(clonedMain.ContextSummarizeModelId!.Value);

        Assert.Same(clonedCompact, resolved);
    }
}
