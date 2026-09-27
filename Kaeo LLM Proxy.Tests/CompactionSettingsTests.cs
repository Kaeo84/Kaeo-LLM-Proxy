using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the installation-wide compaction settings: which instruction set drives summarization, and
/// the advisory token ceiling for the summary.
/// </summary>
/// <remarks>
/// These exist because the ceiling is not merely advisory in one direction — it raises the generation
/// cap and is reserved out of the prompt budget, so getting the arithmetic wrong either makes the
/// stated ceiling unachievable or leaves the gate under-reserving and overflowing the compact model.
/// </remarks>
public class CompactionSettingsTests
{
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-compaction-{Guid.NewGuid():N}.db");

    private static AppDatabase OpenDatabase(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void CompactionTargetTokens_DefaultsToNoCeiling()
    {
        Assert.Equal(0, new AppSettings().CompactionTargetTokens);
        Assert.Equal(0, AppSettings.MinCompactionTargetTokens);
    }

    [Fact]
    public void CompactionInstructionSetName_DefaultsToUnset()
    {
        Assert.Null(new AppSettings().CompactionInstructionSetName);
    }

    // ── Instruction resolution ────────────────────────────────────────────

    [Fact]
    public void ResolveCompactionInstructions_ReturnsNullWhenNoSelection()
    {
        AppSettings settings = new();

        Assert.Null(settings.ResolveCompactionInstructions());
    }

    [Fact]
    public void ResolveCompactionInstructions_ReturnsTheSelectedSet()
    {
        AppSettings settings = new() { CompactionInstructionSetName = "Mine" };
        settings.InstructionSets.Add(new InstructionSet { Name = "Mine", Instructions = "Summarize tersely." });

        Assert.Equal("Summarize tersely.", settings.ResolveCompactionInstructions());
    }

    [Fact]
    public void ResolveCompactionInstructions_ReturnsNullWhenTheNameNoLongerExists()
    {
        // A deleted set must degrade to the built-in prompt rather than throwing or compacting with
        // an empty system prompt.
        AppSettings settings = new() { CompactionInstructionSetName = "Deleted" };

        Assert.Null(settings.ResolveCompactionInstructions());
    }

    [Fact]
    public void ResolveCompactionInstructions_ReturnsNullForABlankBody()
    {
        AppSettings settings = new() { CompactionInstructionSetName = "Empty" };
        settings.InstructionSets.Add(new InstructionSet { Name = "Empty", Instructions = "   " });

        Assert.Null(settings.ResolveCompactionInstructions());
    }

    // ── The ceiling raises the generation cap ─────────────────────────────

    [Fact]
    public void EffectiveSummaryMaxTokens_UsesTheDefaultWhenNoCeilingIsSet()
    {
        Assert.Equal(
            AutoCompactionService.DefaultSummaryMaxTokens,
            AutoCompactionService.EffectiveSummaryMaxTokensFor(0));
    }

    [Fact]
    public void EffectiveSummaryMaxTokens_IgnoresACeilingBelowTheDefault()
    {
        // A smaller ceiling must not shrink the cap: generation is capped at the default regardless,
        // and reserving less than the real cap would under-reserve the prompt budget.
        Assert.Equal(
            AutoCompactionService.DefaultSummaryMaxTokens,
            AutoCompactionService.EffectiveSummaryMaxTokensFor(200));
    }

    [Fact]
    public void EffectiveSummaryMaxTokens_RaisesTheCapForACeilingAboveTheDefault()
    {
        // The point of the change: stating an 8000-token ceiling while capping generation at 1000
        // would truncate the summary mid-thought, so the ceiling has to become the cap.
        Assert.Equal(8000, AutoCompactionService.EffectiveSummaryMaxTokensFor(8000));
    }

    // ── The reserve is taken out of the prompt budget ─────────────────────

    [Fact]
    public void PromptBudget_ShrinksByExactlyTheRaisedReserve()
    {
        // A larger ceiling leaves less room for the conversation. That is intentional, but the two
        // budgets must differ by exactly the reserve or the gate and the summarizer disagree.
        int defaultBudget = AutoCompactionService.GetSummaryPromptBudget(32768);
        int raisedBudget = AutoCompactionService.GetSummaryPromptBudget(
            32768, AutoCompactionService.EffectiveSummaryMaxTokensFor(8000));

        int expectedReserveDifference = 8000 - AutoCompactionService.DefaultSummaryMaxTokens;
        Assert.Equal(expectedReserveDifference, defaultBudget - raisedBudget);
    }

    [Fact]
    public void PromptBudget_StillRespectsTheFloorUnderAHugeCeiling()
    {
        // A ceiling larger than the whole window must not drive the budget to zero, which would make
        // every chunk over-limit and spin the sub-chunk splitter without ever fitting a message.
        int budget = AutoCompactionService.GetSummaryPromptBudget(
            4096, AutoCompactionService.EffectiveSummaryMaxTokensFor(1_000_000));

        Assert.Equal(AutoCompactionService.MinSummaryPromptTokens, budget);
        Assert.True(budget > 0);
    }

    // ── Clamping ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(8000, 8000)]
    [InlineData(99999999, AppSettings.MaxCompactionTargetTokens)]
    public void Normalize_ClampsCompactionTargetTokens(int input, int expected)
    {
        AppSettings settings = new() { CompactionTargetTokens = input };

        settings.Normalize();

        Assert.Equal(expected, settings.CompactionTargetTokens);
    }

    [Fact]
    public void Normalize_TreatsABlankInstructionSetNameAsNoSelection()
    {
        AppSettings settings = new() { CompactionInstructionSetName = "   " };

        settings.Normalize();

        Assert.Null(settings.CompactionInstructionSetName);
    }

    // ── Round-trips ───────────────────────────────────────────────────────

    [Fact]
    public void RuntimeSettingsRoundTrip_CarriesBothCompactionSettings()
    {
        AppSettings settings = new()
        {
            CompactionInstructionSetName = "Mine",
            CompactionTargetTokens = 6000,
        };

        RuntimeSettings runtime = settings.CreateRuntimeSettings();
        AppSettings restored = new();
        restored.ApplyRuntimeSettings(runtime);

        Assert.Equal("Mine", restored.CompactionInstructionSetName);
        Assert.Equal(6000, restored.CompactionTargetTokens);
    }

    [Fact]
    public void BothCompactionSettingsSurviveASaveAndReload()
    {
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            setup.SaveRuntimeSettings(new RuntimeSettings
            {
                CompactionInstructionSetName = "Mine",
                CompactionTargetTokens = 6000,
            });
        }

        using AppDatabase database = OpenDatabase(path);
        RuntimeSettings reloaded = database.LoadRuntimeSettings();

        Assert.Equal("Mine", reloaded.CompactionInstructionSetName);
        Assert.Equal(6000, reloaded.CompactionTargetTokens);

        // The neighbours must read correctly too, which is what catches an ordinal shift.
        Assert.Equal(8192, reloaded.CompactionFallbackContextTokens);
        Assert.True(reloaded.EnableCopilotCompactionRouting);
    }

