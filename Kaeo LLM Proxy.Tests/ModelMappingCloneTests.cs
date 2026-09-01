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
