using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.Shell;

// DTE access is preceded by an explicit main-thread switch; the analyzer cannot prove thread
// affinity through the indirect call chain from BuiltInVsTools.
#pragma warning disable VSTHRD010

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Git inspection/revert tools for the agent's edit-verify loop. All commands run against the
/// repository containing the open solution, never devenv's working directory. Revert is
/// deliberately narrow: tracked, locally-modified files only, restored from the index
/// (staged changes are kept), with a diffstat of what was discarded reported back.
/// </summary>
internal static class GitTools
{
    private const int GitTimeoutMs = 30_000;
    private const int MaxOutputChars = 20_000;
    private const int MaxStatusEntries = 200;

    public static async Task<string> StatusAsync(JsonNode? args, CancellationToken ct)
    {
        var (repoRoot, error) = await GetRepoRootAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var run = await RunGitAsync(repoRoot, "-c core.quotepath=false status --porcelain=v1 -b", ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return $"git status failed (exit {run.ExitCode}): {Trim(run.StdErr)}";

        var branch = "(unknown branch)";
        var staged = new List<string>();
        var unstaged = new List<string>();
        var untracked = new List<string>();
        var conflicts = new List<string>();
        foreach (var line in run.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                branch = line.Substring(3);
                continue;
            }
            if (line.Length < 4) continue;
            var x = line[0];
            var y = line[1];
            var path = line.Substring(3);
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path.Substring(arrow + 4);

            if (x == '?' && y == '?') untracked.Add(path);
            else if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) conflicts.Add($"{path} [{x}{y}]");
            else
            {
                if (x != ' ') staged.Add($"{path} [{x}]");
                if (y != ' ') unstaged.Add($"{path} [{y}]");
            }
        }

