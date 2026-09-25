using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Services;
using Kaeo.LlmProxy.Services.Translation;
using Kaeo.LlmProxy.Tests.Conformance;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Phase-B6 spec-driven conformance: the frames the proxy emits on the OpenAI-compatible
/// chat-completions surface are validated against the vendored OpenAI OpenAPI document rather than
/// against hand-written expectations, so the contract is the vendor's and cannot drift with the code.
/// </summary>
/// <remarks>
/// The documents permit extra members: neither the chunk schema nor the delta schema declares
/// <c>additionalProperties</c>, so JSON Schema's permissive default applies. That is what makes the
/// proxy's deliberate provider extensions legal on this surface - <c>reasoning_content</c> (the
/// reasoning carrier the proxy normalises most upstreams into) and llama.cpp's <c>timings</c> block.
/// A distinct test pins that permissiveness explicitly, because a schema revision that started
/// forbidding extras would silently invalidate that design.
/// </remarks>
public class OpenAiSpecConformanceTests
{
    private const string ChunkSchema = "CreateChatCompletionStreamResponse";
    private const string DeltaSchema = "ChatCompletionStreamResponseDelta";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static VendorSpec Spec => SpecCatalog.OpenAiSpec!;

    private static JsonObject Chunk(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Builds a full chunk envelope around a delta, as an upstream would emit it.</summary>
    private static JsonObject Frame(JsonObject delta, string? finishReason = null)
    => new()
    {
        ["id"] = "chatcmpl-conformance",
        ["object"] = "chat.completion.chunk",
        ["created"] = 1786657431,
        ["model"] = "test-model",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason,
        }),
    };

    /// <summary>
    /// Runs a whole stream through the IR frame translator and returns every emitted frame, since a
    /// single inbound frame can expand into synthesized tool-call frames that must also conform.
    /// </summary>
    private static List<JsonObject> TranslateAll(
        IEnumerable<JsonObject> frames, ThinkingMode mode, IReadOnlySet<string>? declaredTools = null)
    {
        OpenAiSseFrameTranslation translator = new(mode, declaredTools);
        List<JsonObject> emitted = [];

        foreach (JsonObject frame in frames)
        {
            foreach (string line in translator.Process("data: " + frame.ToJsonString(WireOptions)))
                emitted.Add(Chunk(line["data: ".Length..]));
        }

        return emitted;
    }

    private static void AssertConforms(JsonObject instance, string schemaName)
    {
        IReadOnlyList<string> violations = Spec.Validate(schemaName, instance);
        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    // ── The spec itself ───────────────────────────────────────────────────

    [RequiresOpenAiSpecFact]
    public void VendoredSpecDeclaresTheSchemasUnderTest()
    {
        Assert.True(Spec.HasSchema("CreateChatCompletionRequest"));
        Assert.True(Spec.HasSchema(ChunkSchema));
        Assert.True(Spec.HasSchema(DeltaSchema));
        Assert.True(Spec.HasSchema("ChatCompletionMessageToolCallChunk"));
    }

    /// <summary>
    /// Pins the permissive-extra-members property the proxy's extension fields rely on. If a future
    /// spec revision added <c>additionalProperties: false</c>, this test fails at the point of the
    /// change rather than leaving the provider-extension design silently non-conformant.
    /// </summary>
    [RequiresOpenAiSpecFact]
    public void ChunkAndDeltaSchemasPermitExtensionMembers()
    {
        Assert.Null(Spec.Schema(ChunkSchema)!["additionalProperties"]);
        Assert.Null(Spec.Schema(DeltaSchema)!["additionalProperties"]);
    }

    // ── Emitted frames ────────────────────────────────────────────────────

    [RequiresOpenAiSpecTheory]
    [InlineData((int)ThinkingMode.LeaveInline)]
    [InlineData((int)ThinkingMode.MoveToReasoningContent)]
    [InlineData((int)ThinkingMode.StripFromOutput)]
    [InlineData((int)ThinkingMode.QwenThinkingCompatible)]
    public void EveryTranslatedFrameOfATextStreamConforms(int modeValue)
    {
        ThinkingMode mode = (ThinkingMode)modeValue;

        List<JsonObject> emitted = TranslateAll(
        [
            Frame(new JsonObject { ["role"] = "assistant" }),
            Frame(new JsonObject { ["content"] = "Hello" }),
            Frame(new JsonObject { ["content"] = " world" }),
            Frame(new JsonObject(), finishReason: "stop"),
        ], mode);

        Assert.NotEmpty(emitted);
        foreach (JsonObject frame in emitted)
            AssertConforms(frame, ChunkSchema);
    }

    [RequiresOpenAiSpecFact]
    public void ThoughtTagsMovedIntoReasoningContentStillConform()
    {
        List<JsonObject> emitted = TranslateAll(
        [
            Frame(new JsonObject { ["content"] = "\u003cthink\u003eweighing it up\u003c/think\u003eThe answer." }),
            Frame(new JsonObject(), finishReason: "stop"),
        ], ThinkingMode.MoveToReasoningContent);

        foreach (JsonObject frame in emitted)
            AssertConforms(frame, ChunkSchema);

        // The reasoning carrier is the extension member; assert it actually survived validation
        // rather than the stream having been rewritten to omit it.
        Assert.Contains(emitted, frame => DeltaOf(frame)?.ContainsKey("reasoning_content") == true);
    }

    [RequiresOpenAiSpecFact]
    public void SynthesisedToolCallFramesConform()
    {
        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };

        List<JsonObject> emitted = TranslateAll(
        [
            Frame(new JsonObject
            {
                ["content"] = "Checking. <tool_call><function=get_weather><parameter=city>NY</parameter></function></tool_call>",
            }),
            Frame(new JsonObject(), finishReason: "stop"),
        ], ThinkingMode.LeaveInline, declared);

        foreach (JsonObject frame in emitted)
            AssertConforms(frame, ChunkSchema);

        // The promoted call must conform to the tool-call fragment contract too: a synthesised
        // frame is the proxy's invention, so it is the most likely place to violate the spec.
        List<JsonObject> calls = [.. emitted
            .Select(DeltaOf)
            .Where(delta => delta?["tool_calls"] is JsonArray)
            .SelectMany(delta => (JsonArray)delta!["tool_calls"]!)
            .Select(call => (JsonObject)call!)];

        Assert.NotEmpty(calls);
        foreach (JsonObject call in calls)
            AssertConforms(call, "ChatCompletionMessageToolCallChunk");
    }

    [RequiresOpenAiSpecFact]
    public void NativeToolCallFragmentsConform()
    {
        HashSet<string> declared = new(StringComparer.Ordinal) { "get_weather" };

        List<JsonObject> emitted = TranslateAll(
        [
            Frame(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["id"] = "call_abc",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "get_weather", ["arguments"] = "{\"ci" },
                }),
            }),
            Frame(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["function"] = new JsonObject { ["arguments"] = "ty\":\"NY\"}" },
                }),
            }),
            Frame(new JsonObject(), finishReason: "tool_calls"),
        ], ThinkingMode.LeaveInline, declared);

        foreach (JsonObject frame in emitted)
            AssertConforms(frame, ChunkSchema);
    }

    [RequiresOpenAiSpecFact]
    public void LlamaCppTimingsExtensionStaysConformant()
    {
        // llama.cpp adds a timings block and a system_fingerprint alongside the standard members.
        // The proxy forwards both untouched, so they must remain legal on this surface.
        JsonObject frame = Frame(new JsonObject { ["content"] = "hi" });
        frame["system_fingerprint"] = "fp_44709d6fcb";
        frame["timings"] = new JsonObject
        {
            ["prompt_n"] = 12,
            ["prompt_ms"] = 34.5,
            ["predicted_n"] = 5,
            ["predicted_ms"] = 67.8,
        };

        AssertConforms(frame, ChunkSchema);
    }

    /// <summary>
    /// The proxy's normalised reasoning carrier is an extension member on this surface. This test
    /// documents that intentionally: conformance comes from the spec permitting extras, not from the
    /// spec declaring the field.
    /// </summary>
    [RequiresOpenAiSpecFact]
    public void ReasoningContentIsAnExtensionNotASpecMember()
    {
        JsonObject deltaSchema = Spec.Schema(DeltaSchema)!;
        JsonObject deltaProperties = (JsonObject)deltaSchema["properties"]!;

        Assert.False(deltaProperties.ContainsKey("reasoning_content"));

        // A delta carrying it still validates, because the schema does not forbid extras.
        JsonObject frame = Frame(new JsonObject { ["reasoning_content"] = "thinking", ["content"] = "answer" });
        AssertConforms(frame, ChunkSchema);
    }

    [RequiresOpenAiSpecFact]
    public void FinishReasonValuesTheProxyEmitsAreInTheSpecEnum()
    {
        List<string> values = FinishReasonEnum();

        // The proxy rewrites finish_reason to these two values when it filters or promotes calls.
        Assert.Contains("stop", values);
        Assert.Contains("tool_calls", values);
    }

    /// <summary>
    /// Locates the finish_reason enum through the choices schema, which declares the choice shape on
    /// its array item.
    /// </summary>
    private static List<string> FinishReasonEnum()
    {
        JsonObject chunkProperties = (JsonObject)Spec.Schema(ChunkSchema)!["properties"]!;
        JsonObject choiceSchema = ChoiceObjectSchema(chunkProperties["choices"]!);
        JsonObject choiceProperties = (JsonObject)choiceSchema["properties"]!;
        JsonArray finishEnum = (JsonArray)choiceProperties["finish_reason"]!["enum"]!;

        return [.. finishEnum.Select(value => value!.GetValue<string>())];
    }

    /// <summary>Unwraps the choice member schema to the object describing one choice.</summary>
    private static JsonObject ChoiceObjectSchema(JsonNode schema)
    {
        JsonObject current = (JsonObject)schema;

        // The choice shape is declared on the array's item schema.
        if (current["items"] is JsonObject items)
            current = items;

        return current;
    }

    [RequiresOpenAiSpecFact]
    public void ObjectMemberIsAlwaysTheChatCompletionChunkLiteral()
    {
        // The spec pins object to a single-value enum, so a synthesised frame that copied the wrong
        // value from its parent would be caught here rather than by a client.
        JsonObject chunkProperties = (JsonObject)Spec.Schema(ChunkSchema)!["properties"]!;
        JsonArray? objectEnum = ((JsonObject)chunkProperties["object"]!)["enum"] as JsonArray;

        Assert.NotNull(objectEnum);
        Assert.Equal("chat.completion.chunk", objectEnum![0]!.GetValue<string>());

        foreach (JsonObject frame in TranslateAll([Frame(new JsonObject { ["content"] = "x" })], ThinkingMode.LeaveInline))
            Assert.Equal("chat.completion.chunk", frame["object"]!.GetValue<string>());
    }

    private static JsonObject? DeltaOf(JsonObject frame)
        => ((JsonArray)frame["choices"]!)[0] is JsonObject choice
            ? choice["delta"] as JsonObject
            : null;
}

