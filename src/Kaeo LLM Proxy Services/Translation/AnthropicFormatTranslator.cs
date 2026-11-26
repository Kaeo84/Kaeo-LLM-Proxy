using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Pure translation between the OpenAI Chat Completions wire format and the Anthropic Messages
/// API (<c>/v1/messages</c>) wire format, in both directions, for requests, non-streaming
/// responses, and streaming events. No I/O, no settings, no routing: callers own framing and
/// transport. Malformed input degrades to a minimal valid payload instead of throwing.
/// </summary>
internal static class AnthropicFormatTranslator
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------
    // 1. OpenAI chat request -> Anthropic Messages request
    // ------------------------------------------------------------------

    public static string OpenAiRequestToAnthropic(string openAiJson, string upstreamModel, bool stream, int defaultMaxTokens)
    {
        JsonObject result = new()
        {
            ["model"] = upstreamModel,
            ["max_tokens"] = defaultMaxTokens,
            ["messages"] = new JsonArray(),
        };
        if (stream)
            result["stream"] = true;

        JsonObject? root = TryParseObject(openAiJson);
        if (root is null)
            return result.ToJsonString(Options);

        int maxTokens = GetInt(root["max_tokens"]);
        if (maxTokens <= 0)
            maxTokens = GetInt(root["max_completion_tokens"]);
        result["max_tokens"] = maxTokens > 0 ? maxTokens : defaultMaxTokens;

        // tool_choice "none" suppresses tools entirely (Anthropic has no "none").
        bool suppressTools = false;
        JsonNode? toolChoice = root["tool_choice"];
        JsonObject? anthropicToolChoice = null;
        if (GetString(toolChoice) is string choiceString)
        {
            switch (choiceString)
            {
                case "auto": anthropicToolChoice = new JsonObject { ["type"] = "auto" }; break;
                case "required": anthropicToolChoice = new JsonObject { ["type"] = "any" }; break;
                case "none": suppressTools = true; break;
            }
        }
        else if (toolChoice is JsonObject choiceObj && GetString(choiceObj["type"]) == "function")
        {
            string? name = GetString(choiceObj["function"]?["name"]);
            if (!string.IsNullOrEmpty(name))
                anthropicToolChoice = new JsonObject { ["type"] = "tool", ["name"] = name };
        }

        List<string> systemParts = [];
        JsonArray messages = [];
        if (root["messages"] is JsonArray openAiMessages)
        {
            foreach (JsonNode? node in openAiMessages)
            {
                if (node is not JsonObject msg)
                    continue;
                string role = GetString(msg["role"]) ?? "user";
                JsonNode? content = msg["content"];

                if (role == "system")
                {
                    string text = ExtractText(content);
                    if (text.Length > 0)
                        systemParts.Add(text);
                    continue;
                }

                if (role == "tool")
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = GetString(msg["tool_call_id"]) ?? string.Empty,
                                ["content"] = ExtractText(content),
                            },
                        },
                    });
                    continue;
                }

                if (role == "assistant" && msg["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
                {
                    JsonArray blocks = [];
                    string text = ExtractText(content);
                    if (text.Length > 0)
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });

                    foreach (JsonNode? tcNode in toolCalls)
                    {
                        if (tcNode is not JsonObject tc)
                            continue;
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = GetString(tc["id"]) ?? NewToolUseId(),
                            ["name"] = GetString(tc["function"]?["name"]) ?? string.Empty,
                            ["input"] = ParseArgumentsObject(tc["function"]?["arguments"]),
                        });
                    }

                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    continue;
                }

                if (content is JsonArray parts)
                {
                    JsonArray blocks = [];
                    foreach (JsonNode? partNode in parts)
                    {
                        if (partNode is not JsonObject part)
                            continue;
                        string partType = GetString(part["type"]) ?? "text";
                        if (partType == "text")
                        {
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = GetString(part["text"]) ?? string.Empty });
                        }
                        else if (partType == "image_url")
                        {
                            (string mediaType, string data) = ParseDataUrl(GetString(part["image_url"]?["url"]) ?? string.Empty);
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data },
                            });
                        }
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["content"] = blocks.Count > 0 ? blocks : JsonValue.Create(string.Empty),
                    });
                    continue;
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = GetString(content) ?? string.Empty });
            }
        }

        string system = string.Join("\n\n", systemParts);
        if (system.Length > 0)
            result["system"] = system;
        result["messages"] = messages;

        CopyIfPresent(root, result, "temperature");
        CopyIfPresent(root, result, "top_p");
        CopyIfPresent(root, result, "top_k");

        if (root["stop"] is JsonArray stopArray)
        {
            result["stop_sequences"] = stopArray.DeepClone();
        }
        else if (GetString(root["stop"]) is string stopString)
        {
            result["stop_sequences"] = new JsonArray { stopString };
        }

        if (!suppressTools && root["tools"] is JsonArray openAiTools && openAiTools.Count > 0)
        {
            JsonArray tools = [];
            foreach (JsonNode? toolNode in openAiTools)
            {
                if (toolNode is not JsonObject tool || tool["function"] is not JsonObject fn)
                    continue;
                JsonObject anthropicTool = new()
                {
                    ["name"] = GetString(fn["name"]) ?? string.Empty,
                    ["input_schema"] = fn["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                };
                if (GetString(fn["description"]) is string description)
                    anthropicTool["description"] = description;
                tools.Add(anthropicTool);
            }
            if (tools.Count > 0)
                result["tools"] = tools;
        }

        if (!suppressTools && anthropicToolChoice is not null)
            result["tool_choice"] = anthropicToolChoice;

        return result.ToJsonString(Options);
    }

    // ------------------------------------------------------------------
    // 2. Anthropic Messages request -> OpenAI chat request
    // ------------------------------------------------------------------

    public static string AnthropicRequestToOpenAi(string anthropicJson, string upstreamModel, bool stream)
    {
        JsonObject result = new()
        {
            ["model"] = upstreamModel,
            ["stream"] = stream,
            ["messages"] = new JsonArray(),
        };

        JsonObject? root = TryParseObject(anthropicJson);
        if (root is null)
            return result.ToJsonString(Options);

        JsonArray messages = [];
        string system = ExtractText(root["system"]);
        if (system.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        if (root["messages"] is JsonArray anthropicMessages)
        {
            foreach (JsonNode? node in anthropicMessages)
            {
                if (node is not JsonObject msg)
                    continue;
                string role = GetString(msg["role"]) ?? "user";
                JsonNode? content = msg["content"];

                if (content is not JsonArray blocks)
                {
                    messages.Add(new JsonObject { ["role"] = role, ["content"] = GetString(content) ?? string.Empty });
                    continue;
                }

                if (role == "assistant" && blocks.Any(b => GetString(b?["type"]) == "tool_use"))
                {
                    JsonArray toolCalls = [];
                    foreach (JsonNode? blockNode in blocks)
                    {
                        if (blockNode is not JsonObject block || GetString(block["type"]) != "tool_use")
                            continue;
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = GetString(block["id"]) ?? NewToolUseId(),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = GetString(block["name"]) ?? string.Empty,
                                ["arguments"] = block["input"]?.ToJsonString() ?? "{}",
                            },
                        });
                    }

                    // Content is emitted even when the turn is tool-use only: an Anthropic assistant
                    // turn may carry no text block at all, and omitting the member is a hard 400 on
                    // strict OpenAI-compatible providers ("The content field is a required field").
                    JsonObject assistant = new() { ["role"] = "assistant", ["content"] = ConcatTextBlocks(blocks) };
                    assistant["tool_calls"] = toolCalls;
                    messages.Add(assistant);
                    continue;
                }

                if (role == "user" && blocks.Any(b => GetString(b?["type"]) == "tool_result"))
                {
                    foreach (JsonNode? blockNode in blocks)
                    {
                        if (blockNode is not JsonObject block || GetString(block["type"]) != "tool_result")
                            continue;
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = GetString(block["tool_use_id"]) ?? string.Empty,
                            ["content"] = ExtractText(block["content"]),
                        });
                    }

                    JsonNode? others = BuildOpenAiContentFromBlocks(blocks, excludeToolResults: true);
                    if (others is not null)
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = others });
                    continue;
                }

                JsonNode? openAiContent = BuildOpenAiContentFromBlocks(blocks, excludeToolResults: false);
                messages.Add(new JsonObject { ["role"] = role, ["content"] = openAiContent ?? JsonValue.Create(string.Empty) });
            }
        }

        result["messages"] = messages;

        CopyIfPresent(root, result, "max_tokens");
        CopyIfPresent(root, result, "temperature");
        CopyIfPresent(root, result, "top_p");
        CopyIfPresent(root, result, "top_k");

        if (root["stop_sequences"] is JsonArray stopSequences)
            result["stop"] = stopSequences.DeepClone();

        if (root["tools"] is JsonArray anthropicTools && anthropicTools.Count > 0)
        {
            JsonArray tools = [];
            foreach (JsonNode? toolNode in anthropicTools)
            {
                if (toolNode is not JsonObject tool)
                    continue;
                JsonObject fn = new()
                {
                    ["name"] = GetString(tool["name"]) ?? string.Empty,
                    ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                };
                if (GetString(tool["description"]) is string description)
                    fn["description"] = description;
                tools.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
            }
            if (tools.Count > 0)
                result["tools"] = tools;
        }

        if (root["tool_choice"] is JsonObject toolChoice)
        {
            switch (GetString(toolChoice["type"]))
            {
                case "auto":
                    result["tool_choice"] = "auto";
                    break;
                case "any":
                    result["tool_choice"] = "required";
                    break;
                case "tool":
                    result["tool_choice"] = new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = GetString(toolChoice["name"]) ?? string.Empty },
                    };
                    break;
            }
        }

        return result.ToJsonString(Options);
    }

    // ------------------------------------------------------------------
    // 3. Anthropic non-stream response -> OpenAI chat.completion
    // ------------------------------------------------------------------

    public static string AnthropicResponseToOpenAi(string anthropicJson, string clientModel, string completionId)
    {
        JsonObject? root = TryParseObject(anthropicJson);

        string text = string.Empty;
        string thinking = string.Empty;
        JsonArray? toolCalls = null;
        string? stopReason = null;
        int inputTokens = 0;
        int outputTokens = 0;

        if (root is not null)
        {
            stopReason = GetString(root["stop_reason"]);
            inputTokens = GetInt(root["usage"]?["input_tokens"]);
            outputTokens = GetInt(root["usage"]?["output_tokens"]);

            if (root["content"] is JsonArray blocks)
            {
                List<string> textParts = [];
                List<string> thinkingParts = [];
                foreach (JsonNode? blockNode in blocks)
                {
                    if (blockNode is not JsonObject block)
                        continue;
                    switch (GetString(block["type"]))
                    {
                        case "text":
                            textParts.Add(GetString(block["text"]) ?? string.Empty);
                            break;
                        case "thinking":
                            thinkingParts.Add(GetString(block["thinking"]) ?? string.Empty);
                            break;
                        case "tool_use":
                            (toolCalls ??= []).Add(new JsonObject
                            {
                                ["id"] = GetString(block["id"]) ?? NewToolUseId(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = GetString(block["name"]) ?? string.Empty,
                                    ["arguments"] = block["input"]?.ToJsonString() ?? "{}",
                                },
                            });
                            break;
                    }
                }
                text = string.Concat(textParts);
                thinking = string.Concat(thinkingParts);
            }
        }

        JsonObject message = new() { ["role"] = "assistant" };
        if (text.Length > 0)
            message["content"] = text;
        if (thinking.Length > 0)
            message["reasoning_content"] = thinking;
        if (toolCalls is not null)
            message["tool_calls"] = toolCalls;

        string finishReason = stopReason switch
        {
            "tool_use" => "tool_calls",
            "max_tokens" => "length",
            _ => "stop",
        };
        if (toolCalls is not null)
            finishReason = "tool_calls";

        JsonObject result = new()
        {
            ["id"] = completionId,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = clientModel,
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finishReason },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = inputTokens,
                ["completion_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens,
            },
        };
        return result.ToJsonString(Options);
    }

    // ------------------------------------------------------------------
    // 4. OpenAI non-stream chat.completion -> Anthropic message response
    // ------------------------------------------------------------------

    public static string OpenAiResponseToAnthropic(string openAiJson, string clientModel, string messageId)
    {
        JsonObject? root = TryParseObject(openAiJson);
        JsonObject? choice = (root?["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        JsonObject? message = choice?["message"] as JsonObject;
        string? finishReason = GetString(choice?["finish_reason"]);

        JsonArray content = [];
        string reasoning = GetString(message?["reasoning_content"]) ?? string.Empty;
        if (reasoning.Length > 0)
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning });

        string text = GetString(message?["content"]) ?? string.Empty;
        if (text.Length > 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        if (message?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (JsonNode? tcNode in toolCalls)
            {
                if (tcNode is not JsonObject tc)
                    continue;
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = GetString(tc["id"]) ?? NewToolUseId(),
                    ["name"] = GetString(tc["function"]?["name"]) ?? string.Empty,
                    ["input"] = ParseArgumentsObject(tc["function"]?["arguments"]),
                });
            }
        }

        if (content.Count == 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });

        string stopReason = finishReason switch
        {
            "tool_calls" => "tool_use",
            "length" => "max_tokens",
            _ => "end_turn",
        };

        JsonObject result = new()
        {
            ["id"] = messageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = clientModel,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = GetInt(root?["usage"]?["prompt_tokens"]),
                ["output_tokens"] = GetInt(root?["usage"]?["completion_tokens"]),
            },
        };
        return result.ToJsonString(Options);
    }

    // ------------------------------------------------------------------
    // 5. Streaming Anthropic SSE events -> OpenAI chat.completion.chunk
    // ------------------------------------------------------------------

    public static AnthropicToOpenAiStreamState CreateAnthropicToOpenAiState(string clientModel, string completionId) => new()
    {
        ClientModel = clientModel,
        CompletionId = completionId,
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    public static IReadOnlyList<string> AnthropicEventToOpenAiChunks(AnthropicToOpenAiStreamState state, string eventType, string dataJson)
    {
        List<string> chunks = [];
        JsonObject? data = TryParseObject(dataJson);
        if (data is null)
            return chunks;

        switch (eventType)
        {
            case "message_start":
                state.InputTokens = GetInt(data["message"]?["usage"]?["input_tokens"], state.InputTokens);
                if (!state.RoleSent)
                {
                    state.RoleSent = true;
                    chunks.Add(ChunkWithDelta(state, new JsonObject { ["role"] = "assistant" }, finishReason: null));
                }
                break;

            case "content_block_start":
                if (data["content_block"] is JsonObject block && GetString(block["type"]) == "tool_use")
                {
                    int blockIndex = GetInt(data["index"]);
                    int toolIndex = state.NextToolIndex++;
                    state.BlockIndexToToolIndex[blockIndex] = toolIndex;
                    JsonObject delta = new()
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = toolIndex,
                                ["id"] = GetString(block["id"]) ?? NewToolUseId(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = GetString(block["name"]) ?? string.Empty,
                                    ["arguments"] = string.Empty,
                                },
                            },
                        },
                    };
                    chunks.Add(ChunkWithDelta(state, delta, finishReason: null));
                }
                break;

            case "content_block_delta":
                if (data["delta"] is not JsonObject blockDelta)
                    break;
                switch (GetString(blockDelta["type"]))
                {
                    case "text_delta":
                        chunks.Add(ChunkWithDelta(state, new JsonObject { ["content"] = GetString(blockDelta["text"]) ?? string.Empty }, finishReason: null));
                        break;
                    case "thinking_delta":
                        chunks.Add(ChunkWithDelta(state, new JsonObject { ["reasoning_content"] = GetString(blockDelta["thinking"]) ?? string.Empty }, finishReason: null));
                        break;
                    case "input_json_delta":
                        if (state.BlockIndexToToolIndex.TryGetValue(GetInt(data["index"]), out int mappedToolIndex))
                        {
                            JsonObject delta = new()
                            {
                                ["tool_calls"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["index"] = mappedToolIndex,
                                        ["function"] = new JsonObject { ["arguments"] = GetString(blockDelta["partial_json"]) ?? string.Empty },
                                    },
                                },
                            };
                            chunks.Add(ChunkWithDelta(state, delta, finishReason: null));
                        }
                        break;
                }
                break;

            case "message_delta":
                state.OutputTokens = GetInt(data["usage"]?["output_tokens"], state.OutputTokens);
                string finishReason = GetString(data["delta"]?["stop_reason"]) switch
                {
                    "tool_use" => "tool_calls",
                    "max_tokens" => "length",
                    _ => "stop",
                };
                state.FinishSent = true;
                chunks.Add(ChunkWithDelta(state, [], finishReason));
                break;

            case "message_stop":
                state.Finished = true;
                if (state.InputTokens > 0 || state.OutputTokens > 0)
                {
                    JsonObject usageChunk = NewChunkEnvelope(state);
                    usageChunk["choices"] = new JsonArray();
                    usageChunk["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = state.InputTokens,
                        ["completion_tokens"] = state.OutputTokens,
                        ["total_tokens"] = state.InputTokens + state.OutputTokens,
                    };
                    chunks.Add(usageChunk.ToJsonString(Options));
                }
                break;
        }

        return chunks;
    }

    // ------------------------------------------------------------------
    // 6. Streaming OpenAI chat.completion.chunk -> Anthropic SSE events
    // ------------------------------------------------------------------

    public static OpenAiToAnthropicStreamState CreateOpenAiToAnthropicState(string clientModel, string messageId) => new()
    {
        ClientModel = clientModel,
        MessageId = messageId,
    };

    public static IReadOnlyList<string> OpenAiChunkToAnthropicEvents(OpenAiToAnthropicStreamState state, string openAiChunkJson)
    {
        List<string> events = [];
        if (state.Finished)
            return events;

        JsonObject? root = TryParseObject(openAiChunkJson);
        if (root is null)
            return events;

        if (!state.Started)
        {
            state.Started = true;
            events.Add(Frame("message_start", new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["id"] = state.MessageId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = state.ClientModel,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                },
            }));
        }

        if (root["usage"] is JsonObject usage)
        {
            state.InputTokens = GetInt(usage["prompt_tokens"], state.InputTokens);
            state.OutputTokens = GetInt(usage["completion_tokens"], state.OutputTokens);
        }

        if (root["choices"] is not JsonArray choices || choices.Count == 0 || choices[0] is not JsonObject choice)
            return events;

        JsonObject? delta = choice["delta"] as JsonObject;

        string reasoning = GetString(delta?["reasoning_content"]) ?? string.Empty;
        if (reasoning.Length > 0)
        {
            if (state.CurrentThinkingBlock is not int thinkingIndex)
            {
                CloseOpenBlocks(events, state);
                thinkingIndex = state.NextBlockIndex++;
                state.CurrentThinkingBlock = thinkingIndex;
                events.Add(Frame("content_block_start", new JsonObject
                {
                    ["index"] = thinkingIndex,
                    ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = string.Empty },
                }));
            }
            events.Add(Frame("content_block_delta", new JsonObject
            {
                ["index"] = thinkingIndex,
                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = reasoning },
            }));
        }

        string content = GetString(delta?["content"]) ?? string.Empty;
        if (content.Length > 0)
        {
            if (state.CurrentTextBlock is not int textIndex)
            {
                CloseOpenBlocks(events, state);
                textIndex = state.NextBlockIndex++;
                state.CurrentTextBlock = textIndex;
                events.Add(Frame("content_block_start", new JsonObject
                {
                    ["index"] = textIndex,
                    ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = string.Empty },
                }));
            }
            events.Add(Frame("content_block_delta", new JsonObject
            {
                ["index"] = textIndex,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = content },
            }));
        }

        if (delta?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (JsonNode? tcNode in toolCalls)
            {
                if (tcNode is not JsonObject tc)
                    continue;
                int openAiIndex = GetInt(tc["index"], state.OpenAiToolIndexToBlock.Count);
                string? id = GetString(tc["id"]);
                string? name = GetString(tc["function"]?["name"]);
                string? arguments = GetString(tc["function"]?["arguments"]);

                if (id is not null || name is not null || !state.OpenAiToolIndexToBlock.TryGetValue(openAiIndex, out int blockIndex))
                {
                    CloseOpenBlocks(events, state);
                    blockIndex = state.NextBlockIndex++;
                    state.OpenAiToolIndexToBlock[openAiIndex] = blockIndex;
                    state.CurrentToolBlock = blockIndex;
                    events.Add(Frame("content_block_start", new JsonObject
                    {
                        ["index"] = blockIndex,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = id ?? NewToolUseId(),
                            ["name"] = name ?? string.Empty,
                            ["input"] = new JsonObject(),
                        },
                    }));
                }

                if (!string.IsNullOrEmpty(arguments))
                {
                    events.Add(Frame("content_block_delta", new JsonObject
                    {
                        ["index"] = blockIndex,
                        ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments },
                    }));
                }
            }
        }

        if (GetString(choice["finish_reason"]) is string finishReason)
        {
            CloseOpenBlocks(events, state);
            string stopReason = finishReason switch
            {
                "tool_calls" => "tool_use",
                "length" => "max_tokens",
                _ => "end_turn",
            };
            events.Add(Frame("message_delta", new JsonObject
            {
                ["delta"] = new JsonObject { ["stop_reason"] = stopReason },
                ["usage"] = new JsonObject { ["output_tokens"] = state.OutputTokens },
            }));
            events.Add(Frame("message_stop", []));
            state.Finished = true;
        }

        return events;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static JsonObject? TryParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static int GetInt(JsonNode? node, int fallback = 0) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : fallback;

    private static void CopyIfPresent(JsonObject source, JsonObject target, string key)
    {
        if (source[key] is JsonNode node)
            target[key] = node.DeepClone();
    }

    /// <summary>String content as-is; array content reduced to its concatenated text parts.</summary>
    private static string ExtractText(JsonNode? content)
    {
        if (GetString(content) is string s)
            return s;
        if (content is JsonArray array)
            return ConcatTextBlocks(array);
        return string.Empty;
    }

    private static string ConcatTextBlocks(JsonArray blocks) =>
        string.Concat(blocks
            .OfType<JsonObject>()
            .Where(b => GetString(b["type"]) == "text")
            .Select(b => GetString(b["text"]) ?? string.Empty));

    private static JsonObject ParseArgumentsObject(JsonNode? arguments)
    {
        if (arguments is JsonObject obj)
            return (JsonObject)obj.DeepClone();
        if (GetString(arguments) is string s && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject parsed)
                    return parsed;
            }
            catch (JsonException)
            {
                // Malformed arguments degrade to an empty input object.
            }
        }
        return [];
    }

    private static (string MediaType, string Data) ParseDataUrl(string url)
    {
        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = url.IndexOf(',');
            if (comma > 5)
            {
                string header = url[5..comma];
                string mediaType = header.Split(';')[0];
                return (mediaType.Length > 0 ? mediaType : "image/png", url[(comma + 1)..]);
            }
        }
        return ("image/png", url);
    }

    /// <summary>
    /// Anthropic text/image blocks -> OpenAI content: a plain string when only text is present,
    /// otherwise a content-parts array with data-URL image parts. Null when nothing remains.
    /// </summary>
    private static JsonNode? BuildOpenAiContentFromBlocks(JsonArray blocks, bool excludeToolResults)
    {
        List<JsonObject> relevant = [.. blocks
            .OfType<JsonObject>()
            .Where(b => GetString(b["type"]) is "text" or "image")];
        if (excludeToolResults)
            relevant = [.. relevant.Where(b => GetString(b["type"]) != "tool_result")];
        if (relevant.Count == 0)
            return null;

        if (relevant.All(b => GetString(b["type"]) == "text"))
            return string.Concat(relevant.Select(b => GetString(b["text"]) ?? string.Empty));

        JsonArray parts = [];
        foreach (JsonObject block in relevant)
        {
            if (GetString(block["type"]) == "text")
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = GetString(block["text"]) ?? string.Empty });
            }
            else
            {
                string mediaType = GetString(block["source"]?["media_type"]) ?? "image/png";
                string data = GetString(block["source"]?["data"]) ?? string.Empty;
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" },
                });
            }
        }
        return parts;
    }

    private static string NewToolUseId() => "toolu_" + Guid.NewGuid().ToString("N")[..24];

    private static JsonObject NewChunkEnvelope(AnthropicToOpenAiStreamState state) => new()
    {
        ["id"] = state.CompletionId,
        ["object"] = "chat.completion.chunk",
        ["created"] = state.Created,
        ["model"] = state.ClientModel,
    };

    private static string ChunkWithDelta(AnthropicToOpenAiStreamState state, JsonObject delta, string? finishReason)
    {
        JsonObject chunk = NewChunkEnvelope(state);
        JsonObject choice = new() { ["index"] = 0, ["delta"] = delta };
        if (finishReason is not null)
            choice["finish_reason"] = finishReason;
        chunk["choices"] = new JsonArray { choice };
        return chunk.ToJsonString(Options);
    }

    private static string Frame(string eventType, JsonObject payload)
    {
        payload["type"] = eventType;
        return "event: " + eventType + "\ndata: " + payload.ToJsonString(Options) + "\n\n";
    }

    private static void CloseOpenBlocks(List<string> events, OpenAiToAnthropicStreamState state)
    {
        // At most one block is open at a time: opening any block closes the others.
        foreach (int? open in new[] { state.CurrentTextBlock, state.CurrentThinkingBlock, state.CurrentToolBlock })
        {
            if (open is int index)
                events.Add(Frame("content_block_stop", new JsonObject { ["index"] = index }));
        }
        state.CurrentTextBlock = null;
        state.CurrentThinkingBlock = null;
        state.CurrentToolBlock = null;
    }
}

/// <summary>Per-stream state for Anthropic SSE -> OpenAI chunk translation.</summary>
internal sealed class AnthropicToOpenAiStreamState
{
    public string ClientModel { get; init; } = string.Empty;
    public string CompletionId { get; init; } = string.Empty;
    public long Created { get; init; }
    public bool RoleSent { get; set; }
    public Dictionary<int, int> BlockIndexToToolIndex { get; } = [];
    public int NextToolIndex { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public bool FinishSent { get; set; }
    /// <summary>True once message_stop has been seen; the caller may then emit [DONE].</summary>
    public bool Finished { get; set; }
}

/// <summary>Per-stream state for OpenAI chunk -> Anthropic SSE translation.</summary>
internal sealed class OpenAiToAnthropicStreamState
{
    public string ClientModel { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public bool Started { get; set; }
    public int NextBlockIndex { get; set; }
    public int? CurrentTextBlock { get; set; }
    public int? CurrentThinkingBlock { get; set; }
    public int? CurrentToolBlock { get; set; }
    public Dictionary<int, int> OpenAiToolIndexToBlock { get; } = [];
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public bool Finished { get; set; }
}