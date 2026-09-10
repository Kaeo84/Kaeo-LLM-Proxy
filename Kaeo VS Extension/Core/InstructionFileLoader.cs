using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Resolves the Settings → Instructions list into the instruction content injected into the model
/// prompt. The list is the single source of truth: each entry is either inline text or a file that
/// is read from disk. Relative file paths resolve against the solution root.
/// </summary>
internal static class InstructionFileLoader
{
    /// <summary>Default solution-root file used by the seeded "Solution Instructions" entry.</summary>
    public const string SolutionInstructionFile = ".kaeo-instructions.md";

    /// <summary>
    /// The two instruction sources seeded into an empty list: an inline "User Preferences" text entry
    /// and a "Solution Instructions" file entry pointing at <see cref="SolutionInstructionFile"/>.
    /// </summary>
    public static List<InstructionEntry> DefaultEntries() => new()
    {
        new InstructionEntry { Name = "User Preferences", Kind = InstructionKind.Text, Content = string.Empty, Enabled = true, Order = 0 },
        new InstructionEntry { Name = "Solution Instructions", Kind = InstructionKind.File, Path = SolutionInstructionFile, Enabled = true, Order = 1 },
    };

    /// <summary>
    /// Resolves enabled settings entries (ordered) into instruction content. Text entries use their
    /// inline content; file entries are read from disk. Missing or empty sources are skipped.
    /// </summary>
    public static List<InstructionFile> LoadFromSettings(IEnumerable<InstructionEntry>? entries)
    {
        var result = new List<InstructionFile>();

        foreach (var entry in (entries ?? Array.Empty<InstructionEntry>()).Where(e => e.Enabled).OrderBy(e => e.Order))
        {
            if (string.Equals(entry.Kind, InstructionKind.File, StringComparison.OrdinalIgnoreCase))
            {
                var path = ResolvePath(entry.Path);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    result.Add(new InstructionFile
                    {
                        Path = path,
                        Content = File.ReadAllText(path),
                        Scope = InstructionScope.Solution,
                        Name = Display(entry),
                    });
                }
                catch (Exception ex)
                {
                    DebugLog.Error($"Failed to read instruction file '{path}': {ex.Message}");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.Content))
                    continue;
                result.Add(new InstructionFile
                {
                    Path = "(inline)",
                    Content = entry.Content!,
                    Scope = InstructionScope.Solution,
                    Name = Display(entry),
                });
            }
        }

        return result;
    }

    /// <summary>Reads the current on-disk content for a file entry (empty when missing). Used by the editor and tools.</summary>
    public static string ReadFileContent(string? path)
    {
        var full = ResolvePath(path);
        if (string.IsNullOrEmpty(full) || !File.Exists(full))
            return string.Empty;
        try { return File.ReadAllText(full); }
        catch { return string.Empty; }
    }

    /// <summary>Writes <paramref name="content"/> to the file entry's resolved path, creating directories as needed.</summary>
    public static void WriteFileContent(string? path, string? content)
    {
        var full = ResolvePath(path);
        if (string.IsNullOrEmpty(full))
            throw new ArgumentException("No file path is set for this instruction.", nameof(path));
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, content ?? string.Empty);
    }

    /// <summary>Resolves a possibly solution-relative path to an absolute path (null when empty).</summary>
    public static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path))
            return path;
        var root = GetSolutionRootPath();
        return root != null ? Path.Combine(root, path!) : path;
    }

    private static string Display(InstructionEntry e)
        => !string.IsNullOrWhiteSpace(e.Name) ? e.Name!
         : !string.IsNullOrWhiteSpace(e.Path) ? Path.GetFileName(e.Path!)
         : "Instruction";

    /// <summary>
    /// Combines all instruction content into a single string for model context.
    /// </summary>
    public static string CombineInstructions(List<InstructionFile> instructions)
    {
        if (instructions == null || instructions.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# Instructions");
        sb.AppendLine();

        foreach (var instruction in instructions)
        {
            sb.AppendLine($"## {instruction.Name}");
            sb.AppendLine($"*Scope: {instruction.Scope}*");
            sb.AppendLine($"*Path: {instruction.Path}*");
            sb.AppendLine();
            sb.AppendLine(instruction.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the solution root path from the current Visual Studio solution.
    /// </summary>
    public static string GetSolutionRootPath()
    {
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            var solutionPath = dte?.Solution?.FullName;
            if (string.IsNullOrEmpty(solutionPath))
                return null;

            return Path.GetDirectoryName(solutionPath);
        }
        catch
        {
            return null;
        }
    }

    }

/// <summary>
/// Represents an instruction file with its content and scope.
/// </summary>
internal class InstructionFile
{
    /// <summary>Full path to the instruction file.</summary>
    public string Path { get; set; }

    /// <summary>Content of the instruction file.</summary>
    public string Content { get; set; }

    /// <summary>Scope of the instructions (Solution or Project).</summary>
    public InstructionScope Scope { get; set; }

    /// <summary>Display name for the instruction file.</summary>
    public string Name { get; set; }
}

/// <summary>
/// Defines the scope of instruction files.
/// </summary>
internal enum InstructionScope
{
    /// <summary>Applies to the entire solution and all projects.</summary>
    Solution,

    /// <summary>Applies only to a specific project.</summary>
    Project
}
