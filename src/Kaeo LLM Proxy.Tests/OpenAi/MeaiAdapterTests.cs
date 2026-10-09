using System.Text.Json;
using Kaeo.LlmProxy.VSExtension.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies the pure MeaiAdapter mappings between the proxy's Ollama-style wire JSON and
/// Microsoft.Extensions.AI types, especially that model thinking surfaces as
/// <see cref="TextReasoningContent"/> whether the server sends the structured
/// "thinking" field or leaves inline tag blocks in the answer text. Markup in JSON test
/// inputs is written as JSON unicode escapes so the source contains no raw tag sequences.
/// </summary>
public class MeaiAdapterTests
{
    private static ChatResponseUpdate? Parse(
        string line,
        ReasoningSource source = ReasoningSource.Auto,
        ThinkTagStreamSplitter? splitter = null)
    {
        if (source is ReasoningSource.Auto or ReasoningSource.InlineTags && splitter is null)
            splitter = new ThinkTagStreamSplitter();

        using JsonDocument doc = JsonDocument.Parse(line);
        return MeaiAdapter.ToResponseUpdate(doc.RootElement, source, splitter);
    }

    [Fact]
    public void ThinkingFieldBecomesTextReasoningContent()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"the answer","thinking":"step by step"},"done":false}""");

        Assert.NotNull(update);
        Assert.Equal("step by step", Assert.Single(update!.Contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("the answer", Assert.Single(update.Contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public void InlineThinkTagsAreSplitOutOfContent()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"\u003cthink\u003ehidden reasoning\u003c/think\u003epublic answer"},"done":false}""",
            ReasoningSource.InlineTags);

        Assert.NotNull(update);
        Assert.Equal("hidden reasoning", Assert.Single(update!.Contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("public answer", Assert.Single(update.Contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public void AutoPrefersThinkingFieldAndLeavesContentUntouched()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"answer with \u003cthink\u003eleftover","thinking":"structured trace"},"done":false}""");

        Assert.NotNull(update);
        Assert.Equal("structured trace", Assert.Single(update!.Contents.OfType<TextReasoningContent>()).Text);
        // The Auto mode skips inline extraction when a structured thinking field was present,
        // so the raw content reaches the client unchanged.
        Assert.Equal("answer with \u003cthink\u003eleftover", update.Contents.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public void DisabledSourceSuppressesReasoning()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"answer","thinking":"hidden"},"done":false}""",
            ReasoningSource.Disabled);

        Assert.NotNull(update);
        Assert.Empty(update!.Contents.OfType<TextReasoningContent>());
        Assert.Equal("answer", Assert.Single(update.Contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public void ToolCallsWithObjectArgumentsBecomeFunctionCallContent()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c1","function":{"name":"get_weather","arguments":{"city":"Seattle","days":3}}}]},"done":false}""");

        Assert.NotNull(update);
        FunctionCallContent call = Assert.Single(update!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("c1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Seattle", call.Arguments!["city"]);
        Assert.Equal(3L, call.Arguments["days"]);
    }

    [Fact]
    public void ToolCallsWithJsonStringArgumentsAreReparsed()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","tool_calls":[{"id":"c2","function":{"name":"view","arguments":"{\"path\":\"app.config\"}"}}]},"done":false}""");

        Assert.NotNull(update);
        FunctionCallContent call = Assert.Single(update!.Contents.OfType<FunctionCallContent>());
        Assert.Equal("view", call.Name);
        Assert.Equal("app.config", call.Arguments!["path"]);
    }

    [Fact]
    public void DoneReasonMapsToFinishReason()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"go","arguments":{}}}]},"done":true,"done_reason":"tool_calls"}""");

        Assert.NotNull(update);
        Assert.Equal(ChatFinishReason.ToolCalls, update!.FinishReason);
    }

    [Fact]
    public void DoneWithoutMessageStillReportsCompletion()
    {
        ChatResponseUpdate? update = Parse("""{"done":true,"done_reason":"stop"}""");

        Assert.NotNull(update);
        Assert.Empty(update!.Contents);
        Assert.Equal(ChatFinishReason.Stop, update.FinishReason);
    }

    [Fact]
    public void EvalCountsBecomeUsageContent()
    {
        ChatResponseUpdate? update = Parse(
            """{"message":{"role":"assistant","content":"x"},"done":true,"done_reason":"stop","prompt_eval_count":12,"eval_count":5}""");

        Assert.NotNull(update);
        UsageContent usage = Assert.Single(update!.Contents.OfType<UsageContent>());
        Assert.Equal(12, usage.Details.InputTokenCount);
        Assert.Equal(5, usage.Details.OutputTokenCount);
    }

    [Fact]
    public void LineWithoutRenderableContentReturnsNull()
    {
        Assert.Null(Parse("""{"model":"m"}"""));
    }

    [Fact]
    public void AssistantMessageWithMixedContentRoundTripsToOllamaJson()
    {
        ChatMessage message = new(ChatRole.Assistant,
        [
            new TextContent("hi"),
            new TextReasoningContent("thought"),
            new FunctionCallContent("id1", "get_weather", new Dictionary<string, object?> { ["path"] = "x" }),
        ]);

        using JsonDocument doc = JsonDocument.Parse(MeaiAdapter.ToOllamaMessageJson(message).ToJsonString());
        JsonElement json = doc.RootElement;

        Assert.Equal("assistant", json.GetProperty("role").GetString());
        Assert.Equal("hi", json.GetProperty("content").GetString());
        Assert.Equal("thought", json.GetProperty("thinking").GetString());
        Assert.Equal("get_weather", json.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("x", json.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("path").GetString());
    }

    [Fact]
    public void FunctionResultMessageMapsToToolRoleWithCallId()
    {
        ChatMessage message = new(ChatRole.Tool, [new FunctionResultContent("id1", "ok")]);

        using JsonDocument doc = JsonDocument.Parse(MeaiAdapter.ToOllamaMessageJson(message).ToJsonString());
        JsonElement json = doc.RootElement;

        Assert.Equal("tool", json.GetProperty("role").GetString());
        Assert.Equal("id1", json.GetProperty("tool_call_id").GetString());
        Assert.Equal("ok", json.GetProperty("content").GetString());
    }

    [Fact]
    public void SystemMessageMapsToPlainRoleAndContent()
    {
        using JsonDocument doc = JsonDocument.Parse(
            MeaiAdapter.ToOllamaMessageJson(new ChatMessage(ChatRole.System, "instructions")).ToJsonString());

        Assert.Equal("system", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal("instructions", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void SplitterReassemblesTagSplitAcrossChunks()
    {
        ThinkTagStreamSplitter splitter = new();

        (string reasoning1, string visible1) = splitter.Process("pre \u003cthi");
        (string reasoning2, string visible2) = splitter.Process("nk\u003emid\u003c/think\u003epost");
        (string reasoning3, string visible3) = splitter.Flush();

        Assert.Equal("pre ", visible1);
        Assert.Equal(string.Empty, reasoning1);
        Assert.Equal("mid", reasoning2 + reasoning3);
        Assert.Equal("post", visible2 + visible3);
    }
}
