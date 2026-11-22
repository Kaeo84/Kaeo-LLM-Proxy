using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Translation;

/// <summary>
/// Contract tests for the pure OpenAI &lt;-&gt; Ollama wire-format translator: request bodies,
/// non-streaming responses, and streaming NDJSON chunk translation. Outputs are parsed with
/// JsonNode and specific field values are asserted.
/// </summary>
public class OllamaFormatTranslatorTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- Request translation ----

    [Fact]
    public void Request_StringContent_IsCopiedWithModelAndStream()
    {
        string input = """
            {"model":"client-model","messages":[{"role":"user","content":"hello"}]}
            """;

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "upstream-model", stream: true));

        Assert.Equal("upstream-model", (string?)result["model"]);
        Assert.True((bool?)result["stream"]);
        JsonArray messages = result["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", (string?)messages[0]!["role"]);
        Assert.Equal("hello", (string?)messages[0]!["content"]);
        Assert.False(result.ContainsKey("options"));
        Assert.False(result.ContainsKey("tools"));
        Assert.False(result.ContainsKey("format"));
    }

    [Fact]
    public void Request_StreamFalse_IsEmittedAsFalse()
    {
        string input = """{"messages":[{"role":"user","content":"hi"}]}""";

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", stream: false));

        Assert.False((bool?)result["stream"]);
    }

    [Fact]
    public void Request_MultimodalParts_ConcatenatesTextAndCollectsBase64Images()
    {
        string input = """
            {"messages":[{"role":"user","content":[
                {"type":"text","text":"look at "},
                {"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}},
                {"type":"text","text":"this"},
                {"type":"image_url","image_url":{"url":"cmF3"}}
            ]}]}
            """;

        JsonObject message = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![0]!.AsObject();

        Assert.Equal("look at this", (string?)message["content"]);
        JsonArray images = message["images"]!.AsArray();
        Assert.Equal(2, images.Count);
        Assert.Equal("aGVsbG8=", (string?)images[0]);
        Assert.Equal("cmF3", (string?)images[1]);
    }

    [Fact]
    public void Request_TextOnlyParts_OmitsImages()
    {
        string input = """
            {"messages":[{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}
            """;

        JsonObject message = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![0]!.AsObject();

        Assert.Equal("ab", (string?)message["content"]);
        Assert.False(message.ContainsKey("images"));
    }

    [Fact]
    public void Request_AssistantToolCalls_ArgumentsStringBecomesObjectAndIdPreserved()
    {
        string input = """
            {"messages":[
                {"role":"user","content":"weather?"},
                {"role":"assistant","content":null,"tool_calls":[
                    {"id":"call_abc","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Oslo\"}"}}
                ]}
            ]}
            """;

        JsonObject assistant = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![1]!.AsObject();

        Assert.Equal("", (string?)assistant["content"]);
        JsonObject toolCall = assistant["tool_calls"]![0]!.AsObject();
        Assert.Equal("call_abc", (string?)toolCall["id"]);
        Assert.Equal("get_weather", (string?)toolCall["function"]!["name"]);
        Assert.Equal("Oslo", (string?)toolCall["function"]!["arguments"]!["city"]);
        Assert.False(toolCall.ContainsKey("type"));
    }

    [Fact]
    public void Request_ToolCallWithInvalidArguments_UsesEmptyObject()
    {
        string input = """
            {"messages":[{"role":"assistant","tool_calls":[
                {"type":"function","function":{"name":"f","arguments":"not json"}}
            ]}]}
            """;

        JsonObject toolCall = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![0]!["tool_calls"]![0]!.AsObject();

        Assert.Empty(toolCall["function"]!["arguments"]!.AsObject());
        Assert.False(toolCall.ContainsKey("id"));
    }

    [Fact]
    public void Request_ToolReply_CopiesToolCallIdAndMapsNameToToolName()
    {
        string input = """
            {"messages":[{"role":"tool","tool_call_id":"call_abc","name":"get_weather","content":"sunny"}]}
            """;

        JsonObject message = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![0]!.AsObject();

        Assert.Equal("tool", (string?)message["role"]);
        Assert.Equal("call_abc", (string?)message["tool_call_id"]);
        Assert.Equal("get_weather", (string?)message["tool_name"]);
        Assert.Equal("sunny", (string?)message["content"]);
    }

    [Fact]
    public void Request_NullContentWithoutToolCalls_OmitsContent()
    {
        string input = """{"messages":[{"role":"assistant","content":null}]}""";

        JsonObject message = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["messages"]![0]!.AsObject();

        Assert.False(message.ContainsKey("content"));
    }

    [Fact]
    public void Request_Tools_AreCopiedVerbatim()
    {
        string input = """
            {"messages":[],"tools":[
                {"type":"function","function":{"name":"f","description":"d","parameters":{"type":"object","properties":{"x":{"type":"string"}}}}}
            ]}
            """;

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false));

        JsonObject function = result["tools"]![0]!.AsObject();
        Assert.Equal("function", (string?)function["type"]);
        Assert.Equal("f", (string?)function["function"]!["name"]);
        Assert.Equal("d", (string?)function["function"]!["description"]);
        Assert.Equal("string", (string?)function["function"]!["parameters"]!["properties"]!["x"]!["type"]);
    }

    [Fact]
    public void Request_EmptyToolsArray_IsOmitted()
    {
        string input = """{"messages":[],"tools":[]}""";

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false));

        Assert.False(result.ContainsKey("tools"));
    }

    [Fact]
    public void Request_SamplingFields_MapToOptions()
    {
        string input = """
            {"messages":[],"temperature":0.7,"top_p":0.9,"max_tokens":256,"seed":42,
             "frequency_penalty":0.5,"presence_penalty":0.25,"repeat_penalty":1.1,"top_k":40}
            """;

        JsonObject options = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["options"]!.AsObject();

        Assert.Equal(0.7, (double?)options["temperature"]);
        Assert.Equal(0.9, (double?)options["top_p"]);
        Assert.Equal(256, (int?)options["num_predict"]);
        Assert.Equal(42, (int?)options["seed"]);
        Assert.Equal(0.5, (double?)options["frequency_penalty"]);
        Assert.Equal(0.25, (double?)options["presence_penalty"]);
        Assert.Equal(1.1, (double?)options["repeat_penalty"]);
        Assert.Equal(40, (int?)options["top_k"]);
        Assert.False(options.ContainsKey("max_tokens"));
    }

    [Fact]
    public void Request_MaxCompletionTokens_MapsToNumPredict()
    {
        string input = """{"messages":[],"max_completion_tokens":128}""";

        JsonObject options = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["options"]!.AsObject();

        Assert.Equal(128, (int?)options["num_predict"]);
    }

    [Fact]
    public void Request_StopString_NormalizesToSingleElementArray()
    {
        string input = """{"messages":[],"stop":"END"}""";

        JsonArray stop = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["options"]!["stop"]!.AsArray();

        Assert.Single(stop);
        Assert.Equal("END", (string?)stop[0]);
    }

    [Fact]
    public void Request_StopArray_IsPreserved()
    {
        string input = """{"messages":[],"stop":["A","B"]}""";

        JsonArray stop = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false))["options"]!["stop"]!.AsArray();

        Assert.Equal(2, stop.Count);
        Assert.Equal("A", (string?)stop[0]);
        Assert.Equal("B", (string?)stop[1]);
    }

    [Theory]
    [InlineData("json_object")]
    [InlineData("json_schema")]
    public void Request_JsonResponseFormat_SetsFormatJson(string formatType)
    {
        string input = $$$"""{"messages":[],"response_format":{"type":"{{{formatType}}}"}}""";

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false));

        Assert.Equal("json", (string?)result["format"]);
    }

    [Fact]
    public void Request_TextResponseFormat_OmitsFormat()
    {
        string input = """{"messages":[],"response_format":{"type":"text"}}""";

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false));

        Assert.False(result.ContainsKey("format"));
    }

    [Fact]
    public void Request_KeepAlive_IsCopied()
    {
        string input = """{"messages":[],"keep_alive":"10m"}""";

        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", false));

        Assert.Equal("10m", (string?)result["keep_alive"]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void Request_MalformedInput_ReturnsBestEffortWithoutThrowing(string input)
    {
        JsonObject result = Parse(OllamaFormatTranslator.OpenAiRequestToOllama(input, "m", true));

        Assert.Equal("m", (string?)result["model"]);
        Assert.True((bool?)result["stream"]);
        Assert.Empty(result["messages"]!.AsArray());
    }

    // ---- Non-streaming response translation ----

    [Fact]
    public void Response_Content_MapsToChatCompletion()
    {
        string input = """
            {"model":"upstream","created_at":"2026-01-01T00:00:00Z",
             "message":{"role":"assistant","content":"hello there"},
             "done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":5}
            """;

        JsonObject result = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "client-model", "chatcmpl-123"));

        Assert.Equal("chatcmpl-123", (string?)result["id"]);
        Assert.Equal("chat.completion", (string?)result["object"]);
        Assert.Equal("client-model", (string?)result["model"]);
        Assert.True((long?)result["created"] > 0);

        JsonObject choice = result["choices"]![0]!.AsObject();
        Assert.Equal(0, (int?)choice["index"]);
        Assert.Equal("assistant", (string?)choice["message"]!["role"]);
        Assert.Equal("hello there", (string?)choice["message"]!["content"]);
        Assert.Equal("stop", (string?)choice["finish_reason"]);
        Assert.False(choice["message"]!.AsObject().ContainsKey("tool_calls"));
        Assert.False(choice["message"]!.AsObject().ContainsKey("reasoning_content"));

        Assert.Equal(10, (int?)result["usage"]!["prompt_tokens"]);
        Assert.Equal(5, (int?)result["usage"]!["completion_tokens"]);
        Assert.Equal(15, (int?)result["usage"]!["total_tokens"]);
    }

    [Fact]
    public void Response_Thinking_MapsToReasoningContent()
    {
        string input = """{"message":{"role":"assistant","content":"answer","thinking":"pondering"},"done":true}""";

        JsonObject message = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"))["choices"]![0]!["message"]!.AsObject();

        Assert.Equal("pondering", (string?)message["reasoning_content"]);
        Assert.Equal("answer", (string?)message["content"]);
    }

    [Fact]
    public void Response_EmptyThinking_OmitsReasoningContent()
    {
        string input = """{"message":{"role":"assistant","content":"x","thinking":""},"done":true}""";

        JsonObject message = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"))["choices"]![0]!["message"]!.AsObject();

        Assert.False(message.ContainsKey("reasoning_content"));
    }

    [Fact]
    public void Response_NullContent_OmitsContentAndAlwaysEmitsUsage()
    {
        string input = """{"message":{"role":"assistant"},"done":true}""";

        JsonObject result = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"));

        Assert.False(result["choices"]![0]!["message"]!.AsObject().ContainsKey("content"));
        Assert.Equal(0, (int?)result["usage"]!["prompt_tokens"]);
        Assert.Equal(0, (int?)result["usage"]!["completion_tokens"]);
        Assert.Equal(0, (int?)result["usage"]!["total_tokens"]);
    }

    [Fact]
    public void Response_ToolCalls_ArgumentsObjectBecomesStringAndIdPreserved()
    {
        string input = """
            {"message":{"role":"assistant","content":"","tool_calls":[
                {"id":"call_existing","function":{"name":"get_weather","arguments":{"city":"Oslo"}}}
            ]},"done":true,"done_reason":"stop"}
            """;

        JsonObject choice = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"))["choices"]![0]!.AsObject();

        Assert.Equal("tool_calls", (string?)choice["finish_reason"]);
        JsonObject toolCall = choice["message"]!["tool_calls"]![0]!.AsObject();
        Assert.Equal("call_existing", (string?)toolCall["id"]);
        Assert.Equal("function", (string?)toolCall["type"]);
        Assert.Equal("get_weather", (string?)toolCall["function"]!["name"]);

        string? arguments = (string?)toolCall["function"]!["arguments"];
        Assert.NotNull(arguments);
        Assert.Equal("Oslo", (string?)JsonNode.Parse(arguments!)!["city"]);
    }

    [Fact]
    public void Response_ToolCallWithoutId_GeneratesCallPrefixedId()
    {
        string input = """
            {"message":{"role":"assistant","tool_calls":[{"function":{"name":"f","arguments":{}}}]},"done":true}
            """;

        JsonObject toolCall = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"))["choices"]![0]!["message"]!["tool_calls"]![0]!.AsObject();

        string? id = (string?)toolCall["id"];
        Assert.NotNull(id);
        Assert.StartsWith("call_", id);
        Assert.Equal(29, id!.Length);
        Assert.Equal("{}", (string?)toolCall["function"]!["arguments"]);
    }

    [Theory]
    [InlineData("length", "length")]
    [InlineData("stop", "stop")]
    [InlineData("tool_calls", "tool_calls")]
    [InlineData(null, "stop")]
    public void Response_DoneReason_MapsToFinishReason(string? doneReason, string expected)
    {
        string reasonJson = doneReason is null ? "" : $$""" ,"done_reason":"{{doneReason}}" """;
        string input = $$"""{"message":{"role":"assistant","content":"x"},"done":true{{reasonJson}}}""";

        JsonObject choice = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi(input, "m", "id"))["choices"]![0]!.AsObject();

        Assert.Equal(expected, (string?)choice["finish_reason"]);
    }

    [Fact]
    public void Response_MalformedInput_ReturnsBestEffortCompletion()
    {
        JsonObject result = Parse(OllamaFormatTranslator.OllamaResponseToOpenAi("{broken", "m", "id"));

        Assert.Equal("id", (string?)result["id"]);
        Assert.Equal("chat.completion", (string?)result["object"]);
        Assert.Equal("stop", (string?)result["choices"]![0]!["finish_reason"]);
        Assert.Equal(0, (int?)result["usage"]!["total_tokens"]);
    }

    // ---- Streaming translation ----

    private static OllamaToOpenAiStreamState NewState() =>
        OllamaFormatTranslator.CreateOllamaToOpenAiState("client-model", "chatcmpl-stream");

    private static void AssertChunkEnvelope(JsonObject chunk, OllamaToOpenAiStreamState state)
    {
        Assert.Equal(state.CompletionId, (string?)chunk["id"]);
        Assert.Equal("chat.completion.chunk", (string?)chunk["object"]);
        Assert.Equal(state.Created, (long?)chunk["created"]);
        Assert.Equal(state.ClientModel, (string?)chunk["model"]);
    }

    [Fact]
    public void Stream_FirstContentLine_EmitsRoleChunkThenContentChunk()
    {
        OllamaToOpenAiStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"role":"assistant","content":"Hel"},"done":false}""");

        Assert.Equal(2, chunks.Count);
        JsonObject roleChunk = Parse(chunks[0]);
        AssertChunkEnvelope(roleChunk, state);
        Assert.Equal("assistant", (string?)roleChunk["choices"]![0]!["delta"]!["role"]);
        Assert.False(roleChunk["choices"]![0]!.AsObject().ContainsKey("finish_reason"));

        JsonObject contentChunk = Parse(chunks[1]);
        Assert.Equal("Hel", (string?)contentChunk["choices"]![0]!["delta"]!["content"]);
        Assert.False(contentChunk["choices"]![0]!["delta"]!.AsObject().ContainsKey("role"));
        Assert.True(state.RoleSent);
    }

    [Fact]
    public void Stream_RoleChunk_EmittedOnlyOnce()
    {
        OllamaToOpenAiStreamState state = NewState();

        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"content":"a"},"done":false}""");
        IReadOnlyList<string> second = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"content":"b"},"done":false}""");

        Assert.Single(second);
        Assert.Equal("b", (string?)Parse(second[0])["choices"]![0]!["delta"]!["content"]);
    }

    [Fact]
    public void Stream_Thinking_EmitsReasoningContentDelta()
    {
        OllamaToOpenAiStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"thinking":"hmm"},"done":false}""");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("hmm", (string?)Parse(chunks[1])["choices"]![0]!["delta"]!["reasoning_content"]);
    }

    [Fact]
    public void Stream_ToolCalls_EmitIndexedDeltasWithStringifiedArguments()
    {
        OllamaToOpenAiStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """
            {"message":{"tool_calls":[
                {"id":"call_one","function":{"name":"f1","arguments":{"a":1}}},
                {"function":{"name":"f2","arguments":{"b":2}}}
            ]},"done":false}
            """);

        // role chunk + two tool-call chunks
        Assert.Equal(3, chunks.Count);

        JsonObject first = Parse(chunks[1])["choices"]![0]!["delta"]!["tool_calls"]![0]!.AsObject();
        Assert.Equal(0, (int?)first["index"]);
        Assert.Equal("call_one", (string?)first["id"]);
        Assert.Equal("function", (string?)first["type"]);
        Assert.Equal("f1", (string?)first["function"]!["name"]);
        string firstArguments = (string)first["function"]!["arguments"]!;
        Assert.Equal(1, (int?)JsonNode.Parse(firstArguments)!["a"]);

        JsonObject second = Parse(chunks[2])["choices"]![0]!["delta"]!["tool_calls"]![0]!.AsObject();
        Assert.Equal(1, (int?)second["index"]);
        Assert.StartsWith("call_", (string?)second["id"]);
        Assert.Equal("f2", (string?)second["function"]!["name"]);
        Assert.Equal(2, state.NextToolCallIndex);
    }

    [Fact]
    public void Stream_ToolCallIndex_ContinuesAcrossLines()
    {
        OllamaToOpenAiStreamState state = NewState();

        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"tool_calls":[{"function":{"name":"f1","arguments":{}}}]},"done":false}""");
        IReadOnlyList<string> second = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"tool_calls":[{"function":{"name":"f2","arguments":{}}}]},"done":false}""");

        Assert.Single(second);
        Assert.Equal(1, (int?)Parse(second[0])["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]);
    }

    [Fact]
    public void Stream_Done_EmitsFinishChunkAndUsageChunk()
    {
        OllamaToOpenAiStreamState state = NewState();
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"content":"x"},"done":false}""");

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"content":""},"done":true,"done_reason":"stop","prompt_eval_count":7,"eval_count":3}""");

        Assert.Equal(2, chunks.Count);

        JsonObject finishChunk = Parse(chunks[0]);
        AssertChunkEnvelope(finishChunk, state);
        Assert.Empty(finishChunk["choices"]![0]!["delta"]!.AsObject());
        Assert.Equal("stop", (string?)finishChunk["choices"]![0]!["finish_reason"]);

        JsonObject usageChunk = Parse(chunks[1]);
        AssertChunkEnvelope(usageChunk, state);
        Assert.Empty(usageChunk["choices"]!.AsArray());
        Assert.Equal(7, (int?)usageChunk["usage"]!["prompt_tokens"]);
        Assert.Equal(3, (int?)usageChunk["usage"]!["completion_tokens"]);
        Assert.Equal(10, (int?)usageChunk["usage"]!["total_tokens"]);
        Assert.True(state.Finished);
    }

    [Fact]
    public void Stream_DoneWithoutCounts_OmitsUsageChunk()
    {
        OllamaToOpenAiStreamState state = NewState();
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"content":"x"},"done":false}""");

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"done":true}""");

        Assert.Single(chunks);
        Assert.Equal("stop", (string?)Parse(chunks[0])["choices"]![0]!["finish_reason"]);
    }

    [Fact]
    public void Stream_DoneAfterToolCalls_ForcesToolCallsFinishReason()
    {
        OllamaToOpenAiStreamState state = NewState();
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"tool_calls":[{"function":{"name":"f","arguments":{}}}]},"done":false}""");

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"done":true,"done_reason":"stop"}""");

        Assert.Single(chunks);
        Assert.Equal("tool_calls", (string?)Parse(chunks[0])["choices"]![0]!["finish_reason"]);
    }

    [Fact]
    public void Stream_DoneReasonLength_MapsToLength()
    {
        OllamaToOpenAiStreamState state = NewState();
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"content":"x"},"done":false}""");

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"done":true,"done_reason":"length"}""");

        Assert.Equal("length", (string?)Parse(chunks[0])["choices"]![0]!["finish_reason"]);
    }

    [Fact]
    public void Stream_SecondDoneLine_IsIgnored()
    {
        OllamaToOpenAiStreamState state = NewState();
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"content":"x"},"done":false}""");
        OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"done":true,"eval_count":1}""");

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"done":true,"eval_count":1}""");

        Assert.Empty(chunks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{truncated")]
    public void Stream_BlankOrUnparseableLine_ReturnsEmpty(string line)
    {
        OllamaToOpenAiStreamState state = NewState();

        Assert.Empty(OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, line));
        Assert.False(state.RoleSent);
    }

    [Fact]
    public void Stream_EmptyContentNotDone_EmitsNothing()
    {
        OllamaToOpenAiStreamState state = NewState();

        Assert.Empty(OllamaFormatTranslator.OllamaChunkToOpenAiChunks(state, """{"message":{"role":"assistant","content":""},"done":false}"""));
        Assert.False(state.RoleSent);
    }

    [Fact]
    public void Stream_CreatedTimestamp_IsStableAcrossChunks()
    {
        OllamaToOpenAiStreamState state = NewState();

        IReadOnlyList<string> first = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"content":"a"},"done":false}""");
        IReadOnlyList<string> second = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"content":"b"},"done":false}""");

        Assert.Equal((long?)Parse(first[0])["created"], (long?)Parse(second[0])["created"]);
    }

    [Fact]
    public void Stream_NoNullsAreEmittedInAnyChunk()
    {
        OllamaToOpenAiStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"message":{"content":"x"},"done":false}""");
        IReadOnlyList<string> done = OllamaFormatTranslator.OllamaChunkToOpenAiChunks(
            state, """{"done":true,"prompt_eval_count":1,"eval_count":2}""");

        foreach (string chunk in chunks.Concat(done))
        {
            Assert.DoesNotContain("null", chunk);
        }
    }
}