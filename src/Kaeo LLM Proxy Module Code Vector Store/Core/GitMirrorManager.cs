using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Kaeo.LlmProxy.Core.Modules;
using LibGit2Sharp;
using Serilog;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class GitMirrorManager
{
    private readonly string _moduleDataDir;
    private readonly CodeVectorRepository _repository;
    private readonly CodeVectorDatabase _vectorDb;
    private readonly IndexingEngine _indexer;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly ISecretProvider _secrets;
    private System.Threading.Timer? _timer;
    private int _cycleRunning;
    private Task? _cycleTask;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mirrorGates = new(StringComparer.Ordinal);

    /// <summary>Strict allow-list for collection names used as mirror directory names (H4).</summary>
    private static readonly Regex CollectionNamePattern = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    public GitMirrorManager(string moduleDataDir, CodeVectorRepository repository, CodeVectorDatabase vectorDb, IndexingEngine indexer, CodeVectorSettings settings, CodeVectorActivityLogger? activity, ISecretProvider secrets)
     {
         _moduleDataDir = moduleDataDir;
         _repository = repository;
         _vectorDb = vectorDb;
         _indexer = indexer;
         _settings = settings;
         _activity = activity;
         _secrets = secrets;
     }

    public void StartTimer()
    {
        if (_timer is not null || _settings.GitSyncIntervalMinutes <= 0) return;
        var interval = TimeSpan.FromMinutes(_settings.GitSyncIntervalMinutes);
        _timer = new System.Threading.Timer(_ => OnTimerTick(), null, interval, interval);
    }

    /// <summary>
    /// Timer callback. Skips the tick when a previous sync cycle is still running so long
    /// clones/fetches never overlap themselves (H1). The cycle task is tracked so
    /// <see cref="StopTimerAsync"/> can await it before the manager is torn down.
    /// </summary>
    private void OnTimerTick()
    {
        if (Interlocked.CompareExchange(ref _cycleRunning, 1, 0) != 0) return;
        _cycleTask = RunCycleAsync();
    }

    private async Task RunCycleAsync()
    {
        try
        {
            await SyncAllMirrorsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activity?.Log("error", "", $"Mirror sync cycle failed: {ex}");
            Log.Warning(ex, "CodeVector mirror sync cycle failed");
        }
        finally
        {
            Interlocked.Exchange(ref _cycleRunning, 0);
        }
    }

    public void StopTimer() { var t = _timer; _timer = null; t?.Dispose(); }

    /// <summary>Stops the timer and awaits any in-flight sync cycle so callers can safely
    /// dispose the database/engine the cycle writes to (H1/C1).</summary>
    public async Task StopTimerAsync()
    {
        StopTimer();
        var task = _cycleTask;
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch { /* cycle already logged its own errors */ }
        }
    }

    /// <summary>
    /// Validates a collection name before it is used as a mirror directory name or DB key.
    /// Rejects empty names, path separators, traversal segments and anything outside the
    /// strict allow-list, so a semi-trusted MCP client cannot escape the mirrors root (H4).
    /// </summary>
    internal static void ValidateCollectionName(string collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("Collection name must not be empty.", nameof(collectionName));
        if (collectionName is "." or ".."
            || collectionName.Contains('/') || collectionName.Contains('\\')
            || collectionName.Contains("..")
            || !CollectionNamePattern.IsMatch(collectionName))
            throw new ArgumentException(
                $"Invalid collection name '{collectionName}'. Use 1-100 characters from A-Z, a-z, 0-9, '.', '_', '-' (no path separators or '..').",
                nameof(collectionName));
    }

    public async Task<MirrorRegistration> RegisterMirrorAsync(string collectionName, string remoteUrl, string branch, string? credentialName, CancellationToken ct, string? mirrorPath = null, string? pathPrefix = null, string sourceKind = MirrorRegistration.SourceKindGit, string? sourcePath = null)
    {
        ValidateCollectionName(collectionName);
        var mirror = _repository.UpsertMirror(collectionName, remoteUrl, branch, credentialName, mirrorPath, pathPrefix, sourceKind, sourcePath);
        await SyncMirrorAsync(mirror, ct).ConfigureAwait(false);
        return mirror;
    }

    public async Task SyncMirrorAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        // Per-mirror gate serializes the timer, the UI Sync button and MCP code_sync_repo so
        // libgit2 never sees concurrent writers on one repository (H1).
        var gate = _mirrorGates.GetOrAdd(mirror.CollectionName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Clone/fetch/tree-walk are synchronous native calls; run them on a background
            // thread so the WinForms UI thread (or an MCP request thread) never blocks (H2).
            await Task.Run(() => SyncMirrorCoreAsync(mirror, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SyncMirrorCoreAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        _activity?.Log("sync_start", mirror.CollectionName, $"Syncing {mirror.DescribeSource}");
        try
        {
            if (mirror.IsLocalDirectory)
            {
                await IndexLocalDirectoryAsync(mirror, ct).ConfigureAwait(false);
            }
            else
            {
                var mirrorPath = ResolveMirrorPath(mirror);
                if (!Repository.IsValid(mirrorPath))
                {
                    var cloneOptions = new CloneOptions
                    {
                        BranchName = mirror.Branch,
                        FetchOptions =
                        {
                            TagFetchMode = TagFetchMode.None,
                            // Returning false aborts the native transfer, giving the caller a
                            // working cancellation hook during long clones (H2).
                            OnTransferProgress = _ => !ct.IsCancellationRequested,
                        },
                    };
                    if (!string.IsNullOrWhiteSpace(mirror.CredentialName))
                    {
                        var cred = _secrets.ResolveCredential(mirror.CredentialName);
                        if (cred is not null) cloneOptions.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = cred.Username ?? "git", Password = cred.Secret };
                    }
                    Repository.Clone(mirror.RemoteUrl, mirrorPath, cloneOptions);
                    _activity?.Log("clone", mirror.CollectionName, $"Cloned {mirror.RemoteUrl}");
                }
                else
                {
                    using var repo = new Repository(mirrorPath);
                    var fetchOptions = new FetchOptions
                    {
                        TagFetchMode = TagFetchMode.None,
                        OnTransferProgress = _ => !ct.IsCancellationRequested,
                    };
                    if (!string.IsNullOrWhiteSpace(mirror.CredentialName))
                    {
                        var cred = _secrets.ResolveCredential(mirror.CredentialName);
                        if (cred is not null) fetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = cred.Username ?? "git", Password = cred.Secret };
                    }
                    Commands.Fetch(repo, "origin", [mirror.Branch], fetchOptions, "fetch");
                    var branchRef = repo.Branches[$"origin/{mirror.Branch}"];
                    if (branchRef is not null)
                    {
                        var localBranch = repo.Branches[mirror.Branch];
                        if (localBranch is not null) repo.Refs.UpdateTarget(localBranch.Reference, branchRef.Tip.Id);
                    }
                    _activity?.Log("fetch", mirror.CollectionName, "Fetched latest");
                }
                IndexMirrorFilesAsync(mirror, mirrorPath, ct);
            }
            _repository.UpdateMirrorSync(mirror.Id, DateTime.UtcNow.ToString("o"), "success");
            _activity?.Log("sync_success", mirror.CollectionName, "Mirror synced successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Full exception (type, inner exceptions, stack) goes to the MCP activity log so
            // native load failures and other unexpected errors are diagnosable in the Logs tab.
            _activity?.Log("error", mirror.CollectionName, $"Mirror sync failed: {ex}");
            _repository.UpdateMirrorSync(mirror.Id, null, $"failed: {ex.Message}");
            throw;
        }
    }

    public async Task<int> IndexMirrorFilesAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        var gate = _mirrorGates.GetOrAdd(mirror.CollectionName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => IndexMirrorFilesCoreAsync(mirror, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> IndexMirrorFilesCoreAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        if (mirror.IsLocalDirectory)
            return await IndexLocalDirectoryAsync(mirror, ct).ConfigureAwait(false);
        var mirrorPath = ResolveMirrorPath(mirror);
        if (!Repository.IsValid(mirrorPath))
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror not yet cloned. Run Sync first.");
            return 0;
        }
        return await Task.Run(() => IndexMirrorFilesAsync(mirror, mirrorPath, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-walks every registered mirror for a collection and re-enqueues all files. Used by
    /// the engine's Reindex job after the collection's stored hashes/chunks were cleared (H5).
    /// Returns the number of files queued.
    /// </summary>
    public async Task<int> ReindexCollectionAsync(string collection, CancellationToken ct)
    {
        int queued = 0;
        foreach (var mirror in _repository.LoadMirrors())
        {
            if (!string.Equals(mirror.CollectionName, collection, StringComparison.Ordinal)) continue;
            queued += await IndexMirrorFilesAsync(mirror, ct).ConfigureAwait(false);
        }
        return queued;
    }

    private int IndexMirrorFilesAsync(MirrorRegistration mirror, string mirrorPath, CancellationToken ct)
    {
        using var repo = new Repository(mirrorPath);
        if (repo.Head?.Tip?.Tree is not { } tree)
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror has no HEAD commit to index.");
            return 0;
        }

        // Read committed content directly from the git objects (the tip's tree), NOT the
        // on-disk working directory. A `git fetch` updates refs but never touches the
        // working tree, so reading from disk would index stale content and never pick up
        // newly added files. Reading the blobs guarantees the index always matches the tip.
        var files = new List<(string Path, Blob Blob)>();
        CollectFiles(tree, string.Empty, files);

        if (!string.IsNullOrWhiteSpace(mirror.PathPrefix))
        {
            var prefix = mirror.PathPrefix.TrimEnd('/');
            files = files.Where(f => f.Path == prefix || f.Path.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList();
        }

        int queued = 0, skipped = 0;
        var currentPaths = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (relPath, blob) in files)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = blob.GetContentText();
            }
            catch (Exception)
            {
                _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", "Not readable as text (binary); skipped");
                skipped++;
                continue;
            }
            if (string.IsNullOrEmpty(content)) { skipped++; continue; }
            if (content.Length > _settings.MaxFileSizeKb * 1024) { _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"File too large ({content.Length / 1024} KB)"); skipped++; continue; }
            // onlyIfChanged skips files whose content hash matches the stored hash, so a
            // sync cycle only queues what actually changed (and nothing at all when the
            // engine is stopped) instead of re-queueing every file each interval.
            if (_indexer.EnqueueIndexFile(mirror.CollectionName, relPath, content, "mirror", onlyIfChanged: true))
                queued++;
            else
                skipped++;
        }
        int removed = RemoveStaleMirrorFiles(mirror, currentPaths, ct);
        _activity?.Log("sync_complete", mirror.CollectionName, $"Discovered {files.Count} files, queued {queued}, skipped {skipped}, removed {removed}");
        return queued;
    }

    /// <summary>
    /// Indexes the files under a local directory or file-share mirror, keyed by path relative
    /// to the root. Re-scans on every sync so local edits are picked up without a git push.
    /// </summary>
    private async Task<int> IndexLocalDirectoryAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        var root = ResolveSourcePath(mirror);
        if (!Directory.Exists(root))
        {
            _activity?.Log("index_error", mirror.CollectionName, $"Directory not found: {root}");
            return 0;
        }
        var rootFull = Path.GetFullPath(root);

        List<string> fullPaths;
        try
        {
            fullPaths = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _activity?.Log("index_error", mirror.CollectionName, $"Enumerate failed: {ex.Message}");
            return 0;
        }

        var relPaths = fullPaths
            .Select(full => Path.GetRelativePath(rootFull, full).Replace('\\', '/'))
            .ToList();

        if (!string.IsNullOrWhiteSpace(mirror.PathPrefix))
        {
            var prefix = mirror.PathPrefix.TrimEnd('/');
            relPaths = relPaths.Where(p => p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList();
        }

        int queued = 0, skipped = 0;
        var currentPaths = new HashSet<string>(relPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var relPath in relPaths)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(rootFull, relPath);
            if (!File.Exists(fullPath)) { skipped++; continue; }
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > _settings.MaxFileSizeKb * 1024) { _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"File too large ({fileInfo.Length / 1024} KB)"); skipped++; continue; }
            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                if (_indexer.EnqueueIndexFile(mirror.CollectionName, relPath, content, "mirror", onlyIfChanged: true))
                    queued++;
                else
                    skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"Read failed: {ex.Message}");
                skipped++;
            }
        }
        int removed = RemoveStaleMirrorFiles(mirror, currentPaths, ct);
        _activity?.Log("sync_complete", mirror.CollectionName, $"Directory {root}: discovered {relPaths.Count} files, queued {queued}, skipped {skipped}, removed {removed}");
        return queued;
    }

    /// <summary>
    /// Drops index entries for files that were previously indexed from this mirror (source
    /// "mirror") but are no longer present in the current source snapshot, so deleted/renamed
    /// files do not linger and pollute search results. Returns the number of files queued
    /// for removal (0 when the engine is stopped).
    /// </summary>
    private int RemoveStaleMirrorFiles(MirrorRegistration mirror, IReadOnlyCollection<string> currentPaths, CancellationToken ct)
    {
        int removed = 0;
        var stored = _vectorDb.ListFilePaths(mirror.CollectionName, "mirror");
        foreach (var path in stored)
        {
            ct.ThrowIfCancellationRequested();
            if (currentPaths.Contains(path)) continue;
            if (_indexer.EnqueueDeleteFile(mirror.CollectionName, path)) removed++;
        }
        return removed;
    }

    private string ResolveSourcePath(MirrorRegistration mirror)
    {
        string p = mirror.SourcePath ?? string.Empty;
        return Path.IsPathRooted(p) ? p : Path.Combine(_moduleDataDir, p);
    }

    private string ResolveMirrorPath(MirrorRegistration mirror)
    {
        ValidateCollectionName(mirror.CollectionName);
        string basePath = string.IsNullOrWhiteSpace(mirror.MirrorPath)
            ? Path.Combine(_moduleDataDir, "mirrors")
            : mirror.MirrorPath;
        string resolvedBasePath = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(_moduleDataDir, basePath);
        string mirrorPath = Path.Combine(resolvedBasePath, mirror.CollectionName);
        // Defense-in-depth: even with a validated name, confirm the resolved clone path stays
        // under the mirrors base so a custom MirrorPath cannot redirect the clone elsewhere (H4).
        string baseFull = Path.GetFullPath(resolvedBasePath);
        string mirrorFull = Path.GetFullPath(mirrorPath);
        if (!mirrorFull.StartsWith(baseFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mirror path '{mirrorFull}' escapes the mirrors base '{baseFull}'.");
        Directory.CreateDirectory(resolvedBasePath);
        return mirrorPath;
    }

    /// <summary>
    /// Walks a git tree collecting (path, blob) pairs for every blob entry. Paths are
    /// repository-relative with forward-slash separators, matching how files are keyed in
    /// the index.
    /// </summary>
    private static void CollectFiles(Tree tree, string prefix, List<(string Path, Blob Blob)> files)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    if (entry.Target is Blob blob) files.Add((path, blob));
                    break;
                case TreeEntryTargetType.Tree:
                    if (entry.Target is Tree subtree)
                        CollectFiles(subtree, path, files);
                    break;
            }
        }
    }

    private async Task SyncAllMirrorsAsync()
    {
        IReadOnlyList<MirrorRegistration> mirrors;
        try
        {
            mirrors = _repository.LoadMirrors();
        }
        catch (Exception ex)
        {
            // A shared-DB lock/sharing violation must not crash the background cycle (M8).
            _activity?.Log("error", "", $"Mirror sync cycle could not load mirrors: {ex}");
            Log.Warning(ex, "CodeVector mirror sync cycle failed to load mirrors");
            return;
        }
        foreach (var mirror in mirrors)
        {
            // One failing mirror must not starve the rest of the cycle (M8).
            try
            {
                await SyncMirrorAsync(mirror, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _activity?.Log("error", mirror.CollectionName, $"Mirror sync failed: {ex}");
                Log.Warning(ex, "CodeVector mirror sync failed for {Collection}", mirror.CollectionName);
            }
        }
    }
}