    // ── The default instruction set is seeded ─────────────────────────────

    [Fact]
    public void FreshDatabaseContainsTheSeededCompactionInstructionSet()
    {
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);
        IReadOnlyList<InstructionSet> sets = database.LoadInstructionSets();

        InstructionSet seeded = Assert.Single(sets);
        Assert.Equal(SeedData.CompactionInstructionSetName, seeded.Name);

        // It must carry the built-in prompt verbatim, or a fresh install and an install with no
        // selection would summarize differently for no visible reason.
        Assert.Equal(AutoCompactionService.SummarizerInstructions, seeded.Instructions);

        // The tool-activity requirement is load-bearing for compacted conversations.
        Assert.Contains("## Tool activity", seeded.Instructions);
    }

    [Fact]
    public void SeedingIsIdempotentAcrossReopens()
    {
        string path = NewDatabasePath();

        using (AppDatabase first = OpenDatabase(path))
            Assert.Single(first.LoadInstructionSets());

        using AppDatabase second = OpenDatabase(path);

        Assert.Single(second.LoadInstructionSets());
    }

    [Fact]
    public void SeedingDoesNotOverwriteAnEditedSetOfTheSameName()
    {
        // Editing the seeded prompt is the whole point of shipping it as an instruction set, so a
        // later startup must not restore the default over the operator's text.
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            setup.SaveInstructionSets([
                new InstructionSet
                {
                    Name = SeedData.CompactionInstructionSetName,
                    Instructions = "My edited prompt.",
                },
            ]);
        }

        using AppDatabase database = OpenDatabase(path);
        InstructionSet reloaded = Assert.Single(database.LoadInstructionSets());

        Assert.Equal("My edited prompt.", reloaded.Instructions);
    }

    [Fact]
    public void SeedingAddsTheSetToADatabaseThatPredatesIt()
    {
        // The reason seeding is not part of SeedDefaultsIfEmpty: that method returns early once
        // model_mappings holds a row, so an existing install would never receive the set.
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            ModelMapping mapping = new()
            {
                ProxyName = "existing",
                ModelName = "existing-upstream",
                UpstreamUrl = "http://localhost:8080",
            };
            mapping.EnsureId();
            setup.SaveModelMappings([mapping]);

            // Remove the seeded set to reproduce a database written before it existed.
            setup.SaveInstructionSets([]);
            Assert.Empty(setup.LoadInstructionSets());
        }

        using AppDatabase database = OpenDatabase(path);

        InstructionSet reloaded = Assert.Single(database.LoadInstructionSets());
        Assert.Equal(SeedData.CompactionInstructionSetName, reloaded.Name);
        Assert.Single(database.LoadModelMappings());
    }
}