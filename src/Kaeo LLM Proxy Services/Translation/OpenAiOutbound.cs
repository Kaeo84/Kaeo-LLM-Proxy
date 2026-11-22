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
        {
            // OpenAI's wire shape carries one tool message per function result. A ChatMessage
            // batching several results must fan out — ToOpenAiMessage's single-message shape
            // keeps only the first, which would silently drop the rest.
            List<FunctionResultContent> results = [.. message.Contents.OfType<FunctionResultContent>()];
            if (results.Count > 1)
            {
                string text = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
                foreach (FunctionResultContent result in results)
                {
                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId ?? string.Empty,
                        ["content"] = result.Result?.ToString() ?? text,
                    });
                }

                continue;
            }

            array.Add(ToOpenAiMessage(message));
        }

        return array;
    }

    public static JsonObject ToOpenAiMessage(ChatMessage message)
    {
        string role = string.IsNullOrEmpty(message.Role.Value) ? "user" : message.Role.Value;

        string content = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
        string reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(t => t.Text));

        var json = new JsonObject { ["role"] = role, ["content"] = content };

        // Vision: image content becomes OpenAI content parts; plain-text messages keep the
        // string-content shape for back-compat with non-vision upstreams.
        List<DataContent> images = [.. message.Contents.OfType<DataContent>()
            .Where(d => d.HasTopLevelMediaType("image"))];
        if (images.Count > 0)
        {
            JsonArray parts = [];
            if (content.Length > 0)
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = content });

            foreach (DataContent image in images)
            {
                string? url = image.Uri;
                if (string.IsNullOrEmpty(url) && !image.Data.IsEmpty)
                    url = $"data:{image.MediaType ?? "image/png"};base64,{Convert.ToBase64String(image.Data.ToArray())}";

                if (!string.IsNullOrEmpty(url))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = url },
                    });
                }
            }

            json["content"] = parts;
        }

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
            // A message carrying a function result IS a tool message on the OpenAI wire, so the
            // role/content built above are deliberately overwritten (the text survives only as
            // the fallback when Result is null). Batched results are fanned out by
            // ToOpenAiMessages before this single-result shape is ever reached.
            json["role"] = "tool";
            json["tool_call_id"] = result.CallId ?? string.Empty;
            json["content"] = result.Result?.ToString() ?? content;
        }

        return json;
    }
}
