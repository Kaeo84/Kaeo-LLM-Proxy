namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// One line-range edit. Lines are 1-based and inclusive. Modes:
/// replace (swap the range for NewContent's lines), delete (drop the range),
/// insertBefore (insert at StartLine), insertAfter (insert after EndLine).
/// NewContent's trailing newline is ignored; an empty NewContent contributes zero lines.
/// </summary>
public sealed class LineEdit
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Mode { get; set; } = "replace";
    public string? NewContent { get; set; }

    /// <summary>When set, must equal the trimmed current content of the first anchor line.</summary>
    public string? ExpectedFirstLine { get; set; }

    /// <summary>When set, must equal the trimmed current content of the last anchor line.</summary>
    public string? ExpectedLastLine { get; set; }
}

/// <summary>Outcome of applying a batch of line edits: either NewText or a descriptive Error.</summary>
public sealed class LineEditResult
{
    public bool Success { get; }
    public string? Error { get; }
    public string? NewText { get; }
    public string? Preview { get; }
    public int NewLineCount { get; }

    private LineEditResult(bool success, string? error, string? newText, string? preview, int newLineCount)
    {
        Success = success;
        Error = error;
        NewText = newText;
        Preview = preview;
        NewLineCount = newLineCount;
    }

    public static LineEditResult Fail(string error) => new(false, error, null, null, 0);
    public static LineEditResult Ok(string newText, string preview, int newLineCount) => new(true, null, newText, preview, newLineCount);
}

