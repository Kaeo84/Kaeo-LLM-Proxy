using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>Result of one git invocation supplied by the host.</summary>
public sealed class GitRunResult
{
    public GitRunResult(int exitCode, string stdOut, string stdErr)
    {
        ExitCode = exitCode;
        StdOut = stdOut ?? string.Empty;
        StdErr = stdErr ?? string.Empty;
    }

    public int ExitCode { get; }
    public string StdOut { get; }
    public string StdErr { get; }
}

/// <summary>
/// Runs git with the given arguments. The host supplies the process runner so the
/// engine stays platform-agnostic (netstandard2.0, no Windows-only APIs).
/// </summary>
public delegate Task<GitRunResult> GitRunner(string arguments, CancellationToken ct);

/// <summary>One checkpoint (shadow-git commit) entry.</summary>
public sealed class CheckpointInfo
{
    public CheckpointInfo(string id, DateTimeOffset timestamp, string label)
    {
        Id = id;
        Timestamp = timestamp;
        Label = label;
    }

    public string Id { get; }
    public DateTimeOffset Timestamp { get; }
    public string Label { get; }
}

/// <summary>Outcome of creating a checkpoint.</summary>
public sealed class CheckpointCreateResult
{
    public bool Success { get; private set; }
    public string? Error { get; private set; }
    public CheckpointInfo? Checkpoint { get; private set; }

    /// <summary>True when the worktree matched the latest checkpoint and nothing was committed.</summary>
    public bool Unchanged { get; private set; }

    /// <summary>True when retention pruning wiped older history before this checkpoint.</summary>
    public bool PrunedOlder { get; private set; }

    public static CheckpointCreateResult Fail(string error) => new() { Success = false, Error = error };

    public static CheckpointCreateResult Ok(CheckpointInfo checkpoint, bool unchanged, bool prunedOlder) =>
        new() { Success = true, Checkpoint = checkpoint, Unchanged = unchanged, PrunedOlder = prunedOlder };
}

/// <summary>Outcome of listing checkpoints.</summary>
public sealed class CheckpointListResult
{
    public bool Success { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<CheckpointInfo> Items { get; private set; } = Array.Empty<CheckpointInfo>();

    public static CheckpointListResult Fail(string error) => new() { Success = false, Error = error };

    public static CheckpointListResult Ok(IReadOnlyList<CheckpointInfo> items) => new() { Success = true, Items = items };
}

/// <summary>Outcome of diffing the worktree against a checkpoint.</summary>
public sealed class CheckpointDiffResult
{
    public bool Success { get; private set; }
    public string? Error { get; private set; }
    public string DiffStat { get; private set; } = string.Empty;

    public static CheckpointDiffResult Fail(string error) => new() { Success = false, Error = error };

    public static CheckpointDiffResult Ok(string diffStat) => new() { Success = true, DiffStat = diffStat };
}

/// <summary>Outcome of rolling the worktree back to a checkpoint.</summary>
public sealed class CheckpointRollbackResult
{
    public bool Success { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Diffstat of what changed relative to the checkpoint before the rollback.</summary>
    public string DiffStat { get; private set; } = string.Empty;

    /// <summary>Files created after the checkpoint (kept unless deletion was requested).</summary>
    public IReadOnlyList<string> NewFiles { get; private set; } = Array.Empty<string>();

    /// <summary>New files actually deleted (only when deletion was requested).</summary>
    public IReadOnlyList<string> DeletedNewFiles { get; private set; } = Array.Empty<string>();

    /// <summary>New files that could not be deleted (locked, etc.).</summary>
    public IReadOnlyList<string> DeleteFailures { get; private set; } = Array.Empty<string>();

    /// <summary>Non-fatal warning (e.g. the post-rollback bookkeeping commit failed).</summary>
    public string? Warning { get; private set; }

    public static CheckpointRollbackResult Fail(string error) => new() { Success = false, Error = error };

    public static CheckpointRollbackResult Ok(
        string diffStat,
        IReadOnlyList<string> newFiles,
        IReadOnlyList<string> deletedNewFiles,
        IReadOnlyList<string> deleteFailures,
        string? warning) =>
        new()
        {
            Success = true,
            DiffStat = diffStat,
            NewFiles = newFiles,
            DeletedNewFiles = deletedNewFiles,
            DeleteFailures = deleteFailures,
            Warning = warning,
        };
}

/// <summary>
/// Lightweight worktree snapshots via a shadow git repository stored OUTSIDE the user's
/// repo. The user's real .git (index, HEAD, history) is never touched: every command runs
/// with an explicit --git-dir/--work-tree pair pointing at the shadow store.
/// Cross-platform: paths use Path.Combine, storage lives under the caller-supplied
/// per-user directory, and git is invoked through an injectable runner.
/// </summary>
public sealed class CheckpointEngine
{
    private const string ShadowDirName = "shadow.git";

