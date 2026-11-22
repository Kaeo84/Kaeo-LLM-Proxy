using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Contract tests for the reverse streaming direction of the pure OpenAI &lt;-&gt; Ollama
/// wire-format translator: OpenAI chat.completion.chunk payloads become Ollama NDJSON
/// chunks. Outputs are parsed with JsonNode and specific field values are asserted.
/// </summary>
public class OllamaReverseStreamTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static OpenAiToOllamaStreamState NewState() =>
        OllamaFormatTranslator.CreateOpenAiToOllamaState("client-model");

    private static void AssertChunkEnvelope(JsonObject chunk, OpenAiToOllamaStreamState state)
    {
        Assert.Equal(state.ClientModel, (string?)chunk["model"]);
        Assert.Equal(state.CreatedAt, (string?)chunk["created_at"]);
    }

    [Fact]
    public void Reverse_ContentDeltas_StreamAsAssistantMessagesWithDoneFalse()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> first = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"}}]}""");

        Assert.Single(first);
        JsonObject chunk = Parse(first[0]);
        AssertChunkEnvelope(chunk, state);
        Assert.Equal("assistant", (string?)chunk["message"]!["role"]);
        Assert.Equal("Hel", (string?)chunk["message"]!["content"]);
        Assert.False((bool?)chunk["done"]);
        Assert.False(chunk.ContainsKey("done_reason"));
        Assert.False(chunk["message"]!.AsObject().ContainsKey("thinking"));
        Assert.False(chunk["message"]!.AsObject().ContainsKey("tool_calls"));
        Assert.DoesNotContain('\n', first[0]);

        IReadOnlyList<string> second = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"content":"lo"}}]}""");

        Assert.Single(second);
        Assert.Equal("lo", (string?)Parse(second[0])["message"]!["content"]);
        AssertChunkEnvelope(Parse(second[0]), state);
        Assert.False(state.Finished);
    }

    [Fact]
    public void Reverse_ReasoningContent_MapsToThinking()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"reasoning_content":"hmm"}}]}""");

        Assert.Single(chunks);
        JsonObject chunk = Parse(chunks[0]);
        Assert.Equal("assistant", (string?)chunk["message"]!["role"]);
        Assert.Equal("hmm", (string?)chunk["message"]!["thinking"]);
        Assert.False((bool?)chunk["done"]);
    }

    [Fact]
    public void Reverse_ContentAndThinkingInSameDelta_EmitsContentChunkThenThinkingChunk()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"content":"answer","reasoning_content":"ponder"}}]}""");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("answer", (string?)Parse(chunks[0])["message"]!["content"]);
        Assert.Equal("ponder", (string?)Parse(chunks[1])["message"]!["thinking"]);
    }

    [Fact]
    public void Reverse_EmptyContentDeltaWithoutFinish_EmitsNothing()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""");

        Assert.Empty(chunks);
        Assert.False(state.Finished);
    }

    [Fact]
    public void Reverse_ToolCallFragments_AccumulateAcrossChunksAndEmitOnTerminal()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> first = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":0,"id":"call_one","type":"function","function":{"name":"get_weather","arguments":"{\"ci"}}
            ]}}]}
            """);
        IReadOnlyList<string> second = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":0,"function":{"arguments":"ty\":\"Oslo\"}"}}
            ]}}]}
            """);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.False(state.Finished);

        IReadOnlyList<string> terminal = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");

        Assert.Single(terminal);
        JsonObject chunk = Parse(terminal[0]);
        AssertChunkEnvelope(chunk, state);
        Assert.True((bool?)chunk["done"]);
        Assert.Equal("tool_calls", (string?)chunk["done_reason"]);
        JsonArray toolCalls = chunk["message"]!["tool_calls"]!.AsArray();
        Assert.Single(toolCalls);
        Assert.Equal("call_one", (string?)toolCalls[0]!["id"]);
        Assert.Equal("get_weather", (string?)toolCalls[0]!["function"]!["name"]);
        Assert.Equal("Oslo", (string?)toolCalls[0]!["function"]!["arguments"]!["city"]);
        Assert.True(state.Finished);
    }

    [Fact]
    public void Reverse_ToolCallsAcrossIndexes_EmitInIndexOrderWithConcatenatedNames()
    {
        OpenAiToOllamaStreamState state = NewState();

        OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":1,"id":"call_b","function":{"name":"sec","arguments":"{\"b\""}},
                {"index":0,"id":"call_a","function":{"name":"fir","arguments":"{\"a\""}}
            ]}}]}
            """);
        OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":0,"function":{"name":"st","arguments":":1}"}},
                {"index":1,"function":{"name":"ond","arguments":":2}"}}
            ]}}]}
            """);

        IReadOnlyList<string> terminal = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Single(terminal);
        JsonObject chunk = Parse(terminal[0]);
        Assert.Equal("tool_calls", (string?)chunk["done_reason"]);
        JsonArray toolCalls = chunk["message"]!["tool_calls"]!.AsArray();
        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("call_a", (string?)toolCalls[0]!["id"]);
        Assert.Equal("first", (string?)toolCalls[0]!["function"]!["name"]);
        Assert.Equal(1, (int?)toolCalls[0]!["function"]!["arguments"]!["a"]);
        Assert.Equal("call_b", (string?)toolCalls[1]!["id"]);
        Assert.Equal("second", (string?)toolCalls[1]!["function"]!["name"]);
        Assert.Equal(2, (int?)toolCalls[1]!["function"]!["arguments"]!["b"]);
    }

    [Fact]
    public void Reverse_ToolCallWithInvalidArguments_UsesEmptyObject()
    {
        OpenAiToOllamaStreamState state = NewState();

        OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":0,"function":{"name":"f","arguments":"not json"}}
            ]}}]}
            """);

        IReadOnlyList<string> terminal = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");

        JsonObject toolCall = Parse(terminal[0])["message"]!["tool_calls"]![0]!.AsObject();
        Assert.Empty(toolCall["function"]!["arguments"]!.AsObject());
        Assert.False(toolCall.ContainsKey("id"));
    }

    [Fact]
    public void Reverse_FinishReasonStop_EmitsTerminalStopChunkAndSetsFinished()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Single(chunks);
        JsonObject chunk = Parse(chunks[0]);
        AssertChunkEnvelope(chunk, state);
        Assert.True((bool?)chunk["done"]);
        Assert.Equal("stop", (string?)chunk["done_reason"]);
        Assert.Equal("assistant", (string?)chunk["message"]!["role"]);
        Assert.Equal("", (string?)chunk["message"]!["content"]);
        Assert.False(chunk.ContainsKey("prompt_eval_count"));
        Assert.False(chunk.ContainsKey("eval_count"));
        Assert.True(state.Finished);
    }

    [Fact]
    public void Reverse_FinishReasonLength_MapsToLength()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""");

        Assert.Equal("length", (string?)Parse(chunks[0])["done_reason"]);
    }

    [Fact]
    public void Reverse_UnknownFinishReason_DefaultsToStop()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"content_filter"}]}""");

        Assert.Equal("stop", (string?)Parse(chunks[0])["done_reason"]);
    }

    [Fact]
    public void Reverse_UsageOnTerminalChunk_IncludesTokenCounts()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}
            """);

        Assert.Single(chunks);
        JsonObject chunk = Parse(chunks[0]);
        Assert.True((bool?)chunk["done"]);
        Assert.Equal(11, (int?)chunk["prompt_eval_count"]);
        Assert.Equal(7, (int?)chunk["eval_count"]);
    }

    [Fact]
    public void Reverse_UsageOnlyChunk_EmitsNothingAndCountsAppearOnLaterTerminal()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> usageChunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":5,"total_tokens":25}}
            """);

        Assert.Empty(usageChunks);
        Assert.False(state.Finished);

        IReadOnlyList<string> terminal = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Single(terminal);
        JsonObject chunk = Parse(terminal[0]);
        Assert.Equal(20, (int?)chunk["prompt_eval_count"]);
        Assert.Equal(5, (int?)chunk["eval_count"]);
    }

    [Fact]
    public void Reverse_TerminalWithContentDelta_EmitsContentChunkThenTerminal()
    {
        OpenAiToOllamaStreamState state = NewState();

        IReadOnlyList<string> chunks = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"content":"tail"},"finish_reason":"stop"}]}""");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("tail", (string?)Parse(chunks[0])["message"]!["content"]);
        Assert.False((bool?)Parse(chunks[0])["done"]);
        Assert.True((bool?)Parse(chunks[1])["done"]);
        Assert.Equal("stop", (string?)Parse(chunks[1])["done_reason"]);
    }

    [Fact]
    public void Reverse_SecondTerminalChunk_IsIgnored()
    {
        OpenAiToOllamaStreamState state = NewState();

        OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");
        IReadOnlyList<string> second = OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Empty(second);
        Assert.True(state.Finished);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"choices\":")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Reverse_BlankOrUnparseableChunk_ReturnsEmpty(string chunk)
    {
        OpenAiToOllamaStreamState state = NewState();

        Assert.Empty(OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, chunk));
        Assert.False(state.Finished);
    }

    [Fact]
    public void Reverse_NoNullsOrNewlinesAreEmittedInAnyChunk()
    {
        OpenAiToOllamaStreamState state = NewState();

        List<string> all = [];
        all.AddRange(OllamaFormatTranslator.OpenAiChunkToOllamaChunks(
            state, """{"choices":[{"index":0,"delta":{"role":"assistant","content":"ok","reasoning_content":"think"}}]}"""));
        all.AddRange(OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{"tool_calls":[
                {"index":0,"id":"call_x","function":{"name":"f","arguments":"{}"}}
            ]}}]}
            """));
        all.AddRange(OllamaFormatTranslator.OpenAiChunkToOllamaChunks(state, """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2}}
            """));

        Assert.NotEmpty(all);
        foreach (string chunk in all)
        {
            Assert.DoesNotContain("null", chunk);
            Assert.DoesNotContain('\n', chunk);
        }
    }
}