/// <summary>
/// Pure line-range edit engine (no I/O): validates a batch against the original text, then applies
/// it bottom-up so earlier ranges keep their numbering. The dominant line ending and the presence
/// of a trailing newline are preserved. Encoding/BOM handling belongs to the caller.
/// </summary>
public static class LineEditor
{
    public static LineEditResult Apply(string text, IReadOnlyList<LineEdit> edits)
    {
        if (edits is null || edits.Count == 0)
            return LineEditResult.Fail("At least one edit is required.");

        var newline = DetectDominantNewline(text);
        var endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = SplitLines(text, endsWithNewline);

        // Validate every edit against the ORIGINAL text before touching anything (all-or-nothing).
        var normalized = new List<(LineEdit Edit, string[] Content, int GapStart, int GapEnd)>();
        foreach (var edit in edits)
        {
            var mode = (edit.Mode ?? "replace").Trim().ToLowerInvariant();
            if (mode is not ("replace" or "delete" or "insertbefore" or "insertafter"))
                return LineEditResult.Fail($"Unknown edit mode '{edit.Mode}'. Use replace, delete, insertBefore, or insertAfter.");
            if (edit.StartLine < 1 || edit.EndLine < edit.StartLine)
                return LineEditResult.Fail($"Invalid range {edit.StartLine}-{edit.EndLine}: lines are 1-based and EndLine must be >= StartLine.");
            if (edit.EndLine > lines.Count)
                return LineEditResult.Fail($"Range {edit.StartLine}-{edit.EndLine} is past end of file ({lines.Count} lines).");
            if (mode is "replace" or "insertbefore" or "insertafter" && edit.NewContent is null)
                return LineEditResult.Fail($"Mode '{mode}' requires newContent (use an empty string to contribute zero lines).");

            // Anchor lines for the staleness guards: the range ends for replace/delete, the single
            // insertion anchor for inserts (start line for insertBefore, end line for insertAfter).
            var firstAnchor = mode == "insertafter" ? edit.EndLine - 1 : edit.StartLine - 1;
            var lastAnchor = mode == "insertbefore" ? edit.StartLine - 1 : edit.EndLine - 1;
            if (edit.ExpectedFirstLine is not null
                && !string.Equals(lines[firstAnchor].Trim(), edit.ExpectedFirstLine.Trim(), StringComparison.Ordinal))
                return LineEditResult.Fail($"Stale edit: line {firstAnchor + 1} is \"{lines[firstAnchor].Trim()}\" but expected \"{edit.ExpectedFirstLine.Trim()}\". Re-read the file and retry.");
            if (edit.ExpectedLastLine is not null
                && !string.Equals(lines[lastAnchor].Trim(), edit.ExpectedLastLine.Trim(), StringComparison.Ordinal))
                return LineEditResult.Fail($"Stale edit: line {lastAnchor + 1} is \"{lines[lastAnchor].Trim()}\" but expected \"{edit.ExpectedLastLine.Trim()}\". Re-read the file and retry.");

            var content = mode == "delete" ? Array.Empty<string>() : SplitContent(edit.NewContent!);

            // Footprint in "gap" coordinates (gap g sits between line g and line g+1): a range
            // replace/delete occupies gaps start-1..end-1 (inserts at its outer boundaries are
            // allowed); an insert occupies a single gap. Adjacent ranges do not conflict.
            int gapStart, gapEnd;
            switch (mode)
            {
                case "insertbefore": gapStart = gapEnd = edit.StartLine - 1; break;
                case "insertafter": gapStart = gapEnd = edit.EndLine; break;
                default: gapStart = edit.StartLine - 1; gapEnd = edit.EndLine - 1; break;
            }
            normalized.Add((edit, content, gapStart, gapEnd));
        }

        // Overlap / duplicate-insert detection on the original numbering.
        for (var i = 0; i < normalized.Count; i++)
        {
            for (var j = i + 1; j < normalized.Count; j++)
            {
                var a = normalized[i];
                var b = normalized[j];
                if (a.GapStart <= b.GapEnd && b.GapStart <= a.GapEnd)
                    return LineEditResult.Fail($"Edits {i + 1} ({a.Edit.StartLine}-{a.Edit.EndLine}) and {j + 1} ({b.Edit.StartLine}-{b.Edit.EndLine}) overlap or insert at the same position; combine them into one edit.");
            }
        }

        // Apply bottom-up so lower line numbers stay valid while higher edits land.
        var working = new List<string>(lines);
        foreach (var (edit, content, _, _) in normalized.OrderByDescending(e => e.Edit.StartLine).ThenByDescending(e => e.Edit.EndLine))
        {
            var mode = edit.Mode.Trim().ToLowerInvariant();
            switch (mode)
            {
                case "replace":
                    working.RemoveRange(edit.StartLine - 1, edit.EndLine - edit.StartLine + 1);
                    working.InsertRange(edit.StartLine - 1, content);
                    break;
                case "delete":
                    working.RemoveRange(edit.StartLine - 1, edit.EndLine - edit.StartLine + 1);
                    break;
                case "insertbefore":
                    working.InsertRange(edit.StartLine - 1, content);
                    break;
                case "insertafter":
                    working.InsertRange(edit.EndLine, content);
                    break;
            }
        }

        var newText = working.Count == 0
            ? string.Empty
            : string.Join(newline, working) + (endsWithNewline ? newline : string.Empty);

        // Preview around the topmost edit: edits below it cannot shift its position.
        var top = normalized.OrderBy(e => e.Edit.StartLine).First();
        var previewStart = Math.Max(1, top.Edit.StartLine - 3);
        var previewEnd = Math.Min(working.Count, top.Edit.StartLine + Math.Max(top.Content.Length, 1) + 2);
        var previewLines = new List<string>();
        for (var n = previewStart; n <= previewEnd; n++)
            previewLines.Add($"{n}: {working[n - 1]}");

        return LineEditResult.Ok(newText, string.Join("\n", previewLines), working.Count);
    }

    private static string DetectDominantNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        return lf > crlf ? "\n" : "\r\n";
    }

    private static List<string> SplitLines(string text, bool endsWithNewline)
    {
        var parts = text.Replace("\r\n", "\n").Split('\n');
        var count = endsWithNewline ? parts.Length - 1 : parts.Length;
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
            lines.Add(parts[i]);
        return lines;
    }

    private static string[] SplitContent(string content)
    {
        if (content.Length == 0)
            return Array.Empty<string>();
        var normalized = content.Replace("\r\n", "\n");
        if (normalized.EndsWith("\n", StringComparison.Ordinal))
            normalized = normalized.Substring(0, normalized.Length - 1);
        return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
    }
}