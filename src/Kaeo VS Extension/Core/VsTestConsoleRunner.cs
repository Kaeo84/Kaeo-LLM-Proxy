using System.IO;
using System.Text;
using EnvDTE;
using EnvDTE80;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

// DTE/COM access happens on the UI thread (the caller guarantees it); the analyzer cannot
// prove thread affinity through the indirect call chain from BuiltInVsTools.
#pragma warning disable VSTHRD010

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Outcome of a filtered test-run attempt: either a finished <see cref="Result"/> string, or a
/// <see cref="FallbackReason"/> explaining why filtering was unavailable so the caller can run
/// the full suite via Test Explorer and report the substitution honestly. Never both.
/// </summary>
internal sealed record FilteredTestRun(string? Result, string? FallbackReason);

/// <summary>
/// M11: runs genuinely filtered tests through vstest.console.exe. DTE only exposes
/// TestExplorer.RunAllTests / RunTestsInContext, so arbitrary filters cannot go through the
/// Test Explorer command surface. Results from this runner do not appear in Test Explorer;
/// the returned text says so explicitly.
/// </summary>
internal static class VsTestConsoleRunner
{
    private const int RunTimeoutMs = 300_000; // 5 minute ceiling, same as the Test Explorer path
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    private sealed record TestProjectInfo(string Name, string AssemblyPath);

