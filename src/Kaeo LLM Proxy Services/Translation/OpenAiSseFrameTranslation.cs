using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Phase-B IR translation for the OpenAI chat-completions passthrough stream: one SSE frame in,
/// the same frame (plus any synthesised tool-call frames) out.
/// </summary>
/// <remarks>
/// This is the IR-routed counterpart to the legacy string-surgery rewriter, selected per request
/// behind <c>AppSettings.UseIrTranslation</c>. The transformation is expressed at the level of a
/// delta's typed parts - a reasoning part, a visible answer part, and native tool-call fragments -
/// rather than by editing the raw delta object in place.
///
/// Frame fidelity is the governing constraint, so this deliberately differs from
/// <see cref="OpenAiInbound"/> in one important way: that class is a *stream* accumulator, holding
/// tool-call argument fragments until the terminal chunk and then emitting completed calls. An SSE
/// rewriter must not do that, because clients read the fragmented form as it arrives. Native
/// <c>tool_calls</c> fragments are therefore carried through structurally unchanged here - the only
/// decision taken on them is the declared-tool filter, exactly as the legacy rewriter does.
///
/// Thought handling (<see cref="ThinkingMode"/>) and the Qwen marker pair survive as serializer
/// options describing how a reasoning part is rendered onto this wire; they are not pipeline
/// behaviour.
/// </remarks>
internal sealed class OpenAiSseFrameTranslation
{
    private const string DataPrefix = "data:";

    private readonly ThinkingMode _thinkingMode;
    private readonly IReadOnlySet<string>? _declaredToolNames;
    private readonly (string OpenTag, string CloseTag) _thinkTags;
    private readonly Dictionary<int, ChoiceState> _states = [];

    /// <summary>
    /// Creates a translator for one response stream. <paramref name="thinkingMode"/> selects how a
    /// reasoning part is rendered; <paramref name="declaredToolNames"/> is the declared-tool set
    /// driving the sanitizer, where null means "unknown, do not filter".
    /// </summary>
    public OpenAiSseFrameTranslation(ThinkingMode thinkingMode, IReadOnlySet<string>? declaredToolNames)
    {
        _thinkingMode = thinkingMode;
        _declaredToolNames = declaredToolNames;
        _thinkTags = ThinkTagExtractor.TagsFor(thinkingMode);
    }

    /// <summary>
    /// Translates one inbound SSE line. Returns the frame(s) to write; a frame the rewriter does
    /// not understand is returned unchanged so nothing is ever silently dropped.
    /// </summary>
    public IEnumerable<string> Process(string rawLine)
    {
        if (!rawLine.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            yield return rawLine;
            yield break;
        }

        string data = rawLine[DataPrefix.Length..];
        if (data.StartsWith(' '))
            data = data[1..];

        if (data.Length == 0 || data == "[DONE]")
        {
            yield return rawLine;
            yield break;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Skipping unparseable SSE data frame in IR frame translator");
            root = null;
        }

        if (root is null || root["choices"] is not JsonArray choices)
        {
            yield return rawLine;
            yield break;
        }

        // Synthesised tool-call frames are emitted after the rewritten one, mirroring the legacy
        // rewriter's ordering so a client sees content before the call it belongs to.
        List<JsonObject> extraFrames = [];

        foreach (JsonNode? choiceNode in choices)
        {
            if (choiceNode is not JsonObject choice)
                continue;

            int index = choice["index"]?.GetValue<int>() ?? 0;
            if (!_states.TryGetValue(index, out ChoiceState? state))
            {
                state = new ChoiceState(_declaredToolNames);
                _states[index] = state;
            }

            if (choice["delta"] is JsonObject delta)
                RenderInto(delta, root, index, state, extraFrames);

            ApplyFinishReason(choice, state);
        }

        yield return $"data: {root.ToJsonString(InlineXmlToolCall.JsonOptions)}";

        foreach (JsonObject extra in extraFrames)
            yield return $"data: {extra.ToJsonString(InlineXmlToolCall.JsonOptions)}";
    }