        var sb = new StringBuilder();
        sb.Append("Branch: ").AppendLine(branch);
        sb.Append($"Staged: {staged.Count}, unstaged: {unstaged.Count}, untracked: {untracked.Count}, conflicts: {conflicts.Count}");
        AppendGroup(sb, "Staged", staged);
        AppendGroup(sb, "Unstaged", unstaged);
        AppendGroup(sb, "Untracked", untracked);
        AppendGroup(sb, "Conflicts", conflicts);
        if (staged.Count + unstaged.Count + untracked.Count + conflicts.Count == 0)
            sb.AppendLine().Append("Working tree is clean.");
        return sb.ToString();
    }

    public static async Task<string> DiffAsync(JsonNode? args, CancellationToken ct)
    {
        var staged = args?["staged"]?.GetValue<bool>() ?? false;
        var contextLines = Math.Max(0, Math.Min(20, args?["contextLines"]?.GetValue<int>() ?? 3));
        var (repoRoot, error) = await GetRepoRootAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var pathArgs = new StringBuilder();
        if (args?["paths"] is JsonArray paths && paths.Count > 0)
        {
            foreach (var p in paths)
            {
                if (!TryResolveRepoPath(repoRoot, p?.GetValue<string>() ?? "", out var rel, out var pathError))
                    return pathError;
                pathArgs.Append(" \"").Append(rel).Append('"');
            }
            pathArgs.Insert(0, " --");
        }

        var flags = $"--no-pager diff --no-color -U{contextLines}" + (staged ? " --cached" : string.Empty);
        var run = await RunGitAsync(repoRoot, flags + pathArgs, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return $"git diff failed (exit {run.ExitCode}): {Trim(run.StdErr)}";

        var scope = staged ? "staged changes vs HEAD" : "working tree vs index";
        if (string.IsNullOrWhiteSpace(run.StdOut))
            return $"No differences ({scope}).";

        if (run.StdOut.Length <= MaxOutputChars)
            return run.StdOut;

        var stat = await RunGitAsync(repoRoot, flags + " --stat" + pathArgs, ct).ConfigureAwait(false);
        return run.StdOut.Substring(0, MaxOutputChars)
            + $"\n…(diff truncated at {MaxOutputChars} chars; full change summary below)\n"
            + (stat.ExitCode == 0 ? stat.StdOut : Trim(stat.StdErr));
    }

    public static async Task<string> LogAsync(JsonNode? args, CancellationToken ct)
    {
        var maxCount = Math.Max(1, Math.Min(100, args?["maxCount"]?.GetValue<int>() ?? 10));
        var (repoRoot, error) = await GetRepoRootAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var pathArg = string.Empty;
        var path = args?["path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!TryResolveRepoPath(repoRoot, path!, out var rel, out var pathError))
                return pathError;
            pathArg = $" -- \"{rel}\"";
        }

        var run = await RunGitAsync(repoRoot, $"--no-pager log --oneline --no-decorate -n {maxCount}{pathArg}", ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return $"git log failed (exit {run.ExitCode}): {Trim(run.StdErr)}";
        return string.IsNullOrWhiteSpace(run.StdOut) ? "No commits found." : run.StdOut;
    }

    public static async Task<string> RevertAsync(JsonNode? args, CancellationToken ct)
    {
        if (args?["paths"] is not JsonArray paths || paths.Count == 0)
            return "paths is required and must be a non-empty array. Whole-repository reverts are not supported; list the files to restore.";

        var (repoRoot, error) = await GetRepoRootAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var status = await RunGitAsync(repoRoot, "-c core.quotepath=false status --porcelain=v1", ct).ConfigureAwait(false);
        if (status.ExitCode != 0)
            return $"git status failed (exit {status.ExitCode}): {Trim(status.StdErr)}";

        var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in status.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var xy = line.Substring(0, 2);
            var path = line.Substring(3);
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                changed[path.Substring(0, arrow)] = xy;
                path = path.Substring(arrow + 4);
            }
            changed[path] = xy;
        }

        // Validate the whole batch before touching anything (all-or-nothing).
        var relatives = new List<string>();
        foreach (var p in paths)
        {
            if (!TryResolveRepoPath(repoRoot, p?.GetValue<string>() ?? "", out var rel, out var pathError))
                return pathError;
            if (!changed.TryGetValue(rel, out var xy))
                return $"'{rel}' has no local changes; nothing to revert. No files were modified.";
            if (xy == "??")
                return $"'{rel}' is untracked; revert only restores tracked files (use vs_remove_file to delete it). No files were modified.";
            if (!relatives.Contains(rel, StringComparer.OrdinalIgnoreCase))
                relatives.Add(rel);
        }

        var quoted = string.Join(" ", relatives.Select(r => $"\"{r}\""));
        var stat = await RunGitAsync(repoRoot, $"--no-pager diff --stat -- {quoted}", ct).ConfigureAwait(false);
        var discarded = stat.ExitCode == 0 && !string.IsNullOrWhiteSpace(stat.StdOut)
            ? stat.StdOut.TrimEnd()
            : "(no unstaged diffstat available)";

        var checkout = await RunGitAsync(repoRoot, $"checkout -- {quoted}", ct).ConfigureAwait(false);
        if (checkout.ExitCode != 0)
            return $"git checkout failed (exit {checkout.ExitCode}): {Trim(checkout.StdErr)}";

        return $"Reverted {relatives.Count} file(s) to their indexed state. Discarded working-tree changes:\n{discarded}\n"
            + "Note: staged changes (if any) were kept; only working-tree changes were discarded.";
    }

    private static void AppendGroup(StringBuilder sb, string title, List<string> entries)
    {
        if (entries.Count == 0) return;
        sb.AppendLine().Append(title).Append(':');
        foreach (var entry in entries.Take(MaxStatusEntries))
            sb.AppendLine().Append("  ").Append(entry);
        if (entries.Count > MaxStatusEntries)
            sb.AppendLine().Append($"  …and {entries.Count - MaxStatusEntries} more");
    }

    private static async Task<(string RepoRoot, string? Error)> GetRepoRootAsync(CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null)
            return (string.Empty, "No solution is open.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
            return (string.Empty, "The solution directory could not be resolved.");

        var run = await RunGitAsync(solutionDir!, "rev-parse --show-toplevel", ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return (string.Empty, $"Not a git repository (or git is unavailable): {Trim(run.StdErr)}");
        return (run.StdOut.Trim().Replace('/', '\\').TrimEnd('\\'), null);
    }

    private static async Task<ProcessRunResult> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        try
        {
            return await ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessRunResult(-1, string.Empty, $"git was not found on PATH: {ex.Message}", false);
        }
    }

    /// <summary>Resolves a tool path to a repo-relative path, refusing anything outside the repo.</summary>
    private static bool TryResolveRepoPath(string repoRoot, string path, out string relative, out string error)
    {
        relative = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Empty path in paths list.";
            return false;
        }
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path));
        }
        catch (Exception ex)
        {
            error = $"Invalid path '{path}': {ex.Message}";
            return false;
        }
        var rootWithSep = repoRoot.TrimEnd('\\') + "\\";
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Path '{path}' is outside the repository ({repoRoot}).";
            return false;
        }
        relative = full.Substring(rootWithSep.Length).Replace('\\', '/');
        return true;
    }

    private static string Trim(string text)
    {
        var t = text.Trim();
        return t.Length <= 500 ? t : t.Substring(0, 500) + "…";
    }
}