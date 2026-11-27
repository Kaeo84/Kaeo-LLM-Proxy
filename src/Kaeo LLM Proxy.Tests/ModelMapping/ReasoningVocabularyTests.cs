using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.VSExtension.Core;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// The proxy's per-model Thinking Handling list and the extension's Reasoning column describe one
/// axis in two vocabularies. <see cref="ReasoningVocabulary"/> is the single naming table for both;
/// these tests fail if a new mode or source is added without being described, or if the two lists
/// stop covering the same concepts.
/// </summary>
public sealed class ReasoningVocabularyTests
{
    /// <summary>The extension's persisted <c>ReasoningSource</c> names (see
    /// <c>ReasoningSources.Parse</c>). Pinned here because the VSIX project is not referenced by
    /// this test project — drift shows up as a failure rather than as a missing dropdown entry.</summary>
    private static readonly string[] ExtensionSources = ["Auto", "ThinkingField", "InlineTags", "Disabled"];

    [Fact]
    public void EveryThinkingMode_HasAVocabularyEntry()
    {
        // Distinct() because the enum carries aliases (Off/LeaveInline, ExtractThinkTags/MoveToReasoningContent).
        foreach (ThinkingMode mode in Enum.GetValues<ThinkingMode>().Distinct())
        {
            ReasoningConcept? concept = ReasoningVocabulary.ForProxyMode(mode.ToString());
            Assert.True(concept is not null, $"{mode} has no ReasoningVocabulary entry, so the dialog would show its raw enum name.");
            Assert.False(string.IsNullOrWhiteSpace(concept!.Label));
            Assert.False(string.IsNullOrWhiteSpace(concept.Description));
        }
    }

    [Fact]
    public void EveryExtensionReasoningSource_HasAVocabularyEntry()
    {
        foreach (string source in ExtensionSources)
            Assert.True(ReasoningVocabulary.ForReasoningSource(source) is not null, $"{source} has no ReasoningVocabulary entry, so it would vanish from the extension's Reasoning column.");

        // The vocabulary must not invent extension sources the parser would treat as "Auto".
        string[] described = [.. ReasoningVocabulary.All
            .Where(c => c.ExtensionReasoningSource is not null)
            .Select(c => c.ExtensionReasoningSource!)];
        Assert.Equal(ExtensionSources.OrderBy(s => s, StringComparer.Ordinal), described.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void ProxyLabels_AreDistinct()
    {
        // Two modes sharing one label would make the combo box unusable (the second is unreachable).
        string[] labels = [.. Enum.GetValues<ThinkingMode>().Distinct().Select(m => ReasoningVocabulary.LabelForProxyMode(m.ToString()))];
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LabelForProxyMode_FallsBackToTheRawName()
    {
        Assert.Equal("NotAMode", ReasoningVocabulary.LabelForProxyMode("NotAMode"));
        Assert.Equal(string.Empty, ReasoningVocabulary.HelpForProxyMode("NotAMode"));
    }

    [Fact]
    public void EnumAliases_ResolveToTheSameConceptAsTheirPrimary()
    {
        // A mapping file may still say "Off" or "ExtractThinkTags"; neither may fall back to the raw
        // name in the dialog or in the debug summary.
        Assert.Equal(nameof(ThinkingMode.LeaveInline), ReasoningVocabulary.ForProxyMode("Off")!.ProxyThinkingMode);
        Assert.Equal(nameof(ThinkingMode.MoveToReasoningContent), ReasoningVocabulary.ForProxyMode("ExtractThinkTags")!.ProxyThinkingMode);
    }

    [Fact]
    public void HelpForProxyMode_CombinesLabelAndDescription()
    {
        ReasoningConcept concept = ReasoningVocabulary.ForProxyMode(nameof(ThinkingMode.MoveToReasoningContent))!;
        string help = ReasoningVocabulary.HelpForProxyMode(nameof(ThinkingMode.MoveToReasoningContent));
        Assert.Contains(concept.Label, help, StringComparison.Ordinal);
        Assert.Contains(concept.Description, help, StringComparison.Ordinal);
    }

    [Fact]
    public void StripAndDisabled_DescribeTheSameOutcome()
    {
        // The pairing that matters most: a user who strips thinking in the proxy must not select a
        // source that still expects reasoning to arrive.
        Assert.Equal("StripFromOutput", ReasoningVocabulary.ForReasoningSource("Disabled")!.ProxyThinkingMode);
        Assert.Equal("LeaveInline", ReasoningVocabulary.ForReasoningSource("InlineTags")!.ProxyThinkingMode);
        Assert.Equal("MoveToReasoningContent", ReasoningVocabulary.ForReasoningSource("ThinkingField")!.ProxyThinkingMode);
    }
}