    /// <summary>
    /// Transforms one choice's delta. The delta is decomposed into typed parts, each part is
    /// decided on independently, and the result is written back onto the original delta object so
    /// every field the rewriter does not understand survives untouched.
    /// </summary>
    private void RenderInto(
        JsonObject delta, JsonObject root, int index, ChoiceState state, List<JsonObject> extraFrames)
    {
        DeltaParts parts = DeltaParts.Read(delta);

        if (_thinkingMode != ThinkingMode.LeaveInline)
            parts.ApplyThinkTags(_thinkingMode, _thinkTags, index, state);
        else
            parts.MirrorReasoningToContent();

        // Structured calls naming functions the client did not declare must be dropped: OpenAI
        // clients such as Copilot render them as function parts they cannot bind.
        parts.FilterNativeCalls(_declaredToolNames, state);

                    // Inline markup calls are lifted out of the answer text and re-emitted as structured ones.
                    parts.ExtractInlineCalls(_declaredToolNames, state, root, index, extraFrames);

        parts.Write(delta);
    }

    /// <summary>
    /// Keeps <c>finish_reason</c> consistent with what actually reached the client: while calls were
    /// emitted report <c>tool_calls</c>; when every call was filtered out report <c>stop</c> so the
    /// client ends the turn instead of waiting for results that will never arrive.
    /// </summary>
    private static void ApplyFinishReason(JsonObject choice, ChoiceState state)
    {
        if (choice["finish_reason"] is not JsonValue finishValue
            || !finishValue.TryGetValue(out string? finishReason)
            || string.IsNullOrEmpty(finishReason))
        {
            return;
        }

        if (state.EmittedToolCallCount > 0)
            choice["finish_reason"] = "tool_calls";
        else if (finishReason == "tool_calls" && !state.HadValidToolCall)
            choice["finish_reason"] = "stop";
    }

    /// <summary>
    /// How a delta field should be rendered. "Untouched" matters as much as the other two: an
    /// empty <c>content</c> key and an absent one are different frames, and a client-visible
    /// difference, so the distinction is tracked explicitly rather than inferred from emptiness.
    /// </summary>
    private enum FieldIntent
    {
        Untouched,
        Remove,
        Set,
    }

    /// <summary>
    /// The IR view of a single delta: a reasoning part, a visible answer part, and the native
    /// tool-call fragments carried through unchanged. Rendering is deferred until every part has
    /// been decided so the intermediate states never reach the wire.
    /// </summary>
    private sealed class DeltaParts
    {
        private string _content = string.Empty;
        private FieldIntent _contentIntent;
        private string _reasoning = string.Empty;
        private FieldIntent _reasoningIntent;
        private JsonArray? _nativeCalls;
        private bool _hadNativeCallsArray;

        public static DeltaParts Read(JsonObject delta)
        {
            DeltaParts parts = new();

            if (delta["tool_calls"] is JsonArray nativeCalls)
            {
                parts._nativeCalls = nativeCalls;
                parts._hadNativeCallsArray = true;
            }

            if (delta["content"] is JsonValue contentValue
                && contentValue.TryGetValue(out string? content))
            {
                parts._content = content ?? string.Empty;
                parts._contentIntent = FieldIntent.Set;
            }

            if (delta["reasoning_content"] is JsonValue reasoningValue
                && reasoningValue.TryGetValue(out string? reasoning))
            {
                parts._reasoning = reasoning ?? string.Empty;
                parts._reasoningIntent = FieldIntent.Set;
            }

            return parts;
        }

        /// <summary>
        /// Splits inline thought tags out of the answer part and decides what each part becomes.
        /// A trailing partial tag is buffered inside the extractor so a tag split across frames is
        /// never emitted literally.
        ///
        /// The intent rules mirror the legacy rewriter: an answer that was fully consumed as
        /// reasoning drops the content key (a reasoning-only frame), an answer with nothing left
        /// keeps its key so partial-tag buffering stays invisible, and a native reasoning part is
        /// only rewritten when thought was actually extracted. Strip additionally removes any
        /// native reasoning so nothing reaches the client.
        /// </summary>
        public void ApplyThinkTags(
            ThinkingMode mode, (string OpenTag, string CloseTag) tags, int index, ChoiceState state)
        {
            if (_contentIntent != FieldIntent.Set || _content.Length == 0)
                return;

            if (!state.Extractors.TryGetValue(index, out ThinkTagExtractor? extractor))
            {
                extractor = new ThinkTagExtractor(tags.OpenTag, tags.CloseTag);
                state.Extractors[index] = extractor;
            }

            string incoming = _content;
            (string reasoning, string content) = extractor.Process(incoming);

            if (mode == ThinkingMode.StripFromOutput)
                _reasoningIntent = FieldIntent.Remove;
            else if (reasoning.Length > 0)
            {
                _reasoning = _reasoning + reasoning;
                _reasoningIntent = FieldIntent.Set;
            }

            if (content.Length > 0)
            {
                _content = content;
            }
            else if (incoming.Length > 0)
            {
                // Fully consumed (as thought or by partial-tag buffering): drop the key rather
                // than publish an empty answer.
                _contentIntent = FieldIntent.Remove;
            }
        }

