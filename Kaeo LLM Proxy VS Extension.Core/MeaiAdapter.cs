using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Where reasoning (thinking) content is expected on the wire for a model. The proxy
/// normalizes most upstreams into the structured "thinking" field on /api/chat messages;
/// models served with inline thinking left in the answer text need tag extraction instead.
/// </summary>
public enum ReasoningSource
{
    /// <summary>Prefer the structured "thinking" field; fall back to inline tag blocks.</summary>
    Auto,

    /// <summary>Only the structured "thinking" field is treated as reasoning.</summary>
    ThinkingField,

    /// <summary>Only inline tag blocks within "content" are treated as reasoning.</summary>
    InlineTags,

    /// <summary>Never surface reasoning content.</summary>
    Disabled,
}

/// <summary>
/// Pure mapping helpers between the proxy's Ollama-style wire JSON and the
/// Microsoft.Extensions.AI object model (ChatMessage / ChatResponseUpdate with typed
/// content items). No I/O and no per-stream state (the one stateful piece,
/// <see cref="ThinkTagStreamSplitter"/>, is passed in by the caller), so everything here
/// is unit-testable without a server.
/// </summary>
public static class MeaiAdapter
{
    // Markup literals as escapes so this source file (like the test sources) never
    // contains raw angle-bracket tag sequences.
    public const string ThinkOpenTag = "\u003cthink\u003e";
    public const string ThinkCloseTag = "\u003c/think\u003e";

    /// <summary>Maps a conversation history into the Ollama /api/chat "messages" array.</summary>
    public static JsonArray ToOllamaMessagesJson(IEnumerable<ChatMessage> messages)
    {
        JsonArray array = [];
        foreach (ChatMessage message in messages)
            array.Add(ToOllamaMessageJson(message));
        return array;
    }

    /// <summary>
    /// Maps one ChatMessage to an Ollama chat message object: text items join into
    /// "content", reasoning items into "thinking", function calls into a "tool_calls"
    /// array, and a function result into a role:"tool" message with "tool_call_id".
    /// </summary>
    public static JsonObject ToOllamaMessageJson(ChatMessage message)
    {
        string role = string.IsNullOrEmpty(message.Role.Value) ? "user" : message.Role.Value;

        string content = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        string thinking = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(t => t.Text));

        JsonObject json = new() { ["role"] = role, ["content"] = content };

        if (thinking.Length > 0)
            json["thinking"] = thinking;

