using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// IR inbound translation for the OpenAI-compatible upstream side: SSE chunk objects
/// (as emitted by llama.cpp and other OpenAI-flavored servers) to Microsoft.Extensions.AI
/// <see cref="ChatResponseUpdate"/> values. Reasoning arrives as
/// <c>reasoning_content</c> and becomes <see cref="TextReasoningContent"/>; tool-call
/// argument fragments stream across chunks keyed by their call index and surface as
/// completed <see cref="FunctionCallContent"/> items on the terminal chunk, mirroring
/// how OpenAI streaming encodes them.
/// </summary>
internal static class OpenAiInbound
{
    /// <summary>Per-stream accumulator so a call's fragments join across chunks.</summary>
    public sealed class StreamState
    {
        // Keyed by (choice index, tool-call index): id, name and streamed argument text.
        internal readonly Dictionary<(int Choice, int Call), (string Id, string Name, StringBuilder Arguments)> ToolCalls = [];
    }

    /// <summary>
    /// Converts one parsed upstream chunk into an update. Returns null when the chunk
    /// carries nothing emittable (e.g. pure argument fragments accumulate silently until
    /// the terminal chunk flushes them).
    /// </summary>
    public static ChatResponseUpdate? ToUpdate(JsonObject? chunk, StreamState state)
    {
        if (chunk is null || chunk["choices"] is not JsonArray choices)
            return null;

        List<AIContent> contents = [];
        ChatFinishReason? finishReason = null;

        foreach (JsonNode? choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
                continue;

            int choiceIndex = choice["index"] is JsonValue civ && civ.TryGetValue(out int ci) ? ci : 0;

            if (choice["delta"] is JsonObject delta)
            {
                if (delta["content"] is JsonValue textValue
                    && textValue.TryGetValue(out string? text) && !string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent(text));
                }

                if (delta["reasoning_content"] is JsonValue reasoningValue
                    && reasoningValue.TryGetValue(out string? reasoning) && !string.IsNullOrEmpty(reasoning))
                {
                    contents.Add(new TextReasoningContent(reasoning));
                }

                if (delta["tool_calls"] is JsonArray callFrags)
                {
                    foreach (JsonNode? fragNode in callFrags)
                    {
                        if (fragNode is not JsonObject frag)
                            continue;

                        int callIndex = frag["index"] is JsonValue iiv && iiv.TryGetValue(out int ix) ? ix : 0;
                        var key = (choiceIndex, callIndex);
                        if (!state.ToolCalls.TryGetValue(key, out var entry))
                        {
                            entry = (string.Empty, string.Empty, new StringBuilder());
                            state.ToolCalls[key] = entry;
                        }

                        if (frag["id"] is JsonValue idv && idv.TryGetValue(out string? id) && !string.IsNullOrEmpty(id))
                            entry.Id = id;

                        if (frag["function"] is JsonObject fn)
                        {
                            if (fn["name"] is JsonValue nmv && nmv.TryGetValue(out string? nm) && !string.IsNullOrEmpty(nm))
                                entry.Name = nm;
                            if (fn["arguments"] is JsonValue arv && arv.TryGetValue(out string? ar))
                                entry.Arguments.Append(ar);
                        }

                        state.ToolCalls[key] = entry;
                    }
                }
            }

            if (choice["finish_reason"] is JsonValue frValue
                && frValue.TryGetValue(out string? fr) && !string.IsNullOrEmpty(fr))
            {
                finishReason = fr switch
                {
                    "stop" => ChatFinishReason.Stop,
                    "length" => ChatFinishReason.Length,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    _ => new ChatFinishReason(fr),
                };

                // Terminal chunk for this choice: flush its accumulated tool calls.
                if (fr == "tool_calls")
                {
                    foreach (var kv in state.ToolCalls)
                    {
                        if (kv.Key.Choice != choiceIndex)
                            continue;

                        var (id, name, args) = kv.Value;
                        if (string.IsNullOrEmpty(name))
                            continue;

                        contents.Add(new FunctionCallContent(
                            string.IsNullOrEmpty(id) ? kv.Key.Call.ToString() : id,
                            name,
                            ParseArguments(args.ToString())));
                    }
                }
            }
        }

        if (contents.Count == 0 && finishReason is null)
            return null;

        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = contents,
            FinishReason = finishReason,
        };
    }

    private static IDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new Dictionary<string, object?>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new Dictionary<string, object?>();

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
                result[property.Name] = ToClrValue(property.Value);
            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? ToClrValue(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out long l) ? l : (object)element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };
}
