using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;

// DTE/COM access happens on the UI thread (GetOpenDteAsync switches, and COM values are captured
// before any ConfigureAwait(false) hop); the analyzer cannot prove affinity through the chain.
#pragma warning disable VSTHRD010

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Project &amp; package mutation via the dotnet CLI (shared <see cref="ProcessRunner"/>). SDK-style
/// projects only — classic/packages.config projects get an explicit refusal, never a silent
/// substitute. Project names resolve through the DTE solution walk; the TFM edit is the pure,
/// unit-tested <see cref="ProjectFileEditor"/>. Every result reports what actually happened
/// (exit code, resolved version, restore outcome) and points at vs_build for the compile step.
/// </summary>
internal static class VsProjectMutationTools
{
    private const int PackageTimeoutMs = 240_000;
    private const int RestoreTimeoutMs = 180_000;
    private const int NewTimeoutMs = 120_000;
    private const int SlnTimeoutMs = 60_000;
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    public static async Task<string> AddPackageAsync(JsonNode? args, CancellationToken ct)
    {
        var project = args?["project"]?.GetValue<string>();
        var packageId = args?["packageId"]?.GetValue<string>();
        var version = args?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(packageId))
            return "packageId is required.";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        var (path, err) = ResolveProject(dte, project);
        if (err is not null) return err;
        var sdkErr = CheckSdk(path!);
        if (sdkErr is not null) return sdkErr;

        var projDir = Path.GetDirectoryName(path!) ?? string.Empty;
        var solutionDir = Path.GetDirectoryName(dte.Solution.FullName) ?? projDir;
        var cpm = FindCpm(projDir, solutionDir);

        var arguments = "add " + Q(path!) + " package " + Q(packageId!)
            + (string.IsNullOrWhiteSpace(version) ? string.Empty : " --version " + Q(version!));
        var r = await ProcessRunner.RunAsync("dotnet", arguments, projDir, PackageTimeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut)
            return $"dotnet add package timed out after {PackageTimeoutMs / 1000}s. Partial output:\n{Tail(r.StdOut, 1000)}";
        if (r.ExitCode != 0)
            return $"dotnet add package FAILED (exit {r.ExitCode}):\n{Tail(r.StdErr + "\n" + r.StdOut, 1500)}";

