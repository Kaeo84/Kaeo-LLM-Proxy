using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Phase-B IR translation for the /api/chat request side: Ollama wire messages to the
/// Microsoft.Extensions.AI <see cref="ChatMessage"/> representation and back to the
/// OpenAI/llama.cpp request shape. The outbound mapping reproduces the legacy
/// <c>MapMessagesWithToolCorrelation</c> behavior exactly - stable tool-call ids
/// (preserved when supplied, 8-char hex generated otherwise) and FIFO correlation of
/// unlabelled role:"tool" replies to their pending assistant calls - so the IR route is
/// a drop-in behind the <c>UseIrTranslation</c> flag. Parity is structural (semantic
/// JSON): string-encoded call arguments are re-canonicalized through the dictionary.
/// Like the legacy mapper, request-side reasoning is dropped (llama.cpp's chat request
/// format has no assistant reasoning field).
/// </summary>
internal static class OllamaRequestTranslation
{
    /// <summary>IR inbound: Ollama request messages to ChatMessages with typed contents.</summary>
    public static List<ChatMessage> ToChatMessages(List<OllamaMessage> source)
    {
        List<ChatMessage> result = [];
        foreach (OllamaMessage m in source)
        {
            List<AIContent> contents = [];
            string role = m.Role ?? string.Empty;

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                // Tool replies carry correlation id + result text. Empty/missing ids collapse
                // to "" and are restored to null on the outbound leg (legacy treats blank
                // tool_call_id as "needs correlation" identically).
                contents.Add(new FunctionResultContent(m.ToolCallId ?? string.Empty, m.Content));
            }
            else
            {
                if (m.Content is not null)
                    contents.Add(new TextContent(m.Content));
                if (!string.IsNullOrEmpty(m.Thinking))
                    contents.Add(new TextReasoningContent(m.Thinking));

                if (m.ToolCalls is { Count: > 0 })
                {
                    foreach (OllamaToolCall tc in m.ToolCalls)
                    {
                        contents.Add(new FunctionCallContent(
                            string.IsNullOrWhiteSpace(tc.Id) ? Guid.NewGuid().ToString("N")[..8] : tc.Id!,
                            tc.Function?.Name ?? string.Empty,
                            ToArgumentsDictionary(tc.Function?.Arguments)));
                    }
                }
            }

            result.Add(new ChatMessage(new ChatRole(role), contents));
        }

        return result;
    }

    /// <summary>
    /// Full request-side route: Ollama messages through the IR and back to the upstream
    /// shape, with the legacy tool-call id correlation applied on the outbound leg.
    /// </summary>
    public static List<LlamaCppMessage> ToLlamaCppMessages(List<OllamaMessage> source)
    {
        List<ChatMessage> ir = ToChatMessages(source);
        List<LlamaCppMessage> mapped = [];
        Queue<string> pending = new();

        foreach (ChatMessage message in ir)
        {
            List<FunctionCallContent> calls = [.. message.Contents.OfType<FunctionCallContent>()];
            FunctionResultContent? result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            List<TextContent> texts = [.. message.Contents.OfType<TextContent>()];

            // Null-vs-empty content fidelity: the legacy mapper passes OllamaMessage.Content
            // straight through, so a missing TextContent item maps back to null, and a tool
            // reply carries its text in the result item.
            LlamaCppMessage mappedMsg = new(message.Role.Value, null)
            {
                ToolCallId = string.IsNullOrEmpty(result?.CallId) ? null : result!.CallId,
                Content = result is not null
                    ? result.Result as string
                    : texts.Count > 0 ? string.Concat(texts.Select(t => t.Text ?? string.Empty)) : null,
            };

            if (calls.Count > 0)
            {
                mappedMsg.ToolCalls = [.. calls.Select(c => new LlamaCppToolCall
                {
                    Id = c.CallId ?? string.Empty,
                    Function = new LlamaCppToolCallFunction
                    {
                        Name = c.Name,
                        Arguments = c.Arguments is null ? null : JsonSerializer.Serialize(c.Arguments),
                    },
                })];
            }

            if (string.Equals(message.Role.Value, "assistant", StringComparison.OrdinalIgnoreCase) && calls.Count > 0)
            {
                foreach (FunctionCallContent c in calls)
                    pending.Enqueue(c.CallId ?? string.Empty);
            }
            else if (string.Equals(message.Role.Value, "tool", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(mappedMsg.ToolCallId) && pending.Count > 0)
            {
                mappedMsg.ToolCallId = pending.Dequeue();
            }

            mapped.Add(mappedMsg);
        }

        return mapped;
    }

    /// <summary>
    /// Wire arguments arrive as a JSON element (or, from hand-built payloads, a
    /// JSON-encoded string). Parse to the MEAI dictionary shape; non-object payloads
    /// yield null (treated as "no arguments").
    /// </summary>
    private static IDictionary<string, object?>? ToArgumentsDictionary(object? arguments) => arguments switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Object } je => ToDictionary(je),
        JsonElement { ValueKind: JsonValueKind.String } jstr => ParseArgs(jstr.GetString()),
        JsonNode node => ParseArgs(node.ToJsonString()),
        string s => ParseArgs(s),
        _ => ParseArgs(JsonSerializer.Serialize(arguments)),
    };

    private static IDictionary<string, object?>? ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ToDictionary(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        Dictionary<string, object?> dict = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            dict[property.Name] = ToClrValue(property.Value);
        return dict;
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToList(),
        JsonValueKind.Object => ToDictionary(element),
        _ => element.GetRawText(),
    };
}
