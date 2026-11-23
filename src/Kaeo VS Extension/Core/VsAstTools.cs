using System.IO;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// AST-aware C# editing tools backed by the pure <see cref="AstEditor"/> engine (Roslyn).
/// Syntax-validated member replacement that preserves comments/indentation and the file's
/// encoding/BOM/line endings. When Roslyn cannot load in the VS host the tools report that
/// honestly and point at the line-edit fallback instead of failing silently.
/// </summary>
internal static class VsAstTools
{
    public static Task<string> ReplaceMethodAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var type = args?["type"]?.GetValue<string>() ?? throw new ArgumentException("type is required");
        var method = args?["method"]?.GetValue<string>() ?? throw new ArgumentException("method is required");
        var newCode = args?["newCode"]?.GetValue<string>() ?? throw new ArgumentException("newCode is required");
        string[]? parameterTypes = null;
        if (args?["parameterTypes"] is JsonArray array)
            parameterTypes = array.Select(n => n?.GetValue<string>() ?? string.Empty).ToArray();
        return Task.FromResult(Apply(filePath, text => AstEditor.ReplaceMethod(text, type, method, newCode, parameterTypes), ct));
    }

    public static Task<string> ReplaceMemberAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var type = args?["type"]?.GetValue<string>() ?? throw new ArgumentException("type is required");
        var member = args?["member"]?.GetValue<string>() ?? throw new ArgumentException("member is required");
        var newCode = args?["newCode"]?.GetValue<string>() ?? throw new ArgumentException("newCode is required");
        return Task.FromResult(Apply(filePath, text => AstEditor.ReplaceMember(text, type, member, newCode), ct));
    }

    public static Task<string> AddUsingAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = BuiltInVsTools.ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var ns = args?["namespace"]?.GetValue<string>() ?? throw new ArgumentException("namespace is required");
        return Task.FromResult(Apply(filePath, text => AstEditor.AddUsing(text, ns), ct));
    }

    /// <summary>
    /// Shared read → edit → write pipeline. The Roslyn call is invoked through a delegate so
    /// assembly-load failures surface inside the try (the calling method's own JIT never
    /// touches Roslyn types) and can be reported as an honest fallback message.
    /// </summary>
    private static string Apply(string filePath, Func<string, AstEditResult> edit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return "AST editing supports C# (.cs) files only. Use vs_edit_lines or vs_replace_in_file for other files.";
        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        string text;
        System.Text.Encoding encoding;
        try
        {
            (text, encoding) = BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Read(filePath));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not read '{filePath}' for AST edit: {ex.Message}");
            return $"Could not read file: {ex.Message}";
        }

        AstEditResult result;
        try
        {
            result = edit(text);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or TypeLoadException or MissingMethodException or BadImageFormatException)
        {
            DebugLog.Warn($"AST engine unavailable in this host: {ex.Message}");
            return $"AST editing is unavailable in this host (Roslyn failed to load: {ex.Message}). Use vs_edit_lines or vs_replace_in_file instead.";
        }

        if (!result.Success)
            return $"Edit rejected (file unchanged): {result.Error}";

        try
        {
            BuiltInVsTools.FileIoWithRetry(filePath, () => FileTextIo.Write(filePath, result.NewText!, encoding));
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not write '{filePath}' for AST edit: {ex.Message}");
            return $"Could not write file: {ex.Message}";
        }

        return $"AST edit applied to {filePath} (syntax validated before write).";
    }
}