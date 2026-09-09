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
/// Loads instruction/context files from solution and project directories.
/// Solution-level instructions are inherited by all projects.
/// Project-level instructions are only available to that specific project.
/// </summary>
internal static class InstructionFileLoader
{
    public const string SolutionInstructionFile = ".kaeo-instructions.md";
    public const string ProjectInstructionFile = ".kaeo-instructions.md";
    public const string CopilotInstructionsFile = ".github/copilot-instructions.md";
    public const string UserInstructionsFile = "copilot-instructions.md";

    /// <summary>
    /// Gets all applicable instruction files for a given project context.
    /// Returns solution-level instructions plus project-level instructions.
    /// </summary>
    public static async Task<List<InstructionFile>> GetInstructionsForProjectAsync(
        string solutionRootPath,
        string projectDirectory,
        CancellationToken ct = default)
    {
        var instructions = new List<InstructionFile>();

        // Load solution-level instructions (inherited by all projects)
        var solutionInstructions = await LoadSolutionInstructionsAsync(solutionRootPath, ct);
        instructions.AddRange(solutionInstructions);

        // Load project-level instructions (only for this project)
        var projectInstructions = await LoadProjectInstructionsAsync(projectDirectory, ct);
        instructions.AddRange(projectInstructions);

        return instructions;
    }

    /// <summary>
    /// Gets solution-level instructions only (not project-specific).
    /// </summary>
    public static async Task<List<InstructionFile>> GetSolutionInstructionsAsync(
        string solutionRootPath,
        CancellationToken ct = default)
    {
        return await LoadSolutionInstructionsAsync(solutionRootPath, ct);
    }

    /// <summary>
    /// Loads solution-level instruction files from the solution root directory.
    /// </summary>
    private static Task<List<InstructionFile>> LoadSolutionInstructionsAsync(
        string solutionRootPath,
        CancellationToken ct)
    {
        var instructions = new List<InstructionFile>();

        if (string.IsNullOrEmpty(solutionRootPath) || !Directory.Exists(solutionRootPath))
            return Task.FromResult(instructions);

        // Check for .kaeo-instructions.md in solution root
        var kaeoFile = Path.Combine(solutionRootPath, SolutionInstructionFile);
        if (File.Exists(kaeoFile))
        {
            var content = File.ReadAllText(kaeoFile);
            instructions.Add(new InstructionFile
            {
                Path = kaeoFile,
                Content = content,
                Scope = InstructionScope.Solution,
                Name = "Solution Instructions (.kaeo-instructions.md)"
            });
        }

        // Check for .github/copilot-instructions.md (GitHub Copilot format)
        var githubDir = Path.Combine(solutionRootPath, ".github");
        if (Directory.Exists(githubDir))
        {
            var copilotFile = Path.Combine(githubDir, CopilotInstructionsFile);
            if (File.Exists(copilotFile))
            {
                var content = File.ReadAllText(copilotFile);
                instructions.Add(new InstructionFile
                {
                    Path = copilotFile,
                    Content = content,
                    Scope = InstructionScope.Solution,
                    Name = "GitHub Copilot Instructions"
                });
            }
        }

        // Check for copilot-instructions.md in solution root (user-level)
        var userFile = Path.Combine(solutionRootPath, UserInstructionsFile);
        if (File.Exists(userFile))
        {
            var content = File.ReadAllText(userFile);
            instructions.Add(new InstructionFile
            {
                Path = userFile,
                Content = content,
                Scope = InstructionScope.Solution,
                Name = "User Instructions (copilot-instructions.md)"
            });
        }

        return Task.FromResult(instructions);
    }

    /// <summary>
    /// Loads project-level instruction files from the project directory.
    /// </summary>
    private static Task<List<InstructionFile>> LoadProjectInstructionsAsync(
        string projectDirectory,
        CancellationToken ct)
    {
        var instructions = new List<InstructionFile>();

        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            return Task.FromResult(instructions);

        // Check for .kaeo-instructions.md in project directory
        var kaeoFile = Path.Combine(projectDirectory, ProjectInstructionFile);
        if (File.Exists(kaeoFile))
        {
            var content = File.ReadAllText(kaeoFile);
            instructions.Add(new InstructionFile
            {
                Path = kaeoFile,
                Content = content,
                Scope = InstructionScope.Project,
                Name = "Project Instructions (.kaeo-instructions.md)"
            });
        }

        return Task.FromResult(instructions);
    }

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

    /// <summary>
    /// Gets the project directory path from a project name.
    /// </summary>
    public static string GetProjectDirectoryPath(string projectName)
    {
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.Solution?.IsOpen != true)
                return null;

            foreach (Project project in dte.Solution.Projects)
            {
                if (project.Name?.Equals(projectName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    var projectPath = project.FullName;
                    if (!string.IsNullOrEmpty(projectPath))
                        return Path.GetDirectoryName(projectPath);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
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