        List<FunctionCallContent> calls = [.. message.Contents.OfType<FunctionCallContent>()];
        if (calls.Count > 0)
        {
            JsonArray toolCalls = [];
            foreach (FunctionCallContent call in calls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = ToArgumentsNode(call.Arguments),
                    },
                });
            }

            json["tool_calls"] = toolCalls;
        }

        FunctionResultContent? result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (result is not null)
        {
            json["tool_call_id"] = result.CallId;
            json["content"] = result.Result?.ToString() ?? content;
        }

        return json;
    }

    /// <summary>
    /// Maps one NDJSON response line from the proxy's /api/chat stream to a MEAI
    /// ChatResponseUpdate: content -&gt; TextContent, thinking -&gt; TextReasoningContent
    /// (honoring the model's <see cref="ReasoningSource"/>), tool_calls -&gt;
    /// FunctionCallContent, done/done_reason -&gt; ChatFinishReason, eval counts -&gt;
    /// UsageContent. Returns null for lines that carry nothing renderable.
    /// </summary>
    public static ChatResponseUpdate? ToResponseUpdate(
        JsonElement root,
        ReasoningSource source,
        ThinkTagStreamSplitter? splitter)
    {
        List<AIContent> contents = [];
        bool hasThinkingField = false;

        if (root.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.Object)
        {
            string thinking = message.TryGetProperty("thinking", out JsonElement thinkingEl)
                ? thinkingEl.GetString() ?? string.Empty
                : string.Empty;
            hasThinkingField = thinking.Length > 0;
            if (hasThinkingField && source is ReasoningSource.Auto or ReasoningSource.ThinkingField)
                contents.Add(new TextReasoningContent(thinking));

            string text = message.TryGetProperty("content", out JsonElement contentEl)
                ? contentEl.GetString() ?? string.Empty
                : string.Empty;

            bool extractInline = source is ReasoningSource.Auto or ReasoningSource.InlineTags
                && splitter is not null
                && !(source == ReasoningSource.Auto && hasThinkingField);

            if (extractInline)
            {
                (string inlineReasoning, string visible) = splitter!.Process(text);
                if (inlineReasoning.Length > 0)
                    contents.Add(new TextReasoningContent(inlineReasoning));
                if (visible.Length > 0)
                    contents.Add(new TextContent(visible));
            }
            else if (text.Length > 0)
            {
                contents.Add(new TextContent(text));
            }

            if (message.TryGetProperty("tool_calls", out JsonElement toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in toolCalls.EnumerateArray())
                {
                    FunctionCallContent? fc = ToFunctionCall(call);
                    if (fc is not null)
                        contents.Add(fc);
                }
            }
        }

        bool done = root.TryGetProperty("done", out JsonElement doneEl)
            && doneEl.ValueKind == JsonValueKind.True;

        ChatFinishReason? finishReason = null;
        if (done)
        {
            string? doneReason = root.TryGetProperty("done_reason", out JsonElement reasonEl)
                ? reasonEl.GetString()
                : null;
            finishReason = doneReason switch
            {
                null or "" or "stop" => ChatFinishReason.Stop,
                "length" => ChatFinishReason.Length,
                "tool_calls" => ChatFinishReason.ToolCalls,
                "content_filter" => ChatFinishReason.ContentFilter,
                _ => new ChatFinishReason(doneReason),
            };
        }

        if (root.TryGetProperty("prompt_eval_count", out JsonElement promptEl)
            && root.TryGetProperty("eval_count", out JsonElement completionEl)
            && promptEl.TryGetInt32(out int promptTokens)
            && completionEl.TryGetInt32(out int completionTokens)
            && (promptTokens > 0 || completionTokens > 0))
        {
            contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = promptTokens,
                OutputTokenCount = completionTokens,
            }));
        }

        if (contents.Count == 0 && finishReason is null)
            return null;

        DateTimeOffset? createdAt = null;
        if (root.TryGetProperty("created_at", out JsonElement createdEl)
            && createdEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(createdEl.GetString(), out DateTimeOffset parsed))
        {
            createdAt = parsed;
        }

        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = contents,
            FinishReason = finishReason,
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// Builds a complete ChatResponse from collected streaming updates: assistant items
    /// are merged into one message, the terminal finish reason and usage are kept.
    /// </summary>
    public static ChatResponse ToChatResponse(IReadOnlyList<ChatResponseUpdate> updates)
    {
        List<AIContent> merged = [];
        foreach (ChatResponseUpdate update in updates)
            merged.AddRange(update.Contents);

        ChatMessage message = new(ChatRole.Assistant, merged);
        return new ChatResponse(message)
        {
            FinishReason = updates.LastOrDefault(u => u.FinishReason is not null)?.FinishReason,
            Usage = updates.Reverse().SelectMany(u => u.Contents).OfType<UsageContent>().FirstOrDefault()?.Details,
            ResponseId = updates.FirstOrDefault(u => u.ResponseId is not null)?.ResponseId,
            CreatedAt = updates.FirstOrDefault(u => u.CreatedAt is not null)?.CreatedAt,
        };
    }

    /// <summary>Projects MEAI tools (e.g. MCP tools wrapped as AIFunctions) onto the wire format.</summary>
    public static JsonArray? ToOllamaToolsJson(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
            return null;

        JsonArray array = [];
        foreach (AITool tool in tools)
        {
            if (tool is not AIFunction function)
                continue;

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText()),
                },
            });
        }

        return array.Count > 0 ? array : null;
    }

    private static FunctionCallContent? ToFunctionCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object
            || !call.TryGetProperty("function", out JsonElement fn)
            || fn.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string name = fn.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrEmpty(name))
            return null;

        string id = call.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrEmpty(id))
            id = Guid.NewGuid().ToString("N");

        Dictionary<string, object?>? arguments = null;
        if (fn.TryGetProperty("arguments", out JsonElement argsEl))
        {
            if (argsEl.ValueKind == JsonValueKind.String)
            {
                // Ollama may deliver arguments as a JSON-encoded string.
                try
                {
                    using JsonDocument reparsed = JsonDocument.Parse(argsEl.GetString() ?? "{}");
                    arguments = ToArgumentsDictionary(reparsed.RootElement);
                }
                catch (JsonException)
                {
                    arguments = null;
                }
            }
            else
            {
                arguments = ToArgumentsDictionary(argsEl);
            }
        }

        return new FunctionCallContent(id, name, arguments ?? new Dictionary<string, object?>());
    }

    private static Dictionary<string, object?>? ToArgumentsDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        Dictionary<string, object?> args = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            args[property.Name] = ToClrValue(property.Value);
        return args;
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : (object)element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClrValue).ToList(),
        JsonValueKind.Object => ToArgumentsDictionary(element),
        _ => element.GetRawText(),
    };

    private static JsonNode? ToArgumentsNode(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return new JsonObject();

        string json = JsonSerializer.Serialize(arguments);
        return JsonNode.Parse(json) ?? new JsonObject();
    }
}

