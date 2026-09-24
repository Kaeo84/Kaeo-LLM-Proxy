using System.Text;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Incrementally separates <c>&lt;think&gt;...&lt;/think&gt;</c> reasoning blocks from normal
/// answer text as an upstream stream arrives. Providers such as Qwen Cloud (older response
/// format) emit reasoning inline inside the <c>content</c> field; this extractor splits it out
/// so callers can re-emit the reasoning as <c>reasoning_content</c>.
///
/// The extractor is stateful across calls because a <c>&lt;think&gt;</c> / <c>&lt;/think&gt;</c>
/// tag may be split across SSE chunks. Any trailing text that could be the prefix of a tag is
/// held in <see cref="_pending"/> until the next call (or <see cref="Flush"/> at end of stream)
/// so a partial tag is never emitted as literal content.
/// </summary>
/// <remarks>
/// Shared by the legacy string-surgery rewriter and the Phase-B IR frame translator, which
/// applies the same tag rules at the content-part level. It lives here rather than nested in
/// the handler so both paths use one definition of the tag rules.
/// </remarks>
internal sealed class ThinkTagExtractor
{
    // Markup literals as escapes so this source file (like the test sources) never contains
    // raw angle-bracket tag sequences.
    private const string OpenTag = "\u003cthink\u003e";
    private const string CloseTag = "\u003c/think\u003e";

    // Qwen thinking compatibility markers: the model emits a literal [Thinking] marker, the
    // reasoning, then a literal [Answer] marker and the final answer.
    public const string QwenOpenTag = "[Thinking]";
    public const string QwenCloseTag = "[Answer]";

    private readonly string _openTag;
    private readonly string _closeTag;
    private bool _inThink;
    private string _pending = string.Empty;

    /// <summary>
    /// Creates an extractor for a tag pair, defaulting to the standard think tags. Uses an
    /// explicit constructor rather than a primary constructor so the parameter defaults can
    /// reference <see cref="OpenTag"/>/<see cref="CloseTag"/> rather than repeating their
    /// literals, which a primary constructor cannot do.
    /// </summary>
    public ThinkTagExtractor(string openTag = OpenTag, string closeTag = CloseTag)
    {
        _openTag = openTag;
        _closeTag = closeTag;
    }

    /// <summary>
    /// Returns the open/close tag pair for a given <see cref="ThinkingMode"/>: the Qwen
    /// <c>[Thinking]</c>/<c>[Answer]</c> markers for <see cref="ThinkingMode.QwenThinkingCompatible"/>,
    /// or the default <c>&lt;think&gt;</c>/<c>&lt;/think&gt;</c> blocks for every other mode.
    /// </summary>
    public static (string OpenTag, string CloseTag) TagsFor(ThinkingMode mode)
        => mode == ThinkingMode.QwenThinkingCompatible
            ? (QwenOpenTag, QwenCloseTag)
            : (OpenTag, CloseTag);

    /// <summary>
    /// Feeds the next incremental fragment of <c>content</c> text. Returns the separated
    /// reasoning and answer text that is safe to emit now (either may be empty). Call
    /// <see cref="Flush"/> once at end of stream to drain any buffered trailing text.
    /// </summary>
    public (string Reasoning, string Content) Process(string fragment)
    {
        if (fragment.Length == 0)
            return (string.Empty, string.Empty);

        string work = _pending + fragment;
        _pending = string.Empty;

        var reasoning = new StringBuilder();
        var content = new StringBuilder();

        int pos = 0;
        while (pos < work.Length)
        {
            string activeTag = _inThink ? _closeTag : _openTag;
            int tagIndex = work.IndexOf(activeTag, pos, StringComparison.Ordinal);

            if (tagIndex < 0)
            {
                // No complete tag found. Hold back a possible partial tag at the tail so it
                // can be completed by the next fragment instead of being emitted literally.
                int hold = PartialTagSuffixLength(work, pos, activeTag);
                int emitEnd = work.Length - hold;

                if (emitEnd > pos)
                    Append(_inThink ? reasoning : content, work[pos..emitEnd]);

                if (hold > 0)
                    _pending = work[emitEnd..];

                pos = work.Length;
                break;
            }

            // Emit text before the tag, then flip state and continue after the tag.
            if (tagIndex > pos)
                Append(_inThink ? reasoning : content, work[pos..tagIndex]);

            _inThink = !_inThink;
            pos = tagIndex + activeTag.Length;
        }

        return (reasoning.ToString(), content.ToString());
    }

    /// <summary>
    /// Drains any buffered trailing text at end of stream. If the stream ended while still
    /// inside an unterminated <c>&lt;think&gt;</c> block, the remainder is treated as reasoning;
    /// otherwise it is treated as answer content.
    /// </summary>
    public (string Reasoning, string Content) Flush()
    {
        string remaining = _pending;
        _pending = string.Empty;

        if (remaining.Length == 0)
            return (string.Empty, string.Empty);

        return _inThink ? (remaining, string.Empty) : (string.Empty, remaining);
    }

    /// <summary>
    /// One-shot extraction for a complete (non-streaming) <c>content</c> string. Returns the
    /// separated reasoning and answer text with all <c>&lt;think&gt;...&lt;/think&gt;</c> blocks
    /// removed from the answer.
    /// </summary>
    public static (string Reasoning, string Content) ExtractAll(string content)
        => ExtractAll(content, OpenTag, CloseTag);

    public static (string Reasoning, string Content) ExtractAll(string content, string openTag, string closeTag)
    {
        if (string.IsNullOrEmpty(content))
            return (string.Empty, string.Empty);

        ThinkTagExtractor extractor = new(openTag, closeTag);
        (string reasoning, string answer) = extractor.Process(content);
        (string tailReasoning, string tailAnswer) = extractor.Flush();
        return (reasoning + tailReasoning, answer + tailAnswer);
    }

    private static void Append(StringBuilder target, string text)
    {
        if (text.Length > 0)
            target.Append(text);
    }

    /// <summary>
    /// Returns the number of trailing characters (from <paramref name="start"/> onward) that
    /// form a proper prefix of <paramref name="tag"/>. This is the amount to buffer rather than
    /// emit, so a tag split across fragments is still recognised on the next call.
    /// </summary>
    private static int PartialTagSuffixLength(string work, int start, string tag)
    {
        int max = Math.Min(tag.Length - 1, work.Length - start);
        for (int len = max; len > 0; len--)
        {
            if (string.CompareOrdinal(work, work.Length - len, tag, 0, len) == 0)
                return len;
        }

        return 0;
    }
}