    /// <summary>
    /// Attempts a filtered run. Must be called on the UI thread (it enumerates DTE projects);
    /// the test process itself runs and is awaited off the UI thread.
    /// </summary>
    public static async Task<FilteredTestRun> TryRunAsync(DTE2 dte, string filterType, string filterValue, CancellationToken ct)
    {
        var canonical = new[] { "Assembly", "Project", "FullyQualifiedName", "TypeName", "MethodName" }
            .FirstOrDefault(k => k.Equals(filterType, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return new(null, $"unsupported filterType '{filterType}'");

        string vsTestPath;
        try
        {
            // dte.FullName is ...\Common7\IDE\devenv.exe; vstest.console ships beside it.
            var ideDir = Path.GetDirectoryName(dte.FullName) ?? string.Empty;
            vsTestPath = Path.Combine(ideDir, @"CommonExtensions\Microsoft\TestWindow\vstest.console.exe");
        }
        catch (Exception ex)
        {
            return new(null, $"could not locate the Visual Studio install: {ex.Message}");
        }
        if (!File.Exists(vsTestPath))
            return new(null, $"vstest.console.exe not found at '{vsTestPath}'");

        List<TestProjectInfo> testProjects;
        try
        {
            testProjects = CollectTestProjects(dte);
        }
        catch (Exception ex)
        {
            return new(null, $"could not enumerate solution projects: {ex.Message}");
        }
        if (testProjects.Count == 0)
            return new(null, "no test projects detected in the solution");

        List<string> assemblies;
        string? filterExpr = null;
        string scope;

        if (canonical is "Project" or "Assembly")
        {
            // Project/Assembly filters select whole assemblies - no TestCaseFilter needed.
            var matches = testProjects.Where(p => canonical == "Project"
                ? p.Name.Equals(filterValue, StringComparison.OrdinalIgnoreCase)
                : MatchesAssembly(p.AssemblyPath, filterValue)).ToList();
            if (matches.Count == 0)
            {
                var what = canonical == "Project" ? "project" : "test assembly";
                return new($"No {what} matching '{filterValue}' was found. Test projects in the solution: {string.Join(", ", testProjects.Select(p => p.Name))}. Nothing was run.", null);
            }
            var missing = matches.Where(m => !File.Exists(m.AssemblyPath)).ToList();
            if (missing.Count > 0)
                return new($"Test assembly not built: {string.Join(", ", missing.Select(m => m.AssemblyPath))}. Build the solution first; nothing was run.", null);

            assemblies = matches.Select(m => m.AssemblyPath).ToList();
            scope = string.Join(", ", matches.Select(m => m.Name));
        }
        else
        {
            var built = testProjects.Where(p => File.Exists(p.AssemblyPath)).ToList();
            if (built.Count == 0)
                return new($"No built test assemblies found ({string.Join("; ", testProjects.Select(p => p.AssemblyPath))}). Build the solution first; nothing was run.", null);

            assemblies = built.Select(p => p.AssemblyPath).ToList();
            var v = EscapeFilterValue(filterValue);
            filterExpr = canonical switch
            {
                "FullyQualifiedName" => $"FullyQualifiedName={v}",
                // Trailing dot anchors the class name so TypeName=Foo does not match FooBar.
                "TypeName" => $"FullyQualifiedName~{v}.",
                // MSTest exposes Name as the bare method name; xUnit/NUnit expose the full FQN,
                // so the second clause covers them (contains match - may include suffix matches).
                _ => $"Name={v}|FullyQualifiedName~.{v}",
            };
            scope = $"{assemblies.Count} test assembl{(assemblies.Count == 1 ? "y" : "ies")}";
        }

        ct.ThrowIfCancellationRequested();
        var result = await RunVsTestAsync(vsTestPath, assemblies, filterExpr, canonical, filterValue, scope, ct).ConfigureAwait(false);
        return new(result, null);
    }

    private static bool MatchesAssembly(string assemblyPath, string filterValue)
    {
        var fileName = Path.GetFileName(assemblyPath);
        return fileName.Equals(filterValue, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(fileName).Equals(filterValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Escapes the characters vstest's /TestCaseFilter treats as operators.</summary>
    private static string EscapeFilterValue(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '(' or ')' or ',' or '&' or '|' or '=' or '!' or '~' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<TestProjectInfo> CollectTestProjects(DTE2 dte)
    {
        var list = new List<TestProjectInfo>();
        foreach (Project project in dte.Solution.Projects)
            WalkProject(project, list);
        return list;
    }

    private static void WalkProject(Project project, List<TestProjectInfo> list)
    {
        try
        {
            if (project.Kind?.Equals(SolutionFolderKind, StringComparison.OrdinalIgnoreCase) == true)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    var sub = item.SubProject;
                    if (sub is not null)
                        WalkProject(sub, list);
                }
                return;
            }

            var projectFile = project.FullName;
            if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
                return;
            var ext = Path.GetExtension(projectFile);
            if (!ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
                return;

            string content;
            try
            {
                content = File.ReadAllText(projectFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // AllowMultipleInstances: a sharing violation must degrade, not crash the scan.
                DebugLog.Warn($"Test discovery could not read '{projectFile}': {ex.Message}");
                return;
            }
            if (!IsTestProjectContent(content))
                return;

            list.Add(new TestProjectInfo(project.Name, ResolveOutputAssembly(project, projectFile)));
        }
        catch (Exception ex)
        {
            // COM property access can throw for unloaded/unsupported project kinds; skip them.
            DebugLog.Warn($"Test discovery skipped a project: {ex.Message}");
        }
    }

    /// <summary>Test projects are identified by the test SDK/adapter markers in the project file.</summary>
    private static bool IsTestProjectContent(string content)
    {
        string[] markers =
        {
            "Microsoft.NET.Test.Sdk",
            "<IsTestProject>true",
            "xunit.runner.visualstudio",
            "NUnit3TestAdapter",
            "MSTest.TestAdapter",
        };
        return markers.Any(m => content.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string ResolveOutputAssembly(Project project, string projectFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(projectFile) ?? string.Empty;
            var outputPath = project.ConfigurationManager?.ActiveConfiguration?.Properties?.Item("OutputPath")?.Value as string ?? string.Empty;
            var fileName = project.Properties?.Item("OutputFileName")?.Value as string;
            if (string.IsNullOrEmpty(fileName))
            {
                var asmName = project.Properties?.Item("AssemblyName")?.Value as string;
                if (!string.IsNullOrEmpty(asmName))
                    fileName = asmName + ".dll";
            }
            if (string.IsNullOrEmpty(fileName))
                return $"(output assembly unresolved for {project.Name})";

            // OutputPath is usually relative to the project dir; Path.Combine honors absolute ones.
            return Path.Combine(dir, outputPath, fileName);
        }
        catch (Exception)
        {
            return $"(output assembly unresolved for {project.Name})";
        }
    }

    private static async Task<string> RunVsTestAsync(
        string vsTestPath, List<string> assemblies, string? filterExpr,
        string filterType, string filterValue, string scope, CancellationToken ct)
    {
        var args = new StringBuilder();
        foreach (var assembly in assemblies)
            args.Append('"').Append(assembly).Append("\" ");
        if (filterExpr is not null)
            args.Append("/TestCaseFilter:\"").Append(filterExpr).Append('"');

        var psi = new ProcessStartInfo
        {
            FileName = vsTestPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exitTcs.TrySetResult(true);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return $"Could not start vstest.console: {ex.Message}";
        }
        if (process.HasExited)
            exitTcs.TrySetResult(true);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RunTimeoutMs);
        using var registration = timeoutCts.Token.Register(() =>
        {
            // net48 has no Kill(entireProcessTree); testhost children exit when the runner pipe drops.
            try { if (!process.HasExited) process.Kill(); }
            catch { /* already exited or access denied */ }
            exitTcs.TrySetCanceled();
        });

        var timedOut = false;
        try
        {
            await exitTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 15)));
        var summary = lines.LastOrDefault(l =>
            l.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0
            || l.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0
            || l.IndexOf("Total tests:", StringComparison.OrdinalIgnoreCase) >= 0
            || l.IndexOf("No test matches", StringComparison.OrdinalIgnoreCase) >= 0
            || l.IndexOf("No test is available", StringComparison.OrdinalIgnoreCase) >= 0);

        var header = $"Filtered test run via vstest.console ({filterType}={filterValue}; scope: {scope}"
            + (filterExpr is not null ? $"; filter: {filterExpr}" : string.Empty)
            + "; results do not appear in Test Explorer)";

        if (timedOut)
            return $"{header} did not finish within {RunTimeoutMs / 1000}s; partial output:\n{tail}";
        if (summary is null)
            return $"{header} produced no run summary (exit code {process.ExitCode}):\n{tail}";
        return $"{header}:\n{tail}";
    }
}