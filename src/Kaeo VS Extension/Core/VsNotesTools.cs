using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Task-memory tools backed by <c>.kaeo/notes.md</c> at the solution root. The pure
/// formatting/parsing lives in <see cref="AgentNotes"/>; this layer adds DTE root resolution
/// and sharing-violation-tolerant IO (multiple extension instances may run concurrently).
/// </summary>
internal static class VsNotesTools
{
    internal static string GetNotesPath(string solutionRoot)
        => Path.Combine(solutionRoot, ".kaeo", "notes.md");

    internal static async Task<string> AppendNoteAsync(JsonNode? args, CancellationToken ct)
    {
        var text = args?["text"]?.GetValue<string>() ?? throw new ArgumentException("text is required");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text must not be empty");
        var tags = args?["tags"]?.GetValue<string>();

        var root = await InstructionFileLoader.GetSolutionRootPathAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(root))
            return "No solution is open.";

        var path = GetNotesPath(root!);
        var entry = AgentNotes.FormatEntry(DateTime.Now, tags, text);
        var existing = ReadTolerant(path, out var encoding);
        if (!WriteTolerant(path, AgentNotes.AppendEntry(existing, entry), encoding))
            return "Could not write notes (the file is locked by another process). Note not saved.";

        return "Note saved: " + entry;
    }

    internal static async Task<string> ReadNotesAsync(JsonNode? args, CancellationToken ct)
    {
        var maxEntries = args?["maxEntries"]?.GetValue<int?>() ?? AgentNotes.DefaultMaxEntries;

        var root = await InstructionFileLoader.GetSolutionRootPathAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(root))
            return "No solution is open.";

        var content = ReadTolerant(GetNotesPath(root!), out _);
        var tail = AgentNotes.TailNewestFirst(content, maxEntries);
        return string.IsNullOrEmpty(tail) ? "No notes yet." : tail;
    }

    /// <summary>Recent notes for system-prompt injection. Runs on a background thread; the
    /// caller has already resolved the solution root. Never throws — returns empty on any failure.</summary>
    internal static string ReadRecentNotes(string? solutionRoot, int maxEntries)
    {
        if (string.IsNullOrEmpty(solutionRoot))
            return string.Empty;
        var content = ReadTolerant(GetNotesPath(solutionRoot!), out _);
        return AgentNotes.TailNewestFirst(content, maxEntries);
    }

    /// <summary>Compaction hook: persists the compaction summary as a tagged note so task context
    /// survives history replacement. Fire-and-forget safe — swallows and logs every failure.</summary>
    internal static async Task AppendCompactionNoteAsync(string summary)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(summary))
                return;
            var root = await InstructionFileLoader.GetSolutionRootPathAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(root))
                return;

            var path = GetNotesPath(root!);
            var entry = AgentNotes.FormatEntry(DateTime.Now, "compaction", summary);
            var existing = ReadTolerant(path, out var encoding);
            WriteTolerant(path, AgentNotes.AppendEntry(existing, entry), encoding);
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Failed to append compaction note: {ex.Message}");
        }
    }

    private static string ReadTolerant(string path, out Encoding encoding)
    {
        encoding = new UTF8Encoding(false);
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            var (text, enc) = FileTextIo.Read(path);
            encoding = enc;
            return text;
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Notes read skipped (file locked): {ex.Message}");
            return string.Empty;
        }
    }

    private static bool WriteTolerant(string path, string text, Encoding encoding)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            BuiltInVsTools.FileIoWithRetry(path, () => FileTextIo.Write(path, text, encoding));
            return true;
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Notes write skipped (file locked): {ex.Message}");
            return false;
        }
    }
}