/// <summary>
/// Guards the harness itself. A validator that silently accepted everything would make every
/// conformance test above vacuous, so the checks below fail if the validator stops rejecting.
/// </summary>
public class SpecValidatorSelfTests
{
    private static VendorSpec Spec => SpecCatalog.OpenAiSpec!;

        private const string ChunkSchema = "CreateChatCompletionStreamResponse";

    [RequiresOpenAiSpecFact]
    public void MissingRequiredMemberIsRejected()
    {
        // The chunk schema requires choices, created, id, model and object.
        JsonObject incomplete = new()
        {
            ["id"] = "chatcmpl-x",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "m",
        };

        IReadOnlyList<string> violations = Spec.Validate(ChunkSchema, incomplete);

        Assert.Contains(violations, message => message.Contains("choices", StringComparison.Ordinal));
    }

    [RequiresOpenAiSpecFact]
    public void WrongMemberTypeIsRejected()
    {
        JsonObject bad = new()
        {
            ["id"] = "chatcmpl-x",
            ["object"] = "chat.completion.chunk",
            ["created"] = "not-a-number",
            ["model"] = "m",
            ["choices"] = new JsonArray(),
        };

        IReadOnlyList<string> violations = Spec.Validate(ChunkSchema, bad);

        Assert.Contains(violations, message => message.Contains("created", StringComparison.Ordinal));
    }

