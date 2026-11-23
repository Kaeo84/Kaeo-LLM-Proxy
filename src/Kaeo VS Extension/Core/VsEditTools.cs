using System.IO;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Line-range editing tools backed by the pure <see cref="LineEditor"/> engine. These complement
/// vs_replace_in_file (exact-string) with positional edits: replace/insert/delete by line number,
/// append, and atomic multi-edit batches. Encoding/BOM and the dominant line ending are preserved.
/// </summary>
internal static class VsEditTools
{
    public static Task<string> EditLinesAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var edit = new LineEdit
        {
            StartLine = args?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine is required"),
            EndLine = args?["endLine"]?.GetValue<int>() ?? args?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine is required"),
            Mode = args?["mode"]?.GetValue<string>() ?? "replace",
            NewContent = args?["newContent"]?.GetValue<string>(),
            ExpectedFirstLine = args?["expectedFirstLine"]?.GetValue<string>(),
            ExpectedLastLine = args?["expectedLastLine"]?.GetValue<string>(),
        };
        return ApplyBatchAsync(filePath, [edit], ct);
    }

    public static Task<string> ApplyEditsAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        if (args?["edits"] is not JsonArray array || array.Count == 0)
            return Task.FromResult("edits is required and must be a non-empty array.");

        var edits = new List<LineEdit>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj)
                return Task.FromResult("Each edit must be an object with startLine/endLine/mode/newContent.");
            edits.Add(new LineEdit
            {
                StartLine = obj["startLine"]?.GetValue<int>() ?? 0,
                EndLine = obj["endLine"]?.GetValue<int>() ?? obj["startLine"]?.GetValue<int>() ?? 0,
                Mode = obj["mode"]?.GetValue<string>() ?? "replace",
                NewContent = obj["newContent"]?.GetValue<string>(),
                ExpectedFirstLine = obj["expectedFirstLine"]?.GetValue<string>(),
                ExpectedLastLine = obj["expectedLastLine"]?.GetValue<string>(),
            });
        }
        return ApplyBatchAsync(filePath, edits, ct);
    }

    public static Task<string> AppendFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var content = args?["content"]?.GetValue<string>() ?? throw new ArgumentException("content is required");
        if (content.Length == 0)
            return Task.FromResult("content must not be empty.");
        if (!File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}. Use vs_create_file to create it.");

        string text;
        System.Text.Encoding encoding;
        try
        {
            (text, encoding) = BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Read(filePath));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not read '{filePath}' for append: {ex.Message}");
            return Task.FromResult($"Could not read file: {ex.Message}");
        }

        // Keep the appended block on its own line and end the file with a newline.
        var newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        var appended = content.Replace("\r\n", "\n").Replace("\n", newline);
        var updated = text;
        if (updated.Length > 0 && !updated.EndsWith("\n", StringComparison.Ordinal))
            updated += newline;
        updated += appended;
        if (!updated.EndsWith("\n", StringComparison.Ordinal))
            updated += newline;

        try
        {
            BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Write(filePath, updated, encoding));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not write '{filePath}' for append: {ex.Message}");
            return Task.FromResult($"Could not write file: {ex.Message}");
        }

        var lineCount = updated.Split('\n').Length - (updated.EndsWith("\n", StringComparison.Ordinal) ? 1 : 0);
        return Task.FromResult($"Appended {appended.Split('\n').Length} line(s) to {filePath} (now {lineCount} lines).");
    }

    private static Task<string> ApplyBatchAsync(string filePath, IReadOnlyList<LineEdit> edits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        string text;
        System.Text.Encoding encoding;
        try
        {
            (text, encoding) = BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Read(filePath));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not read '{filePath}' for line edit: {ex.Message}");
            return Task.FromResult($"Could not read file: {ex.Message}");
        }

        var oldLineCount = text.Length == 0 ? 0 : text.Split('\n').Length - (text.EndsWith("\n", StringComparison.Ordinal) ? 1 : 0);
        var result = LineEditor.Apply(text, edits);
        if (!result.Success)
            return Task.FromResult($"Edit rejected (file unchanged): {result.Error}");

        try
        {
            BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Write(filePath, result.NewText!, encoding));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not write '{filePath}' for line edit: {ex.Message}");
            return Task.FromResult($"Could not write file: {ex.Message}");
        }

        return Task.FromResult(
            $"Applied {edits.Count} edit(s) to {filePath}: {oldLineCount} → {result.NewLineCount} lines.\nPreview:\n{result.Preview}");
    }
}