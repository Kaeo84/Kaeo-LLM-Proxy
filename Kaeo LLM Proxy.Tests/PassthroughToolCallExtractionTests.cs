using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies that the non-streaming <c>/v1/chat/completions</c> passthrough path converts
/// inline XML tool-call blocks into structured OpenAI <c>tool_calls</c>, matching what the
/// streaming <c>OpenAiSseRewriter</c> and the <c>/api/chat</c> path do.
/// </summary>
public class PassthroughToolCallExtractionTests
{
    // XML tool-call markup the model emits inline in content. Angle brackets are written as
    // C# unicode escapes so the test source never contains raw markup sequences.
    private const string WeatherToolCall =
        "\u003ctool_call\u003e\u003cfunction=get_weather\u003e" +
        "\u003cparameter=city\u003eSeattle\u003c/parameter\u003e" +
        "\u003cparameter=days\u003e3\u003c/parameter\u003e" +
        "\u003c/function\u003e\u003c/tool_call\u003e";

    // A second well-formed block with a different function name, used to verify
    // declared-tool filtering keeps only the blocks the client can actually execute.
    private const string OtherToolCall =
        "\u003ctool_call\u003e\u003cfunction=lookup_time\u003e" +
        "\u003cparameter=tz\u003eUTC\u003c/parameter\u003e" +
        "\u003c/function\u003e\u003c/tool_call\u003e";

    private static JsonElement Transform(string json, ThinkingMode mode, bool extractToolCalls, IReadOnlySet<string>? declaredToolNames = null)
    {
        string result = OllamaProxyHandler.TransformNonStreamingChatBody(json, mode, extractToolCalls, declaredToolNames);
        return JsonDocument.Parse(result).RootElement.Clone();
    }

    private static string ChatBody(string content, string finishReason = "stop") =>
        "{" +
        "\"id\":\"chatcmpl-1\"," +
        "\"object\":\"chat.completion\"," +
        "\"created\":1700000000,\"model\":\"test-model\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" +
        JsonSerializer.Serialize(content) +
        "},\"finish_reason\":\"" + finishReason + "\"}]," +
        "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7,\"total_tokens\":12}" +
        "}";

    [Fact]
    public void XmlToolCallIsConvertedToStructuredToolCalls()
    {
        JsonElement root = Transform(ChatBody("Sure, checking now." + WeatherToolCall), ThinkingMode.LeaveInline, extractToolCalls: true);

        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");

        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("Sure, checking now.", message.GetProperty("content").GetString());

        JsonElement toolCalls = message.GetProperty("tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());

        JsonElement toolCall = toolCalls[0];
        Assert.Equal("function", toolCall.GetProperty("type").GetString());
        Assert.StartsWith("call_", toolCall.GetProperty("id").GetString());
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());

        string? argumentsJson = toolCall.GetProperty("function").GetProperty("arguments").GetString();
        Assert.NotNull(argumentsJson);
        JsonElement arguments = JsonDocument.Parse(argumentsJson).RootElement;
        Assert.Equal("Seattle", arguments.GetProperty("city").GetString());
        Assert.Equal(3, arguments.GetProperty("days").GetInt32());
    }