    [RequiresOpenAiSpecFact]
    public void WrongEnumValueIsRejected()
    {
        JsonObject bad = new()
        {
            ["id"] = "chatcmpl-x",
            ["object"] = "chat.completion",
            ["created"] = 1,
            ["model"] = "m",
            ["choices"] = new JsonArray(),
        };

        IReadOnlyList<string> violations = Spec.Validate(ChunkSchema, bad);

        Assert.Contains(violations, message => message.Contains("object", StringComparison.Ordinal));
    }

    [RequiresOpenAiSpecFact]
    public void UnknownSchemaNameIsReported()
    {
        IReadOnlyList<string> violations = Spec.Validate("NoSuchSchema", JsonNode.Parse("{}")!);

        Assert.Contains(violations, message => message.Contains("NoSuchSchema", StringComparison.Ordinal));
    }

    [RequiresOpenAiSpecFact]
    public void ConformingInstanceProducesNoViolations()
    {
        // The choice object requires delta, finish_reason (nullable) and index, so a minimal
        // conforming chunk must carry all three.
        JsonObject good = new()
        {
            ["id"] = "chatcmpl-x",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "m",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject(),
                ["finish_reason"] = null,
            }),
        };

        Assert.Empty(Spec.Validate(ChunkSchema, good));
    }
}