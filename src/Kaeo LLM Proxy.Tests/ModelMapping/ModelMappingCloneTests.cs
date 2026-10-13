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
    public void DuplicateIsAFullCopyOfTheSourceConfiguration()
    {
        // A duplicate keeps the source's entire configuration, including the compaction target
        // and the manual-compaction redirect, so the copy behaves exactly like the model it came
        // from. Only the identity and proxy name change.
        ModelMapping compact = new() { ProxyName = "compaction-model" };
        compact.EnsureId();

        ModelMapping source = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = compact.Id,
            ContextSummarizeModelName = compact.ProxyName,
            RedirectManualCompaction = true,
            AutoCompactPaths = AutoCompactPaths.Both,
            ProactiveOverflowPercent = 85,
        };
        source.EnsureId();

        ModelMapping duplicate = source.Clone();
        duplicate.AssignNewId();
        duplicate.ProxyName = "main-model - Copy";

        Assert.Equal(compact.Id, duplicate.ContextSummarizeModelId);
        Assert.Equal(compact.ProxyName, duplicate.ContextSummarizeModelName);
        Assert.True(duplicate.RedirectManualCompaction);
        Assert.Equal(AutoCompactPaths.Both, duplicate.AutoCompactPaths);
        Assert.Equal(85, duplicate.ProactiveOverflowPercent);
    }

    [Fact]
    public void DuplicatedMappingsAreIndependentSoEditingOneLeavesTheOtherAlone()
    {
        // "Full copy" must not mean "shared": clearing or changing a relation on the duplicate
        // must affect only the duplicate. This is the property the user depends on when they
        // duplicate a model to try a different compaction setup.
        ModelMapping compact = new() { ProxyName = "compaction-model" };
        compact.EnsureId();

        ModelMapping source = new()
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            ContextSummarizeModelId = compact.Id,
            ContextSummarizeModelName = compact.ProxyName,
            RedirectManualCompaction = true,
            Capabilities = ["vision"],
        };
        source.EnsureId();

        ModelMapping duplicate = source.Clone();
        duplicate.AssignNewId();
        duplicate.ProxyName = "main-model - Copy";

        // Clear the duplicate's compaction relation.
        duplicate.ContextSummarizeModelId = null;
        duplicate.ContextSummarizeModelName = null;
        duplicate.RedirectManualCompaction = false;
        duplicate.Capabilities.Clear();

        // The source must be untouched, and the two must be distinct identities.
        Assert.Equal(compact.Id, source.ContextSummarizeModelId);
        Assert.Equal(compact.ProxyName, source.ContextSummarizeModelName);
        Assert.True(source.RedirectManualCompaction);
        Assert.Single(source.Capabilities);
        Assert.NotEqual(source.Id, duplicate.Id);
    }

    [Fact]
    public void ChangingTheSourceAfterDuplicatingDoesNotAffectTheDuplicate()
    {
        // The reverse direction: independence has to hold both ways, so retargeting the source's
        // compaction relation must not drag the already-created duplicate along with it.
        ModelMapping compact = new() { ProxyName = "compaction-model" };
        compact.EnsureId();
        ModelMapping other = new() { ProxyName = "other-model" };
        other.EnsureId();

        ModelMapping source = new()
        {
            ProxyName = "main-model",
            ContextSummarizeModelId = compact.Id,
            ContextSummarizeModelName = compact.ProxyName,
            RedirectManualCompaction = true,
        };
        source.EnsureId();

        ModelMapping duplicate = source.Clone();
        duplicate.AssignNewId();
        duplicate.ProxyName = "main-model - Copy";

        source.ContextSummarizeModelId = other.Id;
        source.ContextSummarizeModelName = other.ProxyName;

        Assert.Equal(compact.Id, duplicate.ContextSummarizeModelId);
        Assert.Equal(compact.ProxyName, duplicate.ContextSummarizeModelName);
    }

    [Fact]
    public void DuplicateResolvesItsOwnCopiedCompactionTarget()
    {
        // End-to-end shape of the requirement: the copy is a full duplicate, so it redirects
        // /compact to the same target the source did — and resolves that target by its own
        // stored copy of the relation rather than by sharing state with the source.
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
        settings.ModelMappings.Add(duplicate);

        Assert.Same(compact, settings.FindContextSummarizeTarget(duplicate));

        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, duplicate);
        Assert.True(redirected);
        Assert.Same(compact, resolved);
    }

    [Fact]
    public void ClearingTheDuplicateCompactionTargetStopsOnlyItsRedirect()
    {
        // The reported failure: the copy's Configure dialog showed no compaction model while the
        // copy still redirected. With a full, independent copy, clearing the duplicate's target
        // stops the duplicate's redirect and leaves the source's redirect working.
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
        settings.ModelMappings.Add(duplicate);

        // Simulate the user clearing the compaction model in the duplicate's Configure dialog.
        duplicate.ContextSummarizeModelId = null;
        duplicate.ContextSummarizeModelName = null;
        duplicate.RedirectManualCompaction = false;

        Assert.Null(settings.FindContextSummarizeTarget(duplicate));
        (_, bool duplicateRedirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, duplicate);
        Assert.False(duplicateRedirected);

        // The source is unaffected and still redirects.
        (ModelMapping sourceResolved, bool sourceRedirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, main);
        Assert.True(sourceRedirected);
        Assert.Same(compact, sourceResolved);
    }

    [Fact]
    public void CloneCoversEveryPublicInstanceProperty()
    {
        // When a new public property is added to ModelMapping, it must be copied in Clone() or
        // it silently resets to its default after a settings save. This test fails if any
        // get/set property differs between a fully-populated source and its clone, forcing the
        // developer to update Clone() alongside the new property.
        ModelMapping source = CreateFullyPopulatedMapping();
        ModelMapping clone = source.Clone();

        foreach (System.Reflection.PropertyInfo prop in typeof(ModelMapping).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                continue;

            object? sourceValue = prop.GetValue(source);
            object? cloneValue = prop.GetValue(clone);

            // Reference-type collections must be distinct (deep-copied), not shared.
            if (sourceValue is System.Collections.IList sourceList
                && cloneValue is System.Collections.IList cloneList)
            {
                Assert.NotSame(sourceValue, cloneValue);
                Assert.Equal(sourceList.Count, cloneList.Count);
                // Compare element-by-element.
                for (int i = 0; i < sourceList.Count; i++)
                    Assert.Equal(sourceList[i], cloneList[i]);
                continue;
            }

            // Value types and strings must be equal.
            Assert.Equal(sourceValue, cloneValue);
        }
    }

    private static ModelMapping CreateFullyPopulatedMapping()
    {
        ModelMapping m = new()
        {
            IsEnabled = true,
            Hidden = true,
            ProxyName = "clone-test",
            ModelName = "clone-test-upstream",
            EnableThinkingCompatibility = false,
            EnableSseKeepAlive = true,
            EnableHeartbeats = false,
            CredentialName = "test-cred",
            UpstreamType = UpstreamType.OpenAI,
            ThinkingMode = ThinkingMode.MoveToReasoningContent,
            UpstreamUrl = "https://api.example.com/v1",
            UpstreamTimeoutSeconds = 120,
            RepeatPenalty = 1.15,
            TemperaturePriority = SamplingPriority.Proxy,
            RepeatPenaltyPriority = SamplingPriority.Provider,
            Temperature = 0.3,
            ReasoningEffortPriority = SamplingPriority.Proxy,
            ReasoningEffort = "high",
            ReasoningEffortValues = ["low", "medium", "high"],
            ReasoningEffortFormat = ReasoningEffortFormat.Legacy | ReasoningEffortFormat.Modern,
            InstructionSetName = "test-instructions",
            ContextSummarizeModelId = 7,
            ContextSummarizeModelName = "compact-target",
            RedactRequestBodies = true,
            RedactResponseBodies = true,
            RedactSensitiveJsonFields = false,
            ContextWindowTokens = 128000,
            ProactiveOverflowPercent = 90,
            ProactiveOverflowTokens = 10000,
            AutoCompactPaths = AutoCompactPaths.Both,
            RedirectManualCompaction = true,
            EnableCopilotCompatibility = false,
            ForwardClientAuthorization = true,
        };
        m.Capabilities.AddRange(["vision", "function_calling"]);
        m.EnsureId();
        return m;
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