/// <summary>
/// Stateful per-stream splitter that lifts inline
/// &lt;think&gt;...&lt;/think&gt; (or any configured tag pair) blocks out of a token
/// stream, emitting reasoning and visible text separately. Tags split across chunk
/// boundaries are buffered until the next token, mirroring the proxy's
/// ThinkTagExtractor behavior for clients that connect with thinking left inline.
/// </summary>
public sealed class ThinkTagStreamSplitter
{
    private readonly string _openTag;
    private readonly string _closeTag;
    private readonly StringBuilder _pending = new();
    private bool _inTag;

    public ThinkTagStreamSplitter(string openTag = MeaiAdapter.ThinkOpenTag, string closeTag = MeaiAdapter.ThinkCloseTag)
    {
        _openTag = openTag;
        _closeTag = closeTag;
    }

    /// <summary>Feeds one streamed token; returns the reasoning and visible parts.</summary>
    public (string Reasoning, string Content) Process(string token)
    {
        if (string.IsNullOrEmpty(token))
            return (string.Empty, string.Empty);

        StringBuilder reasoning = new();
        StringBuilder content = new();

        _pending.Append(token);
        string work = _pending.ToString();
        int pos = 0;

        while (pos < work.Length)
        {
            string activeTag = _inTag ? _closeTag : _openTag;
            int tagIndex = work.IndexOf(activeTag, pos, StringComparison.OrdinalIgnoreCase);

            if (tagIndex < 0)
            {
                int hold = TrailingTagPrefixLength(work, pos, activeTag);
                int emitEnd = work.Length - hold;
                if (emitEnd > pos)
                    Append(_inTag ? reasoning : content, work.Substring(pos, emitEnd - pos));
                _pending.Clear();
                if (hold > 0)
                    _pending.Append(work, emitEnd, hold);
                return (reasoning.ToString(), content.ToString());
            }

            if (tagIndex > pos)
                Append(_inTag ? reasoning : content, work.Substring(pos, tagIndex - pos));

            _inTag = !_inTag;
            pos = tagIndex + activeTag.Length;
        }

        _pending.Clear();
        return (reasoning.ToString(), content.ToString());
    }

    /// <summary>
    /// Drains buffered text at end of stream. Text left while still inside an
    /// unterminated thinking block is treated as reasoning.
    /// </summary>
    public (string Reasoning, string Content) Flush()
    {
        string remaining = _pending.ToString();
        _pending.Clear();
        if (remaining.Length == 0)
            return (string.Empty, string.Empty);
        return _inTag ? (remaining, string.Empty) : (string.Empty, remaining);
    }

    private static void Append(StringBuilder target, string text)
    {
        if (text.Length > 0)
            target.Append(text);
    }

    /// <summary>
    /// Number of trailing characters that form a proper prefix of the active tag -
    /// held back rather than emitted so a tag split across chunks is still detected.
    /// </summary>
    private static int TrailingTagPrefixLength(string work, int start, string tag)
    {
        int max = Math.Min(tag.Length - 1, work.Length - start);
        for (int len = max; len > 0; len--)
        {
            bool match = true;
            for (int i = 0; i < len; i++)
            {
                if (char.ToLowerInvariant(work[work.Length - len + i]) != char.ToLowerInvariant(tag[i]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return len;
        }

        return 0;
    }
}