        /// <summary>
        /// LeaveInline keeps reasoning where the upstream put it, but mirrors a reasoning-only
        /// frame into the answer so a client that ignores <c>reasoning_content</c> still shows text.
        /// </summary>
        public void MirrorReasoningToContent()
        {
            if (_reasoningIntent == FieldIntent.Set
                && _reasoning.Length > 0
                && _contentIntent != FieldIntent.Set)
            {
                _content = _reasoning;
                _contentIntent = FieldIntent.Set;
            }
        }

        /// <summary>
        /// Applies the declared-tool filter to native fragments. An argument fragment of an
        /// already-judged call inherits that verdict, because only the first fragment carries a name.
        /// </summary>
        public void FilterNativeCalls(IReadOnlySet<string>? declaredToolNames, ChoiceState state)
        {
            if (_nativeCalls is null)
                return;

            List<JsonNode?> drop = [];
            foreach (JsonNode? callNode in _nativeCalls)
            {
                if (callNode is not JsonObject call)
                    continue;

                int callIndex = call["index"] is JsonValue indexValue && indexValue.TryGetValue(out int idx) ? idx : 0;
                bool valid;

                if (call["function"] is JsonObject function
                    && function["name"] is JsonValue nameValue
                    && nameValue.TryGetValue(out string? callName)
                    && !string.IsNullOrEmpty(callName))
                {
                    valid = OllamaProxyHandler.IsToolNameAllowed(declaredToolNames, callName);
                }
                else
                {
                    valid = !state.NativeCallValidity.TryGetValue(callIndex, out bool previous) || previous;
                }

                state.NativeCallValidity[callIndex] = valid;
                if (valid)
                    state.HadValidToolCall = true;
                else
                    drop.Add(callNode);
            }

            foreach (JsonNode? dropped in drop)
                _nativeCalls.Remove(dropped);

            if (_nativeCalls.Count == 0)
                _nativeCalls = null;
        }

        /// <summary>
        /// Strips inline markup calls from the answer part, queuing a synthesised frame for each
        /// complete call. A call naming an undeclared function is handed back to the client as
        /// visible text rather than as a call it could not execute.
        /// </summary>
        public void ExtractInlineCalls(
            IReadOnlySet<string>? declaredToolNames,
            ChoiceState state,
            JsonObject root,
            int index,
            List<JsonObject> extraFrames)
        {
            if (_contentIntent != FieldIntent.Set || _content.Length == 0)
                return;

            (string visible, List<string> blocks) = state.ScanForInlineCalls(_content);

            if (visible.Length == 0)
                _contentIntent = FieldIntent.Remove;
            else
                _content = visible;

            foreach (string block in blocks)
                EmitInlineCall(declaredToolNames, state, root, index, extraFrames, block);
        }

        private static void EmitInlineCall(
            IReadOnlySet<string>? declaredToolNames,
            ChoiceState state,
            JsonObject root,
            int choiceIndex,
            List<JsonObject> extraFrames,
            string block)
        {
            (string Name, Dictionary<string, object?> Arguments)? parsed = InlineXmlToolCall.Parse(block);
            if (parsed is null)
                return;

            if (!OllamaProxyHandler.IsToolNameAllowed(declaredToolNames, parsed.Value.Name))
            {
                extraFrames.Add(ChoiceFrame.WithContent(root, choiceIndex, block.Trim()));
                return;
            }

            string callId = "call_" + Guid.NewGuid().ToString("N")[..16];
            int toolIndex = state.EmittedToolCallCount++;

            extraFrames.Add(ChoiceFrame.WithToolCall(
                root, choiceIndex, InlineXmlToolCall.BuildCallObject(parsed.Value.Name, parsed.Value.Arguments, callId, toolIndex)));
        }

