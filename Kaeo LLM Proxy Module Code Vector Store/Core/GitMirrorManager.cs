using Kaeo.LlmProxy.Core.Modules;
using LibGit2Sharp;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class GitMirrorManager
{
    private readonly string _moduleDataDir;
    private readonly CodeVectorRepository _repository;
    private readonly IndexingEngine _indexer;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly ISecretProvider _secrets;
    private System.Threading.Timer? _timer;

    public GitMirrorManager(string moduleDataDir, CodeVectorRepository repository, IndexingEngine indexer, CodeVectorSettings settings, CodeVectorActivityLogger? activity, ISecretProvider secrets)
     {
         _moduleDataDir = moduleDataDir;
         _repository = repository;
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

    public async Task<MirrorRegistration> RegisterMirrorAsync(string collectionName, string remoteUrl, string branch, string? credentialName, CancellationToken ct, string? mirrorPath = null, string? pathPrefix = null)
    {
        var mirror = _repository.UpsertMirror(collectionName, remoteUrl, branch, credentialName, mirrorPath, pathPrefix);
        await SyncMirrorAsync(mirror, ct);
        return mirror;
    }

    public async Task SyncMirrorAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        _activity?.Log("sync_start", mirror.CollectionName, $"Syncing {mirror.RemoteUrl} [{mirror.Branch}]");
        try
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
        var mirrorPath = ResolveMirrorPath(mirror);
        if (!Repository.IsValid(mirrorPath))
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror not yet cloned. Run Sync first.");
            return;
        }
        await IndexMirrorFilesAsync(mirror, mirrorPath, ct);
    }

    private async Task IndexMirrorFilesAsync(MirrorRegistration mirror, string mirrorPath, CancellationToken ct)
    {
        using var repo = new Repository(mirrorPath);
        var workDir = repo.Info.WorkingDirectory;
        var trackedFiles = new List<string>();
        WalkTree(repo.Head!.Tip.Tree, string.Empty, trackedFiles);

        if (!string.IsNullOrWhiteSpace(mirror.PathPrefix))
        {
            var prefix = mirror.PathPrefix.TrimEnd('/');
            trackedFiles = trackedFiles.Where(f => f == prefix || f.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList();
        }

        int queued = 0, skipped = 0;
        foreach (var relPath in trackedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(workDir, relPath);
            if (!File.Exists(fullPath)) { skipped++; continue; }
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > _settings.MaxFileSizeKb * 1024) { _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"File too large ({fileInfo.Length / 1024} KB)"); skipped++; continue; }
            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                // onlyIfChanged skips files whose content hash matches the stored hash, so a
                // sync cycle only queues what actually changed (and nothing at all when the
                // engine is stopped) instead of re-queueing every file each interval.
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
        _activity?.Log("sync_complete", mirror.CollectionName, $"Discovered {trackedFiles.Count} files, queued {queued}, skipped {skipped}");
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

    private static void WalkTree(Tree tree, string prefix, List<string> files)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    files.Add(path);
                    break;
                case TreeEntryTargetType.Tree:
                    if (entry.Target is Tree subtree)
                        WalkTree(subtree, path, files);
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

