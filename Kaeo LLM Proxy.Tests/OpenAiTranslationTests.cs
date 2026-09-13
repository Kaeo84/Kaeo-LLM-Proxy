using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Services.Translation;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kaeo.LLMProxy.Tests;

/// <summary>
/// Phase-B step B1: the pure IR translation layer in Services/Translation. Inbound maps
/// OpenAI-compatible SSE chunks (llama.cpp upstream) to MEAI ChatResponseUpdate values,
/// accumulating streamed tool-call argument fragments by index until the terminal chunk
/// flushes them; outbound renders MEAI ChatMessage history to the strict OpenAI messages
/// shape. These cover the translators directly; legacy-path parity routing is step B2.
/// </summary>
public class OpenAiTranslationTests
{
    private static JsonObject Chunk(string json) => (JsonNode.Parse(json) as JsonObject)!;

    private static ChatResponseUpdate? Parse(string json, OpenAiInbound.StreamState state)
        => OpenAiInbound.ToUpdate(JsonNode.Parse(json) as JsonObject, state);

    [Fact]
    public void ContentAndReasoningDeltasBecomeTypedContents()
    {
        var state = new OpenAiInbound.StreamState();

        ChatResponseUpdate? update = Parse(
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"the answer","reasoning_content":"step by step"},"finish_reason":null}]}""",
            state);

        Assert.NotNull(update);
        Assert.Equal("the answer", Assert.Single(update!.Contents.OfType<TextContent>()).Text);
        Assert.Equal("step by step", Assert.Single(update.Contents.OfType<TextReasoningContent>()).Text);
        Assert.Null(update.FinishReason);
    }

    [Fact]
    public void FragmentOnlyToolCallChunksAccumulateWithoutEmitting()
    {
        var state = new OpenAiInbound.StreamState();

        ChatResponseUpdate? first = Parse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"view","arguments":"{"}}]},"finish_reason":null}]}""",
            state);
        ChatResponseUpdate? second = Parse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"path\"" }}]},"finish_reason":null}]}""",
            state);

        // Nothing is emitted until the call completes; fragments accumulate silently.
        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void TerminalToolCallsChunkFlushesAccumulatedCalls()
    {
        var state = new OpenAiInbound.StreamState();

        Parse("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_9","function":{"name":"view","arguments":"{"}}]},"finish_reason":null}]}""", state);
        Parse("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"path\"" }}]},"finish_reason":null}]}""", state);
        ChatResponseUpdate? done = Parse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":": \"a.txt\"}" }}]},"finish_reason":"tool_calls"}]}""",
            state);

        Assert.NotNull(done);
        FunctionCallContent call = Assert.Single(done!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_9", call.CallId);
        Assert.Equal("view", call.Name);
        Assert.Equal("a.txt", call.Arguments!["path"]);
        Assert.Equal(ChatFinishReason.ToolCalls, done.FinishReason);
    }

    [Fact]
    public void FinishReasonStopMapsToChatFinishReason()
    {
        var state = new OpenAiInbound.StreamState();

        ChatResponseUpdate? update = Parse("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", state);

        Assert.NotNull(update);
        Assert.Equal(ChatFinishReason.Stop, update!.FinishReason);
        Assert.Empty(update.Contents);
    }

    [Fact]
    public void OutboundRendersAssistantToolCallArgumentsAsJsonString()
    {
        ChatMessage message = new(ChatRole.Assistant,
        [
            new TextContent("checking"),
            new FunctionCallContent("c1", "get_weather", new Dictionary<string, object?> { ["city"] = "Seattle", ["days"] = 3L }),
        ]);

        using JsonDocument doc = JsonDocument.Parse(OpenAiOutbound.ToOpenAiMessage(message).ToJsonString());
        JsonElement json = doc.RootElement;

        Assert.Equal("assistant", json.GetProperty("role").GetString());
        Assert.Equal("checking", json.GetProperty("content").GetString());
        JsonElement call = json.GetProperty("tool_calls")[0];
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        // OpenAI wire shape: arguments is a JSON string, not an object.
        string arguments = call.GetProperty("function").GetProperty("arguments").GetString()!;
        using JsonDocument argsDoc = JsonDocument.Parse(arguments);
        Assert.Equal("Seattle", argsDoc.RootElement.GetProperty("city").GetString());
        Assert.Equal(3, argsDoc.RootElement.GetProperty("days").GetInt32());
    }

    [Fact]
    public void OutboundRendersToolResultWithCorrelationId()
    {
        ChatMessage message = new(ChatRole.Tool, [new FunctionResultContent("c1", "port 11434")]);

        using JsonDocument doc = JsonDocument.Parse(OpenAiOutbound.ToOpenAiMessage(message).ToJsonString());
        JsonElement json = doc.RootElement;

        Assert.Equal("tool", json.GetProperty("role").GetString());
        Assert.Equal("c1", json.GetProperty("tool_call_id").GetString());
        Assert.Equal("port 11434", json.GetProperty("content").GetString());
    }

    [Fact]
    public void OutboundCarriesReasoningInReasoningContentField()
    {
        ChatMessage message = new(ChatRole.Assistant, [new TextReasoningContent("planning"), new TextContent("done")]);

        using JsonDocument doc = JsonDocument.Parse(OpenAiOutbound.ToOpenAiMessage(message).ToJsonString());

        Assert.Equal("planning", doc.RootElement.GetProperty("reasoning_content").GetString());
        Assert.Equal("done", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void OutboundArrayPreservesOrderAndRoles()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "instructions"),
            new(ChatRole.User, "hello"),
        ];

        JsonArray array = OpenAiOutbound.ToOpenAiMessages(messages);

        Assert.Equal(2, array.Count);
        Assert.Equal("system", array[0]!["role"]!.GetValue<string>());
        Assert.Equal("hello", array[1]!["content"]!.GetValue<string>());
    }
}