    [Fact]
    public void ExistingStructuredToolCallsArePreserved()
    {
        string body =
            "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" +
            JsonSerializer.Serialize("text" + WeatherToolCall) +
            ",\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"existing\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        JsonElement root = Transform(body, ThinkingMode.LeaveInline, extractToolCalls: true);

        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement toolCalls = choice.GetProperty("message").GetProperty("tool_calls");

        // The upstream's structured tool call wins; the XML block stays in the content.
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("call_abc", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Contains("tool_call", choice.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void ExtractionDisabledLeavesXmlInContent()
    {
        string content = "result: " + WeatherToolCall;

        JsonElement root = Transform(ChatBody(content), ThinkingMode.LeaveInline, extractToolCalls: false);

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal(content, choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void PlainContentIsUnchanged()
    {
        JsonElement root = Transform(ChatBody("Just a normal answer."), ThinkingMode.LeaveInline, extractToolCalls: true);

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal("Just a normal answer.", choice.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void ThinkingAndXmlToolCallAreBothTransformed()
    {
        string content = "\u003cthink\u003eplanning the call\u003c/think\u003e" + WeatherToolCall;

        JsonElement root = Transform(ChatBody(content), ThinkingMode.MoveToReasoningContent, extractToolCalls: true);

        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");

        Assert.Equal("planning the call", message.GetProperty("reasoning_content").GetString());
        Assert.Equal("get_weather", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(string.Empty, message.GetProperty("content").GetString());
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void MalformedXmlToolCallLeavesContentUnchanged()
    {
        string content = "\u003ctool_call\u003e\u003cfunction=get_weather\u003etruncated";

        JsonElement root = Transform(ChatBody(content), ThinkingMode.LeaveInline, extractToolCalls: true);

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal(content, choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void EmptyDeclaredToolSetStripsStructuredToolCalls()
    {
        string body =
            "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"I will check.\"," +
            "\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"get_weather\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        JsonElement root = Transform(body, ThinkingMode.LeaveInline, extractToolCalls: true,
            declaredToolNames: new HashSet<string>(StringComparer.Ordinal));

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("I will check.", choice.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void OnlyDeclaredStructuredToolCallsSurvive()
    {
        string body =
            "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"two calls\"," +
            "\"tool_calls\":[" +
            "{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{}\"}}," +
            "{\"id\":\"call_2\",\"type\":\"function\",\"function\":{\"name\":\"delete_repo\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };
        JsonElement root = Transform(body, ThinkingMode.LeaveInline, extractToolCalls: true, declaredToolNames: declared);

        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement toolCalls = choice.GetProperty("message").GetProperty("tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("get_weather", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        // A declared call survived, so the turn still ends as a tool-call turn.
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void UndeclaredXmlToolCallBlocksStayInlineAsText()
    {
        string content = "ok " + WeatherToolCall + OtherToolCall;
        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };

        JsonElement root = Transform(ChatBody(content), ThinkingMode.LeaveInline, extractToolCalls: true, declaredToolNames: declared);

        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");

        Assert.Equal(1, message.GetProperty("tool_calls").GetArrayLength());
        Assert.Equal("get_weather", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());

        string resultContent = message.GetProperty("content").GetString() ?? string.Empty;
        Assert.DoesNotContain("get_weather", resultContent);
        Assert.Contains("lookup_time", resultContent);
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void NoDeclaredToolsKeepsXmlBlocksAsText()
    {
        string content = "answer " + WeatherToolCall;

        JsonElement root = Transform(ChatBody(content), ThinkingMode.LeaveInline, extractToolCalls: true,
            declaredToolNames: new HashSet<string>(StringComparer.Ordinal));

        JsonElement choice = root.GetProperty("choices")[0];
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal(content, choice.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void ExtractDeclaredToolNamesReadsRequestToolFunctions()
    {
        string request =
            "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"tools\":[" +
            "{\"type\":\"function\",\"function\":{\"name\":\"alpha\",\"parameters\":{}}}," +
            "{\"type\":\"function\",\"function\":{\"name\":\"beta\",\"parameters\":{}}}]}";

        IReadOnlySet<string>? names = OllamaProxyHandler.ExtractDeclaredToolNames(request);

        Assert.NotNull(names);
        Assert.Equal(2, names!.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void ExtractDeclaredToolNamesReturnsEmptySetWhenNoToolsDeclared()
    {
        IReadOnlySet<string>? names = OllamaProxyHandler.ExtractDeclaredToolNames(
            """{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.NotNull(names);
        Assert.Empty(names!);
    }

    [Fact]
    public void ExtractDeclaredToolNamesReturnsNullForUnparseableBody()
    {
        Assert.Null(OllamaProxyHandler.ExtractDeclaredToolNames("{ not json"));
    }
}
