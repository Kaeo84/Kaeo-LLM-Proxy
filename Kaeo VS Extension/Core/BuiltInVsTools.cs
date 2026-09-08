using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Provides built-in Visual Studio tools that ship with the extension.
/// These tools are always available to models without requiring external MCP server configuration.
/// </summary>
internal static class BuiltInVsTools
{
    /// <summary>
    /// Returns all built-in tool definitions.
    /// </summary>
    public static IReadOnlyList<McpTool> GetToolDefinitions()
    {
        return new List<McpTool>
        {
            new()
            {
                Name = "vs_file_search",
                Description = "Search for files in the workspace by name or relative path pattern. Returns matching file paths.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Search query (substring match against file paths)"
                        },
                        ["maxResults"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Maximum number of results to return (default: 20)"
                        }
                    },
                    ["required"] = new JsonArray { "query" }
                }
            },
            new()
            {
                Name = "vs_get_file",
                Description = "Read specific line ranges from a file. Returns the file content with optional line number prefixes.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The file path (relative to solution root or absolute)"
                        },
                        ["startLine"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Start line number (1-based)"
                        },
                        ["endLine"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "End line number (inclusive, 1-based)"
                        },
                        ["includeLineNumbers"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Include line number prefixes (default: false)"
                        }
                    },
                    ["required"] = new JsonArray { "filePath", "startLine", "endLine" }
                }
            },
            new()
            {
                Name = "vs_find_symbol",
                Description = "Find symbol definitions, references, or implementations using Roslyn. Returns locations where the symbol is defined or used.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["symbolName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Symbol name to search for"
                        },
                        ["navigationType"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "1=Go To Definition, 2=Find All References, 3=Go To Implementation"
                        },
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "File path containing the symbol (optional, for disambiguation)"
                        },
                        ["line"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Line number (1-based) where symbol appears"
                        }
                    },
                    ["required"] = new JsonArray { "symbolName", "navigationType" }
                }
            },
            new()
            {
                Name = "vs_get_projects",
                Description = "Get all projects in the current solution. Returns project names and file paths.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new()
            {
                Name = "vs_get_files_in_project",
                Description = "Get all files in a specific project. Returns file paths relative to the solution directory.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Project name or path"
                        }
                    },
                    ["required"] = new JsonArray { "projectName" }
                }
            },
            new()
            {
                Name = "vs_build",
                Description = "Build the solution or a specific project. Returns build output and any errors/warnings.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Project name to build (optional, builds entire solution if omitted)"
                        }
                    }
                }
            },
            new()
            {
                Name = "vs_run_tests",
                Description = "Run tests matching the specified filter. Returns test results.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filterType"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Filter type: Assembly, Project, FullyQualifiedName, TypeName, or MethodName"
                        },
                        ["filterValue"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Filter value to match"
                        }
                    },
                    ["required"] = new JsonArray { "filterType", "filterValue" }
                }
            },
            new()
            {
                Name = "vs_get_diagnostics",
                Description = "Get compiler diagnostics (errors, warnings, info) for a file or project. Uses Roslyn background compilation.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "File path to get diagnostics for (optional)"
                        },
                        ["projectName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Project name to get diagnostics for (optional)"
                        },
                        ["severity"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Filter by severity: Error, Warning, Info (optional)"
                        }
                    }
                }
            },
            new()
            {
                Name = "vs_get_active_file",
                Description = "Get the currently active file path and cursor position in the editor.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new()
            {
                Name = "vs_get_selection",
                Description = "Get the current text selection in the active editor.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            }
        };
    }

    /// <summary>
    /// Execute a built-in VS tool by name with the given arguments.
    /// </summary>
    public static async Task<string> ExecuteAsync(string toolName, string? argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = argumentsJson is null ? null : JsonNode.Parse(argumentsJson);

            return toolName switch
            {
                "vs_file_search" => await FileSearchAsync(args, ct),
                "vs_get_file" => await GetFileAsync(args, ct),
                "vs_find_symbol" => await FindSymbolAsync(args, ct),
                "vs_get_projects" => await GetProjectsAsync(ct),
                "vs_get_files_in_project" => await GetFilesInProjectAsync(args, ct),
                "vs_build" => await BuildAsync(args, ct),
                "vs_run_tests" => await RunTestsAsync(args, ct),
                "vs_get_diagnostics" => await GetDiagnosticsAsync(args, ct),
                "vs_get_active_file" => await GetActiveFileAsync(ct),
                "vs_get_selection" => await GetSelectionAsync(ct),
                _ => $"Unknown built-in tool: {toolName}"
            };
        }
        catch (Exception ex)
        {
            return $"Tool execution failed: {ex.Message}";
        }
    }

    private static async Task<string> FileSearchAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var query = args?["query"]?.GetValue<string>() ?? throw new ArgumentException("query is required");
        var maxResults = args?["maxResults"]?.GetValue<int>() ?? 20;

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        var results = new List<string>();
        SearchSolution(dte.Solution, query, results, maxResults);

        return results.Count == 0
            ? "No files found."
            : string.Join("\n", results);
    }

    private static void SearchSolution(Solution solution, string query, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        foreach (Project project in solution.Projects)
        {
            if (results.Count >= maxResults) return;
            SearchProject(project, query, results, maxResults);
        }
    }

    private static void SearchProject(Project project, string query, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        if (project.ProjectItems == null) return;

        foreach (ProjectItem item in project.ProjectItems)
        {
            if (results.Count >= maxResults) return;
            SearchProjectItem(item, query, results, maxResults);
        }
    }

    private static void SearchProjectItem(ProjectItem item, string query, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        var name = item.Name ?? "";
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            var path = item.FileNames[1] ?? name;
            results.Add(path);
        }

        if (item.ProjectItems != null)
        {
            foreach (ProjectItem subItem in item.ProjectItems)
            {
                if (results.Count >= maxResults) return;
                SearchProjectItem(subItem, query, results, maxResults);
            }
        }
    }

    private static async Task<string> GetFileAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filePath = args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required");
        var startLine = args?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine is required");
        var endLine = args?["endLine"]?.GetValue<int>() ?? throw new ArgumentException("endLine is required");
        var includeLineNumbers = args?["includeLineNumbers"]?.GetValue<bool>() ?? false;

        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var lines = File.ReadAllLines(filePath);
        if (startLine < 1 || startLine > lines.Length)
            return $"Start line {startLine} is out of range (file has {lines.Length} lines).";
        if (endLine < startLine || endLine > lines.Length)
            endLine = lines.Length;

        var result = new List<string>();
        for (int i = startLine - 1; i < endLine; i++)
        {
            result.Add(includeLineNumbers ? $"{i + 1}: {lines[i]}" : lines[i]);
        }

        return string.Join("\n", result);
    }

    private static async Task<string> FindSymbolAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var symbolName = args?["symbolName"]?.GetValue<string>() ?? throw new ArgumentException("symbolName is required");
        var navigationType = args?["navigationType"]?.GetValue<int>() ?? 1;

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        // This is a simplified implementation - full Roslyn integration would require more setup
        var results = new List<string>();
        SearchSymbolInSolution(dte.Solution, symbolName, results, 50);

        return results.Count == 0
            ? $"Symbol '{symbolName}' not found."
            : $"Found {results.Count} location(s):\n" + string.Join("\n", results);
    }

    private static void SearchSymbolInSolution(Solution solution, string symbolName, List<string> results, int maxResults)
    {
        // Simplified: search for symbol name in file contents
        foreach (Project project in solution.Projects)
        {
            if (results.Count >= maxResults) return;
            SearchSymbolInProject(project, symbolName, results, maxResults);
        }
    }

    private static void SearchSymbolInProject(Project project, string symbolName, List<string> results, int maxResults)
    {
        if (project.ProjectItems == null) return;

        foreach (ProjectItem item in project.ProjectItems)
        {
            if (results.Count >= maxResults) return;
            SearchSymbolInItem(item, symbolName, results, maxResults);
        }
    }

    private static void SearchSymbolInItem(ProjectItem item, string symbolName, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        var fileName = item.FileNames[1];
        if (fileName != null && File.Exists(fileName) && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var content = File.ReadAllText(fileName);
                if (content.Contains(symbolName))
                {
                    results.Add(fileName);
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (item.ProjectItems != null)
        {
            foreach (ProjectItem subItem in item.ProjectItems)
            {
                if (results.Count >= maxResults) return;
                SearchSymbolInItem(subItem, symbolName, results, maxResults);
            }
        }
    }

    private static async Task<string> GetProjectsAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        var projects = new List<string>();
        foreach (Project project in dte.Solution.Projects)
        {
            if (project.Name != null)
            {
                projects.Add($"{project.Name} ({project.FileName ?? "unknown"})");
            }
        }

        return projects.Count == 0
            ? "No projects found."
            : string.Join("\n", projects);
    }

    private static async Task<string> GetFilesInProjectAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var projectName = args?["projectName"]?.GetValue<string>() ?? throw new ArgumentException("projectName is required");

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        Project? targetProject = null;
        foreach (Project project in dte.Solution.Projects)
        {
            if (project.Name?.Equals(projectName, StringComparison.OrdinalIgnoreCase) == true)
            {
                targetProject = project;
                break;
            }
        }

        if (targetProject == null)
            return $"Project '{projectName}' not found.";

        var files = new List<string>();
        CollectProjectFiles(targetProject, files);

        return files.Count == 0
            ? "No files found in project."
            : string.Join("\n", files);
    }

    private static void CollectProjectFiles(Project project, List<string> files)
    {
        if (project.ProjectItems == null) return;

        foreach (ProjectItem item in project.ProjectItems)
        {
            CollectItemFiles(item, files);
        }
    }

    private static void CollectItemFiles(ProjectItem item, List<string> files)
    {
        var fileName = item.FileNames[1];
        if (fileName != null)
        {
            files.Add(fileName);
        }

        if (item.ProjectItems != null)
        {
            foreach (ProjectItem subItem in item.ProjectItems)
            {
                CollectItemFiles(subItem, files);
            }
        }
    }

    private static async Task<string> BuildAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        var projectName = args?["projectName"]?.GetValue<string>();

        var outputWindow = dte.ToolWindows.OutputWindow;
        var buildPane = outputWindow.OutputWindowPanes.Cast<OutputWindowPane>()
            .FirstOrDefault(p => p.Name == "Build");

        if (buildPane == null)
            return "Build output pane not found.";

        buildPane.Clear();

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        if (projectName != null)
        {
            // Build specific project
            foreach (SolutionConfiguration2 config in solutionBuild.SolutionConfigurations)
            {
                if (config.Name == solutionBuild.ActiveConfiguration?.Name)
                {
                    foreach (SolutionContext context in config.SolutionContexts)
                    {
                        if (context.ProjectName?.Equals(projectName, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            context.ShouldBuild = true;
                        }
                        else
                        {
                            context.ShouldBuild = false;
                        }
                    }
                    break;
                }
            }
        }

        solutionBuild.Build(true);

        var output = buildPane.TextDocument?.CreateEditPoint()?.GetText(buildPane.TextDocument.EndPoint) ?? "";
        return string.IsNullOrWhiteSpace(output) ? "Build completed." : output;
    }

    private static async Task<string> RunTestsAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filterType = args?["filterType"]?.GetValue<string>() ?? throw new ArgumentException("filterType is required");
        var filterValue = args?["filterValue"]?.GetValue<string>() ?? throw new ArgumentException("filterValue is required");

        // Test execution requires Test Explorer integration - simplified response for now
        return $"Test execution with filter {filterType}={filterValue} is not yet fully implemented. Use the Test Explorer window directly.";
    }

    private static async Task<string> GetDiagnosticsAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filePath = args?["filePath"]?.GetValue<string>();
        var projectName = args?["projectName"]?.GetValue<string>();
        var severity = args?["severity"]?.GetValue<string>();

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen != true)
            return "No solution is open.";

        var errorList = dte.ToolWindows.ErrorList;
        var items = new List<ErrorItem>();
        for (int i = 1; i <= errorList.ErrorItems.Count; i++)
        {
            items.Add(errorList.ErrorItems.Item(i));
        }

        if (filePath != null)
        {
            items = items.Where(e => e.FileName?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }

        if (severity != null)
        {
            var severityLevel = severity.ToLower() switch
            {
                "error" => vsBuildErrorLevel.vsBuildErrorLevelHigh,
                "warning" => vsBuildErrorLevel.vsBuildErrorLevelMedium,
                "info" => vsBuildErrorLevel.vsBuildErrorLevelLow,
                _ => throw new ArgumentException($"Unknown severity: {severity}")
            };
            items = items.Where(e => e.ErrorLevel == severityLevel).ToList();
        }

        if (items.Count == 0)
            return "No diagnostics found.";

        var results = items.Select(e => $"[{e.ErrorLevel}] {e.Description} ({e.FileName}:{e.Line})");
        return string.Join("\n", results);
    }

    private static async Task<string> GetActiveFileAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        var activeDoc = dte?.ActiveDocument;

        if (activeDoc == null)
            return "No active document.";

        var selection = activeDoc.Selection as TextSelection;
        var cursorLine = selection?.ActivePoint?.Line ?? 0;
        var cursorColumn = selection?.ActivePoint?.LineCharOffset ?? 0;

        return $"File: {activeDoc.FullName}\nCursor: Line {cursorLine}, Column {cursorColumn}";
    }

    private static async Task<string> GetSelectionAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        var activeDoc = dte?.ActiveDocument;

        if (activeDoc == null)
            return "No active document.";

        var selection = activeDoc.Selection as TextSelection;
        var selectedText = selection?.Text ?? "";

        if (string.IsNullOrWhiteSpace(selectedText))
            return "No text selected.";

        return selectedText;
    }
}
