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

// Every method in this file switches to the UI thread before touching DTE/COM objects,
// but the static analyzer cannot prove thread affinity through indirect call chains.
#pragma warning disable VSTHRD010

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
                Description = "Run tests matching the specified filter. Filters are applied for real via vstest.console (results do not appear in Test Explorer). If filtering is unavailable, the FULL suite runs via Test Explorer and the response says so explicitly.",
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
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Foreground timeout in seconds before the process is killed (default: 120, range 5-3600)"
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
            },
            new()
            {
                Name = "vs_list_instructions",
                Description = "List the configured instruction sources (name, kind text|file, path, enabled, order) that are injected into the model prompt.",
                Schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            },
            new()
            {
                Name = "vs_read_instruction",
                Description = "Read the full content of one instruction source by its name.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The instruction name as shown by vs_list_instructions" }
                    },
                    ["required"] = new JsonArray { "name" }
                }
            },
            new()
            {
                Name = "vs_update_instruction",
                Description = "Update (or create) an instruction source by name. For a text source this sets its inline content; for a file source it writes the file. Use to record user preferences.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray { "name", "content" }
                }
            },
            new()
            {
                Name = "vs_create_plan_file",
                Description = "Create a markdown (.md) plan file. Only .md paths are accepted — use this to write implementation plans for user review. The directory will be created if it does not exist.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The .md file path to create (relative to solution root or absolute)"
                        },
                        ["content"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The markdown content to write"
                        }
                    },
                    ["required"] = new JsonArray { "filePath", "content" }
                }
            },
            new()
            {
                Name = "vs_get_errors",
                Description = "Get compilation errors in specific code files. Returns errors with file, line, column, and description.",
                Schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["filePath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The file path to get errors for"
                        }
                    },
                    ["required"] = new JsonArray { "filePath" }
                }
            }
        };
    }

    /// <summary>
    /// Switches to the UI thread and returns the DTE automation object, or null when the
    /// service is unavailable. Callers that need an open solution use <see cref="GetOpenDteAsync"/>.
    /// </summary>
    private static async Task<DTE2?> GetDteAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        return Package.GetGlobalService(typeof(DTE)) as DTE2;
    }

    /// <summary>
    /// Switches to the UI thread and returns the DTE automation object only when a solution
    /// is open; null otherwise. Replaces the repeated
    /// <c>SwitchToMainThreadAsync + GetGlobalService + IsOpen</c> guard in every tool method.
    /// </summary>
    private static async Task<DTE2?> GetOpenDteAsync(CancellationToken ct)
    {
        var dte = await GetDteAsync(ct);
        return dte?.Solution?.IsOpen == true ? dte : null;
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
                "vs_get_errors" => await GetErrorsAsync(args, ct),
                "vs_get_active_file" => await GetActiveFileAsync(ct),
                "vs_get_selection" => await GetSelectionAsync(ct),
                "vs_create_file" => await CreateFileAsync(args, ct),
                "vs_create_plan_file" => await CreatePlanFileAsync(args, ct),
                "vs_replace_in_file" => await ReplaceInFileAsync(args, ct),
                "vs_run_command" => await RunCommandAsync(args, ct),
                "vs_get_output_logs" => await GetOutputLogsAsync(args, ct),
                "vs_get_web_pages" => await GetWebPagesAsync(args, ct),
                "vs_remove_file" => await RemoveFileAsync(args, ct),
                "vs_inquiry" => await InquiryAsync(args, ct),
                "vs_detect_memories" => await DetectMemoriesAsync(args, ct),
                "vs_list_instructions" => await ListInstructionsAsync(ct),
                "vs_read_instruction" => await ReadInstructionAsync(args, ct),
                "vs_update_instruction" => await UpdateInstructionAsync(args, ct),
                _ => $"Unknown built-in tool: {toolName}"
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate so the agent loop stops; never hand it back as a result.
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Error($"Built-in tool '{toolName}' failed.", ex);
            return $"Tool execution failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs a file read/write with a short retry/backoff on <see cref="IOException"/> (a second
    /// devenv instance, an editor lock, or an AV scan can hold the file briefly). Each retry is
    /// logged as a warning; after the budget the exception propagates to <see cref="ExecuteAsync"/>,
    /// which logs it and returns a graceful error rather than crashing (project sharing rule).
    /// </summary>
    private static T FileIoWithRetry<T>(string path, Func<T> op)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return op();
            }
            catch (IOException ex) when (attempt < 2)
            {
                DebugLog.Warn($"File '{path}' is busy (attempt {attempt + 1}); retrying: {ex.Message}");
                System.Threading.Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    /// <summary>Void-returning overload of <see cref="FileIoWithRetry{T}"/> for writes/deletes.</summary>
    private static void FileIoWithRetry(string path, Action op)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                op();
                return;
            }
            catch (IOException ex) when (attempt < 2)
            {
                DebugLog.Warn($"File '{path}' is busy (attempt {attempt + 1}); retrying: {ex.Message}");
                System.Threading.Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static async Task<string> FileSearchAsync(JsonNode? args, CancellationToken ct)
    {
        var query = args?["query"]?.GetValue<string>() ?? throw new ArgumentException("query is required");
        var maxResults = args?["maxResults"]?.GetValue<int>() ?? 20;

        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
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

    /// <summary>
    /// First file path of a project item, or null. FileNames is 1-based, so a single-file
    /// item has FileCount == 1 and its path at FileNames[1]; some item kinds throw on
    /// FileNames access even when they report a file count.
    /// </summary>
    private static string? GetItemFilePath(ProjectItem item)
    {
        try
        {
            return item.FileCount >= 1 ? item.FileNames[1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a tool-supplied path against the solution directory. Relative paths must not
    /// depend on devenv.exe's working directory (usually the VS install folder, not the repo).
    /// </summary>
    private static string ResolveToolPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || Path.IsPathRooted(filePath))
            return filePath;
        var root = InstructionFileLoader.GetSolutionRootPath();
        return string.IsNullOrEmpty(root)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(root!, filePath));
    }

    private static void SearchProjectItem(ProjectItem item, string query, List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;

        var name = item.Name ?? "";
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            var path = GetItemFilePath(item) ?? name;
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

        var filePath = ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var startLine = args?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine is required");
        var endLine = args?["endLine"]?.GetValue<int>() ?? throw new ArgumentException("endLine is required");
        var includeLineNumbers = args?["includeLineNumbers"]?.GetValue<bool>() ?? false;

        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var lines = FileIoWithRetry(filePath, () => File.ReadAllLines(filePath));
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
        var symbolName = args?["symbolName"]?.GetValue<string>() ?? throw new ArgumentException("symbolName is required");
        var navigationType = args?["navigationType"]?.GetValue<int>() ?? 2; // default: Find All References
        var filePath = args?["filePath"]?.GetValue<string>(); // optional: scope to one file
        if (!string.IsNullOrEmpty(filePath))
            filePath = ResolveToolPath(filePath!);

        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
            return "No solution is open.";

        // navigationType: 1 = Go To Definition, 2 = Find All References, 3 = Go To Implementation
        switch (navigationType)
        {
            case 1:
                return await FindSymbolDefinitionAsync(dte, symbolName, filePath, ct);
            case 2:
                return await FindSymbolReferencesAsync(dte, symbolName, filePath, ct);
            case 3:
                return await FindSymbolImplementationsAsync(dte, symbolName, filePath, ct);
            default:
                return $"Unknown navigation type: {navigationType}. Use 1=Go To Definition, 2=Find All References, 3=Go To Implementation.";
        }
    }

    /// <summary>Uses DTE's Find to iterate all references, collecting file/line/column from TextSelection.</summary>
    private static async Task<string> FindSymbolReferencesAsync(DTE2 dte, string symbolName, string? filePath, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Remember where the user was so we can restore the caret.
        var savedDoc = dte.ActiveDocument;
        var savedSel = savedDoc?.Selection as TextSelection;
        var savedLine = savedSel?.ActivePoint?.AbsoluteCharOffset ?? 0;

        var find = dte.Find;

        // Save current find state
        var savedFindWhat = find.FindWhat;
        var savedAction = find.Action;
        var savedTarget = find.Target;
        var savedMatchCase = find.MatchCase;
        var savedMatchWholeWord = find.MatchWholeWord;
        var savedPatternSyntax = find.PatternSyntax;
        var savedSearchSubfolders = find.SearchSubfolders;
        var savedFilesOfType = find.FilesOfType;

        try
        {
            find.FindWhat = symbolName;
            find.Action = vsFindAction.vsFindActionFindAll;
            find.Target = string.IsNullOrWhiteSpace(filePath)
                ? vsFindTarget.vsFindTargetSolution
                : vsFindTarget.vsFindTargetCurrentDocument;
            find.MatchCase = true;
            find.MatchWholeWord = true;
            find.PatternSyntax = vsFindPatternSyntax.vsFindPatternSyntaxLiteral;
            find.SearchSubfolders = true;
            find.FilesOfType = "*.cs;*.vb;*.xaml;*.json;*.xml;*.csproj;*.props;*.targets;*.razor;*.cshtml";

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                dte.ItemOperations.OpenFile(filePath);
            }

            var result = find.Execute();

            if (result != vsFindResult.vsFindResultFound)
                return $"Symbol '{symbolName}' not found.";

            // Walk through matches using Find Next to collect file/line/col.
            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int maxResults = 50;

            find.Action = vsFindAction.vsFindActionFind;
            // Find Next wraps around to the first match after the last one, so a solution
            // with fewer than maxResults unique matches would otherwise loop forever. Stop
            // when a match repeats, and bound the total iterations as a hard safety net.
            const int maxIterations = 500;
            var iterations = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                if (++iterations > maxIterations)
                    break;

                var activeDoc = dte.ActiveDocument;
                var sel = activeDoc?.Selection as TextSelection;
                if (sel is null)
                    break;

                var f = activeDoc?.FullName ?? "(unknown)";
                var l = sel.ActivePoint.Line;
                var c = sel.ActivePoint.LineCharOffset;
                var key = f + "(" + l + ")";
                if (!seen.Add(key))
                    break; // wrapped around to an already-collected match

                // Capture one line of context from the match line
                var matchLine = sel.ActivePoint.CreateEditPoint().GetLines(l, l + 1).TrimEnd();
                lines.Add($"{f}({l},{c}): {matchLine}");
                if (lines.Count >= maxResults)
                    break;
            }
            while (find.Execute() == vsFindResult.vsFindResultFound);

            if (lines.Count == 0)
                return $"Symbol '{symbolName}' not found.";

            return $"Found {lines.Count} reference(s):\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Symbol search failed: {ex.Message}";
        }
        finally
        {
            // Restore find state and caret
            try
            {
                find.FindWhat = savedFindWhat;
                find.Action = savedAction;
                find.Target = savedTarget;
                find.MatchCase = savedMatchCase;
                find.MatchWholeWord = savedMatchWholeWord;
                find.PatternSyntax = savedPatternSyntax;
                find.SearchSubfolders = savedSearchSubfolders;
                find.FilesOfType = savedFilesOfType;
                if (savedDoc is not null && savedLine > 0)
                {
                    try { savedDoc.Activate(); } catch { }
                    try { (savedDoc.Selection as TextSelection)?.MoveToAbsoluteOffset(savedLine); } catch { }
                }
            }
            catch { /* best-effort restore */ }
        }
    }

    /// <summary>For Go To Definition, do a Find All and return the first file location — the model can inspect the file.</summary>
    private static async Task<string> FindSymbolDefinitionAsync(DTE2 dte, string symbolName, string? filePath, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            // L7: save/restore the shared Find state (mirrors FindSymbolReferencesAsync) so a
            // definition lookup does not leave the user's Find dialog mutated. The caret is left
            // on the definition — that is the point of go-to-definition.
            var find = dte.Find;
            var savedFindWhat = find.FindWhat;
            var savedAction = find.Action;
            var savedTarget = find.Target;
            var savedMatchCase = find.MatchCase;
            var savedMatchWholeWord = find.MatchWholeWord;
            var savedPatternSyntax = find.PatternSyntax;

            try
            {
                dte.ItemOperations.OpenFile(filePath);
                find.Target = vsFindTarget.vsFindTargetCurrentDocument;
                find.FindWhat = symbolName;
                find.MatchCase = true;
                find.MatchWholeWord = true;
                find.PatternSyntax = vsFindPatternSyntax.vsFindPatternSyntaxLiteral;
                find.Action = vsFindAction.vsFindActionFind;
                if (find.Execute() == vsFindResult.vsFindResultFound)
                {
                    var sel = dte.ActiveDocument?.Selection as TextSelection;
                    if (sel is not null)
                        return $"Definition: {filePath}, Line {sel.ActivePoint.Line}, Column {sel.ActivePoint.LineCharOffset}";
                }
                return $"Symbol '{symbolName}' not found in {filePath}.";
            }
            finally
            {
                try
                {
                    find.FindWhat = savedFindWhat;
                    find.Action = savedAction;
                    find.Target = savedTarget;
                    find.MatchCase = savedMatchCase;
                    find.MatchWholeWord = savedMatchWholeWord;
                    find.PatternSyntax = savedPatternSyntax;
                }
                catch { /* best-effort restore */ }
            }
        }

        // Without a specific file, search for the symbol using Find All and show the first hits.
        return await FindSymbolReferencesAsync(dte, symbolName, null, ct);
    }

    /// <summary>Find implementations uses the same Find All approach; true implementation detection needs Roslyn.</summary>
    private static async Task<string> FindSymbolImplementationsAsync(DTE2 dte, string symbolName, string? filePath, CancellationToken ct)
    {
        return await FindSymbolReferencesAsync(dte, symbolName, filePath, ct);
    }

    private static async Task<string> GetProjectsAsync(CancellationToken ct)
    {
        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
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
        var projectName = args?["projectName"]?.GetValue<string>() ?? throw new ArgumentException("projectName is required");

        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
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
        var fileName = GetItemFilePath(item);
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
        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
            return "No solution is open.";

        var projectName = args?["projectName"]?.GetValue<string>();

        var outputWindow = dte.ToolWindows.OutputWindow;
        var buildPane = outputWindow.OutputWindowPanes.Cast<OutputWindowPane>()
            .FirstOrDefault(p => p.Name == "Build");

        if (buildPane == null)
            return "Build output pane not found.";

        buildPane.Clear();

        SolutionBuild solutionBuild = dte.Solution.SolutionBuild;
        List<(SolutionContext Context, bool ShouldBuild)>? savedContexts = null;
        if (projectName != null)
        {
            // Build a specific project by toggling ShouldBuild. Snapshot the previous flags
            // so the user's Configuration Manager choices are restored afterwards.
            foreach (SolutionConfiguration2 config in solutionBuild.SolutionConfigurations)
            {
                if (config.Name == solutionBuild.ActiveConfiguration?.Name)
                {
                    savedContexts = new List<(SolutionContext, bool)>();
                    foreach (SolutionContext context in config.SolutionContexts)
                    {
                        savedContexts.Add((context, context.ShouldBuild));
                        context.ShouldBuild = context.ProjectName?.Equals(projectName, StringComparison.OrdinalIgnoreCase) == true;
                    }
                    break;
                }
            }
        }

        try
        {
            // Non-blocking build: Build(true) would block the UI thread until completion.
            solutionBuild.Build(false);

            // Poll the build state, waiting off the UI thread and hopping back only to read DTE.
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (true)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                if (solutionBuild.BuildState != vsBuildState.vsBuildStateInProgress)
                    break;
                if (DateTime.UtcNow > deadline)
                    return "Build timed out after 10 minutes; it may still be running in Visual Studio.";
            }

            var output = buildPane.TextDocument?.CreateEditPoint()?.GetText(buildPane.TextDocument.EndPoint) ?? "";
            return string.IsNullOrWhiteSpace(output) ? "Build completed." : output;
        }
        finally
        {
            if (savedContexts is not null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                foreach (var (context, shouldBuild) in savedContexts)
                {
                    try { context.ShouldBuild = shouldBuild; }
                    catch (Exception ex) { DebugLog.Warn($"Could not restore ShouldBuild for {context.ProjectName}: {ex.Message}"); }
                }
            }
        }
    }

    private static async Task<string> RunTestsAsync(JsonNode? args, CancellationToken ct)
    {
        var filterType = args?["filterType"]?.GetValue<string>() ?? "Project";
        var filterValue = args?["filterValue"]?.GetValue<string>() ?? string.Empty;

        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
            return "No solution is open.";

        // M11: honor the filter for real via vstest.console when possible. When filtering is
        // unavailable, fall back to Test Explorer's full-suite run and say so explicitly -
        // a requested filter is never silently ignored.
        string? fallbackReason = null;
        if (!string.IsNullOrWhiteSpace(filterValue))
        {
            var filtered = await VsTestConsoleRunner.TryRunAsync(dte, filterType, filterValue, ct);
            if (filtered.Result is not null)
                return filtered.Result;
            fallbackReason = filtered.FallbackReason;
            // The runner may have finished off the UI thread; DTE access below needs it back.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        }
        var scopeNote = string.IsNullOrWhiteSpace(filterValue)
            ? string.Empty
            : $" (requested filter: {filterType}={filterValue} was NOT applied - {fallbackReason ?? "unknown reason"}; Test Explorer ran the FULL suite)";

        // Snapshot the Test output pane before running so we can detect the tail.
        var outputWindow = dte.ToolWindows.OutputWindow;
        OutputWindowPane? testPane = null;
        foreach (OutputWindowPane p in outputWindow.OutputWindowPanes)
        {
            if (p.Name?.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                testPane = p;
                break;
            }
        }

        if (testPane is null)
        {
            // No Test pane; try running and report status from the command.
            try
            {
                dte.ExecuteCommand("TestExplorer.RunAllTests");
                return "Tests started. Check the Test Explorer window for results." + scopeNote;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return $"Could not start tests: {ex.Message}";
            }
        }

        // Clear the pane and capture its pre-run length so we can extract the tail after.
        testPane.Clear();
        // Activate the Test pane so output is visible.
        testPane.Activate();

        try
        {
            // Filtering was unavailable (see scopeNote) and DTE has no arbitrary-filter command,
            // so run the full suite and report the substitution explicitly rather than silently.
            dte.ExecuteCommand("TestExplorer.RunAllTests");

            // Poll the test pane until a real run-summary appears and the output stabilizes.
            string? lastText = null;
            int stableCount = 0;
            const int maxWaitMs = 300_000; // 5 minute ceiling
            const int pollIntervalMs = 2000;
            var elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(pollIntervalMs, ct).ConfigureAwait(true); // must stay on UI thread
                elapsed += pollIntervalMs;

                var textDoc = testPane.TextDocument;
                if (textDoc is null)
                    continue;

                var currentText = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint) ?? string.Empty;
                if (currentText == lastText)
                {
                    stableCount++;
                    // Require a summary marker as well as stability so a quiet discovery phase
                    // (>6 s of no output) does not end the wait early with partial results.
                    if (stableCount >= 3 && HasTestRunSummary(currentText))
                        break;
                }
                else
                {
                    stableCount = 0;
                    lastText = currentText;
                }
            }

            var finalText = lastText ?? "No test output captured.";
            var lines = finalText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 10)));

            var summaryLine = lines.LastOrDefault(l =>
                l.Contains("Passed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("No tests found", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Test execution time", StringComparison.OrdinalIgnoreCase));

            // If no summary ever appeared, the run did not finish within the ceiling — label the
            // output as partial rather than claiming completion.
            if (!HasTestRunSummary(finalText))
                return $"Test run did not finish within {maxWaitMs / 1000}s{scopeNote}; partial output:\n{tail}";

            return summaryLine is not null
                ? $"Test run complete{scopeNote}.\n{summaryLine.Trim()}"
                : $"Test run complete{scopeNote}.\n{tail}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Test execution failed: {ex.Message}";
        }
    }

    /// <summary>True once the Test pane shows a run-summary marker (passed/failed counts or timing).</summary>
    private static bool HasTestRunSummary(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Passed:", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Failed:", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Total tests:", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("No tests found", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Test execution time", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static async Task<string> GetDiagnosticsAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = args?["filePath"]?.GetValue<string>();
        var projectName = args?["projectName"]?.GetValue<string>();
        var severity = args?["severity"]?.GetValue<string>();

        var dte = await GetOpenDteAsync(ct);
        if (dte is null)
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
        var dte = await GetDteAsync(ct);
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
        var dte = await GetDteAsync(ct);
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
        var filePath = ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var content = args?["content"]?.GetValue<string>() ?? throw new ArgumentException("content is required");

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileIoWithRetry(filePath, () => File.WriteAllText(filePath, content));
        return Task.FromResult($"File created: {filePath}");
    }

    private static Task<string> CreatePlanFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var content = args?["content"]?.GetValue<string>() ?? throw new ArgumentException("content is required");

        if (!filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("Plan files must have a .md extension.");

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileIoWithRetry(filePath, () => File.WriteAllText(filePath, content));
        return Task.FromResult($"Plan file created: {filePath}");
    }

    private static async Task<string> GetErrorsAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required");

        var dte = await GetDteAsync(ct);
        if (dte is null)
            return "Visual Studio DTE not available.";

        var errorList = dte.ToolWindows.ErrorList;
        var items = new List<string>();
        for (int i = 1; i <= errorList.ErrorItems.Count; i++)
        {
            var e = errorList.ErrorItems.Item(i);
            if (e.FileName?.Equals(filePath, StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if (e.ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelHigh)
                continue;

            items.Add($"{e.FileName}({e.Line},{e.Column}): {e.ErrorLevel}: {e.Description}");
        }

        if (items.Count == 0)
            return "No compilation errors found in this file.";

        return $"Found {items.Count} error(s):\n" + string.Join("\n", items);
    }

    private static Task<string> ReplaceInFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));
        var oldString = args?["oldString"]?.GetValue<string>() ?? throw new ArgumentException("oldString is required");
        var newString = args?["newString"]?.GetValue<string>() ?? throw new ArgumentException("newString is required");

        if (oldString.Length == 0)
            return Task.FromResult("oldString must not be empty.");
        if (!File.Exists(filePath))
            return Task.FromResult($"File not found: {filePath}");

        // Read raw bytes so the original encoding and BOM can be preserved on rewrite.
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not read '{filePath}' for replace: {ex.Message}");
            return Task.FromResult($"Could not read file: {ex.Message}");
        }

        System.Text.Encoding encoding;
        int preamble;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { encoding = new System.Text.UTF8Encoding(true); preamble = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { encoding = new System.Text.UnicodeEncoding(false, true); preamble = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { encoding = new System.Text.UnicodeEncoding(true, true); preamble = 2; }
        else
        { encoding = new System.Text.UTF8Encoding(false); preamble = 0; }

        var content = encoding.GetString(bytes, preamble, bytes.Length - preamble);

        // The contract is a unique single-instance replace; count occurrences before editing.
        var count = 0;
        var scan = 0;
        while ((scan = content.IndexOf(oldString, scan, StringComparison.Ordinal)) >= 0)
        {
            count++;
            scan += oldString.Length;
        }
        if (count == 0)
            return Task.FromResult($"String not found in file: {oldString}");
        if (count > 1)
            return Task.FromResult($"oldString occurs {count} times in {filePath}; add surrounding context so it matches exactly one occurrence.");

        var pos = content.IndexOf(oldString, StringComparison.Ordinal);
        var updated = content.Substring(0, pos) + newString + content.Substring(pos + oldString.Length);

        try
        {
            var body = encoding.GetBytes(updated);
            var bom = encoding.GetPreamble();
            byte[] output;
            if (bom.Length > 0)
            {
                output = new byte[bom.Length + body.Length];
                Buffer.BlockCopy(bom, 0, output, 0, bom.Length);
                Buffer.BlockCopy(body, 0, output, bom.Length, body.Length);
            }
            else
            {
                output = body;
            }
            File.WriteAllBytes(filePath, output);
        }
        catch (IOException ex)
        {
            DebugLog.Warn($"Could not write '{filePath}' for replace: {ex.Message}");
            return Task.FromResult($"Could not write file: {ex.Message}");
        }

        return Task.FromResult($"Replaced in {filePath}");
    }

    private static async Task<string> RunCommandAsync(JsonNode? args, CancellationToken ct)
    {
        var command = args?["command"]?.GetValue<string>() ?? throw new ArgumentException("command is required");
        var background = args?["background"]?.GetValue<bool>() ?? false;
        var timeoutSeconds = args?["timeoutSeconds"]?.GetValue<int>() ?? 120;
        timeoutSeconds = Math.Max(5, Math.Min(3600, timeoutSeconds));

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            // Use Base64-encoded command to prevent PowerShell injection through
            // model-generated content containing metacharacters ($, `, ", etc.).
            Arguments = $"-NoProfile -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))}",
            UseShellExecute = false,
            CreateNoWindow = true,
            // A background process must not redirect its pipes: nobody reads them, so a
            // full buffer would block the child forever. Foreground reads both concurrently.
            RedirectStandardOutput = !background,
            RedirectStandardError = !background
        };

        if (background)
        {
            using var bgProcess = new System.Diagnostics.Process { StartInfo = startInfo };
            bgProcess.Start();
            return $"Command started in background (PID {bgProcess.Id}).";
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        // net48 has no ReadToEndAsync; run both reads on the thread pool CONCURRENTLY so a
        // full stderr buffer cannot deadlock the stdout read (the original sequential
        // ReadToEnd calls hung whenever the child wrote enough to stderr first).
        var outputTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var errorTask = Task.Run(() => process.StandardError.ReadToEnd());
        var exitTask = Task.Run(() => process.WaitForExit());

        var finished = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct)).ConfigureAwait(false);
        if (finished != exitTask)
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* already exited */ }
            // Let the pipe reads drain whatever the child produced before it was killed.
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(2000)).ConfigureAwait(false);
            var partialOutput = outputTask.IsCompleted ? Truncate(await outputTask.ConfigureAwait(false)) : string.Empty;
            var partialError = errorTask.IsCompleted ? Truncate(await errorTask.ConfigureAwait(false)) : string.Empty;
            var reason = ct.IsCancellationRequested ? "cancelled" : $"timed out after {timeoutSeconds}s";
            var partial = string.IsNullOrWhiteSpace(partialError) ? partialOutput : $"{partialOutput}\n[ERROR] {partialError}";
            return $"Command {reason}." + (string.IsNullOrWhiteSpace(partial) ? string.Empty : $"\nPartial output:\n{partial}");
        }

        var output = Truncate(await outputTask.ConfigureAwait(false));
        var error = Truncate(await errorTask.ConfigureAwait(false));
        var result = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n[ERROR] {error}";
        return string.IsNullOrWhiteSpace(result) ? "Command completed." : result;
    }

    private static string Truncate(string value) =>
        value.Length <= 100_000 ? value : value.Substring(0, 100_000) + "\n…[output truncated]";

    private static async Task<string> GetOutputLogsAsync(JsonNode? args, CancellationToken ct)
    {
        var paneName = args?["paneName"]?.GetValue<string>();
        var tailLines = args?["tailLines"]?.GetValue<int>() ?? 200;

        var dte = await GetDteAsync(ct);
        if (dte is null)
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

        OutputWindowPane? targetPane = null;
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

    /// <summary>Shared HttpClient for web fetches: one instance avoids socket exhaustion
    /// from per-call clients. The timeout bounds each fetch even without caller cancellation.</summary>
    private static readonly System.Net.Http.HttpClient s_webClient = new System.Net.Http.HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Per-page content cap; large enough for real documentation pages.</summary>
    private const int WebContentCap = 30_000;

    private static async Task<string> GetWebPagesAsync(JsonNode? args, CancellationToken ct)
    {
        var urlsNode = args?["urls"];
        if (urlsNode is not JsonArray urlsArray)
            throw new ArgumentException("urls array is required");

        var urls = urlsArray
            .Select(n => n?.GetValue<string>())
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()
            .ToList();
        if (urls.Count == 0)
            return "No URLs provided.";

        // Fetch in parallel; one failing URL must not sink the others.
        var tasks = urls.Select(async url =>
        {
            try
            {
                using var response = await s_webClient.GetAsync(url, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return content.Length > WebContentCap
                    ? $"[{url}]: {content.Substring(0, WebContentCap)}\n...[truncated at {WebContentCap} characters]"
                    : $"[{url}]: {content}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[{url}]: Error - {ex.Message}";
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return string.Join("\n\n", results);
    }

    private static async Task<string> RemoveFileAsync(JsonNode? args, CancellationToken ct)
    {
        var filePath = ResolveToolPath(args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath is required"));

        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var dte = await GetOpenDteAsync(ct);
        if (dte is not null)
        {
            var projectItem = FindProjectItem(dte.Solution, filePath);
            if (projectItem != null)
            {
                projectItem.Delete();
                return $"Removed from project and deleted: {filePath}";
            }
        }

        FileIoWithRetry(filePath, () => File.Delete(filePath));
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
        var fileName = GetItemFilePath(item);
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
            .Cast<string>()
            .ToList();

        string? response = null;
        int? selectedIndex = null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dialog = new ToolWindow.InquiryDialog(question, suggestions);
        // M9: Application.Current.MainWindow is null inside devenv, so parent the dialog to the
        // VS main window HWND via the shell service instead (otherwise it can open behind the IDE).
        if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell uiShell
            && uiShell.GetDialogOwnerHwnd(out IntPtr ownerHwnd) == 0 // S_OK
            && ownerHwnd != IntPtr.Zero)
        {
            new System.Windows.Interop.WindowInteropHelper(dialog).Owner = ownerHwnd;
        }

        // M9: cancelling the turn must dismiss the modal dialog. Marshal the close to the UI thread;
        // a closed dialog yields DialogResult != true, so the inquiry resolves to "cancelled".
        // Deliberate fire-and-forget: the registration is disposed once ShowDialog returns, so there
        // is nothing to await/join here (VSSDK007 does not apply to this dismissal path).
#pragma warning disable VSSDK007
        using var registration = ct.Register(() =>
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { dialog.Close(); }
                catch (InvalidOperationException) { /* already closed */ }
            });
        });
#pragma warning restore VSSDK007

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
        var filePath = args?["filePath"]?.GetValue<string>();
        if (string.IsNullOrEmpty(filePath))
        {
            var dte = await GetDteAsync(ct);
            filePath = dte?.ActiveDocument?.FullName;
        }
        else
        {
            filePath = ResolveToolPath(filePath!);
        }

        if (filePath is null || filePath.Length == 0 || !File.Exists(filePath))
            return "No file to analyze.";

        var content = FileIoWithRetry(filePath, () => File.ReadAllText(filePath));
        var memories = new List<string>();

        // Detect using directives patterns
        if (content.Contains("using System.Text.Json;") && !content.Contains("using Newtonsoft.Json;"))
            memories.Add("Prefer System.Text.Json over Newtonsoft.Json");

        if (content.Contains("System.Text.Json.Nodes"))
            memories.Add("Use System.Text.Json.Nodes for JSON manipulation");

        // Detect async patterns. L5: 'async void' is an anti-pattern, not evidence of async style.
        if (content.Contains("async Task"))
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

        // Detect record types. L5: match a record declaration, not the word in comments/strings.
        if (Regex.IsMatch(content, @"\brecord\s+(?:class\s+|struct\s+)?[A-Za-z_]\w*"))
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

        // Detect primary constructors. L5: a parameter list on the type declaration itself, not a
        // conventional constructor with a ': base(...)' / ': this(...)' initializer.
        if (Regex.IsMatch(content, @"\b(?:class|record|struct)\s+[A-Za-z_]\w*\s*\([^)]*\)"))
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

    // --- Instruction sources (Settings -> Instructions) ---

    /// <summary>Lists the configured instruction sources so the model knows what context it is given.</summary>
    private static async Task<string> ListInstructionsAsync(CancellationToken ct)
    {
        var store = new ExtensionSettingsStore();
        var settings = await store.LoadAsync();
        var entries = (settings.Instructions is null || settings.Instructions.Length == 0)
            ? InstructionFileLoader.DefaultEntries()
            : settings.Instructions.ToList();

        var arr = new JsonArray();
        foreach (var e in entries.OrderBy(e => e.Order))
        {
            arr.Add(new JsonObject
            {
                ["name"] = e.Name ?? string.Empty,
                ["kind"] = e.Kind,
                ["path"] = e.Path ?? string.Empty,
                ["enabled"] = e.Enabled,
                ["order"] = e.Order,
            });
        }
        return arr.ToJsonString();
    }

    /// <summary>Returns the full content of one instruction source (inline text or the file on disk).</summary>
    private static async Task<string> ReadInstructionAsync(JsonNode? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return "A 'name' is required.";

        var store = new ExtensionSettingsStore();
        var settings = await store.LoadAsync();
        var entries = (settings.Instructions is null || settings.Instructions.Length == 0)
            ? InstructionFileLoader.DefaultEntries()
            : settings.Instructions.ToList();

        var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return $"No instruction named '{name}'.";

        var content = string.Equals(entry.Kind, InstructionKind.File, StringComparison.OrdinalIgnoreCase)
            ? InstructionFileLoader.ReadFileContent(entry.Path)
            : entry.Content ?? string.Empty;

        return new JsonObject
        {
            ["name"] = entry.Name ?? string.Empty,
            ["kind"] = entry.Kind,
            ["path"] = entry.Path ?? string.Empty,
            ["content"] = content,
        }.ToJsonString();
    }

    /// <summary>Updates (or creates) an instruction source by name: inline text, or writes the file.</summary>
    private static async Task<string> UpdateInstructionAsync(JsonNode? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>();
        var content = args?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return "A 'name' is required.";
        if (content is null)
            return "A 'content' is required.";

        // Coordinated load-modify-save under the store's write lock so a concurrent save
        // from the settings window or MCP manager is not clobbered by a stale snapshot.
        var store = new ExtensionSettingsStore();
        try
        {
            await store.UpdateAsync(settings =>
            {
                var entries = (settings.Instructions is null || settings.Instructions.Length == 0)
                    ? InstructionFileLoader.DefaultEntries()
                    : settings.Instructions.ToList();

                var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    entry = new InstructionEntry
                    {
                        Name = name,
                        Kind = InstructionKind.Text,
                        Content = content,
                        Enabled = true,
                        Order = entries.Count,
                    };
                    entries.Add(entry);
                }
                else if (string.Equals(entry.Kind, InstructionKind.File, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        InstructionFileLoader.WriteFileContent(entry.Path, content);
                    }
                    catch (Exception ex)
                    {
                        // Abort without saving; surfaced as the tool result below.
                        throw new IOException($"Could not write the instruction file: {ex.Message}", ex);
                    }
                }
                else
                {
                    entry.Content = content;
                }

                settings.Instructions = entries.ToArray();
                return Task.CompletedTask;
            });
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
        return $"Updated instruction '{name}'.";
    }
}
