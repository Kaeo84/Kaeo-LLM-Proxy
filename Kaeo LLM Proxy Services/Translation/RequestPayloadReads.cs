using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Nil-safe reads over a parsed request body, shared by the payload steps.
/// </summary>
/// <remarks>
/// These were private helpers inside <c>OllamaProxyHandler.NormalizeRequestBody</c>'s neighbourhood.
/// They live here now so the pipeline steps and the legacy method cannot drift apart on how a body
/// member is interpreted (notably that <c>stream_options</c> matching is case-insensitive, because a
/// client capitalizing the member would otherwise slip through unstripped).
/// </remarks>
internal static class RequestPayloadReads
{
    /// <summary>
    /// Extracts the first message's text content from a <c>messages</c> array. Content may be a plain
    /// string or an array of typed parts (e.g. <c>[{"type":"text","text":"..."}]</c>), which
    /// OpenAI-compatible clients such as Copilot commonly emit even for plain text.
    /// </summary>
    public static string? FirstMessageContent(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
            return null;

        foreach (JsonNode? messageNode in messages)
        {
            if (messageNode is not JsonObject message)
                return null;

            if (message is null || !message.ContainsKey("content"))
                return null;

            switch (message["content"])
            {
                case JsonValue value when value.TryGetValue(out string? text):
                    return text;
                case JsonArray parts:
                    System.Text.StringBuilder joined = new();
                    foreach (JsonNode? partNode in parts)
                    {
                        if (partNode is JsonObject part
                            && part["text"] is JsonValue partText
                            && partText.TryGetValue(out string? partString))
                        {
                            joined.Append(partString);
                        }
                    }
                    return joined.Length > 0 ? joined.ToString() : null;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether the body carries a top-level <c>stream_options</c> member and whether it sets
    /// <c>include_usage</c>.
    /// </summary>
    public static (bool Present, bool IncludeUsage) ReadStreamOptions(JsonObject root)
    {
        foreach ((string name, JsonNode? value) in root)
        {
            if (!name.Equals("stream_options", StringComparison.OrdinalIgnoreCase))
                continue;

            if (value is not JsonObject options)
                return (true, false);

            foreach ((string optionName, JsonNode? optionValue) in options)
            {
                if (optionName.Equals("include_usage", StringComparison.OrdinalIgnoreCase)
                    && optionValue is JsonValue flag
                    && flag.TryGetValue(out bool enabled)
                    && enabled)
                {
                    return (true, true);
                }
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    /// True when a message is an assistant turn with no tool activity, which several upstreams reject
    /// when thinking mode is on.
    /// </summary>
    public static bool IsAssistantResponsePrefill(JsonNode? messageNode)
    {
        if (messageNode is not JsonObject message)
            return false;

        if (message["role"] is not JsonValue roleValue
            || !roleValue.TryGetValue(out string? role)
            || !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message["tool_calls"] is JsonArray calls && calls.Count > 0)
            return false;

        if (message["tool_call_id"] is JsonValue idValue
            && idValue.TryGetValue(out string? id)
            && !string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Counts the leading system messages in a <c>messages</c> array, stopping at the first other role.
    /// </summary>
    public static int LeadingSystemCount(JsonArray messages)
    {
        int count = 0;
        while (count < messages.Count
            && messages[count] is JsonObject message
            && message["role"] is JsonValue roleValue
            && roleValue.TryGetValue(out string? role)
            && SystemPromptComposer.IsSystemRole(role))
        {
            count++;
        }

        return count;
    }

    /// <summary>Reads a top-level string member, matching the name case-insensitively.</summary>
    public static string? StringMember(JsonObject root, string name)
    {
        foreach ((string key, JsonNode? value) in root)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && value is JsonValue jsonValue
                && jsonValue.TryGetValue(out string? text))
            {
                return text;
            }
        }

        return null;
    }
}