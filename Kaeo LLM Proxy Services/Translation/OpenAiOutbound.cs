using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// IR outbound translation for the OpenAI-compatible upstream side: Microsoft.Extensions.AI
/// <see cref="ChatMessage"/> history to the strict OpenAI <c>messages</c> array shape used by
/// llama.cpp's server (assistant <c>tool_calls[].function.arguments</c> as a JSON *string*,
/// tool results as role <c>"tool"</c> correlated by <c>tool_call_id</c>, reasoning carried in
/// <c>reasoning_content</c>). This is the upstream-facing counterpart to the shared
/// <c>MeaiAdapter</c>'s Ollama-shaped writers.
/// </summary>
internal static class OpenAiOutbound
{
    public static JsonArray ToOpenAiMessages(IEnumerable<ChatMessage> messages)
    {
        JsonArray array = [];
        foreach (ChatMessage message in messages)
            array.Add(ToOpenAiMessage(message));
        return array;
    }

    public static JsonObject ToOpenAiMessage(ChatMessage message)
    {
        string role = string.IsNullOrEmpty(message.Role.Value) ? "user" : message.Role.Value;

        string content = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        string reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(t => t.Text));

        var json = new JsonObject { ["role"] = role, ["content"] = content };

        if (reasoning.Length > 0)
            json["reasoning_content"] = reasoning;

        List<FunctionCallContent> calls = [.. message.Contents.OfType<FunctionCallContent>()];
        if (calls.Count > 0)
        {
            JsonArray toolCalls = [];
            foreach (FunctionCallContent call in calls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.CallId ?? string.Empty,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        // OpenAI wire: arguments is a JSON-encoded string, not an object.
                        ["arguments"] = call.Arguments is { Count: > 0 }
                            ? JsonSerializer.Serialize(call.Arguments)
                            : "{}",
                    },
                });
            }

            json["tool_calls"] = toolCalls;
        }

        FunctionResultContent? result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (result is not null)
        {
            json["role"] = "tool";
            json["tool_call_id"] = result.CallId ?? string.Empty;
            json["content"] = result.Result?.ToString() ?? content;
        }

        return json;
    }
}
