using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Map-reduce ("hierarchical") compaction for long histories (Defaults.HierarchicalCompaction,
/// off by default). Instead of one summarization call over the whole transcript — which loses
/// early architectural decisions once the transcript approaches the model's window — the
/// history is chunked into ~8K-token segments, each segment is summarized independently, and
/// the segment summaries are merged into one final summary. Model calls are injected as a
/// delegate so the engine stays testable and host-agnostic.
/// </summary>
public static class HierarchicalCompactor
{
    /// <summary>Target segment size in estimated tokens (chars / 4, same heuristic as the context bar).</summary>
    public const int DefaultChunkTokens = 8000;

    /// <summary>
    /// Splits messages into segments of at most <paramref name="chunkTokens"/> estimated tokens.
    /// Messages are never split; a single oversized message becomes its own segment.
    /// </summary>
    public static IReadOnlyList<List<ChatMessage>> Chunk(IReadOnlyList<ChatMessage> messages, int chunkTokens = DefaultChunkTokens)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (chunkTokens <= 0) throw new ArgumentOutOfRangeException(nameof(chunkTokens));

        var chunks = new List<List<ChatMessage>>();
        var current = new List<ChatMessage>();
        var currentTokens = 0;
        foreach (var message in messages)
        {
            var tokens = EstimateTokens(message);
            if (current.Count > 0 && currentTokens + tokens > chunkTokens)
            {
                chunks.Add(current);
                current = [];
                currentTokens = 0;
            }
            current.Add(message);
            currentTokens += tokens;
        }
        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    /// <summary>Best-effort token estimate for one message (content chars / 4, minimum 1).</summary>
    public static int EstimateTokens(ChatMessage message) => Math.Max(1, ContentChars(message) / 4);

    /// <summary>
    /// Runs the map-reduce pipeline: one summarize call per segment plus a final merge call
    /// (a single-segment history needs just one call). The delegate receives the complete prompt
    /// and must return the summary text only.
    /// </summary>
    public static async Task<string> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        Func<string, CancellationToken, Task<string>> summarize,
        int chunkTokens = DefaultChunkTokens,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));
        if (summarize is null) throw new ArgumentNullException(nameof(summarize));

        var chunks = Chunk(history, chunkTokens);
        if (chunks.Count == 0) return string.Empty;

        if (chunks.Count == 1)
        {
            progress?.Invoke("[hierarchical compaction: summarizing single segment]");
            return await summarize(BuildSegmentPrompt(RenderTranscript(chunks[0]), 1, 1), ct).ConfigureAwait(false);
        }

        var partials = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"[hierarchical compaction: summarizing segment {i + 1}/{chunks.Count}]");
            var partial = await summarize(BuildSegmentPrompt(RenderTranscript(chunks[i]), i + 1, chunks.Count), ct).ConfigureAwait(false);
            partials.Add(partial);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Invoke("[hierarchical compaction: merging segment summaries]");
        return await summarize(BuildMergePrompt(partials), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders messages as plain transcript text, including tool calls and results — they carry
    /// the decisions and outcomes a summary must preserve.
    /// </summary>
    public static string RenderTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.User ? "User"
                : message.Role == ChatRole.Assistant ? "Assistant"
                : message.Role == ChatRole.System ? "System"
                : "Tool";
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        sb.Append(role).Append(": ").AppendLine(text.Text);
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        sb.Append(role).Append(" (reasoning): ").AppendLine(reasoning.Text);
                        break;
                    case FunctionCallContent call:
                        sb.Append(role).Append(": [tool call ").Append(call.Name).Append(' ')
                            .Append(SerializeSafe(call.Arguments)).AppendLine("]");
                        break;
                    case FunctionResultContent result:
                        sb.Append("Tool result: ").AppendLine(SerializeSafe(result.Result));
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private static string BuildSegmentPrompt(string transcript, int index, int total) =>
        $"Summarize segment {index} of {total} of a coding-agent conversation. Preserve every key fact, "
        + "decision, file path, command, error, and outcome; drop pleasantries and redundant detail. "
        + "Output ONLY the summary text, no commentary.\n\n---\n" + transcript;

    private static string BuildMergePrompt(IReadOnlyList<string> partials) =>
        $"Merge these {partials.Count} chronological segment summaries of one conversation into a single coherent "
        + "summary. Deduplicate, keep chronological order, and preserve all key facts, decisions, file paths, "
        + "commands, errors, and outcomes. Output ONLY the merged summary text, no commentary.\n\n---\n"
        + string.Join("\n\n---\n", partials);

    private static int ContentChars(ChatMessage message)
    {
        var chars = 0;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    chars += text.Text?.Length ?? 0;
                    break;
                case TextReasoningContent reasoning:
                    chars += reasoning.Text?.Length ?? 0;
                    break;
                case FunctionCallContent call:
                    chars += call.Name?.Length ?? 0;
                    chars += SerializedLength(call.Arguments);
                    break;
                case FunctionResultContent result:
                    chars += SerializedLength(result.Result);
                    break;
            }
        }
        return chars;
    }

    private static int SerializedLength(object? value) => SerializeSafe(value).Length;

    private static string SerializeSafe(object? value)
    {
        if (value is null) return string.Empty;
        if (value is string s) return s;
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
