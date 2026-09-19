using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the manual-compaction redirect to the model the user actually selected.
/// </summary>
/// <remarks>
/// The bug these tests exist because of: compaction for one model was routed to a different model
/// than the one chosen in the dialog. The redirect rule itself is simple — redirect when a
/// compaction model is selected AND "Redirect manual compaction" is checked, otherwise pass the
/// request to its original destination untouched. What broke it was that the target was stored
/// only as an integer ID, and an ID can end up belonging to a different mapping, after which it
/// still resolves. The selected model's proxy name is now stored alongside the ID and resolved
/// ahead of it, so the redirect follows the name the user picked.
/// </remarks>
public class CompactionTargetResolutionTests
{
    /// <summary>
    /// Builds settings where <paramref name="main"/> redirects manual compaction to
    /// <paramref name="target"/>. A decoy is included so a target resolved by the wrong key lands
    /// somewhere visibly different rather than coincidentally correct.
    /// </summary>
    private static AppSettings CreateSettings(out ModelMapping main, out ModelMapping target)
    {
        AppSettings settings = new();

        ModelMapping decoy = new()
        {
            ProxyName = "decoy",
            ModelName = "decoy-upstream",
            UpstreamUrl = "http://localhost:8089",
        };
        decoy.EnsureId();

        target = new ModelMapping
        {
            ProxyName = "compaction-model",
            ModelName = "compaction-upstream",
            UpstreamUrl = "http://localhost:8081",
        };
        target.EnsureId();

        main = new ModelMapping
        {
            ProxyName = "main-model",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            RedirectManualCompaction = true,
            ContextSummarizeModelId = target.Id,
            ContextSummarizeModelName = target.ProxyName,
        };
        main.EnsureId();

        settings.ModelMappings.Add(decoy);
        settings.ModelMappings.Add(target);
        settings.ModelMappings.Add(main);

        return settings;
    }

    // ── The redirect rule ─────────────────────────────────────────────────

