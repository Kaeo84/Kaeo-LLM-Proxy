using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Translation;

/// <summary>
/// Covers all six AnthropicFormatTranslator API areas: request translation both directions,
/// non-streaming response translation both directions, and streaming event translation both
/// directions, including SSE framing and malformed-input degradation.
/// </summary>
public class AnthropicFormatTranslatorTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static string DataOf(string sseEvent)
    {
        int start = sseEvent.IndexOf("\ndata: ", StringComparison.Ordinal) + "\ndata: ".Length;
        return sseEvent[start..].TrimEnd('\n');
    }

    private static void AssertSseFraming(string sseEvent, string expectedType)
    {
        Assert.StartsWith("event: " + expectedType + "\n", sseEvent);
        Assert.Contains("\ndata: ", sseEvent);
        Assert.EndsWith("\n\n", sseEvent);
        Assert.Equal(expectedType, (string)Parse(DataOf(sseEvent))["type"]!);
    }

    private static string OpenAiChunk(string? deltaJson, string? finishReason = null, string? usageJson = null)
    {
        JsonObject choice = new() { ["index"] = 0, ["delta"] = deltaJson is null ? new JsonObject() : Parse(deltaJson) };
        if (finishReason is not null)
            choice["finish_reason"] = finishReason;
        JsonObject chunk = new()
        {
            ["id"] = "chatcmpl-up",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray { choice },
        };
        if (usageJson is not null)
            chunk["usage"] = Parse(usageJson);
        return chunk.ToJsonString();
    }

    // ==================================================================
    // 1. OpenAI request -> Anthropic request
    // ==================================================================

    [Fact]
    public void OpenAiRequest_SystemMessages_ExtractedAndJoined()
    {
        string openAi = """
        {"model":"gpt-x","messages":[
          {"role":"system","content":"Be brief."},
          {"role":"system","content":"Be kind."},
          {"role":"user","content":"Hi"}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "claude-up", stream: false, defaultMaxTokens: 1024));

        Assert.Equal("claude-up", (string)result["model"]!);
        Assert.Equal("Be brief.\n\nBe kind.", (string)result["system"]!);
        JsonArray messages = (JsonArray)result["messages"]!;
        Assert.Single(messages);
        Assert.Equal("user", (string)messages[0]!["role"]!);
        Assert.Equal("Hi", (string)messages[0]!["content"]!);
        Assert.Equal(1024, (int)result["max_tokens"]!);
        Assert.Null(result["stream"]);
    }

    [Fact]
    public void OpenAiRequest_SystemContentParts_Concatenated()
    {
        string openAi = """
        {"messages":[{"role":"system","content":[{"type":"text","text":"A"},{"type":"text","text":"B"}]},
                     {"role":"user","content":"x"}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        Assert.Equal("AB", (string)result["system"]!);
    }

    [Fact]
    public void OpenAiRequest_NoSystem_SystemOmitted()
    {
        string openAi = """{"messages":[{"role":"user","content":"x"}]}""";
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        Assert.Null(result["system"]);
    }

    [Theory]
    [InlineData("""{"max_tokens":256,"messages":[]}""", 256)]
    [InlineData("""{"max_completion_tokens":512,"messages":[]}""", 512)]
    [InlineData("""{"max_tokens":0,"max_completion_tokens":77,"messages":[]}""", 77)]
    [InlineData("""{"messages":[]}""", 4096)]
    public void OpenAiRequest_MaxTokens_AlwaysEmitted(string openAi, int expected)
    {
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 4096));
        Assert.Equal(expected, (int)result["max_tokens"]!);
    }

    [Fact]
    public void OpenAiRequest_StreamTrue_Emitted()
    {
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic("""{"messages":[]}""", "m", stream: true, 16));
        Assert.True((bool)result["stream"]!);
    }

    [Fact]
    public void OpenAiRequest_AssistantToolCalls_BecomeToolUseBlocks()
    {
        string openAi = """
        {"messages":[
          {"role":"assistant","content":"Let me check.","tool_calls":[
            {"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Oslo\"}"}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        JsonArray blocks = (JsonArray)result["messages"]![0]!["content"]!;
        Assert.Equal(2, blocks.Count);
        Assert.Equal("text", (string)blocks[0]!["type"]!);
        Assert.Equal("Let me check.", (string)blocks[0]!["text"]!);
        Assert.Equal("tool_use", (string)blocks[1]!["type"]!);
        Assert.Equal("call_1", (string)blocks[1]!["id"]!);
        Assert.Equal("get_weather", (string)blocks[1]!["name"]!);
        Assert.Equal("Oslo", (string)blocks[1]!["input"]!["city"]!);
    }

    [Fact]
    public void OpenAiRequest_ToolCallMissingId_GeneratesTooluId()
    {
        string openAi = """
        {"messages":[{"role":"assistant","tool_calls":[
          {"type":"function","function":{"name":"f","arguments":"not json"}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        JsonNode block = result["messages"]![0]!["content"]![0]!;
        string id = (string)block["id"]!;
        Assert.StartsWith("toolu_", id);
        Assert.Equal(30, id.Length);
        Assert.Empty((JsonObject)block["input"]!);
    }

    [Fact]
    public void OpenAiRequest_ToolRoleMessage_BecomesToolResultBlock()
    {
        string openAi = """
        {"messages":[{"role":"tool","tool_call_id":"call_9","content":"sunny"}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        JsonNode msg = result["messages"]![0]!;
        Assert.Equal("user", (string)msg["role"]!);
        JsonNode block = msg["content"]![0]!;
        Assert.Equal("tool_result", (string)block["type"]!);
        Assert.Equal("call_9", (string)block["tool_use_id"]!);
        Assert.Equal("sunny", (string)block["content"]!);
    }

    [Fact]
    public void OpenAiRequest_ImageParts_BecomeBase64ImageBlocks()
    {
        string openAi = """
        {"messages":[{"role":"user","content":[
          {"type":"text","text":"what is this?"},
          {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,QUJD"}},
          {"type":"image_url","image_url":{"url":"https://example.com/plain"}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        JsonArray blocks = (JsonArray)result["messages"]![0]!["content"]!;
        Assert.Equal(3, blocks.Count);
        Assert.Equal("text", (string)blocks[0]!["type"]!);
        Assert.Equal("image", (string)blocks[1]!["type"]!);
        Assert.Equal("base64", (string)blocks[1]!["source"]!["type"]!);
        Assert.Equal("image/jpeg", (string)blocks[1]!["source"]!["media_type"]!);
        Assert.Equal("QUJD", (string)blocks[1]!["source"]!["data"]!);
        Assert.Equal("image/png", (string)blocks[2]!["source"]!["media_type"]!);
    }

    [Fact]
    public void OpenAiRequest_Tools_BecomeInputSchemaTools()
    {
        string openAi = """
        {"messages":[],"tools":[
          {"type":"function","function":{"name":"lookup","description":"Look up",
           "parameters":{"type":"object","properties":{"q":{"type":"string"}}}}}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        JsonNode tool = result["tools"]![0]!;
        Assert.Equal("lookup", (string)tool["name"]!);
        Assert.Equal("Look up", (string)tool["description"]!);
        Assert.Equal("object", (string)tool["input_schema"]!["type"]!);
        Assert.Equal("string", (string)tool["input_schema"]!["properties"]!["q"]!["type"]!);
    }

    [Theory]
    [InlineData("\"auto\"", "auto", null)]
    [InlineData("\"required\"", "any", null)]
    [InlineData("""{"type":"function","function":{"name":"lookup"}}""", "tool", "lookup")]
    public void OpenAiRequest_ToolChoice_Mapped(string openAiChoice, string expectedType, string? expectedName)
    {
        string openAi = $$"""{"messages":[],"tool_choice":{{openAiChoice}}}""";
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        Assert.Equal(expectedType, (string)result["tool_choice"]!["type"]!);
        if (expectedName is not null)
            Assert.Equal(expectedName, (string)result["tool_choice"]!["name"]!);
    }

    [Fact]
    public void OpenAiRequest_ToolChoiceNone_OmitsToolsAndChoice()
    {
        string openAi = """
        {"messages":[],"tool_choice":"none",
         "tools":[{"type":"function","function":{"name":"f","parameters":{}}}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        Assert.Null(result["tools"]);
        Assert.Null(result["tool_choice"]);
    }

    [Fact]
    public void OpenAiRequest_StopStringOrArray_BecomesStopSequences()
    {
        JsonNode fromString = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(
            """{"messages":[],"stop":"END"}""", "m", false, 16));
        Assert.Equal("END", (string)fromString["stop_sequences"]![0]!);

        JsonNode fromArray = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(
            """{"messages":[],"stop":["A","B"]}""", "m", false, 16));
        Assert.Equal(2, ((JsonArray)fromArray["stop_sequences"]!).Count);
        Assert.Equal("B", (string)fromArray["stop_sequences"]![1]!);
    }

    [Fact]
    public void OpenAiRequest_SamplingParams_Copied()
    {
        string openAi = """{"messages":[],"temperature":0.7,"top_p":0.9,"top_k":40}""";
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic(openAi, "m", false, 16));
        Assert.Equal(0.7, (double)result["temperature"]!);
        Assert.Equal(0.9, (double)result["top_p"]!);
        Assert.Equal(40, (int)result["top_k"]!);
    }

    [Fact]
    public void OpenAiRequest_MalformedJson_DegradesToMinimalRequest()
    {
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiRequestToAnthropic("{not json", "m", true, 333));
        Assert.Equal("m", (string)result["model"]!);
        Assert.Equal(333, (int)result["max_tokens"]!);
        Assert.True((bool)result["stream"]!);
        Assert.Empty((JsonArray)result["messages"]!);
    }

    // ==================================================================
    // 2. Anthropic request -> OpenAI request
    // ==================================================================

    [Fact]
    public void AnthropicRequest_SystemString_BecomesLeadingSystemMessage()
    {
        string anthropic = """
        {"model":"claude","max_tokens":100,"system":"Be brief.",
         "messages":[{"role":"user","content":"Hi"}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "gpt-up", stream: true));
        Assert.Equal("gpt-up", (string)result["model"]!);
        Assert.True((bool)result["stream"]!);
        Assert.Equal(100, (int)result["max_tokens"]!);
        JsonArray messages = (JsonArray)result["messages"]!;
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", (string)messages[0]!["role"]!);
        Assert.Equal("Be brief.", (string)messages[0]!["content"]!);
        Assert.Equal("user", (string)messages[1]!["role"]!);
    }

    [Fact]
    public void AnthropicRequest_SystemBlockArray_Concatenated()
    {
        string anthropic = """
        {"max_tokens":10,"system":[{"type":"text","text":"A"},{"type":"text","text":"B"}],"messages":[]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        Assert.Equal("AB", (string)result["messages"]![0]!["content"]!);
        Assert.False((bool)result["stream"]!);
    }

    [Fact]
    public void AnthropicRequest_ToolResultBlocks_SplitIntoToolMessages()
    {
        string anthropic = """
        {"max_tokens":10,"messages":[{"role":"user","content":[
          {"type":"tool_result","tool_use_id":"toolu_1","content":"sunny"},
          {"type":"tool_result","tool_use_id":"toolu_2","content":[{"type":"text","text":"windy"}]},
          {"type":"text","text":"thanks"}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        JsonArray messages = (JsonArray)result["messages"]!;
        Assert.Equal(3, messages.Count);
        Assert.Equal("tool", (string)messages[0]!["role"]!);
        Assert.Equal("toolu_1", (string)messages[0]!["tool_call_id"]!);
        Assert.Equal("sunny", (string)messages[0]!["content"]!);
        Assert.Equal("tool", (string)messages[1]!["role"]!);
        Assert.Equal("windy", (string)messages[1]!["content"]!);
        Assert.Equal("user", (string)messages[2]!["role"]!);
        Assert.Equal("thanks", (string)messages[2]!["content"]!);
    }

    [Fact]
    public void AnthropicRequest_AssistantToolUse_BecomesToolCalls()
    {
        string anthropic = """
        {"max_tokens":10,"messages":[{"role":"assistant","content":[
          {"type":"text","text":"checking"},
          {"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Oslo"}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        JsonNode msg = result["messages"]![0]!;
        Assert.Equal("assistant", (string)msg["role"]!);
        Assert.Equal("checking", (string)msg["content"]!);
        JsonNode call = msg["tool_calls"]![0]!;
        Assert.Equal("toolu_1", (string)call["id"]!);
        Assert.Equal("function", (string)call["type"]!);
        Assert.Equal("get_weather", (string)call["function"]!["name"]!);
        JsonNode args = Parse((string)call["function"]!["arguments"]!);
        Assert.Equal("Oslo", (string)args["city"]!);
    }

    [Fact]
    public void AnthropicRequest_ToolUseWithoutText_ContentOmitted()
    {
        string anthropic = """
        {"max_tokens":10,"messages":[{"role":"assistant","content":[
          {"type":"tool_use","id":"t","name":"f","input":{}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        Assert.Null(result["messages"]![0]!["content"]);
        Assert.Equal("{}", (string)result["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!);
    }

    [Fact]
    public void AnthropicRequest_ImageBlock_BecomesDataUrlPart()
    {
        string anthropic = """
        {"max_tokens":10,"messages":[{"role":"user","content":[
          {"type":"text","text":"see"},
          {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"QUJD"}}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        JsonArray parts = (JsonArray)result["messages"]![0]!["content"]!;
        Assert.Equal("text", (string)parts[0]!["type"]!);
        Assert.Equal("image_url", (string)parts[1]!["type"]!);
        Assert.Equal("data:image/jpeg;base64,QUJD", (string)parts[1]!["image_url"]!["url"]!);
    }

    [Fact]
    public void AnthropicRequest_TextOnlyBlocks_CollapseToString()
    {
        string anthropic = """
        {"max_tokens":10,"messages":[{"role":"user","content":[
          {"type":"text","text":"a"},{"type":"text","text":"b"}]}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        Assert.Equal("ab", (string)result["messages"]![0]!["content"]!);
    }

    [Fact]
    public void AnthropicRequest_ToolsStopAndSampling_Mapped()
    {
        string anthropic = """
        {"max_tokens":64,"temperature":0.2,"top_p":0.8,"top_k":5,"stop_sequences":["X","Y"],
         "messages":[],
         "tools":[{"name":"lookup","description":"d","input_schema":{"type":"object"}}],
         "tool_choice":{"type":"tool","name":"lookup"}}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        Assert.Equal(64, (int)result["max_tokens"]!);
        Assert.Equal(0.2, (double)result["temperature"]!);
        Assert.Equal(0.8, (double)result["top_p"]!);
        Assert.Equal(5, (int)result["top_k"]!);
        Assert.Equal("Y", (string)result["stop"]![1]!);
        JsonNode fn = result["tools"]![0]!["function"]!;
        Assert.Equal("function", (string)result["tools"]![0]!["type"]!);
        Assert.Equal("lookup", (string)fn["name"]!);
        Assert.Equal("d", (string)fn["description"]!);
        Assert.Equal("object", (string)fn["parameters"]!["type"]!);
        Assert.Equal("function", (string)result["tool_choice"]!["type"]!);
        Assert.Equal("lookup", (string)result["tool_choice"]!["function"]!["name"]!);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "required")]
    public void AnthropicRequest_AutoAnyToolChoice_Mapped(string anthropicType, string expected)
    {
        string anthropic = $$$"""{"max_tokens":10,"messages":[],"tool_choice":{"type":"{{{anthropicType}}}"}}""";
        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi(anthropic, "m", false));
        Assert.Equal(expected, (string)result["tool_choice"]!);
    }

    [Fact]
    public void AnthropicRequest_MalformedJson_DegradesToMinimalRequest()
    {
        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicRequestToOpenAi("]bad[", "m", true));
        Assert.Equal("m", (string)result["model"]!);
        Assert.True((bool)result["stream"]!);
        Assert.Empty((JsonArray)result["messages"]!);
    }

    // ==================================================================
    // 3. Anthropic response -> OpenAI response
    // ==================================================================

    [Fact]
    public void AnthropicResponse_TextThinkingToolUse_MappedToMessage()
    {
        string anthropic = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude",
         "content":[
           {"type":"thinking","thinking":"hmm"},
           {"type":"text","text":"Hello "},
           {"type":"text","text":"world"},
           {"type":"tool_use","id":"toolu_1","name":"f","input":{"a":1}}],
         "stop_reason":"tool_use",
         "usage":{"input_tokens":10,"output_tokens":20}}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicResponseToOpenAi(anthropic, "client-model", "chatcmpl-42"));
        Assert.Equal("chatcmpl-42", (string)result["id"]!);
        Assert.Equal("chat.completion", (string)result["object"]!);
        Assert.Equal("client-model", (string)result["model"]!);
        Assert.True((long)result["created"]! > 0);

        JsonNode choice = result["choices"]![0]!;
        JsonNode message = choice["message"]!;
        Assert.Equal("assistant", (string)message["role"]!);
        Assert.Equal("Hello world", (string)message["content"]!);
        Assert.Equal("hmm", (string)message["reasoning_content"]!);
        JsonNode call = message["tool_calls"]![0]!;
        Assert.Equal("toolu_1", (string)call["id"]!);
        Assert.Equal("f", (string)call["function"]!["name"]!);
        Assert.Equal(1, (int)Parse((string)call["function"]!["arguments"]!)["a"]!);
        Assert.Equal("tool_calls", (string)choice["finish_reason"]!);

        Assert.Equal(10, (int)result["usage"]!["prompt_tokens"]!);
        Assert.Equal(20, (int)result["usage"]!["completion_tokens"]!);
        Assert.Equal(30, (int)result["usage"]!["total_tokens"]!);
    }

    [Theory]
    [InlineData("max_tokens", "length")]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData(null, "stop")]
    public void AnthropicResponse_StopReason_Mapped(string? stopReason, string expected)
    {
        string anthropic = stopReason is null
            ? """{"content":[{"type":"text","text":"x"}],"stop_reason":null}"""
            : $$"""{"content":[{"type":"text","text":"x"}],"stop_reason":"{{stopReason}}"}""";

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicResponseToOpenAi(anthropic, "m", "id"));
        Assert.Equal(expected, (string)result["choices"]![0]!["finish_reason"]!);
    }

    [Fact]
    public void AnthropicResponse_ToolCallsForceToolCallsFinish()
    {
        string anthropic = """
        {"content":[{"type":"tool_use","id":"t","name":"f","input":{}}],"stop_reason":"end_turn"}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicResponseToOpenAi(anthropic, "m", "id"));
        Assert.Equal("tool_calls", (string)result["choices"]![0]!["finish_reason"]!);
        Assert.Null(result["choices"]![0]!["message"]!["content"]);
    }

    [Fact]
    public void AnthropicResponse_TextOnly_OmitsReasoningAndToolCalls()
    {
        string anthropic = """{"content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn"}""";
        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicResponseToOpenAi(anthropic, "m", "id"));
        JsonNode message = result["choices"]![0]!["message"]!;
        Assert.Equal("hi", (string)message["content"]!);
        Assert.Null(message["reasoning_content"]);
        Assert.Null(message["tool_calls"]);
    }

    [Fact]
    public void AnthropicResponse_MalformedJson_DegradesToValidCompletion()
    {
        JsonNode result = Parse(AnthropicFormatTranslator.AnthropicResponseToOpenAi("oops", "m", "id"));
        Assert.Equal("chat.completion", (string)result["object"]!);
        Assert.Equal("stop", (string)result["choices"]![0]!["finish_reason"]!);
        Assert.Equal(0, (int)result["usage"]!["total_tokens"]!);
    }

    // ==================================================================
    // 4. OpenAI response -> Anthropic response
    // ==================================================================

    [Fact]
    public void OpenAiResponse_ContentReasoningToolCalls_BecomeOrderedBlocks()
    {
        string openAi = """
        {"id":"chatcmpl-1","object":"chat.completion",
         "choices":[{"index":0,"finish_reason":"tool_calls","message":{
           "role":"assistant","content":"answer","reasoning_content":"thought",
           "tool_calls":[{"id":"call_1","type":"function","function":{"name":"f","arguments":"{\"x\":2}"}}]}}],
         "usage":{"prompt_tokens":7,"completion_tokens":9}}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiResponseToAnthropic(openAi, "client-model", "msg_42"));
        Assert.Equal("msg_42", (string)result["id"]!);
        Assert.Equal("message", (string)result["type"]!);
        Assert.Equal("assistant", (string)result["role"]!);
        Assert.Equal("client-model", (string)result["model"]!);

        JsonArray blocks = (JsonArray)result["content"]!;
        Assert.Equal(3, blocks.Count);
        Assert.Equal("thinking", (string)blocks[0]!["type"]!);
        Assert.Equal("thought", (string)blocks[0]!["thinking"]!);
        Assert.Equal("text", (string)blocks[1]!["type"]!);
        Assert.Equal("answer", (string)blocks[1]!["text"]!);
        Assert.Equal("tool_use", (string)blocks[2]!["type"]!);
        Assert.Equal("call_1", (string)blocks[2]!["id"]!);
        Assert.Equal("f", (string)blocks[2]!["name"]!);
        Assert.Equal(2, (int)blocks[2]!["input"]!["x"]!);

        Assert.Equal("tool_use", (string)result["stop_reason"]!);
        Assert.Equal(7, (int)result["usage"]!["input_tokens"]!);
        Assert.Equal(9, (int)result["usage"]!["output_tokens"]!);
    }

    [Theory]
    [InlineData("length", "max_tokens")]
    [InlineData("stop", "end_turn")]
    [InlineData("content_filter", "end_turn")]
    public void OpenAiResponse_FinishReason_Mapped(string finishReason, string expected)
    {
        string openAi = $$$"""
        {"choices":[{"index":0,"finish_reason":"{{{finishReason}}}","message":{"role":"assistant","content":"x"}}]}
        """;

        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiResponseToAnthropic(openAi, "m", "id"));
        Assert.Equal(expected, (string)result["stop_reason"]!);
    }

    [Fact]
    public void OpenAiResponse_EmptyMessage_EmitsEmptyTextBlock()
    {
        string openAi = """{"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant"}}]}""";
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiResponseToAnthropic(openAi, "m", "id"));
        JsonArray blocks = (JsonArray)result["content"]!;
        Assert.Single(blocks);
        Assert.Equal("text", (string)blocks[0]!["type"]!);
        Assert.Equal(string.Empty, (string)blocks[0]!["text"]!);
    }

    [Fact]
    public void OpenAiResponse_MalformedJson_DegradesToValidMessage()
    {
        JsonNode result = Parse(AnthropicFormatTranslator.OpenAiResponseToAnthropic("{bad", "m", "msg_x"));
        Assert.Equal("msg_x", (string)result["id"]!);
        Assert.Equal("message", (string)result["type"]!);
        Assert.Equal("end_turn", (string)result["stop_reason"]!);
        Assert.Equal("text", (string)result["content"]![0]!["type"]!);
    }

    // ==================================================================
    // 5. Streaming Anthropic -> OpenAI
    // ==================================================================

    [Fact]
    public void AnthropicStream_MessageStart_EmitsRoleChunkOnce()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("client-m", "chatcmpl-7");
        string data = """
        {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant",
         "model":"claude","content":[],"stop_reason":null,"usage":{"input_tokens":11,"output_tokens":0}}}
        """;

        IReadOnlyList<string> chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_start", data);
        Assert.Single(chunks);
        JsonNode chunk = Parse(chunks[0]);
        Assert.Equal("chatcmpl-7", (string)chunk["id"]!);
        Assert.Equal("chat.completion.chunk", (string)chunk["object"]!);
        Assert.Equal("client-m", (string)chunk["model"]!);
        Assert.Equal("assistant", (string)chunk["choices"]![0]!["delta"]!["role"]!);
        Assert.True(state.RoleSent);
        Assert.Equal(11, state.InputTokens);

        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_start", data));
    }

    [Fact]
    public void AnthropicStream_TextDelta_BecomesContentDelta()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        IReadOnlyList<string> chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""");

        Assert.Single(chunks);
        Assert.Equal("Hello", (string)Parse(chunks[0])["choices"]![0]!["delta"]!["content"]!);
    }

    [Fact]
    public void AnthropicStream_ThinkingDelta_BecomesReasoningContent()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        IReadOnlyList<string> chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"pondering"}}""");

        Assert.Equal("pondering", (string)Parse(chunks[0])["choices"]![0]!["delta"]!["reasoning_content"]!);
    }

    [Fact]
    public void AnthropicStream_ToolUseBlock_MapsToStableToolIndices()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");

        IReadOnlyList<string> start1 = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_start",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_a","name":"f","input":{}}}""");
        JsonNode call1 = Parse(start1[0])["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal(0, (int)call1["index"]!);
        Assert.Equal("toolu_a", (string)call1["id"]!);
        Assert.Equal("function", (string)call1["type"]!);
        Assert.Equal("f", (string)call1["function"]!["name"]!);
        Assert.Equal(string.Empty, (string)call1["function"]!["arguments"]!);

        IReadOnlyList<string> args1 = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_delta",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}""");
        JsonNode cont = Parse(args1[0])["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal(0, (int)cont["index"]!);
        Assert.Equal("{\"q\":", (string)cont["function"]!["arguments"]!);
        Assert.Null(cont["id"]);
        Assert.Null(cont["function"]!["name"]);

        IReadOnlyList<string> start2 = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_start",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_b","name":"g","input":{}}}""");
        Assert.Equal(1, (int)Parse(start2[0])["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!);

        IReadOnlyList<string> args2 = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "content_block_delta",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"1}"}}""");
        Assert.Equal(1, (int)Parse(args2[0])["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!);
    }

    [Fact]
    public void AnthropicStream_TextBlockStart_Stop_Signature_Ping_Error_EmitNothing()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "content_block_start",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "content_block_stop",
            """{"type":"content_block_stop","index":0}"""));
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}"""));
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "ping", """{"type":"ping"}"""));
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "error",
            """{"type":"error","error":{"type":"overloaded_error","message":"busy"}}"""));
    }

    [Theory]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("max_tokens", "length")]
    [InlineData("end_turn", "stop")]
    public void AnthropicStream_MessageDelta_EmitsMappedFinish(string stopReason, string expected)
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        IReadOnlyList<string> chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "message_delta",
            $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{stopReason}}}"},"usage":{"output_tokens":25}}""");

        Assert.Single(chunks);
        JsonNode choice = Parse(chunks[0])["choices"]![0]!;
        Assert.Equal(expected, (string)choice["finish_reason"]!);
        Assert.Empty((JsonObject)choice["delta"]!);
        Assert.Equal(25, state.OutputTokens);
        Assert.True(state.FinishSent);
    }

    [Fact]
    public void AnthropicStream_MessageStop_EmitsUsageChunkWithTotals()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_start",
            """{"type":"message_start","message":{"usage":{"input_tokens":11,"output_tokens":0}}}""");
        AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_delta",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":25}}""");

        IReadOnlyList<string> chunks = AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(
            state, "message_stop", """{"type":"message_stop"}""");

        Assert.Single(chunks);
        JsonNode chunk = Parse(chunks[0]);
        Assert.Empty((JsonArray)chunk["choices"]!);
        Assert.Equal(11, (int)chunk["usage"]!["prompt_tokens"]!);
        Assert.Equal(25, (int)chunk["usage"]!["completion_tokens"]!);
        Assert.Equal(36, (int)chunk["usage"]!["total_tokens"]!);
    }

    [Fact]
    public void AnthropicStream_MessageStop_NoTokens_OmitsUsageChunk()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_stop", """{"type":"message_stop"}"""));
    }

    [Fact]
    public void AnthropicStream_MalformedData_EmitsNothing()
    {
        AnthropicToOpenAiStreamState state = AnthropicFormatTranslator.CreateAnthropicToOpenAiState("m", "id");
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "message_start", "{broken"));
        Assert.Empty(AnthropicFormatTranslator.AnthropicEventToOpenAiChunks(state, "content_block_delta", ""));
    }

    // ==================================================================
    // 6. Streaming OpenAI -> Anthropic
    // ==================================================================

    [Fact]
    public void OpenAiStream_FirstChunk_EmitsMessageStartWithFraming()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("client-m", "msg_9");
        IReadOnlyList<string> events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"role":"assistant"}"""));

        Assert.Single(events);
        AssertSseFraming(events[0], "message_start");
        JsonNode message = Parse(DataOf(events[0]))["message"]!;
        Assert.Equal("msg_9", (string)message["id"]!);
        Assert.Equal("message", (string)message["type"]!);
        Assert.Equal("assistant", (string)message["role"]!);
        Assert.Equal("client-m", (string)message["model"]!);
        Assert.Empty((JsonArray)message["content"]!);
        Assert.Equal(0, (int)message["usage"]!["input_tokens"]!);
        Assert.True(state.Started);
    }

    [Fact]
    public void OpenAiStream_TextDeltas_OpenOneTextBlock()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));

        IReadOnlyList<string> first = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"content":"Hel"}"""));
        Assert.Equal(2, first.Count);
        AssertSseFraming(first[0], "content_block_start");
        JsonNode start = Parse(DataOf(first[0]));
        Assert.Equal(0, (int)start["index"]!);
        Assert.Equal("text", (string)start["content_block"]!["type"]!);
        Assert.Equal(string.Empty, (string)start["content_block"]!["text"]!);
        AssertSseFraming(first[1], "content_block_delta");
        JsonNode delta = Parse(DataOf(first[1]));
        Assert.Equal(0, (int)delta["index"]!);
        Assert.Equal("text_delta", (string)delta["delta"]!["type"]!);
        Assert.Equal("Hel", (string)delta["delta"]!["text"]!);

        IReadOnlyList<string> second = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"content":"lo"}"""));
        Assert.Single(second);
        AssertSseFraming(second[0], "content_block_delta");
        Assert.Equal(0, (int)Parse(DataOf(second[0]))["index"]!);
    }

    [Fact]
    public void OpenAiStream_ReasoningDeltas_OpenThinkingBlock()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));

        IReadOnlyList<string> events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"reasoning_content":"hmm"}"""));
        Assert.Equal(2, events.Count);
        AssertSseFraming(events[0], "content_block_start");
        Assert.Equal("thinking", (string)Parse(DataOf(events[0]))["content_block"]!["type"]!);
        AssertSseFraming(events[1], "content_block_delta");
        JsonNode delta = Parse(DataOf(events[1]))["delta"]!;
        Assert.Equal("thinking_delta", (string)delta["type"]!);
        Assert.Equal("hmm", (string)delta["thinking"]!);
    }

    [Fact]
    public void OpenAiStream_ToolCall_ClosesTextBlockAndOpensToolUseBlock()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"content":"hi"}"""));

        IReadOnlyList<string> events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"f","arguments":"{\"a\":"}}]}"""));

        Assert.Equal(3, events.Count);
        AssertSseFraming(events[0], "content_block_stop");
        Assert.Equal(0, (int)Parse(DataOf(events[0]))["index"]!);
        AssertSseFraming(events[1], "content_block_start");
        JsonNode start = Parse(DataOf(events[1]));
        Assert.Equal(1, (int)start["index"]!);
        Assert.Equal("tool_use", (string)start["content_block"]!["type"]!);
        Assert.Equal("call_1", (string)start["content_block"]!["id"]!);
        Assert.Equal("f", (string)start["content_block"]!["name"]!);
        Assert.Empty((JsonObject)start["content_block"]!["input"]!);
        AssertSseFraming(events[2], "content_block_delta");
        JsonNode delta = Parse(DataOf(events[2]));
        Assert.Equal(1, (int)delta["index"]!);
        Assert.Equal("input_json_delta", (string)delta["delta"]!["type"]!);
        Assert.Equal("{\"a\":", (string)delta["delta"]!["partial_json"]!);

        IReadOnlyList<string> continuation = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]}"""));
        Assert.Single(continuation);
        AssertSseFraming(continuation[0], "content_block_delta");
        JsonNode cont = Parse(DataOf(continuation[0]));
        Assert.Equal(1, (int)cont["index"]!);
        Assert.Equal("1}", (string)cont["delta"]!["partial_json"]!);
    }

    [Fact]
    public void OpenAiStream_FinishReason_ClosesBlocksAndEmitsMessageDeltaAndStop()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"content":"hi"}"""));

        IReadOnlyList<string> events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("{}", finishReason: "stop", usageJson: """{"prompt_tokens":4,"completion_tokens":6}"""));

        Assert.Equal(3, events.Count);
        AssertSseFraming(events[0], "content_block_stop");
        Assert.Equal(0, (int)Parse(DataOf(events[0]))["index"]!);
        AssertSseFraming(events[1], "message_delta");
        JsonNode messageDelta = Parse(DataOf(events[1]));
        Assert.Equal("end_turn", (string)messageDelta["delta"]!["stop_reason"]!);
        Assert.Equal(6, (int)messageDelta["usage"]!["output_tokens"]!);
        AssertSseFraming(events[2], "message_stop");
        Assert.True(state.Finished);

        Assert.Empty(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"content":"late"}""")));
    }

    [Theory]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("length", "max_tokens")]
    [InlineData("stop", "end_turn")]
    public void OpenAiStream_FinishReason_Mapped(string finishReason, string expected)
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        IReadOnlyList<string> events = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("{}", finishReason: finishReason));

        AssertSseFraming(events[0], "message_start");
        AssertSseFraming(events[1], "message_delta");
        Assert.Equal(expected, (string)Parse(DataOf(events[1]))["delta"]!["stop_reason"]!);
        AssertSseFraming(events[2], "message_stop");
    }

    [Fact]
    public void OpenAiStream_UsageOnlyChunk_StoresTokensWithoutEvents()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));

        IReadOnlyList<string> usageEvents = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, """{"id":"x","object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":34}}""");
        Assert.Empty(usageEvents);
        Assert.Equal(12, state.InputTokens);
        Assert.Equal(34, state.OutputTokens);

        IReadOnlyList<string> finish = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("{}", finishReason: "stop"));
        Assert.Equal(34, (int)Parse(DataOf(finish[0]))["usage"]!["output_tokens"]!);
    }

    [Fact]
    public void OpenAiStream_MultipleToolCalls_GetSequentialBlockIndices()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}"""));

        IReadOnlyList<string> first = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":""}}]}"""));
        Assert.Single(first);
        Assert.Equal(0, (int)Parse(DataOf(first[0]))["index"]!);

        IReadOnlyList<string> second = AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(
            state, OpenAiChunk("""{"tool_calls":[{"index":1,"id":"c2","function":{"name":"g","arguments":"{}"}}]}"""));
        Assert.Equal(3, second.Count);
        AssertSseFraming(second[0], "content_block_stop");
        Assert.Equal(0, (int)Parse(DataOf(second[0]))["index"]!);
        AssertSseFraming(second[1], "content_block_start");
        Assert.Equal(1, (int)Parse(DataOf(second[1]))["index"]!);
        Assert.Equal("c2", (string)Parse(DataOf(second[1]))["content_block"]!["id"]!);
        AssertSseFraming(second[2], "content_block_delta");
        Assert.Equal("{}", (string)Parse(DataOf(second[2]))["delta"]!["partial_json"]!);
    }

    [Fact]
    public void OpenAiStream_MalformedChunk_EmitsNothing()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        Assert.Empty(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, "not json at all"));
        Assert.False(state.Started);
    }

    [Fact]
    public void OpenAiStream_AllEvents_HaveExactSseFraming()
    {
        OpenAiToAnthropicStreamState state = AnthropicFormatTranslator.CreateOpenAiToAnthropicState("m", "id");
        List<string> all = [];
        all.AddRange(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"role":"assistant"}""")));
        all.AddRange(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"reasoning_content":"r"}""")));
        all.AddRange(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("""{"content":"c"}""")));
        all.AddRange(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state,
            OpenAiChunk("""{"tool_calls":[{"index":0,"id":"c1","function":{"name":"f","arguments":"{}"}}]}""")));
        all.AddRange(AnthropicFormatTranslator.OpenAiChunkToAnthropicEvents(state, OpenAiChunk("{}", finishReason: "tool_calls")));

        Assert.Equal(12, all.Count);
        foreach (string sseEvent in all)
        {
            Assert.StartsWith("event: ", sseEvent);
            Assert.Contains("\ndata: ", sseEvent);
            Assert.EndsWith("\n\n", sseEvent);
            string type = sseEvent["event: ".Length..sseEvent.IndexOf('\n')];
            Assert.Equal(type, (string)Parse(DataOf(sseEvent))["type"]!);
        }
    }
}