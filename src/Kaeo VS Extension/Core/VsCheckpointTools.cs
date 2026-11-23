using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.Shell;

// DTE access is preceded by an explicit main-thread switch; the analyzer cannot prove thread
// affinity through the indirect call chain from BuiltInVsTools.
#pragma warning disable VSTHRD010

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Checkpoint tools backed by <see cref="CheckpointEngine"/>: lightweight worktree snapshots
/// in a shadow git store outside the user's repository. The user's real .git is never touched.
/// Rollback restores tracked files; files created after the snapshot are listed and only
/// deleted when explicitly requested.
/// </summary>
internal static class VsCheckpointTools
{
    private const int GitTimeoutMs = 120_000;
    private const int MaxOutputChars = 20_000;

    public static async Task<string> CreateAsync(JsonNode? args, CancellationToken ct)
    {
        var (engine, error) = await GetEngineAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var label = args?["label"]?.GetValue<string>();
        var result = await engine!.CreateAsync(label, ct).ConfigureAwait(false);
        if (!result.Success) return "Checkpoint failed: " + result.Error;

        var info = result.Checkpoint!;
        if (result.Unchanged)
            return $"No changes since checkpoint {info.Id} ({info.Label}).";
        var text = $"Checkpoint {info.Id} created: {info.Label}";
        if (result.PrunedOlder)
            text += "\nNote: retention limit reached; older checkpoint history was pruned.";
        return text;
    }

    public static async Task<string> ListAsync(JsonNode? args, CancellationToken ct)
    {
        var (engine, error) = await GetEngineAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var max = Math.Max(1, Math.Min(100, args?["max"]?.GetValue<int>() ?? 20));
        var result = await engine!.ListAsync(max, ct).ConfigureAwait(false);
        if (!result.Success) return "Checkpoint list failed: " + result.Error;
        if (result.Items.Count == 0) return "No checkpoints exist yet.";

        var sb = new StringBuilder();
        sb.AppendLine("Checkpoints (newest first):");
        foreach (var item in result.Items)
            sb.AppendLine($"  {item.Id}  {item.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {item.Label}");
        return sb.ToString();
    }

    public static async Task<string> DiffAsync(JsonNode? args, CancellationToken ct)
    {
        var id = args?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return "Missing required argument 'id' (checkpoint hash from vs_checkpoint_list).";

        var (engine, error) = await GetEngineAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var result = await engine!.DiffAsync(id!, ct).ConfigureAwait(false);
        if (!result.Success) return "Checkpoint diff failed: " + result.Error;
        var text = $"Worktree vs checkpoint {id}:\n{result.DiffStat}";
        return text.Length <= MaxOutputChars ? text : text.Substring(0, MaxOutputChars) + "\n…(truncated)";
    }

    public static async Task<string> RollbackAsync(JsonNode? args, CancellationToken ct)
    {
        var id = args?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return "Missing required argument 'id' (checkpoint hash from vs_checkpoint_list).";
        var deleteNewFiles = args?["deleteNewFiles"]?.GetValue<bool>() ?? false;

        var (engine, error) = await GetEngineAsync(ct).ConfigureAwait(true);
        if (error is not null) return error;

        var result = await engine!.RollbackAsync(id!, deleteNewFiles, ct).ConfigureAwait(false);
        if (!result.Success) return "Rollback failed: " + result.Error;

        var sb = new StringBuilder();
        sb.AppendLine($"Rolled back to checkpoint {id}. Changes discarded:");
        sb.AppendLine(result.DiffStat);
        if (result.NewFiles.Count > 0)
        {
            sb.AppendLine().AppendLine(deleteNewFiles
                ? "Files created after the checkpoint (deletion requested):"
                : "Files created after the checkpoint were KEPT (pass deleteNewFiles=true to remove):");
            foreach (var f in result.NewFiles.Take(200))
                sb.AppendLine("  " + f);
            if (result.NewFiles.Count > 200)
                sb.AppendLine($"  …and {result.NewFiles.Count - 200} more");
        }
        if (result.DeletedNewFiles.Count > 0)
            sb.AppendLine().AppendLine($"Deleted {result.DeletedNewFiles.Count} new file(s).");
        if (result.DeleteFailures.Count > 0)
        {
            sb.AppendLine().AppendLine("Could not delete (locked or access denied):");
            foreach (var f in result.DeleteFailures.Take(50))
                sb.AppendLine("  " + f);
        }
        if (result.Warning is not null)
            sb.AppendLine().AppendLine("Warning: " + result.Warning);
        var text = sb.ToString();
        return text.Length <= MaxOutputChars ? text : text.Substring(0, MaxOutputChars) + "\n…(truncated)";
    }

