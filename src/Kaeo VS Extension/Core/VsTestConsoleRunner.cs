using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using EnvDTE;
using EnvDTE80;

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
    private const int ListTimeoutMs = 120_000;
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    private sealed record TestProjectInfo(string Name, string AssemblyPath);

    // /ListTests output cached per assembly until a rebuild changes its timestamp.
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, string[] Tests)> ListCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts a filtered run (or a full-suite run when filterValue is empty). Must be called on
    /// the UI thread (it enumerates DTE projects); the test process itself runs and is awaited off
    /// the UI thread. With structured=true the result is JSON parsed from a TRX logger file.
    /// </summary>
    public static async Task<FilteredTestRun> TryRunAsync(DTE2 dte, string filterType, string filterValue, CancellationToken ct, bool structured = false)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(filterValue);
        string? canonical = null;
        if (hasFilter)
        {
            canonical = new[] { "Assembly", "Project", "FullyQualifiedName", "TypeName", "MethodName" }
                .FirstOrDefault(k => k.Equals(filterType, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
                return new(null, $"unsupported filterType '{filterType}'");
        }

        var (vsTestPath, locateError) = LocateVsTest(dte);
        if (locateError is not null)
            return new(null, locateError);

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

        if (!hasFilter)
        {
            var built = testProjects.Where(p => File.Exists(p.AssemblyPath)).ToList();
            if (built.Count == 0)
                return new(null, "no built test assemblies found; build the solution first");
            assemblies = [.. built.Select(p => p.AssemblyPath)];
            scope = $"full suite ({assemblies.Count} assembl{(assemblies.Count == 1 ? "y" : "ies")})";
        }
        else if (canonical is "Project" or "Assembly")
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

            assemblies = [.. matches.Select(m => m.AssemblyPath)];
            scope = string.Join(", ", matches.Select(m => m.Name));
        }
        else
        {
            var built = testProjects.Where(p => File.Exists(p.AssemblyPath)).ToList();
            if (built.Count == 0)
                return new($"No built test assemblies found ({string.Join("; ", testProjects.Select(p => p.AssemblyPath))}). Build the solution first; nothing was run.", null);

            assemblies = [.. built.Select(p => p.AssemblyPath)];
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
        var result = await RunVsTestAsync(vsTestPath, assemblies, filterExpr, canonical ?? string.Empty, filterValue, scope, structured, ct).ConfigureAwait(false);
        return new(result, null);
    }

    private static (string Path, string? Error) LocateVsTest(DTE2 dte)
    {
        try
        {
            // dte.FullName is ...\Common7\IDE\devenv.exe; vstest.console ships beside it.
            var ideDir = Path.GetDirectoryName(dte.FullName) ?? string.Empty;
            var path = Path.Combine(ideDir, @"CommonExtensions\Microsoft\TestWindow\vstest.console.exe");
            return File.Exists(path) ? (path, null) : (string.Empty, $"vstest.console.exe not found at '{path}'");
        }
        catch (Exception ex)
        {
            return (string.Empty, $"could not locate the Visual Studio install: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists discovered tests from built test assemblies via vstest.console /ListTests, cached
    /// per assembly until its timestamp changes. Returns JSON; nameContains narrows the list and
    /// maxResults caps it (truncation is reported, never silent). UI thread required (DTE walk).
    /// </summary>
    public static async Task<string> ListTestsAsync(DTE2 dte, string? nameContains, int maxResults, CancellationToken ct)
    {
        var (vsTestPath, locateError) = LocateVsTest(dte);
        if (locateError is not null)
            return $"Could not list tests: {locateError}";

        List<TestProjectInfo> testProjects;
        try
        {
            testProjects = CollectTestProjects(dte);
        }
        catch (Exception ex)
        {
            return $"Could not enumerate solution projects: {ex.Message}";
        }

        var built = testProjects.Where(p => File.Exists(p.AssemblyPath)).ToList();
        if (built.Count == 0)
            return "No built test assemblies found. Build the solution first; nothing was listed.";

        maxResults = Math.Max(1, Math.Min(2000, maxResults));
        var all = new List<string>();
        var assemblyInfos = new JsonArray();
        var problems = new List<string>();
        foreach (var project in built)
        {
            ct.ThrowIfCancellationRequested();
            string[] tests;
            var stamp = File.GetLastWriteTimeUtc(project.AssemblyPath);
            if (ListCache.TryGetValue(project.AssemblyPath, out var cached) && cached.Stamp == stamp)
            {
                tests = cached.Tests;
            }
            else
            {
                ProcessRunResult run;
                try
                {
                    run = await ProcessRunner.RunAsync(vsTestPath, $"\"{project.AssemblyPath}\" /ListTests", null, ListTimeoutMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    problems.Add($"{project.Name}: could not start vstest.console ({ex.Message})");
                    continue;
                }
                if (run.TimedOut)
                {
                    problems.Add($"{project.Name}: /ListTests did not finish within {ListTimeoutMs / 1000}s");
                    continue;
                }
                tests = ParseListTestsOutput(run.StdOut);
                if (run.ExitCode != 0 && tests.Length == 0)
                {
                    var err = run.StdErr.Trim();
                    problems.Add($"{project.Name}: /ListTests failed (exit {run.ExitCode}): {(err.Length <= 200 ? err : err.Substring(0, 200))}");
                    continue;
                }
                ListCache[project.AssemblyPath] = (stamp, tests);
            }
            all.AddRange(tests);
            assemblyInfos.Add(new JsonObject
            {
                ["project"] = project.Name,
                ["assembly"] = project.AssemblyPath,
                ["testCount"] = tests.Length,
            });
        }

        var matched = string.IsNullOrWhiteSpace(nameContains)
            ? all
            : [.. all.Where(t => t.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0)];
        var returned = matched.Take(maxResults).ToList();

        var json = new JsonObject
        {
            ["totalDiscovered"] = all.Count,
            ["totalMatching"] = matched.Count,
            ["returned"] = returned.Count,
            ["truncated"] = matched.Count > returned.Count,
            ["nameContains"] = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains,
            ["assemblies"] = assemblyInfos,
            ["tests"] = new JsonArray(returned.Select(t => (JsonNode)t).ToArray()),
        };
        if (problems.Count > 0)
            json["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray());
        return json.ToJsonString();
    }

    private static string[] ParseListTestsOutput(string stdout)
    {
        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(lines, l => l.IndexOf("The following Tests are available", StringComparison.OrdinalIgnoreCase) >= 0);
        if (marker < 0)
            return [];
        // vstest indents test names; adapter diagnostics (e.g. "[xUnit.net ...] Discovering:")
        // start at column 0 and must not be mistaken for tests.
        return [.. lines.Skip(marker + 1)
            .Where(l => l.Length > 0 && char.IsWhiteSpace(l[0]))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '[')];
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
        [
            "Microsoft.NET.Test.Sdk",
            "<IsTestProject>true",
            "xunit.runner.visualstudio",
            "NUnit3TestAdapter",
            "MSTest.TestAdapter",
        ];
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
        string filterType, string filterValue, string scope, bool structured, CancellationToken ct)
    {
        var args = new StringBuilder();
        foreach (var assembly in assemblies)
            args.Append('"').Append(assembly).Append("\" ");
        if (filterExpr is not null)
            args.Append("/TestCaseFilter:\"").Append(filterExpr).Append("\" ");

        string? trxPath = null;
        if (structured)
        {
            trxPath = Path.Combine(Path.GetTempPath(), "kaeo-tests-" + Guid.NewGuid().ToString("N") + ".trx");
            args.Append("/logger:\"trx;LogFileName=").Append(trxPath).Append('"');
        }

        try
        {
            ProcessRunResult run;
            try
            {
                run = await ProcessRunner.RunAsync(vsTestPath, args.ToString(), null, RunTimeoutMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"Could not start vstest.console: {ex.Message}";
            }

            if (structured && !run.TimedOut)
            {
                var json = TryBuildStructuredResult(trxPath!, filterType, filterValue, filterExpr, scope, run);
                if (json is not null)
                    return json;
                // TRX missing/unparseable: fall through to text output with an honesty note.
            }

            var output = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdOut + "\n" + run.StdErr;

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 15)));
            var summary = lines.LastOrDefault(l =>
                l.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("Total tests:", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("No test matches", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("No test is available", StringComparison.OrdinalIgnoreCase) >= 0);

            var header = string.IsNullOrWhiteSpace(filterValue)
                ? $"Full test run via vstest.console (scope: {scope}; results do not appear in Test Explorer)"
                : $"Filtered test run via vstest.console ({filterType}={filterValue}; scope: {scope}"
                    + (filterExpr is not null ? $"; filter: {filterExpr}" : string.Empty)
                    + "; results do not appear in Test Explorer)";
            if (structured)
                header += " (structured JSON was requested but the TRX results were unavailable; text output only)";

            if (run.TimedOut)
                return $"{header} did not finish within {RunTimeoutMs / 1000}s; partial output:\n{tail}";
            if (summary is null)
                return $"{header} produced no run summary (exit code {run.ExitCode}):\n{tail}";
            return $"{header}:\n{tail}";
        }
        finally
        {
            if (trxPath is not null)
            {
                try { File.Delete(trxPath); }
                catch (Exception ex) { DebugLog.Warn($"Could not delete temp TRX '{trxPath}': {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Builds the structured JSON result from the TRX file. Non-passed tests are listed first so
    /// failures are never hidden by the entry cap; truncation is flagged explicitly. Returns null
    /// when the TRX is missing or unparseable (caller falls back to text with a note).
    /// </summary>
    private static string? TryBuildStructuredResult(
        string trxPath, string filterType, string filterValue, string? filterExpr, string scope, ProcessRunResult run)
    {
        try
        {
            if (!File.Exists(trxPath))
                return null;
            var report = TrxParser.Parse(File.ReadAllText(trxPath));

            const int maxEntries = 500;
            var ordered = report.Results
                .OrderByDescending(r => !string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.TestName, StringComparer.Ordinal)
                .ToList();
            var tests = new JsonArray();
            foreach (var r in ordered.Take(maxEntries))
            {
                var obj = new JsonObject
                {
                    ["name"] = r.TestName,
                    ["fullyQualifiedName"] = r.FullyQualifiedName,
                    ["outcome"] = r.Outcome,
                    ["duration"] = r.Duration,
                };
                if (!string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase))
                {
                    obj["errorMessage"] = Cap(r.ErrorMessage, 4000);
                    obj["stackTrace"] = Cap(r.StackTrace, 4000);
                }
                if (!string.IsNullOrEmpty(r.StdOut))
                    obj["stdOut"] = Cap(r.StdOut, 1000);
                tests.Add(obj);
            }

            var hasFilter = !string.IsNullOrWhiteSpace(filterValue);
            var json = new JsonObject
            {
                ["mode"] = hasFilter ? "filtered-vstest" : "full-vstest",
                ["filterType"] = hasFilter ? filterType : null,
                ["filterValue"] = hasFilter ? filterValue : null,
                ["filter"] = filterExpr,
                ["scope"] = scope,
                ["exitCode"] = run.ExitCode,
                ["note"] = "Run via vstest.console; results do not appear in Test Explorer.",
                ["summary"] = new JsonObject
                {
                    ["outcome"] = report.Summary.Outcome,
                    ["total"] = report.Summary.Total,
                    ["executed"] = report.Summary.Executed,
                    ["passed"] = report.Summary.Passed,
                    ["failed"] = report.Summary.Failed,
                    ["notExecuted"] = report.Summary.NotExecuted,
                    ["error"] = report.Summary.Error,
                    ["timeout"] = report.Summary.Timeout,
                    ["aborted"] = report.Summary.Aborted,
                    ["inconclusive"] = report.Summary.Inconclusive,
                },
                ["testsReturned"] = tests.Count,
                ["testsTruncated"] = ordered.Count > maxEntries,
                ["tests"] = tests,
            };
            return json.ToJsonString();
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Could not parse TRX results '{trxPath}': {ex.Message}");
            return null;
        }
    }

    private static string? Cap(string? value, int max)
        => value is null || value.Length <= max ? value : value.Substring(0, max) + "…(truncated)";
}