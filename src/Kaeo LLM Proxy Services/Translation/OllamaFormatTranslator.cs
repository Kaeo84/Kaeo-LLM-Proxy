using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Pure translation between the OpenAI Chat Completions wire format and the Ollama
/// /api/chat wire format (requests, non-streaming responses, and streaming NDJSON chunks).
/// All methods are null/missing-field tolerant: malformed input yields a best-effort result
/// rather than an exception. No SSE framing is produced; callers add "data:" prefixes and
/// the final [DONE] marker themselves.
/// </summary>
internal static class OllamaFormatTranslator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string OpenAiRequestToOllama(string openAiJson, string upstreamModel, bool stream)
    {
        JsonObject root = TryParseObject(openAiJson) ?? [];

        JsonObject result = new()
        {
            ["model"] = upstreamModel,
            ["stream"] = stream,
        };

        JsonArray messages = [];
        if (root["messages"] is JsonArray openAiMessages)
        {
            foreach (JsonNode? node in openAiMessages)
            {
                if (node is JsonObject message)
                {
                    messages.Add(TranslateRequestMessage(message));
                }
            }
        }
        result["messages"] = messages;

        if (root["tools"] is JsonArray { Count: > 0 } tools)
        {
            result["tools"] = tools.DeepClone();
        }

        JsonObject options = BuildOptions(root);
        if (options.Count > 0)
        {
            result["options"] = options;
        }

        string? responseFormatType = GetString(root["response_format"]?["type"]);
        if (responseFormatType is "json_object" or "json_schema")
        {
            result["format"] = "json";
        }

        if (root["keep_alive"] is JsonNode keepAlive)
        {
            result["keep_alive"] = keepAlive.DeepClone();
        }

        return result.ToJsonString(SerializerOptions);
    }

    public static string OllamaResponseToOpenAi(string ollamaJson, string clientModel, string completionId)
    {
        JsonObject root = TryParseObject(ollamaJson) ?? [];
        JsonObject? ollamaMessage = root["message"] as JsonObject;

        JsonObject message = new() { ["role"] = "assistant" };

        string? content = GetString(ollamaMessage?["content"]);
        if (content is not null)
        {
            message["content"] = content;
        }

        string? thinking = GetString(ollamaMessage?["thinking"]);
        if (!string.IsNullOrEmpty(thinking))
        {
            message["reasoning_content"] = thinking;
        }

        JsonArray? toolCalls = TranslateOllamaToolCallsToOpenAi(ollamaMessage?["tool_calls"] as JsonArray);
        if (toolCalls is { Count: > 0 })
        {
            message["tool_calls"] = toolCalls;
        }

        string finishReason = toolCalls is { Count: > 0 }
            ? "tool_calls"
            : MapFinishReason(GetString(root["done_reason"]), toolCallsEmitted: false);

        int promptTokens = GetInt(root["prompt_eval_count"]) ?? 0;
        int completionTokens = GetInt(root["eval_count"]) ?? 0;

        JsonObject result = new()
        {
            ["id"] = completionId,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = clientModel,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = finishReason,
                },
            },
            ["usage"] = BuildUsage(promptTokens, completionTokens),
        };

        return result.ToJsonString(SerializerOptions);
    }

    public static OllamaToOpenAiStreamState CreateOllamaToOpenAiState(string clientModel, string completionId)
        => new(clientModel, completionId);

    public static IReadOnlyList<string> OllamaChunkToOpenAiChunks(OllamaToOpenAiStreamState state, string ollamaNdjsonLine)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(ollamaNdjsonLine))
        {
            return [];
        }

        JsonObject? root = TryParseObject(ollamaNdjsonLine);
        if (root is null)
        {
            return [];
        }

        JsonObject? message = root["message"] as JsonObject;
        string? content = GetString(message?["content"]);
        string? thinking = GetString(message?["thinking"]);
        JsonArray? toolCalls = message?["tool_calls"] as JsonArray;
        bool done = GetBool(root["done"]);
        bool emitFinish = done && !state.Finished;

        bool hasPayload = !string.IsNullOrEmpty(content)
            || !string.IsNullOrEmpty(thinking)
            || toolCalls is { Count: > 0 }
            || emitFinish;
        if (!hasPayload)
        {
            return [];
        }

        List<string> chunks = [];

        if (!state.RoleSent)
        {
            state.RoleSent = true;
            chunks.Add(BuildChunk(state, new JsonObject { ["role"] = "assistant" }, finishReason: null));
        }

        if (!string.IsNullOrEmpty(content))
        {
            chunks.Add(BuildChunk(state, new JsonObject { ["content"] = content }, finishReason: null));
        }

        if (!string.IsNullOrEmpty(thinking))
        {
            chunks.Add(BuildChunk(state, new JsonObject { ["reasoning_content"] = thinking }, finishReason: null));
        }

        if (toolCalls is { Count: > 0 })
        {
            foreach (JsonNode? node in toolCalls)
            {
                if (node is not JsonObject toolCall)
                {
                    continue;
                }

                JsonObject function = toolCall["function"] as JsonObject ?? [];
                JsonObject deltaToolCall = new()
                {
                    ["index"] = state.NextToolCallIndex++,
                    ["id"] = GetString(toolCall["id"]) ?? GenerateToolCallId(),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = GetString(function["name"]) ?? string.Empty,
                        ["arguments"] = StringifyArguments(function["arguments"]),
                    },
                };

                chunks.Add(BuildChunk(state, new JsonObject { ["tool_calls"] = new JsonArray { deltaToolCall } }, finishReason: null));
            }
        }

        if (emitFinish)
        {
            state.Finished = true;
            string finishReason = MapFinishReason(GetString(root["done_reason"]), state.NextToolCallIndex > 0);
            chunks.Add(BuildChunk(state, [], finishReason));

            int? promptTokens = GetInt(root["prompt_eval_count"]);
            int? completionTokens = GetInt(root["eval_count"]);
            if (promptTokens.HasValue || completionTokens.HasValue)
            {
                JsonObject usageChunk = new()
                {
                    ["id"] = state.CompletionId,
                    ["object"] = "chat.completion.chunk",
                    ["created"] = state.Created,
                    ["model"] = state.ClientModel,
                    ["choices"] = new JsonArray(),
                    ["usage"] = BuildUsage(promptTokens ?? 0, completionTokens ?? 0),
                };
                chunks.Add(usageChunk.ToJsonString(SerializerOptions));
            }
        }

        return chunks;
    }

    private static JsonObject TranslateRequestMessage(JsonObject openAiMessage)
    {
        string role = GetString(openAiMessage["role"]) ?? "user";
        JsonObject message = new() { ["role"] = role };

        JsonNode? contentNode = openAiMessage["content"];
        bool hasToolCalls = openAiMessage["tool_calls"] is JsonArray { Count: > 0 };
        if (contentNode is JsonArray parts)
        {
            StringBuilder text = new();
            JsonArray images = [];
            foreach (JsonNode? partNode in parts)
            {
                if (partNode is not JsonObject part)
                {
                    continue;
                }

                string? partType = GetString(part["type"]);
                if (partType == "text")
                {
                    text.Append(GetString(part["text"]) ?? string.Empty);
                }
                else if (partType == "image_url")
                {
                    string? url = GetString(part["image_url"]?["url"]) ?? GetString(part["image_url"]);
                    if (!string.IsNullOrEmpty(url))
                    {
                        images.Add(StripBase64DataPrefix(url));
                    }
                }
            }

            message["content"] = text.ToString();
            if (images.Count > 0)
            {
                message["images"] = images;
            }
        }
        else if (GetString(contentNode) is string contentText)
        {
            message["content"] = contentText;
        }
        else if (role == "assistant" && hasToolCalls)
        {
            message["content"] = string.Empty;
        }

        if (hasToolCalls)
        {
            JsonArray toolCalls = [];
            foreach (JsonNode? node in (JsonArray)openAiMessage["tool_calls"]!)
            {
                if (node is not JsonObject openAiToolCall)
                {
                    continue;
                }

                JsonObject function = openAiToolCall["function"] as JsonObject ?? [];
                JsonObject ollamaToolCall = new()
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = GetString(function["name"]) ?? string.Empty,
                        ["arguments"] = TryParseObject(GetString(function["arguments"])) ?? [],
                    },
                };

                if (GetString(openAiToolCall["id"]) is string id)
                {
                    ollamaToolCall["id"] = id;
                }

                toolCalls.Add(ollamaToolCall);
            }

            if (toolCalls.Count > 0)
            {
                message["tool_calls"] = toolCalls;
            }
        }

        if (GetString(openAiMessage["tool_call_id"]) is string toolCallId)
        {
            message["tool_call_id"] = toolCallId;
        }

        if (GetString(openAiMessage["name"]) is string name)
        {
            message["tool_name"] = name;
        }

        return message;
    }

    private static JsonObject BuildOptions(JsonObject root)
    {
        JsonObject options = [];

        foreach (string field in (string[])["temperature", "top_p", "seed", "frequency_penalty", "presence_penalty", "repeat_penalty", "top_k"])
        {
            if (root[field] is JsonNode value)
            {
                options[field] = value.DeepClone();
            }
        }

        if ((root["max_tokens"] ?? root["max_completion_tokens"]) is JsonNode maxTokens)
        {
            options["num_predict"] = maxTokens.DeepClone();
        }

        JsonNode? stop = root["stop"];
        if (GetString(stop) is string stopText)
        {
            options["stop"] = new JsonArray { stopText };
        }
        else if (stop is JsonArray { Count: > 0 } stopArray)
        {
            options["stop"] = stopArray.DeepClone();
        }

        return options;
    }

    private static JsonArray? TranslateOllamaToolCallsToOpenAi(JsonArray? ollamaToolCalls)
    {
        if (ollamaToolCalls is not { Count: > 0 })
        {
            return null;
        }

        JsonArray toolCalls = [];
        foreach (JsonNode? node in ollamaToolCalls)
        {
            if (node is not JsonObject ollamaToolCall)
            {
                continue;
            }

            JsonObject function = ollamaToolCall["function"] as JsonObject ?? [];
            toolCalls.Add(new JsonObject
            {
                ["id"] = GetString(ollamaToolCall["id"]) ?? GenerateToolCallId(),
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = GetString(function["name"]) ?? string.Empty,
                    ["arguments"] = StringifyArguments(function["arguments"]),
                },
            });
        }

        return toolCalls.Count > 0 ? toolCalls : null;
    }

    private static string BuildChunk(OllamaToOpenAiStreamState state, JsonObject delta, string? finishReason)
    {
        JsonObject choice = new()
        {
            ["index"] = 0,
            ["delta"] = delta,
        };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        JsonObject chunk = new()
        {
            ["id"] = state.CompletionId,
            ["object"] = "chat.completion.chunk",
            ["created"] = state.Created,
            ["model"] = state.ClientModel,
            ["choices"] = new JsonArray { choice },
        };
        return chunk.ToJsonString(SerializerOptions);
    }

    private static JsonObject BuildUsage(int promptTokens, int completionTokens) => new()
    {
        ["prompt_tokens"] = promptTokens,
        ["completion_tokens"] = completionTokens,
        ["total_tokens"] = promptTokens + completionTokens,
    };

    private static string MapFinishReason(string? doneReason, bool toolCallsEmitted)
    {
        if (toolCallsEmitted || doneReason == "tool_calls")
        {
            return "tool_calls";
        }
        return doneReason == "length" ? "length" : "stop";
    }

    private static string StringifyArguments(JsonNode? arguments)
        => arguments?.ToJsonString() ?? "{}";

    private static string GenerateToolCallId()
        => "call_" + Guid.NewGuid().ToString("N")[..24];

    private static string StripBase64DataPrefix(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int marker = url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                return url[(marker + ";base64,".Length)..];
            }
        }
        return url;
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static bool GetBool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;

    private static int? GetInt(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out int intValue))
            {
                return intValue;
            }
            if (value.TryGetValue<long>(out long longValue) && longValue is >= int.MinValue and <= int.MaxValue)
            {
                return (int)longValue;
            }
        }
        return null;
    }
}

/// <summary>
/// Per-stream state for translating Ollama NDJSON chunks into OpenAI chat.completion.chunk
/// payloads. Create one instance per upstream stream via
/// <see cref="OllamaFormatTranslator.CreateOllamaToOpenAiState"/>.
/// </summary>
public sealed class OllamaToOpenAiStreamState
{
    public OllamaToOpenAiStreamState(string clientModel, string completionId)
    {
        ClientModel = clientModel;
        CompletionId = completionId;
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public string ClientModel { get; }

    public string CompletionId { get; }

    public long Created { get; }

    public bool RoleSent { get; set; }

    public int NextToolCallIndex { get; set; }

    public bool Finished { get; set; }
}