    [Fact]
    public void RedirectsToTheSelectedCompactionModel()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);

        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.True(redirected);
        Assert.Same(target, resolved);
    }

    [Fact]
    public void DoesNotRedirectWhenRedirectManualCompactionIsUnchecked()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        main.RedirectManualCompaction = false;

        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, resolved);
    }

    [Fact]
    public void DoesNotRedirectWhenNoCompactionModelIsSelected()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out _);
        main.ContextSummarizeModelId = null;
        main.ContextSummarizeModelName = null;

        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, resolved);
    }

    // ── The stored name outranks the stored ID ────────────────────────────

    [Fact]
    public void StoredNameWinsWhenItDisagreesWithTheStoredId()
    {
        // The reported failure mode: the ID had come to belong to a different mapping, so
        // resolving by ID alone sent compaction somewhere the user never chose.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        main.ContextSummarizeModelId = settings.FindModelMapping("decoy")!.Id;

        Assert.Same(target, settings.FindContextSummarizeTarget(main));
    }

    [Fact]
    public void FallsBackToTheStoredIdForDatabasesSavedBeforeTheNameWasPersisted()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        main.ContextSummarizeModelName = null;

        Assert.Same(target, settings.FindContextSummarizeTarget(main));
    }

    [Fact]
    public void ReturnsNullWhenTheSelectedCompactionModelWasDeleted()
    {
        // A deleted target must degrade to "the requested model handles its own compaction",
        // never to whichever mapping happens to hold the now-recycled ID.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        settings.ModelMappings.Remove(target);

        Assert.Null(settings.FindContextSummarizeTarget(main));
    }

    [Fact]
    public void RenamingTheCompactionModelKeepsTheRedirectWorking()
    {
        // The ID is stable across a rename, so it stays the key that identifies the target once
        // the stored name has gone stale. Dropping it here would turn a harmless rename into
        // silently disabled compaction.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        target.ProxyName = "renamed-compaction-model";

        Assert.Same(target, settings.FindContextSummarizeTarget(main));
    }

    [Fact]
    public void RefreshCompactionTargetNamesRepointsAStaleStoredNameAfterARename()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        target.ProxyName = "renamed-compaction-model";

        settings.RefreshCompactionTargetNames();

        Assert.Equal("renamed-compaction-model", main.ContextSummarizeModelName);
        Assert.Equal(target.Id, main.ContextSummarizeModelId);
    }

    [Fact]
    public void DoesNotRedirectToAMappingWithoutAnUpstreamUrl()
    {
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);
        target.UpstreamUrl = string.Empty;

        (ModelMapping resolved, bool redirected) =
            OllamaProxyHandler.ResolveManualCompactTarget(settings, main);

        Assert.False(redirected);
        Assert.Same(main, resolved);
    }

    [Fact]
    public void CloneCarriesTheStoredCompactionTargetName()
    {
        // The grid commit clones every mapping, so a target name dropped here would be lost on
        // every save and the redirect would quietly stop working.
        AppSettings settings = CreateSettings(out ModelMapping main, out ModelMapping target);

        ModelMapping clone = main.Clone();

        Assert.Equal(target.ProxyName, clone.ContextSummarizeModelName);
    }

    // ── Persisting the selection ──────────────────────────────────────────

    [Fact]
    public void CompactionTargetNameSurvivesASaveAndReload()
    {
        string path = NewDatabasePath();

        using (AppDatabase setup = new(new LoggingSettings { ApplicationDatabasePath = path }))
        {
            AppSettings settings = CreateSettings(out _, out _);
            setup.SaveModelMappings(settings.ModelMappings);
        }

        using AppDatabase database = new(new LoggingSettings { ApplicationDatabasePath = path });
        List<ModelMapping> reloaded = [.. database.LoadModelMappings()];

        ModelMapping main = Assert.Single(reloaded, m => m.ProxyName == "main-model");
        Assert.Equal("compaction-model", main.ContextSummarizeModelName);
    }

    [Fact]
    public void OriginalModelSurvivesASaveAndReload()
    {
        // The log detail view renders "Original → Effective (compact redirect)" from this field,
        // so a redirect that is only visible until the next restart is not a real audit trail.
        string path = NewDatabasePath();
        DateTime timestamp = new(2026, 9, 18, 21, 14, 50, DateTimeKind.Local);

        using (AppDatabase setup = new(new LoggingSettings { ApplicationDatabasePath = path }))
        {
            setup.Insert(new RequestLog
            {
                Timestamp = timestamp,
                Method = "POST",
                OllamaPath = "/v1/chat/completions",
                UpstreamPath = "/v1/chat/completions",
                Model = "compaction-model",
                OriginalModel = "main-model",
                Streaming = true,
            });
        }

        using AppDatabase database = new(new LoggingSettings { ApplicationDatabasePath = path });
        RequestLog? reloaded = database.LoadFullLogEntry(timestamp);

        Assert.NotNull(reloaded);
        Assert.Equal("main-model", reloaded.OriginalModel);
    }

    [Fact]
    public void OriginalModelIsEmptyWhenNoRedirectOccurred()
    {
        string path = NewDatabasePath();
        DateTime timestamp = new(2026, 9, 18, 21, 15, 0, DateTimeKind.Local);

        using (AppDatabase setup = new(new LoggingSettings { ApplicationDatabasePath = path }))
        {
            setup.Insert(new RequestLog
            {
                Timestamp = timestamp,
                Method = "POST",
                OllamaPath = "/v1/chat/completions",
                UpstreamPath = "/v1/chat/completions",
                Model = "main-model",
            });
        }

        using AppDatabase database = new(new LoggingSettings { ApplicationDatabasePath = path });
        RequestLog? reloaded = database.LoadFullLogEntry(timestamp);

        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, reloaded.OriginalModel);
    }

    // ── The debug summary reads in routing order ──────────────────────────

    [Fact]
    public void CompactRedirectDebugLineNamesTheClientModelAndTheTarget()
    {
        // Two adjacent debug lines both use an arrow, and the first line's target is the second
        // line's source. Labelling the sides is what stops the pair from reading as reversed.
        string line = DebugNotes.ContextSummarizeRedirectPassthrough("main-model", "compaction-model");

        Assert.Equal(
            "compact redirect: client requested \"main-model\" → routed to compaction model "
            + "\"compaction-model\" (context-summarize signature detected)",
            line);
    }

    [Fact]
    public void ModelResolutionDebugLineLabelsProxyNameAndUpstreamModel()
    {
        string line = DebugNotes.ModelResolution("compaction-model", "compaction-upstream", mapped: true);

        Assert.Equal(
            "model: proxy name \"compaction-model\" → upstream model \"compaction-upstream\" "
            + "(mapping \"compaction-model\")",
            line);
    }

    [Fact]
    public void DebugSummaryRedirectsFromTheClientModelToTheSelectedCompactionModel()
    {
        AppSettings settings = CreateSettings(out _, out ModelMapping target);
        settings.DebugMode = true;

        RequestLog log = new();
        const string compactPrompt = "Your task is to **produce an authoritative, self-contained summary** "
            + "of the current session. <ConversationSummary>";
        string json = $$"""{"model":"main-model","messages":[{"role":"system","content":"{{compactPrompt}}"}]}""";

        string upstreamBody = OllamaProxyHandler.NormalizeRequestBody(json, settings, log);

        using JsonDocument upstream = JsonDocument.Parse(upstreamBody);
        Assert.Equal(target.ModelName, upstream.RootElement.GetProperty("model").GetString());
        Assert.Equal("main-model", log.OriginalModel);
        Assert.NotNull(log.DebugSummary);
        Assert.Contains("client requested \"main-model\"", log.DebugSummary);
        Assert.Contains("routed to compaction model \"compaction-model\"", log.DebugSummary);
    }

    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left in
    /// the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-compact-{Guid.NewGuid():N}.db");
}