        var resolved = version;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var m = Regex.Match(r.StdOut, @"version '([^']+)' added");
            resolved = m.Success ? m.Groups[1].Value : "(latest stable)";
        }
        var note = cpm is not null
            ? $" Central package management is active ({Path.GetFileName(cpm)}); versions are managed centrally."
            : string.Empty;
        return $"Added package {packageId} version {resolved} to {Path.GetFileName(path)}.{note} Run vs_build to compile.\n{Tail(r.StdOut, 1000)}";
    }

    public static async Task<string> RemovePackageAsync(JsonNode? args, CancellationToken ct)
    {
        var project = args?["project"]?.GetValue<string>();
        var packageId = args?["packageId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(packageId))
            return "packageId is required.";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        var (path, err) = ResolveProject(dte, project);
        if (err is not null) return err;
        var sdkErr = CheckSdk(path!);
        if (sdkErr is not null) return sdkErr;

        var projDir = Path.GetDirectoryName(path!) ?? string.Empty;
        var arguments = "remove " + Q(path!) + " package " + Q(packageId!);
        var r = await ProcessRunner.RunAsync("dotnet", arguments, projDir, PackageTimeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut)
            return $"dotnet remove package timed out after {PackageTimeoutMs / 1000}s. Partial output:\n{Tail(r.StdOut, 1000)}";
        if (r.ExitCode != 0)
            return $"dotnet remove package FAILED (exit {r.ExitCode}):\n{Tail(r.StdErr + "\n" + r.StdOut, 1500)}";
        return $"Removed package {packageId} from {Path.GetFileName(path)}. Run vs_build to compile.\n{Tail(r.StdOut, 1000)}";
    }

    public static async Task<string> AddProjectReferenceAsync(JsonNode? args, CancellationToken ct)
    {
        var project = args?["project"]?.GetValue<string>();
        var referencedProject = args?["referencedProject"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(referencedProject))
            return "referencedProject is required (name or path).";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        var (path, err) = ResolveProject(dte, project);
        if (err is not null) return err;
        var (refPath, refErr) = ResolveProject(dte, referencedProject);
        if (refErr is not null) return refErr;
        var sdkErr = CheckSdk(path!);
        if (sdkErr is not null) return sdkErr;
        if (string.Equals(path, refPath, StringComparison.OrdinalIgnoreCase))
            return "A project cannot reference itself.";

        var projDir = Path.GetDirectoryName(path!) ?? string.Empty;
        var arguments = "add " + Q(path!) + " reference " + Q(refPath!);
        var r = await ProcessRunner.RunAsync("dotnet", arguments, projDir, RestoreTimeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut)
            return $"dotnet add reference timed out after {RestoreTimeoutMs / 1000}s. Partial output:\n{Tail(r.StdOut, 1000)}";
        if (r.ExitCode != 0)
            return $"dotnet add reference FAILED (exit {r.ExitCode}):\n{Tail(r.StdErr + "\n" + r.StdOut, 1500)}";
        return $"Added reference {Path.GetFileName(refPath)} -> {Path.GetFileName(path)}. Run vs_build to compile.\n{Tail(r.StdOut, 1000)}";
    }

    public static async Task<string> RemoveProjectReferenceAsync(JsonNode? args, CancellationToken ct)
    {
        var project = args?["project"]?.GetValue<string>();
        var referencedProject = args?["referencedProject"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(referencedProject))
            return "referencedProject is required (name or path).";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        var (path, err) = ResolveProject(dte, project);
        if (err is not null) return err;
        var (refPath, refErr) = ResolveProject(dte, referencedProject);
        if (refErr is not null) return refErr;
        var sdkErr = CheckSdk(path!);
        if (sdkErr is not null) return sdkErr;

        var projDir = Path.GetDirectoryName(path!) ?? string.Empty;
        var arguments = "remove " + Q(path!) + " reference " + Q(refPath!);
        var r = await ProcessRunner.RunAsync("dotnet", arguments, projDir, RestoreTimeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut)
            return $"dotnet remove reference timed out after {RestoreTimeoutMs / 1000}s. Partial output:\n{Tail(r.StdOut, 1000)}";
        if (r.ExitCode != 0)
            return $"dotnet remove reference FAILED (exit {r.ExitCode}):\n{Tail(r.StdErr + "\n" + r.StdOut, 1500)}";
        return $"Removed reference {Path.GetFileName(refPath)} from {Path.GetFileName(path)}. Run vs_build to compile.\n{Tail(r.StdOut, 1000)}";
    }

    public static async Task<string> CreateProjectAsync(JsonNode? args, CancellationToken ct)
    {
        var template = args?["template"]?.GetValue<string>();
        var name = args?["name"]?.GetValue<string>();
        var framework = args?["framework"]?.GetValue<string>();
        var addToSolution = args?["addToSolution"]?.GetValue<bool>() ?? true;
        if (string.IsNullOrWhiteSpace(template))
            return "template is required (e.g. console, classlib, xunit, mstest, web).";
        if (string.IsNullOrWhiteSpace(name))
            return "name is required.";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        // Capture COM values on the UI thread before any off-thread process hop.
        var slnPath = dte.Solution.FullName;
        var solutionDir = Path.GetDirectoryName(slnPath);
        if (string.IsNullOrEmpty(solutionDir))
            return "Could not resolve the solution directory.";

        var dir = Path.Combine(solutionDir!, name!);
        if (Directory.Exists(dir))
            return $"Directory already exists: {dir}. Choose a different name or remove it first.";

        var arguments = "new " + template + " -n " + Q(name!) + " -o " + Q(dir)
            + (string.IsNullOrWhiteSpace(framework) ? string.Empty : " -f " + framework);
        var r = await ProcessRunner.RunAsync("dotnet", arguments, solutionDir, NewTimeoutMs, ct).ConfigureAwait(false);
        if (r.TimedOut)
            return $"dotnet new timed out after {NewTimeoutMs / 1000}s. Partial output:\n{Tail(r.StdOut, 1000)}";
        if (r.ExitCode != 0)
            return $"dotnet new {template} FAILED (exit {r.ExitCode}):\n{Tail(r.StdErr + "\n" + r.StdOut, 1500)}";

        var projFile = Directory.EnumerateFiles(dir, "*.*proj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => IsProjectExtension(Path.GetExtension(f)));
        if (projFile is null)
            return $"Template ran but no project file was found in {dir}. Output:\n{Tail(r.StdOut, 800)}";

        var sb = new StringBuilder();
        sb.Append($"Created {template} project '{name}' at {dir}.");
        if (addToSolution)
        {
            var addArgs = "sln " + Q(slnPath) + " add " + Q(projFile);
            var ar = await ProcessRunner.RunAsync("dotnet", addArgs, solutionDir, SlnTimeoutMs, ct).ConfigureAwait(false);
            if (!ar.TimedOut && ar.ExitCode == 0)
                sb.Append($" Added to solution {Path.GetFileName(slnPath)}.");
            else
                sb.Append($" Could NOT add to solution (exit {ar.ExitCode}): {Tail(ar.StdErr, 400)}. Add it manually with 'dotnet sln add'.");
        }
        sb.Append(" Run vs_build to compile.");
        return sb.ToString();
    }

    public static async Task<string> SetTargetFrameworkAsync(JsonNode? args, CancellationToken ct)
    {
        var project = args?["project"]?.GetValue<string>();
        var framework = args?["framework"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(framework))
            return "framework is required (e.g. net10.0, or 'net8.0;net10.0' to multi-target).";

        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null) return "No solution is open.";
        var (path, err) = ResolveProject(dte, project);
        if (err is not null) return err;

        string text;
        Encoding encoding;
        try
        {
            (text, encoding) = BuiltInVsTools.FileIoWithRetry(path!, () => FileTextIo.Read(path!));
        }
        catch (Exception ex)
        {
            return $"Could not read project file: {ex.Message}";
        }
        if (!ProjectFileEditor.IsSdkStyle(text))
            return $"Project '{Path.GetFileName(path)}' is not SDK-style; target-framework mutation is not supported for it. Edit the project manually.";

        var edit = ProjectFileEditor.SetTargetFramework(text, framework!);
        if (!edit.Success)
            return edit.Error ?? "Target-framework edit failed.";
        try
        {
            BuiltInVsTools.FileIoWithRetry(path!, () => { FileTextIo.Write(path!, edit.NewXml!, encoding); return true; });
        }
        catch (Exception ex)
        {
            return $"Could not write project file: {ex.Message}";
        }

        var projDir = Path.GetDirectoryName(path!) ?? string.Empty;
        var r = await ProcessRunner.RunAsync("dotnet", "restore " + Q(path!), projDir, RestoreTimeoutMs, ct).ConfigureAwait(false);
        var restoreNote = !r.TimedOut && r.ExitCode == 0
            ? "Restored packages."
            : $"Restore {(r.TimedOut ? "timed out" : "failed (exit " + r.ExitCode + ")")}: {Tail(r.StdErr, 500)}";
        return $"{edit.ChangeDescription} {restoreNote} VS will reload the project; run vs_build to compile.";
    }

    // ---- helpers ----

    private static (string? Path, string? Error) ResolveProject(DTE2 dte, string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return (null, "project is required (name or path).");
        var solutionDir = Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty;
        var candidate = nameOrPath!.Trim();

        var looksLikePath = candidate.IndexOf('\\') >= 0 || candidate.IndexOf('/') >= 0
            || IsProjectExtension(Path.GetExtension(candidate));
        if (looksLikePath)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(solutionDir, candidate));
            return File.Exists(full) ? (full, null) : (null, $"Project file not found: {full}");
        }

        var projects = CollectProjects(dte);
        var matches = projects.Where(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return (matches[0].Path, null);
        if (matches.Count > 1)
            return (null, $"Ambiguous project name '{candidate}': {string.Join(", ", matches.Select(m => m.Path))}");
        return (null, $"Project '{candidate}' not found in the solution. Available: {string.Join(", ", projects.Select(p => p.Name))}");
    }

    private static List<(string Name, string Path)> CollectProjects(DTE2 dte)
    {
        var list = new List<(string, string)>();
        foreach (Project project in dte.Solution.Projects)
            Walk(project, list);
        return list;
    }

    private static void Walk(Project project, List<(string Name, string Path)> list)
    {
        try
        {
            if (project.Kind?.Equals(SolutionFolderKind, StringComparison.OrdinalIgnoreCase) == true)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    var sub = item.SubProject;
                    if (sub is not null)
                        Walk(sub, list);
                }
                return;
            }
            var file = project.FullName;
            if (string.IsNullOrEmpty(file) || !File.Exists(file) || !IsProjectExtension(Path.GetExtension(file)))
                return;
            list.Add((project.Name, file));
        }
        catch (Exception ex)
        {
            // COM property access can throw for unloaded/unsupported project kinds; skip them.
            DebugLog.Warn($"Project mutation skipped a project: {ex.Message}");
        }
    }

    private static bool IsProjectExtension(string ext)
        => ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);

    private static string? CheckSdk(string projectPath)
    {
        string text;
        try
        {
            text = BuiltInVsTools.FileIoWithRetry(projectPath, () => FileTextIo.Read(projectPath).Text);
        }
        catch (Exception ex)
        {
            return $"Could not read project file: {ex.Message}";
        }
        if (!ProjectFileEditor.IsSdkStyle(text))
            return $"Project '{Path.GetFileName(projectPath)}' is not SDK-style (classic/packages.config). dotnet CLI mutation is not supported for it; edit the project manually.";
        return null;
    }

    /// <summary>Walks up from the project dir to the solution dir looking for Directory.Packages.props.</summary>
    private static string? FindCpm(string projectDir, string solutionDir)
    {
        try
        {
            var stop = Path.GetFullPath(solutionDir);
            var dir = projectDir;
            while (!string.IsNullOrEmpty(dir))
            {
                var props = Path.Combine(dir, "Directory.Packages.props");
                if (File.Exists(props))
                    return props;
                if (Path.GetFullPath(dir).Equals(stop, StringComparison.OrdinalIgnoreCase))
                    break;
                var parent = Path.GetDirectoryName(dir);
                if (parent is null || parent.Equals(dir, StringComparison.OrdinalIgnoreCase))
                    break;
                dir = parent;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"CPM detection failed: {ex.Message}");
        }
        return null;
    }

    private static string Q(string s) => "\"" + s + "\"";

    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        s = s!.TrimEnd();
        return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
    }
}