    /// <summary>
    /// Auto-checkpoint hook for the agent runtime. Never throws: any failure is returned as an
    /// honest notice so the turn can proceed without a checkpoint.
    /// </summary>
    public static async Task<string> TryAutoCheckpointAsync(CancellationToken ct)
    {
        try
        {
            var (engine, error) = await GetEngineAsync(ct).ConfigureAwait(true);
            if (error is not null) return "Checkpoint unavailable: " + error;

            var result = await engine!.CreateAsync("auto: turn start", ct).ConfigureAwait(false);
            if (!result.Success) return "Checkpoint failed: " + result.Error;
            var info = result.Checkpoint!;
            return result.Unchanged
                ? $"Checkpoint {info.Id} (no changes since last snapshot)."
                : $"Checkpoint {info.Id} created (auto, turn start).";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Warn("Auto-checkpoint failed: " + ex.Message);
            return "Checkpoint unavailable: " + ex.Message;
        }
    }

    private static async Task<(CheckpointEngine? Engine, string? Error)> GetEngineAsync(CancellationToken ct)
    {
        var dte = await BuiltInVsTools.GetOpenDteAsync(ct);
        if (dte is null)
            return (null, "No solution is open.");
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
        if (string.IsNullOrEmpty(solutionDir) || !Directory.Exists(solutionDir))
            return (null, "The solution directory could not be resolved.");

        var rootRun = await RunGitAsync(solutionDir!, "rev-parse --show-toplevel", ct).ConfigureAwait(false);
        if (rootRun.ExitCode != 0)
            return (null, $"Not a git repository (or git is unavailable): {Trim(rootRun.StdErr)}");
        var repoRoot = Path.GetFullPath(rootRun.StdOut.Trim());

        var settings = await new ExtensionSettingsStore().LoadAsync().ConfigureAwait(false);
        var defaults = settings.Defaults ?? new Defaults();
        var engine = new CheckpointEngine(
            GetStoreDirectory(repoRoot),
            repoRoot,
            (arguments, token) => RunGitAsync(repoRoot, arguments, token),
            defaults.CheckpointRetention,
            defaults.CheckpointExcludes);
        return (engine, null);
    }

    /// <summary>Per-repo shadow store under the user's local app data (never inside the repo).</summary>
    internal static string GetStoreDirectory(string repoRoot)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(repoRoot.ToLowerInvariant()));
        var hex = BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
        return Path.Combine(local, "Kaeo", "Checkpoints", hex);
    }

    private static async Task<GitRunResult> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        try
        {
            var run = await ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeoutMs, ct).ConfigureAwait(false);
            if (run.TimedOut)
                return new GitRunResult(-1, run.StdOut, "git timed out after " + (GitTimeoutMs / 1000) + "s.");
            return new GitRunResult(run.ExitCode, run.StdOut, run.StdErr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new GitRunResult(-1, string.Empty, $"git was not found on PATH: {ex.Message}");
        }
    }

    private static string Trim(string text)
    {
        var t = text.Trim();
        return t.Length <= 500 ? t : t.Substring(0, 500) + "…";
    }
}