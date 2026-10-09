using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Single source of truth for composing the system prompt an upstream receives.
/// </summary>
/// <remarks>
/// The canonical rule, applied identically on the <c>/v1/*</c> passthrough, <c>/api/chat</c>, and
/// <c>/api/generate</c> paths: the configured instruction-set text comes first, followed by each of
/// the client's <em>leading</em> system messages in order, all joined by a blank line and emitted as
/// exactly one system message. Strict chat templates (Qwen3, which every seeded mapping targets)
/// allow only a single system message, so a second one is rejected upstream rather than ignored.
/// <para>
/// Only leading system messages participate. A system message appearing later in the conversation is
/// deliberate client content and is always forwarded untouched.
/// </para>
/// </remarks>
internal static class SystemPromptComposer
{
    /// <summary>Separator placed between the instruction text and each merged system message.</summary>
    public const string Separator = "\n\n";

    /// <summary>True when <paramref name="role"/> names the system role, matching case-insensitively.</summary>
    public static bool IsSystemRole(string? role) =>
        role?.Equals("system", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// True when the message list has to be rewritten: either instruction text is being injected, or
    /// the client sent more than one leading system message that has to be folded into one. When
    /// false, a lone client system message is forwarded verbatim so its other properties survive.
    /// </summary>
    public static bool ShouldRecompose(string? instructions, int leadingSystemCount) =>
        !string.IsNullOrWhiteSpace(instructions) || leadingSystemCount > 1;

    /// <summary>
    /// Merges the instruction text with the client's leading system messages into one system prompt.
    /// Parts are preserved verbatim and joined by <see cref="Separator"/>. Returns an empty string
    /// when there is nothing to merge.
    /// </summary>
    public static string Merge(string? instructions, IReadOnlyList<string> leadingSystemContents)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(instructions))
            parts.Add(instructions);

        parts.AddRange(leadingSystemContents);

        return parts.Count == 0 ? string.Empty : string.Join(Separator, parts);
    }

    /// <summary>Counts the leading messages whose role is system, stopping at the first other role.</summary>
    public static int LeadingSystemCount(IReadOnlyList<LlamaCppMessage> messages)
    {
        int count = 0;
        while (count < messages.Count && IsSystemRole(messages[count].Role))
            count++;

        return count;
    }

    /// <summary>
    /// Returns the content of the first <paramref name="count"/> leading system messages, in order.
    /// </summary>
    public static List<string> LeadingSystemContents(IReadOnlyList<LlamaCppMessage> messages, int count)
    {
        List<string> contents = [];
        for (int i = 0; i < count && i < messages.Count; i++)
            contents.Add(messages[i].Content ?? string.Empty);

        return contents;
    }

    /// <summary>Counts the leading JSON messages whose role is system, stopping at the first other role.</summary>
    public static int LeadingSystemCount(IReadOnlyList<JsonElement> messages)
    {
        int count = 0;
        while (count < messages.Count && IsSystemRole(ReadRole(messages[count])))
            count++;

        return count;
    }

    /// <summary>
    /// Returns the content of the first <paramref name="count"/> leading JSON system messages, in
    /// order. Non-string content is taken verbatim so structured parts are not silently dropped.
    /// </summary>
    public static List<string> LeadingSystemContents(IReadOnlyList<JsonElement> messages, int count)
    {
        List<string> contents = [];

        for (int i = 0; i < count && i < messages.Count; i++)
        {
            if (!messages[i].TryGetProperty("content", out JsonElement content))
            {
                contents.Add(string.Empty);
                continue;
            }

            contents.Add(content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : content.GetRawText());
        }

        return contents;
    }

    private static string? ReadRole(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("role", out JsonElement role)
        && role.ValueKind == JsonValueKind.String
            ? role.GetString()
            : null;
}
