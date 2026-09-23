using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
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
    public void ClonePreservesHidden()
    {
        // Hidden only affects discovery, but a clone that dropped it would silently re-list a
        // hidden model when the settings grid commits, so pin it explicitly.
        ModelMapping original = new() { ProxyName = "embedder", Hidden = true };

        ModelMapping clone = original.Clone();

        Assert.True(clone.Hidden);
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
    public void DetachCompactionTargetClearsTargetAndRedirect()
    {
        // Duplicating a mapping detaches the inherited compaction pointer so the copy does not
        // redirect /compact to a model the user never selected for it. The clone itself still
        // carries the pointer (the grid-commit path depends on that), so the detach must be an
        // explicit, separate step.
        ModelMapping original = new()
        {
            ProxyName = "main",
            ContextSummarizeModelId = 42,
            ContextSummarizeModelName = "compaction-model",
            RedirectManualCompaction = true,
        };
        original.EnsureId();

        ModelMapping duplicate = original.Clone();
        duplicate.DetachCompactionTarget();

        Assert.Null(duplicate.ContextSummarizeModelId);
        Assert.Null(duplicate.ContextSummarizeModelName);
        Assert.False(duplicate.RedirectManualCompaction);
    }

    [Fact]
    public void DetachedDuplicateDoesNotRedirectToTheSourceCompactionModel()
    {
        // End-to-end shape of the reported bug: duplicating a mapping that redirected /compact
        // used to leave the copy pointing at the source's compaction model, so /compact for the
        // copy was silently routed to a model the user never chose for it.
        ModelMapping compact = new()
        {
            ProxyName = "compaction-model",
            ModelName = "compaction-upstream",
            UpstreamUrl = "http://localhost:8081",
        };
        compact.EnsureId();

        ModelMapping main = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = compact.Id,
            ContextSummarizeModelName = compact.ProxyName,
            RedirectManualCompaction = true,
        };
        main.EnsureId();

        AppSettings settings = new();
        settings.ModelMappings.Add(compact);
        settings.ModelMappings.Add(main);

        ModelMapping duplicate = main.Clone();
        duplicate.AssignNewId();
        duplicate.ProxyName = "main-model - Copy";
        duplicate.DetachCompactionTarget();
        settings.ModelMappings.Add(duplicate);

        Assert.Null(settings.FindContextSummarizeTarget(duplicate));
        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, duplicate);
        Assert.False(redirected);
        Assert.Same(duplicate, resolved);
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