    private static readonly string[] DefaultExcludes =
    {
        ".git/",
        ".vs/",
        "bin/",
        "obj/",
        "node_modules/",
        ".kaeo/",
    };

    private readonly string _storeDir;
    private readonly string _workTree;
    private readonly GitRunner _git;
    private readonly int _retention;
    private readonly string[] _extraExcludes;

    public CheckpointEngine(
        string storeDirectory,
        string workTree,
        GitRunner git,
        int retention = 20,
        IEnumerable<string>? extraExcludes = null)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory)) throw new ArgumentException("Store directory is required.", nameof(storeDirectory));
        if (string.IsNullOrWhiteSpace(workTree)) throw new ArgumentException("Worktree is required.", nameof(workTree));
        _storeDir = Path.GetFullPath(storeDirectory);
        _workTree = Path.GetFullPath(workTree);
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _retention = Math.Max(1, retention);
        _extraExcludes = (extraExcludes ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim())
            .Where(e => e.Length > 0)
            .ToArray();
    }

    private string ShadowDir => Path.Combine(_storeDir, ShadowDirName);

    private string Prefix =>
        "--git-dir=\"" + ShadowDir + "\" --work-tree=\"" + _workTree + "\" -c core.quotepath=false";

    /// <summary>Creates a checkpoint of the current worktree. Returns the existing HEAD when nothing changed.</summary>
    public async Task<CheckpointCreateResult> CreateAsync(string? label, CancellationToken ct = default)
    {
        var initError = await EnsureInitializedAsync(ct).ConfigureAwait(false);
        if (initError != null) return CheckpointCreateResult.Fail(initError);

        var pruned = false;
        var count = await CountCommitsAsync(ct).ConfigureAwait(false);
        if (count >= _retention)
        {
            // Retention reached: wipe the shadow history and start fresh. The shadow
            // store is disposable by design; the user's real git is never affected.
            try
            {
                // Git marks object files read-only on Windows; clear attributes first.
                foreach (var file in Directory.EnumerateFiles(ShadowDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(ShadowDir, recursive: true);
            }
            catch (IOException ex)
            {
                return CheckpointCreateResult.Fail("Could not prune old checkpoints (store in use): " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CheckpointCreateResult.Fail("Could not prune old checkpoints (access denied): " + ex.Message);
            }

            initError = await EnsureInitializedAsync(ct).ConfigureAwait(false);
            if (initError != null) return CheckpointCreateResult.Fail(initError);
            pruned = true;
            count = 0;
        }

        var add = await _git(Prefix + " add -A", ct).ConfigureAwait(false);
        if (add.ExitCode != 0) return CheckpointCreateResult.Fail("git add failed: " + FirstLine(add.StdErr));

        var staged = await _git(Prefix + " diff --cached --quiet", ct).ConfigureAwait(false);
        if (staged.ExitCode != 0 && staged.ExitCode != 1)
            return CheckpointCreateResult.Fail("git diff failed: " + FirstLine(staged.StdErr));

        if (staged.ExitCode == 0 && count > 0)
        {
            var head = await ReadHeadAsync(ct).ConfigureAwait(false);
            if (head == null) return CheckpointCreateResult.Fail("Could not read the latest checkpoint.");
            return CheckpointCreateResult.Ok(head, unchanged: true, prunedOlder: pruned);
        }

        var safeLabel = SanitizeLabel(label);
        var commit = await _git(Prefix + " commit --allow-empty -m \"" + safeLabel + "\"", ct).ConfigureAwait(false);
        if (commit.ExitCode != 0) return CheckpointCreateResult.Fail("git commit failed: " + FirstLine(commit.StdErr));

        var info = await ReadHeadAsync(ct).ConfigureAwait(false);
        if (info == null) return CheckpointCreateResult.Fail("Checkpoint committed but could not be read back.");
        return CheckpointCreateResult.Ok(info, unchanged: false, prunedOlder: pruned);
    }

    /// <summary>Lists checkpoints, newest first.</summary>
    public async Task<CheckpointListResult> ListAsync(int max = 20, CancellationToken ct = default)
    {
        if (!Directory.Exists(ShadowDir)) return CheckpointListResult.Ok(Array.Empty<CheckpointInfo>());
        max = Math.Max(1, max);
        var log = await _git(Prefix + " log --format=%h%x09%ct%x09%s -n " + max.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (log.ExitCode != 0)
        {
            if (log.StdErr.IndexOf("does not have any commits", StringComparison.OrdinalIgnoreCase) >= 0)
                return CheckpointListResult.Ok(Array.Empty<CheckpointInfo>());
            return CheckpointListResult.Fail("git log failed: " + FirstLine(log.StdErr));
        }

        var items = new List<CheckpointInfo>();
        foreach (var line in log.StdOut.Split('\n'))
        {
            var info = ParseInfo(line.TrimEnd('\r'));
            if (info != null) items.Add(info);
        }
        return CheckpointListResult.Ok(items);
    }

    /// <summary>Diffstat of the current worktree against a checkpoint.</summary>
    public async Task<CheckpointDiffResult> DiffAsync(string id, CancellationToken ct = default)
    {
        var error = await ValidateIdAsync(id, ct).ConfigureAwait(false);
        if (error != null) return CheckpointDiffResult.Fail(error);

        var add = await _git(Prefix + " add -A", ct).ConfigureAwait(false);
        if (add.ExitCode != 0) return CheckpointDiffResult.Fail("git add failed: " + FirstLine(add.StdErr));

        var diff = await _git(Prefix + " diff --cached --stat " + id, ct).ConfigureAwait(false);
        if (diff.ExitCode != 0) return CheckpointDiffResult.Fail("git diff failed: " + FirstLine(diff.StdErr));
        var text = diff.StdOut.Trim();
        return CheckpointDiffResult.Ok(text.Length == 0 ? "(no differences)" : text);
    }

    /// <summary>
    /// Restores tracked files to their state at the checkpoint. Files created after the
    /// checkpoint are listed but kept unless <paramref name="deleteNewFiles"/> is true.
    /// A bookkeeping commit records the post-rollback state in the shadow history.
    /// </summary>
    public async Task<CheckpointRollbackResult> RollbackAsync(string id, bool deleteNewFiles = false, CancellationToken ct = default)
    {
        var error = await ValidateIdAsync(id, ct).ConfigureAwait(false);
        if (error != null) return CheckpointRollbackResult.Fail(error);

        var add = await _git(Prefix + " add -A", ct).ConfigureAwait(false);
        if (add.ExitCode != 0) return CheckpointRollbackResult.Fail("git add failed: " + FirstLine(add.StdErr));

        var added = await _git(Prefix + " diff --cached --name-only --diff-filter=A " + id, ct).ConfigureAwait(false);
        if (added.ExitCode != 0) return CheckpointRollbackResult.Fail("git diff failed: " + FirstLine(added.StdErr));
        var newFiles = added.StdOut.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        var stat = await _git(Prefix + " diff --cached --stat " + id, ct).ConfigureAwait(false);
        var diffStat = stat.ExitCode == 0 ? stat.StdOut.Trim() : string.Empty;
        if (diffStat.Length == 0) diffStat = "(no differences)";

        var checkout = await _git(Prefix + " checkout " + id + " -- .", ct).ConfigureAwait(false);
        if (checkout.ExitCode != 0) return CheckpointRollbackResult.Fail("git checkout failed: " + FirstLine(checkout.StdErr));

        var deleted = new List<string>();
        var failures = new List<string>();
        if (deleteNewFiles)
        {
            foreach (var rel in newFiles)
            {
                var full = Path.Combine(_workTree, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!IsUnderWorkTree(full))
                {
                    failures.Add(rel);
                    continue;
                }
                try
                {
                    if (File.Exists(full)) File.Delete(full);
                    deleted.Add(rel);
                }
                catch (IOException)
                {
                    failures.Add(rel);
                }
                catch (UnauthorizedAccessException)
                {
                    failures.Add(rel);
                }
            }
        }

        string? warning = null;
        var readd = await _git(Prefix + " add -A", ct).ConfigureAwait(false);
        var commit = readd.ExitCode == 0
            ? await _git(Prefix + " commit --allow-empty -m \"rollback to " + id + "\"", ct).ConfigureAwait(false)
            : readd;
        if (commit.ExitCode != 0)
            warning = "Rollback applied, but the shadow bookkeeping commit failed: " + FirstLine(commit.StdErr);

        return CheckpointRollbackResult.Ok(diffStat, newFiles, deleted, failures, warning);
    }

    private async Task<string?> EnsureInitializedAsync(CancellationToken ct)
    {
        if (Directory.Exists(ShadowDir) && File.Exists(Path.Combine(ShadowDir, "HEAD")))
            return null;

        try
        {
            Directory.CreateDirectory(_storeDir);
        }
        catch (IOException ex)
        {
            return "Could not create checkpoint store: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "Could not create checkpoint store (access denied): " + ex.Message;
        }

        var init = await _git("init --bare \"" + ShadowDir + "\"", ct).ConfigureAwait(false);
        if (init.ExitCode != 0) return "git init failed: " + FirstLine(init.StdErr);

        try
        {
            var infoDir = Path.Combine(ShadowDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludes = DefaultExcludes.Concat(_extraExcludes).ToArray();
            File.WriteAllLines(Path.Combine(infoDir, "exclude"), excludes);
        }
        catch (IOException ex)
        {
            return "Could not write checkpoint excludes: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            return "Could not write checkpoint excludes (access denied): " + ex.Message;
        }

        // Shadow commits need an identity; keep it local to the shadow config so the
        // user's global git identity is never read or modified. autocrlf off preserves
        // byte fidelity of snapshots regardless of the user's global settings.
        string[][] config =
        {
            new[] { "user.name", "Kaeo Checkpoints" },
            new[] { "user.email", "checkpoints@kaeo.local" },
            new[] { "core.autocrlf", "false" },
            new[] { "core.safecrlf", "false" },
        };
        foreach (var pair in config)
        {
            var cfg = await _git(Prefix + " config " + pair[0] + " \"" + pair[1] + "\"", ct).ConfigureAwait(false);
            if (cfg.ExitCode != 0) return "git config failed: " + FirstLine(cfg.StdErr);
        }

        return null;
    }

    private async Task<int> CountCommitsAsync(CancellationToken ct)
    {
        var count = await _git(Prefix + " rev-list --count HEAD", ct).ConfigureAwait(false);
        if (count.ExitCode != 0) return 0;
        return int.TryParse(count.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private async Task<CheckpointInfo?> ReadHeadAsync(CancellationToken ct)
    {
        var log = await _git(Prefix + " log -1 --format=%h%x09%ct%x09%s", ct).ConfigureAwait(false);
        if (log.ExitCode != 0) return null;
        return ParseInfo(log.StdOut.Trim());
    }

    private async Task<string?> ValidateIdAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id)) return "Invalid checkpoint id.";
        if (!Directory.Exists(ShadowDir)) return "No checkpoints exist yet.";
        var type = await _git(Prefix + " cat-file -t " + id, ct).ConfigureAwait(false);
        if (type.ExitCode != 0 || type.StdOut.Trim() != "commit")
            return "Unknown checkpoint id: " + id;
        return null;
    }

    /// <summary>Checkpoint ids are short hex hashes; reject anything else to keep git arguments safe.</summary>
    internal static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length < 4 || id.Length > 40) return false;
        foreach (var c in id)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    private bool IsUnderWorkTree(string fullPath)
    {
        var root = _workTree.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static CheckpointInfo? ParseInfo(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 3) return null;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) return null;
        var label = string.Join("\t", parts.Skip(2));
        return new CheckpointInfo(parts[0], DateTimeOffset.FromUnixTimeSeconds(unix), label);
    }

    private static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "checkpoint";
        var chars = label.Where(c => c != '"' && c != '\\' && !char.IsControl(c)).ToArray();
        var text = new string(chars).Trim();
        if (text.Length > 200) text = text.Substring(0, 200);
        return text.Length == 0 ? "checkpoint" : text;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no output)";
        var line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length > 300 ? line.Substring(0, 300) : line;
    }
}