        /// <summary>
        /// Writes the decided parts back onto the delta, honouring the recorded intent so a field
        /// the translator had no opinion about is left exactly as the upstream sent it.
        /// </summary>
        public void Write(JsonObject delta)
        {
            switch (_contentIntent)
            {
                case FieldIntent.Set:
                    delta["content"] = _content;
                    break;
                case FieldIntent.Remove:
                    delta.Remove("content");
                    break;
            }

            switch (_reasoningIntent)
            {
                case FieldIntent.Set:
                    delta["reasoning_content"] = _reasoning;
                    break;
                case FieldIntent.Remove:
                    delta.Remove("reasoning_content");
                    break;
            }

            // Only touch tool_calls when the delta actually carried the array: a foreign field by
            // that name must not be invented or dropped.
            if (_hadNativeCallsArray && _nativeCalls is null)
                delta.Remove("tool_calls");
        }
    }

    /// <summary>
    /// Per-choice stream state: the incremental thought extractor, the native-call filter
    /// verdicts, and the buffer for a markup call split across frames.
    /// </summary>
    private sealed class ChoiceState(IReadOnlySet<string>? declaredToolNames)
    {
        private readonly StringBuilder _toolBuffer = new();
        private bool _inToolCall;

        /// <summary>Incremental thought extractors, keyed by choice index.</summary>
        public Dictionary<int, ThinkTagExtractor> Extractors { get; } = [];

        /// <summary>Filter verdict per native tool-call stream index.</summary>
        public Dictionary<int, bool> NativeCallValidity { get; } = [];

        /// <summary>True when at least one upstream tool-call fragment survived filtering.</summary>
        public bool HadValidToolCall { get; set; }

        /// <summary>Number of synthesised calls emitted so far.</summary>
        public int EmittedToolCallCount { get; set; }

        /// <summary>
        /// Consumes the next answer token, returning the text that should remain visible and the
        /// complete markup call blocks it contained. Buffers a call that has not yet closed.
        /// </summary>
        public (string Visible, List<string> Blocks) ScanForInlineCalls(string token)
        {
            StringBuilder visible = new();
            List<string> blocks = [];
            int cursor = 0;

            while (cursor < token.Length)
            {
                if (_inToolCall)
                {
                    int end = token.IndexOf(InlineXmlToolCall.CloseMarker, cursor, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        _toolBuffer.Append(token, cursor, token.Length - cursor);
                        cursor = token.Length;
                    }
                    else
                    {
                        int closeEnd = end + InlineXmlToolCall.CloseMarker.Length;
                        _toolBuffer.Append(token, cursor, closeEnd - cursor);
                        cursor = closeEnd;
                        _inToolCall = false;

                        blocks.Add(_toolBuffer.ToString());
                        _toolBuffer.Clear();
                    }
                }
                else
                {
                    int start = token.IndexOf(InlineXmlToolCall.OpenMarker, cursor, StringComparison.OrdinalIgnoreCase);
                    if (start < 0)
                    {
                        visible.Append(token, cursor, token.Length - cursor);
                        cursor = token.Length;
                    }
                    else
                    {
                        visible.Append(token, cursor, start - cursor);
                        cursor = start;
                        _inToolCall = true;
                    }
                }
            }

            return (visible.ToString(), blocks);
        }
    }

    /// <summary>
    /// Builds a synthesised chunk envelope mirroring the original frame's metadata, so a client
    /// cannot tell a promoted call apart from one the upstream sent natively.
    /// </summary>
    private static class ChoiceFrame
    {
        public static JsonObject WithContent(JsonObject root, int choiceIndex, string content)
            => Build(root, choiceIndex, new JsonObject { ["content"] = content });

        public static JsonObject WithToolCall(JsonObject root, int choiceIndex, JsonObject call)
            => Build(root, choiceIndex, new JsonObject { ["tool_calls"] = new JsonArray(call) });

        private static JsonObject Build(JsonObject root, int choiceIndex, JsonObject delta) => new()
        {
            ["id"] = root["id"]?.DeepClone(),
            ["object"] = root["object"]?.DeepClone(),
            ["created"] = root["created"]?.DeepClone(),
            ["model"] = root["model"]?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = choiceIndex,
                ["delta"] = delta,
                ["finish_reason"] = null,
            }),
        };
    }
}