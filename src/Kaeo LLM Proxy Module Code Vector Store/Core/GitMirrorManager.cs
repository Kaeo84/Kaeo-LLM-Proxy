using Kaeo.LlmProxy.Core.Modules;
using LibGit2Sharp;

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
        _timer = new System.Threading.Timer(_ => _ = SyncAllMirrorsAsync(), null, interval, interval);
    }

    public void StopTimer() { _timer?.Dispose(); _timer = null; }

    public async Task<MirrorRegistration> RegisterMirrorAsync(string collectionName, string remoteUrl, string branch, string? credentialName, CancellationToken ct, string? mirrorPath = null, string? pathPrefix = null, string sourceKind = MirrorRegistration.SourceKindGit, string? sourcePath = null)
    {
        var mirror = _repository.UpsertMirror(collectionName, remoteUrl, branch, credentialName, mirrorPath, pathPrefix, sourceKind, sourcePath);
        await SyncMirrorAsync(mirror, ct);
        return mirror;
    }

    public async Task SyncMirrorAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        _activity?.Log("sync_start", mirror.CollectionName, $"Syncing {mirror.DescribeSource}");
        try
        {
            if (mirror.IsLocalDirectory)
            {
                await IndexLocalDirectoryAsync(mirror, ct);
            }
            else
            {
                var mirrorPath = ResolveMirrorPath(mirror);
                if (!Repository.IsValid(mirrorPath))
                {
                    var cloneOptions = new CloneOptions { BranchName = mirror.Branch, FetchOptions = { TagFetchMode = TagFetchMode.None } };
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
                    var fetchOptions = new FetchOptions { TagFetchMode = TagFetchMode.None };
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
                await IndexMirrorFilesAsync(mirror, mirrorPath, ct);
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

    public async Task IndexMirrorFilesAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        if (mirror.IsLocalDirectory)
        {
            await IndexLocalDirectoryAsync(mirror, ct);
            return;
        }
        var mirrorPath = ResolveMirrorPath(mirror);
        if (!Repository.IsValid(mirrorPath))
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror not yet cloned. Run Sync first.");
            return;
        }
        await IndexMirrorFilesAsync(mirror, mirrorPath, ct);
    }

    private Task IndexMirrorFilesAsync(MirrorRegistration mirror, string mirrorPath, CancellationToken ct)
    {
        using var repo = new Repository(mirrorPath);
        if (repo.Head?.Tip?.Tree is not { } tree)
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror has no HEAD commit to index.");
            return Task.CompletedTask;
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// Indexes the files under a local directory or file-share mirror, keyed by path relative
    /// to the root. Re-scans on every sync so local edits are picked up without a git push.
    /// </summary>
    private async Task IndexLocalDirectoryAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        var root = ResolveSourcePath(mirror);
        if (!Directory.Exists(root))
        {
            _activity?.Log("index_error", mirror.CollectionName, $"Directory not found: {root}");
            return;
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
            return;
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
                var content = await File.ReadAllTextAsync(fullPath, ct);
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
        string basePath = string.IsNullOrWhiteSpace(mirror.MirrorPath)
            ? Path.Combine(_moduleDataDir, "mirrors")
            : mirror.MirrorPath;
        string resolvedBasePath = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(_moduleDataDir, basePath);
        string mirrorPath = Path.Combine(resolvedBasePath, mirror.CollectionName);
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
        try
        {
            var mirrors = _repository.LoadMirrors();
            foreach (var mirror in mirrors) await SyncMirrorAsync(mirror, CancellationToken.None);
        }
        catch (Exception ex) { _activity?.Log("error", "", $"Mirror sync cycle failed: {ex.Message}"); }
    }
}

