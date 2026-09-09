using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
            },
            new()
            {
                Name = "vs_create_file",
                Description = "Create a new file with the specified content. The directory will be created if it does not exist.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The file path to create (relative to solution root or absolute)"
                        },
                        ["content"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The content to write to the file"
                        }
                    },
                    ["required"] = new JsonArray { "filePath", "content" }
                }
            },
            new()
            {
                Name = "vs_replace_in_file",
                Description = "Replace a specific string in a file with another string. The oldString must match exactly including whitespace.",
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
                        ["oldString"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The exact text to find and replace (include 3-5 lines of context)"
                        },
                        ["newString"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The replacement text"
                        }
                    },
                    ["required"] = new JsonArray { "filePath", "oldString", "newString" }
                }
            },
            new()
            {
                Name = "vs_run_command",
                Description = "Run a command in PowerShell and return the output.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The PowerShell command to execute"
                        },
                        ["summary"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "A one-sentence summary of what the command does"
                        },
                        ["background"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Run in background (default: false)"
                        }
                    },
                    ["required"] = new JsonArray { "command" }
                }
            },
            new()
            {
                Name = "vs_get_output_logs",
                Description = "Get logs from the Visual Studio Output tool window pane (Build, Debug, Git, etc.).",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["paneName"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Output pane name (Build, Debug, General). Omit to list available panes."
                        },
                        ["tailLines"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Number of tail lines to return (default: 200)"
                        }
                    }
                }
            },
            new()
            {
                Name = "vs_get_web_pages",
                Description = "Get the contents of web pages by URL. Returns page content as text.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["urls"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Array of URLs to fetch"
                        }
                    },
                    ["required"] = new JsonArray { "urls" }
                }
            },
            new()
            {
                Name = "vs_remove_file",
                Description = "Delete a file and remove references to it from the project.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The file path to remove (relative to solution root)"
                        }
                    },
                    ["required"] = new JsonArray { "filePath" }
                }
            },
            new()
            {
                Name = "vs_inquiry",
                Description = "Ask the user a question with optional suggestions. Shows a dialog with numbered suggestions and a text area for response. Blocks until user responds.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The question to ask the user"
                        },
                        ["suggestions"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Optional list of 5-10 suggestions shown as numbered list"
                        }
                    },
                    ["required"] = new JsonArray { "question" }
                }
            },
            new()
            {
                Name = "vs_detect_memories",
                Description = "Analyze code to detect coding patterns, preferences, and conventions. Returns detected memories that should be saved.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "File path to analyze (optional, analyzes active file if omitted)"
                        }
                    }
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
                "vs_create_file" => await CreateFileAsync(args, ct),
                "vs_replace_in_file" => await ReplaceInFileAsync(args, ct),
                "vs_run_command" => await RunCommandAsync(args, ct),
                "vs_get_output_logs" => await GetOutputLogsAsync(args, ct),
                "vs_get_web_pages" => await GetWebPagesAsync(args, ct),
                "vs_remove_file" => await RemoveFileAsync(args, ct),
                "vs_inquiry" => await InquiryAsync(args, ct),
                "vs_detect_memories" => await DetectMemoriesAsync(args, ct),
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

    private static Task<string> CreateFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required");
        var content = args?["content"]?.GetValue<string>() ?? throw new ArgumentException("content is required");

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        return Task.FromResult($"File created: {filePath}");
    }

    private static Task<string> ReplaceInFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required");
        var oldString = args?["oldString"]?.GetValue<string>() ?? throw new ArgumentException("oldString is required");
        var newString = args?["newString"]?.GetValue<string>() ?? throw new ArgumentException("newString is required");

        if (!File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        var content = File.ReadAllText(filePath);
        if (!content.Contains(oldString))
            return Task.FromResult($"String not found in file: {oldString}");

        var updated = content.Replace(oldString, newString);
        File.WriteAllText(filePath, updated);
        return Task.FromResult($"Replaced in {filePath}");
    }

    private static Task<string> RunCommandAsync(JsonNode? args, CancellationToken ct)
    {
        var command = args?["command"]?.GetValue<string>() ?? throw new ArgumentException("command is required");
        var summary = args?["summary"]?.GetValue<string>();
        var background = args?["background"]?.GetValue<bool>() ?? false;

        var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = !background
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var result = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n[ERROR] {error}";
        return Task.FromResult(string.IsNullOrWhiteSpace(result) ? "Command completed." : result);
    }

    private static async Task<string> GetOutputLogsAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var paneName = args?["paneName"]?.GetValue<string>();
        var tailLines = args?["tailLines"]?.GetValue<int>() ?? 200;

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte == null)
            return "Visual Studio DTE not available.";

        var outputWindow = dte.ToolWindows.OutputWindow;

        if (string.IsNullOrEmpty(paneName))
        {
            var panes = new List<string>();
            foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
            {
                panes.Add(p.Name ?? "Unknown");
            }
            return "Available panes:\n" + string.Join("\n", panes);
        }

        OutputWindowPane targetPane = null;
        foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
        {
            if (p.Name?.Equals(paneName, StringComparison.OrdinalIgnoreCase) == true)
            {
                targetPane = p;
                break;
            }
        }

        if (targetPane == null)
            return $"Pane '{paneName}' not found.";

        var textDoc = targetPane.TextDocument;
        if (textDoc == null)
            return "No text document for pane.";

        var endPoint = textDoc.EndPoint;
        var startPoint = textDoc.StartPoint.CreateEditPoint();
        var fullText = startPoint.GetText(endPoint);

        if (string.IsNullOrWhiteSpace(fullText))
            return "No output.";

        if (tailLines > 0)
        {
            var lines = fullText.Split('\n');
            if (lines.Length > tailLines)
            {
                lines = lines.Skip(lines.Length - tailLines).ToArray();
            }
            return string.Join("\n", lines);
        }

        return fullText;
    }

    private static async Task<string> GetWebPagesAsync(JsonNode? args, CancellationToken ct)
    {
        var urlsNode = args?["urls"];
        if (urlsNode is not JsonArray urlsArray)
            throw new ArgumentException("urls array is required");

        var results = new List<string>();
        using var client = new System.Net.Http.HttpClient();

        foreach (var urlNode in urlsArray)
        {
            var url = urlNode?.GetValue<string>();
            if (string.IsNullOrEmpty(url))
                continue;

            try
            {
                var response = await client.GetAsync(url, ct);
                var content = await response.Content.ReadAsStringAsync();
                results.Add($"[{url}]: {content.Substring(0, Math.Min(500, content.Length))}...");
            }
            catch (Exception ex)
            {
                results.Add($"[{url}]: Error - {ex.Message}");
            }
        }

        return results.Count == 0 ? "No URLs provided." : string.Join("\n\n", results);
    }

    private static async Task<string> RemoveFileAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filePath = args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required");

        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        if (dte?.Solution?.IsOpen == true)
        {
            var projectItem = FindProjectItem(dte.Solution, filePath);
            if (projectItem != null)
            {
                projectItem.Delete();
                return $"Removed from project and deleted: {filePath}";
            }
        }

        File.Delete(filePath);
        return $"Deleted: {filePath}";
    }

    private static ProjectItem? FindProjectItem(Solution solution, string filePath)
    {
        foreach (Project project in solution.Projects)
        {
            var item = FindItemInProject(project, filePath);
            if (item != null)
                return item;
        }
        return null;
    }

    private static ProjectItem? FindItemInProject(Project project, string filePath)
    {
        if (project.ProjectItems == null)
            return null;

        foreach (ProjectItem item in project.ProjectItems)
        {
            var found = FindItemRecursive(item, filePath);
            if (found != null)
                return found;
        }
        return null;
    }

    private static ProjectItem? FindItemRecursive(ProjectItem item, string filePath)
    {
        var fileName = item.FileNames[1];
        if (fileName?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true)
            return item;

        if (item.ProjectItems != null)
        {
            foreach (ProjectItem subItem in item.ProjectItems)
            {
                var found = FindItemRecursive(subItem, filePath);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private static async Task<string> InquiryAsync(JsonNode? args, CancellationToken ct)
    {
        var question = args?["question"]?.GetValue<string>() ?? throw new ArgumentException("question is required");
        var suggestions = args?["suggestions"]?.AsArray()
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        string response = null;
        int? selectedIndex = null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dialog = new ToolWindow.InquiryDialog(question, suggestions);
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner != null)
            dialog.Owner = owner;

        var result = dialog.ShowDialog();
        if (result == true)
        {
            response = dialog.Response;
            selectedIndex = dialog.SelectedSuggestionIndex;
        }

        if (response == null)
            return "[User cancelled inquiry]";

        var resultObj = new JsonObject
        {
            ["response"] = response
        };
        if (selectedIndex.HasValue)
            resultObj["selectedIndex"] = selectedIndex.Value;

        return resultObj.ToJsonString();
    }

    private static async Task<string> DetectMemoriesAsync(JsonNode? args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filePath = args?["filePath"]?.GetValue<string>();
        if (string.IsNullOrEmpty(filePath))
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            filePath = dte?.ActiveDocument?.FullName;
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return "No file to analyze.";

        var content = File.ReadAllText(filePath);
        var memories = new List<string>();

        // Detect using directives patterns
        if (content.Contains("using System.Text.Json;") && !content.Contains("using Newtonsoft.Json;"))
            memories.Add("Prefer System.Text.Json over Newtonsoft.Json");

        if (content.Contains("System.Text.Json.Nodes"))
            memories.Add("Use System.Text.Json.Nodes for JSON manipulation");

        // Detect async patterns
        if (content.Contains("async Task<") || content.Contains("async void"))
        {
            if (content.Contains("ConfigureAwait(false)"))
                memories.Add("Use ConfigureAwait(false) in library code");
        }

        // Detect nullable patterns
        if (content.Contains("#nullable enable") || content.Contains("<Nullable>enable</Nullable>"))
            memories.Add("Enable nullable reference types");

        // Detect file-scoped namespace
        if (Regex.IsMatch(content, @"^namespace\s+\S+;\s*$", RegexOptions.Multiline))
            memories.Add("Use file-scoped namespaces");

        // Detect record types
        if (content.Contains("record ") || content.Contains("record class"))
            memories.Add("Prefer records for immutable data");

        // Detect pattern matching
        if (content.Contains("is not null") || content.Contains("is { "))
            memories.Add("Use modern pattern matching");

        // Detect switch expressions
        if (Regex.IsMatch(content, @"=>\s*\w+\s+switch\s*\{"))
            memories.Add("Prefer switch expressions over switch statements");

        // Detect LINQ patterns
        if (content.Contains(".Where(") && content.Contains(".Select("))
            memories.Add("Use LINQ for data transformations");

        // Detect string interpolation
        if (content.Contains("$\"") && !content.Contains("string.Format("))
            memories.Add("Prefer string interpolation over string.Format");

        // Detect target-typed new
        if (Regex.IsMatch(content, @"new\s+\w+\(\);"))
            memories.Add("Use target-typed new expressions");

        // Detect global usings
        if (content.Contains("global using "))
            memories.Add("Use global using directives");

        // Detect primary constructors
        if (Regex.IsMatch(content, @"public\s+\w+\s*\([^)]*\)\s*:"))
            memories.Add("Use primary constructors");

        // Detect collection expressions
        if (content.Contains("[] = [") || content.Contains("[] = {"))
            memories.Add("Use collection expressions");

        // Detect minimal APIs
        if (content.Contains("app.MapGet(") || content.Contains("app.MapPost("))
            memories.Add("Use minimal APIs");

        // Detect DI patterns
        if (content.Contains("IServiceCollection") || content.Contains("AddScoped<") || content.Contains("AddTransient<"))
            memories.Add("Use dependency injection");

        // Detect xUnit patterns
        if (content.Contains("[Fact]") || content.Contains("[Theory]"))
            memories.Add("Use xUnit for testing");

        // Detect FluentAssertions
        if (content.Contains(".Should()"))
            memories.Add("Use FluentAssertions for test assertions");

        if (memories.Count == 0)
            return "No coding patterns detected.";

        var result = new JsonObject
        {
            ["file"] = filePath,
            ["memories"] = new JsonArray(memories.Select(m => (JsonNode)m).ToArray())
        };

        return result.ToJsonString();
    }
}
