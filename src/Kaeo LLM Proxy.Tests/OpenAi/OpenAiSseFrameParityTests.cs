using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Phase-B golden parity for the passthrough rewriter: the IR-routed frame translator
/// (OpenAiSseFrameTranslation, behind AppSettings.UseIrTranslation) must produce byte-identical
/// output to the legacy string-surgery rewriter (OpenAiSseRewriter) for every frame of a stream.
///
/// Frames are compared as raw text, not as re-serialized JSON, precisely because the fidelity
/// contract is byte-level: key presence, key order, and the null/empty distinctions all matter to
/// the clients this stream feeds.
/// </summary>
public class OpenAiSseFrameParityTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds one SSE line for a delta object. Every frame carries the full envelope so metadata
    /// preservation is exercised rather than assumed.
    /// </summary>
    private static string Frame(JsonObject delta, string? finishReason = null, int choiceIndex = 0)
    {
        JsonObject root = new()
        {
            ["id"] = "chatcmpl-parity",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1786657431,
            ["model"] = "Bonsai-27B-Q1_0.gguf",
            ["system_fingerprint"] = "fp_parity",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = choiceIndex,
                ["delta"] = delta,
                ["finish_reason"] = finishReason,
            }),
        };

        return "data: " + root.ToJsonString(WireOptions);
    }

    private static JsonObject Delta(string? content = null, string? reasoning = null)
    {
        JsonObject delta = new();
        if (content is not null)
            delta["content"] = content;
        if (reasoning is not null)
            delta["reasoning_content"] = reasoning;
        return delta;
    }

    /// <summary>A delta carrying native tool-call fragments.</summary>
    private static JsonObject ToolCallDelta(params JsonObject[] calls)
        => new() { ["tool_calls"] = new JsonArray(calls) };

    private static JsonObject NativeCall(int index, string? name, string? arguments, string? id = null)
    {
        JsonObject function = new();
        if (name is not null)
            function["name"] = name;
        if (arguments is not null)
            function["arguments"] = arguments;

        JsonObject call = new() { ["index"] = index, ["function"] = function };
        if (id is not null)
            call["id"] = id;
        return call;
    }

    /// <summary>
    /// Drives the same frame sequence through both implementations and asserts each emitted line
    /// matches. Synthesised tool-call frames carry a generated id, so those are normalized.
    /// </summary>
    private static void AssertParity(
        IEnumerable<string> frames, ThinkingMode mode, IReadOnlySet<string>? declaredToolNames = null)
    {
        Func<string, IEnumerable<string>> legacy = OllamaProxyHandler.CreateLegacySseRewriter(mode, declaredToolNames);
        OpenAiSseFrameTranslation ir = new(mode, declaredToolNames);

        List<string> expected = [];
        List<string> actual = [];

        foreach (string frame in frames)
        {
            expected.AddRange(legacy(frame));
            actual.AddRange(ir.Process(frame));
        }

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    /// <summary>Replaces generated call ids so two runs are comparable.</summary>
    private static List<string> Normalize(List<string> lines)
        => [.. lines.Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"""call_[0-9a-f]{16}""", "\"GEN\""))];

    [Fact]
    public void PlainTextStreamMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "Hello")),
            Frame(Delta(content: " world")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline);
    }

    [Fact]
    public void PassThroughLinesAreForwardedUnchanged()
    {
        AssertParity(
        [
            ": keep-alive comment",
            "data: [DONE]",
            "",
            "event: ping",
        ], ThinkingMode.MoveToReasoningContent);
    }

    [Fact]
    public void UnparseableDataFrameIsForwardedUnchanged()
    {
        AssertParity(["data: {not json"], ThinkingMode.MoveToReasoningContent);
    }

    [Fact]
    public void RoleOnlyDeltaMatchesLegacy()
    {
        AssertParity([Frame(new JsonObject { ["role"] = "assistant" })], ThinkingMode.MoveToReasoningContent);
    }

    [Theory]
    [InlineData((int)ThinkingMode.LeaveInline)]
    [InlineData((int)ThinkingMode.MoveToReasoningContent)]
    [InlineData((int)ThinkingMode.StripFromOutput)]
    [InlineData((int)ThinkingMode.QwenThinkingCompatible)]
    public void InlineThoughtMatchesLegacyAcrossModes(int modeValue)
    {
        ThinkingMode mode = (ThinkingMode)modeValue;
        AssertParity(
        [
            Frame(Delta(content: "<think>step one</think>Answer A")),
            Frame(Delta(content: " more")),
            Frame(Delta(), finishReason: "stop"),
        ], mode);
    }

    [Theory]
    [InlineData((int)ThinkingMode.MoveToReasoningContent)]
    [InlineData((int)ThinkingMode.StripFromOutput)]
    public void ThoughtTagSplitAcrossFramesMatchesLegacy(int modeValue)
    {
        ThinkingMode mode = (ThinkingMode)modeValue;
        AssertParity(
        [
            Frame(Delta(content: "<thi")),
            Frame(Delta(content: "nk>hidden")),
            Frame(Delta(content: "</thin")),
            Frame(Delta(content: "k>visible")),
            Frame(Delta(), finishReason: "stop"),
        ], mode);
    }

    [Fact]
    public void ReasoningOnlyFrameMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta()),
            Frame(Delta(content: "<think>all reasoning</think>")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.MoveToReasoningContent);
    }

    [Fact]
    public void NativeReasoningContentIsMirroredToContentInLeaveInline()
    {
        AssertParity(
        [
            Frame(Delta(reasoning: "native reasoning")),
            Frame(Delta(content: "answer", reasoning: "native reasoning")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline);
    }

    [Fact]
    public void StripFromOutputRemovesNativeReasoningMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "answer", reasoning: "native reasoning")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.StripFromOutput);
    }

    [Fact]
    public void QwenMarkersMatchLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "[Thinking]pondering")),
            Frame(Delta(content: "[Answer]the answer")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.QwenThinkingCompatible);
    }

    [Fact]
    public void UndeclaredNativeCallIsDroppedMatchesLegacy()
    {
        AssertParity(
        [
            Frame(ToolCallDelta(NativeCall(0, "delete_repo", "{}", "call_abc"))),
            Frame(Delta(), finishReason: "tool_calls"),
        ], ThinkingMode.LeaveInline, new HashSet<string>(StringComparer.Ordinal) { "get_weather" });
    }

    [Fact]
    public void DeclaredNativeCallIsKeptMatchesLegacy()
    {
        AssertParity(
        [
            Frame(ToolCallDelta(NativeCall(0, "get_weather", "{\"city\":\"NY\"}", "call_abc"))),
            Frame(Delta(), finishReason: "tool_calls"),
        ], ThinkingMode.LeaveInline, new HashSet<string>(StringComparer.Ordinal) { "get_weather" });
    }

    /// <summary>
    /// The fidelity constraint that distinguishes this translator from a stream accumulator:
    /// argument fragments must stay fragmented on the wire, never merged into a completed call.
    /// </summary>
    [Fact]
    public void NativeCallArgumentFragmentsStayFragmented()
    {
        string first = Frame(ToolCallDelta(NativeCall(0, "get_weather", "{\"ci", "call_abc")));
        string second = Frame(ToolCallDelta(NativeCall(0, null, "ty\":\"NY\"}")));

        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };

        OpenAiSseFrameTranslation ir = new(ThinkingMode.LeaveInline, declared);
        List<string> emitted = [.. ir.Process(first), .. ir.Process(second)];

        // Two fragments in, two frames out: they were never coalesced into one completed call.
        Assert.Equal(2, emitted.Count);
        Assert.Equal("{\"ci", ArgumentsOf(emitted[0]));
        Assert.Equal("ty\":\"NY\"}", ArgumentsOf(emitted[1]));
    }

    /// <summary>Reads the first tool-call fragment's streamed arguments out of an emitted frame.</summary>
    private static string ArgumentsOf(string emittedFrame)
    {
        JsonObject root = JsonNode.Parse(emittedFrame["data: ".Length..])!.AsObject();
        return root["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
    }

    [Fact]
    public void UndeclaredArgumentFragmentInheritsRejectedVerdict()
    {
        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };

        AssertParity(
        [
            Frame(ToolCallDelta(NativeCall(0, "delete_repo", "{\"pa"))),
            Frame(ToolCallDelta(NativeCall(0, null, "th\":\"/\"}"))),
            Frame(Delta(), finishReason: "tool_calls"),
        ], ThinkingMode.LeaveInline, declared);
    }

    [Fact]
    public void EmptyContentKeyMatchesLegacy()
    {
        AssertParity([Frame(Delta(content: string.Empty))], ThinkingMode.MoveToReasoningContent);
    }

    [Fact]
    public void InlineMarkupCallIsPromotedMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "<tool_call><function=get_weather><parameter=city>NY</parameter></function></tool_call>")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline, new HashSet<string>(StringComparer.Ordinal) { "get_weather" });
    }

    [Fact]
    public void InlineMarkupCallForUndeclaredToolReturnedAsTextMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "<tool_call><function=delete_repo><parameter=path>/</parameter></function></tool_call>")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline, new HashSet<string>(StringComparer.Ordinal) { "get_weather" });
    }

    [Fact]
    public void MarkupCallSplitAcrossFramesMatchesLegacy()
    {
        AssertParity(
        [
            Frame(Delta(content: "before <tool_call><function=get_")),
            Frame(Delta(content: "weather><parameter=city>NY</parameter>")),
            Frame(Delta(content: "</function></tool_call> after")),
            Frame(Delta(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline, new HashSet<string>(StringComparer.Ordinal) { "get_weather" });
    }

    [Fact]
    public void NullFinishReasonAndExtraEnvelopeFieldsSurvive()
    {
        string frame = Frame(Delta(content: "hi"));

        OpenAiSseFrameTranslation ir = new(ThinkingMode.MoveToReasoningContent, null);
        string emitted = ir.Process(frame).Single();

        JsonObject root = JsonNode.Parse(emitted["data: ".Length..])!.AsObject();
        Assert.True(root.ContainsKey("finish_reason") || root["choices"]![0]!.AsObject().ContainsKey("finish_reason"));
        Assert.Equal("fp_parity", root["system_fingerprint"]!.GetValue<string>());
        Assert.Equal(1786657431, root["created"]!.GetValue<long>());
        Assert.Equal("chatcmpl-parity", root["id"]!.GetValue<string>());
    }

    [Fact]
    public void MultipleChoicesEachTransformedIndependently()
    {
        JsonObject root = new()
        {
            ["id"] = "chatcmpl-multi",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "m",
            ["choices"] = new JsonArray(
                new JsonObject { ["index"] = 0, ["delta"] = Delta(content: "<think>a</think>one"), ["finish_reason"] = null },
                new JsonObject { ["index"] = 1, ["delta"] = Delta(content: "<think>b</think>two"), ["finish_reason"] = null }),
        };

        AssertParity(["data: " + root.ToJsonString(WireOptions)], ThinkingMode.MoveToReasoningContent);
    }
}