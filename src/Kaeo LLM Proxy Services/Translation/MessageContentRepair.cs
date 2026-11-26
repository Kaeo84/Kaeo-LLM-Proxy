using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Guarantees a <c>content</c> member on every message of an upstream-bound OpenAI-shaped body.
/// </summary>
/// <remarks>
/// OpenAI-shaped clients legitimately send <c>"content": null</c> — or omit the member entirely — on
/// an assistant turn that only carries <c>tool_calls</c>, and on the replay of such a turn. OpenAI
/// and llama.cpp accept that shape, but stricter providers reject it: Alibaba DashScope / Qwen Cloud
/// answers <c>{"error":{"code":"invalid_parameter_error","message":"The content field is a required
/// field."}}</c> with a 400, which surfaces to the client as a failed request that has nothing wrong
/// with it.
///
/// The repair only ever supplies an empty string where no usable content exists. Real content —
/// including multimodal part arrays — is never rewritten, and a body that already satisfies the
/// strict providers is left byte-for-byte untouched by the callers.
/// </remarks>
internal static class MessageContentRepair
{
    private const string ContentMember = "content";

    /// <summary>
    /// True when at least one message in <paramref name="messages"/> has no <c>content</c> member or
    /// carries a JSON null there.
    /// </summary>
    public static bool NeedsRepair(IReadOnlyList<JsonElement> messages)
    {
        foreach (JsonElement message in messages)
        {
            if (NeedsRepair(message))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the single message element has no <c>content</c> member or carries a JSON null
    /// there. Non-object elements are never repaired.
    /// </summary>
    public static bool NeedsRepair(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryFindContent(message, out JsonElement content))
            return true;

        return content.ValueKind == JsonValueKind.Null;
    }

    /// <summary>
    /// True when at least one message in <paramref name="messages"/> has no <c>content</c> member or
    /// carries a JSON null there.
    /// </summary>
    public static bool NeedsRepair(JsonArray messages)
    {
        foreach (JsonNode? node in messages)
        {
            if (node is not JsonObject message)
                continue;

            if (!TryFindContent(message, out JsonNode? content) || content is null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Sets <c>content</c> to the empty string on every message that has no usable content, leaving
    /// every other member (and every message that already has content) untouched. Returns the number
    /// of messages repaired so the caller can report the transformation.
    /// </summary>
    public static int Repair(JsonArray messages)
    {
        int repaired = 0;

        foreach (JsonNode? node in messages)
        {
            if (node is not JsonObject message)
                continue;

            if (TryFindContent(message, out JsonNode? content) && content is not null)
                continue;

            message[ContentMember] = string.Empty;
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// Writes one message element, substituting <c>""</c> for a null content and appending the member
    /// when it is absent. Every other member is emitted exactly as the client sent it, so the legacy
    /// writer pass keeps its byte fidelity for messages that need no repair.
    /// </summary>
    public static void WriteRepaired(Utf8JsonWriter writer, JsonElement message)
    {
        writer.WriteStartObject();

        bool wroteContent = false;
        foreach (JsonProperty prop in message.EnumerateObject())
        {
            if (prop.Name.Equals(ContentMember, StringComparison.OrdinalIgnoreCase))
            {
                wroteContent = true;
                if (prop.Value.ValueKind == JsonValueKind.Null)
                    writer.WriteString(ContentMember, string.Empty);
                else
                    prop.WriteTo(writer);
                continue;
            }

            prop.WriteTo(writer);
        }

        if (!wroteContent)
            writer.WriteString(ContentMember, string.Empty);

        writer.WriteEndObject();
    }

    private static bool TryFindContent(JsonElement message, out JsonElement content)
    {
        foreach (JsonProperty prop in message.EnumerateObject())
        {
            if (prop.Name.Equals(ContentMember, StringComparison.OrdinalIgnoreCase))
            {
                content = prop.Value;
                return true;
            }
        }

        content = default;
        return false;
    }

    private static bool TryFindContent(JsonObject message, out JsonNode? content)
    {
        foreach ((string name, JsonNode? value) in message)
        {
            if (name.Equals(ContentMember, StringComparison.OrdinalIgnoreCase))
            {
                content = value;
                return true;
            }
        }

        content = null;
        return false;
    }
}