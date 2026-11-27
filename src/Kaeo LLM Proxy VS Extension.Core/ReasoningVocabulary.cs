using System;
using System.Collections.Generic;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// One way of delivering a model's thinking to the client, named once so the proxy's per-model
/// "Thinking Handling" list and the extension's per-model "Reasoning" list describe the same
/// concept with the same words.
/// </summary>
/// <remarks>
/// The two products grew their own vocabularies for one axis: the proxy configures what it does to
/// the upstream output (<c>ThinkingMode</c>), the extension configures what it expects to receive
/// (<see cref="ReasoningSource"/>). Nothing is persisted here — the proxy enum and the extension's
/// <c>ModelEntry.ReasoningSource</c> strings remain the wire/storage format, so renaming a label
/// never invalidates a saved mapping or settings file.
/// </remarks>
public sealed class ReasoningConcept
{
    public ReasoningConcept(
        string label,
        string description,
        string? proxyThinkingMode = null,
        string? extensionReasoningSource = null,
        params string[] proxyThinkingModeAliases)
    {
        Label = label;
        Description = description;
        ProxyThinkingMode = proxyThinkingMode;
        ExtensionReasoningSource = extensionReasoningSource;
        ProxyThinkingModeAliases = proxyThinkingModeAliases;
    }

    /// <summary>Short label for a combo box or grid cell.</summary>
    public string Label { get; }

    /// <summary>One sentence explaining what the option does, for tooltips and help text.</summary>
    public string Description { get; }

    /// <summary>Name of the proxy's <c>ThinkingMode</c> member this describes, if any.</summary>
    public string? ProxyThinkingMode { get; }

    /// <summary>Name of the extension's <c>ReasoningSource</c> member this describes, if any.</summary>
    public string? ExtensionReasoningSource { get; }

    /// <summary>
    /// Other names for the same proxy mode. The proxy's enum carries aliases (<c>Off</c> for
    /// <c>LeaveInline</c>, <c>ExtractThinkTags</c> for <c>MoveToReasoningContent</c>) that a saved
    /// mapping file may still contain, and they must resolve to the same concept rather than to
    /// nothing.
    /// </summary>
    public IReadOnlyList<string> ProxyThinkingModeAliases { get; }
}

/// <summary>
/// The single naming table behind both GUIs' thinking/reasoning options (see
/// <see cref="ReasoningConcept"/>). Lookups are by member name rather than by enum value because
/// the proxy's <c>ThinkingMode</c> lives in an assembly this netstandard2.0 project cannot
/// reference.
/// </summary>
public static class ReasoningVocabulary
{
    /// <summary>Every concept, in the order the proxy's dialog presents them (extension-only
    /// options first).</summary>
    public static IReadOnlyList<ReasoningConcept> All { get; } = new[]
    {
        new ReasoningConcept(
            "Auto (structured field, else inline tags)",
            "Reasoning is read from the structured thinking field when the model sends one, and from inline tag blocks otherwise.",
            extensionReasoningSource: "Auto"),
        new ReasoningConcept(
            "Leave thinking in the visible answer",
            "Reasoning stays inline in content exactly as the model emitted it; the client may still split tags out itself.",
            proxyThinkingMode: "LeaveInline",
            extensionReasoningSource: "InlineTags",
            proxyThinkingModeAliases: "Off"),
        new ReasoningConcept(
            "Move thinking into reasoning_content (Qwen Cloud compatibility)",
            "The proxy extracts reasoning into the structured reasoning_content field so clients can render a collapsible thinking panel.",
            proxyThinkingMode: "MoveToReasoningContent",
            extensionReasoningSource: "ThinkingField",
            proxyThinkingModeAliases: "ExtractThinkTags"),
        new ReasoningConcept(
            "Remove thinking from client output (kept in logs)",
            "Reasoning is removed before the response reaches the client; captured logs still hold the original upstream body.",
            proxyThinkingMode: "StripFromOutput",
            extensionReasoningSource: "Disabled"),
        new ReasoningConcept(
            "Qwen Thinking Compatible ([Thinking] → reasoning_content, [Answer] → answer)",
            "Handles models that emit literal [Thinking] and [Answer] markers: the marked reasoning becomes reasoning_content and the rest the answer.",
            proxyThinkingMode: "QwenThinkingCompatible"),
    };

    /// <summary>Finds the concept for a proxy <c>ThinkingMode</c> member name.</summary>
    public static ReasoningConcept? ForProxyMode(string? thinkingMode)
        => Find(c => c.ProxyThinkingMode, thinkingMode, includeProxyAliases: true);

    /// <summary>Finds the concept for an extension <c>ReasoningSource</c> member name.</summary>
    public static ReasoningConcept? ForReasoningSource(string? reasoningSource)
        => Find(c => c.ExtensionReasoningSource, reasoningSource, includeProxyAliases: false);

    /// <summary>
    /// Label for a proxy <c>ThinkingMode</c> member name, falling back to the raw name so an
    /// unmapped mode still shows something selectable rather than a blank row.
    /// </summary>
    public static string LabelForProxyMode(string thinkingMode)
        => ForProxyMode(thinkingMode)?.Label ?? thinkingMode;

    /// <summary>
    /// Help text for a proxy <c>ThinkingMode</c>: "Label — description". Empty for unknown modes.
    /// </summary>
    public static string HelpForProxyMode(string thinkingMode)
    {
        ReasoningConcept? concept = ForProxyMode(thinkingMode);
        return concept is null ? string.Empty : concept.Label + " — " + concept.Description;
    }

    private static ReasoningConcept? Find(Func<ReasoningConcept, string?> selector, string? name, bool includeProxyAliases)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (ReasoningConcept concept in All)
        {
            if (string.Equals(selector(concept), name, StringComparison.Ordinal))
                return concept;
            if (includeProxyAliases)
            {
                foreach (string alias in concept.ProxyThinkingModeAliases)
                {
                    if (string.Equals(alias, name, StringComparison.Ordinal))
                        return concept;
                }
            }
        }

        return null;
    }
}