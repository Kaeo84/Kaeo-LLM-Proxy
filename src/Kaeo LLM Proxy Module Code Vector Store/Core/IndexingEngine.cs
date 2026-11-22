using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Serilog;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class IndexingEngine
{
    private readonly CodeVectorDatabase _db;
    private readonly IEmbeddingBackend _backend;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly Channel<IndexJob> _queue;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<QueueItemInfo> _pendingJobs = new();
    private Task[]? _workers;
    private SemaphoreSlim? _embedSemaphore;
    private volatile QueueItemInfo? _currentJob;
    private volatile string? _stopReason;

    public IndexingEngine(CodeVectorDatabase db, IEmbeddingBackend backend, CodeVectorSettings settings, CodeVectorActivityLogger? activity)
    {
        _db = db;
        _backend = backend;
        _settings = settings;
        _activity = activity;
        _queue = Channel.CreateUnbounded<IndexJob>(new UnboundedChannelOptions { SingleReader = false });
    }

    public bool IsRunning => _workers is { Length: > 0 } && _workers.Any(w => !w.IsCompleted);
    public int QueueDepth => _pendingJobs.Count;
    public int ActiveWorkerCount => _workers is null ? 0 : _workers.Count(w => !w.IsCompleted);
    public QueueItemInfo? CurrentJob => _currentJob;

    /// <summary>
    /// Optional callback wired by the module to re-walk a collection's registered mirror(s)
    /// and re-enqueue every file. Used by the Reindex job (H5); null when no mirror manager
    /// has been created yet.
    /// </summary>
    internal Func<string, CancellationToken, Task<int>>? ReindexFromSource { get; set; }

    /// <summary>Why the engine last stopped (e.g. "Stopped", "Worker faulted: ..."), or null while running.</summary>
    public string? StopReason => _stopReason;

    public IReadOnlyList<QueueItemInfo> GetQueueSnapshot() => _pendingJobs.ToArray();

    /// <summary>
    /// Starts (or restarts) the worker pool. Safe to call when already running (no-op) and after
    /// a prior stop or worker crash. Recreates the cancellation source and embedding semaphore
    /// if they were consumed by a previous stop.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning) return;
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new();
            }
            int parallelism = Math.Clamp(_settings.RemoteParallelism, 1, 16);
            _embedSemaphore ??= new SemaphoreSlim(parallelism);
            _workers = new Task[parallelism];
            for (int i = 0; i < parallelism; i++)
                _workers[i] = Task.Run(() => ProcessQueueAsync(_cts.Token));
            _stopReason = null;
            _activity?.Log("engine_start", "-", $"Engine started with {parallelism} workers");
            Log.Information("CodeVector indexing engine started ({Parallelism} workers)", parallelism);
        }
    }

    /// <summary>
    /// Stops the worker pool and records why. Disposes the embedding semaphore so the next
    /// <see cref="Start"/> recreates it. Does not dispose the embedding backend or vector
    /// database — those are owned by the module.
    /// </summary>
    public async Task StopAsync(string? reason = null)
    {
        lock (_lifecycleLock)
        {
            _stopReason = reason ?? "Stopped";
            _cts.Cancel();
        }
        if (_workers is not null)
        {
            try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { }
            _workers = null;
        }
        _embedSemaphore?.Dispose();
        _embedSemaphore = null;
        _activity?.Log("engine_stop", "-", $"Engine stopped: {reason ?? "Stopped"}");
        Log.Information("CodeVector indexing engine stopped ({Reason})", reason ?? "Stopped");
    }

    /// <summary>
    /// Drops all pending jobs from the queue to release memory. Best-effort: a job enqueued
    /// concurrently may be dropped from the snapshot or processed without showing.
    /// </summary>
    public void ClearQueue()
    {
        int drained = 0;
        while (_queue.Reader.TryRead(out _)) drained++;
        _pendingJobs.Clear();
        _activity?.Log("queue_clear", "-", $"Cleared {drained} queued job(s)");
        Log.Information("CodeVector indexing queue cleared ({Drained} job(s))", drained);
    }

    /// <summary>
    /// Enqueues a file for indexing. Returns true when the file was queued.
    /// <para>When the engine is not running the file is NOT queued (prevents the queue from
    /// growing unboundedly while the engine is down); it will be picked up on the next sync.</para>
    /// <para>When <paramref name="onlyIfChanged"/> is true the file is only queued if its content
    /// hash differs from the stored hash, so unchanged files are not re-queued on every mirror sync.</para>
    /// </summary>
    public bool EnqueueIndexFile(string collection, string path, string content, string source = "agent", bool onlyIfChanged = false)
    {
        if (!IsRunning)
        {
            _activity?.Log("skip", $"{collection}:{path}", "Engine stopped; not queued");
            return false;
        }
        if (onlyIfChanged)
        {
            string hash = ComputeSha256(content);
            string? existingHash = _db.GetFileHash(collection, path);
            if (existingHash == hash)
            {
                _activity?.Log("skip", $"{collection}:{path}", "Unchanged");
                return false;
            }
        }
        var info = new QueueItemInfo("Index", collection, path, source);
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.IndexFile, Collection = collection, Path = path, Content = content, Source = source, Info = info });
        return true;
    }

    /// <summary>
    /// Enqueues deletion of a single file's chunks (and its file row) from the collection.
    /// Used by mirror sync to drop files that no longer exist in the source. Returns true
    /// when queued; false when the engine is stopped (mirrors the <see cref="EnqueueIndexFile"/> gate).
    /// </summary>
    public bool EnqueueDeleteFile(string collection, string path)
    {
        if (!IsRunning)
        {
            _activity?.Log("skip", $"{collection}:{path}", "Engine stopped; delete not queued");
            return false;
        }
        var info = new QueueItemInfo("DeleteFile", collection, path, "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeleteFile, Collection = collection, Path = path, Info = info });
        return true;
    }

    public void EnqueueDeletePath(string collection, string pathPrefix)
    {
        var info = new QueueItemInfo("DeletePath", collection, pathPrefix, "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeletePath, Collection = collection, Path = pathPrefix, Info = info });
    }

    public void EnqueueDeleteCollection(string collection)
    {
        var info = new QueueItemInfo("DeleteCollection", collection, "(all)", "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeleteCollection, Collection = collection, Info = info });
    }

    public void EnqueueReindex(string collection)
    {
        var info = new QueueItemInfo("Reindex", collection, "(all)", "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.Reindex, Collection = collection, Info = info });
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                _currentJob = job.Info;
                try
                {
                    switch (job.Type)
                    {
                        case JobType.IndexFile: await IndexFileAsync(job.Collection!, job.Path!, job.Content!, job.Source!, ct); break;
                        case JobType.DeleteFile: DeleteFileAsync(job.Collection!, job.Path!); break;
                        case JobType.DeletePath: _db.DeleteFilesByPathPrefix(job.Collection!, job.Path!); break;
                        case JobType.DeleteCollection: _db.DeleteCollection(job.Collection!); break;
                        case JobType.Reindex: await ReindexCollectionAsync(job.Collection!, ct).ConfigureAwait(false); break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancellation requested; let the loop exit via ReadAllAsync.
                }
                catch (Exception ex)
                {
                    // A single bad job must not kill the worker; log and continue.
                    _activity?.Log("error", job.Collection ?? "", $"Indexing failed: {ex.Message}");
                    Log.Warning("CodeVector job failed ({Collection}/{Path}): {Message}", job.Collection, job.Path, ex.Message);
                }
                finally
                {
                    _pendingJobs.TryDequeue(out _);
                    _currentJob = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown via cancellation.
        }
        catch (Exception ex)
        {
            // The worker itself faulted (e.g. the channel read threw). Record why and surface it
            // so the engine stop is diagnosable rather than silent.
            _stopReason = $"Worker faulted: {ex.Message}";
            _activity?.Log("error", "", $"Index worker crashed: {ex.Message}");
            Log.Error(ex, "CodeVector index worker faulted; engine stopped");
        }
        finally
        {
            _currentJob = null;
        }
    }

    private async Task IndexFileAsync(string collection, string path, string content, string source, CancellationToken ct)
    {
        var chunker = new CodeChunker(_settings.ChunkLines, _settings.ChunkOverlapLines, _settings.MaxFileSizeKb * 1024);
        if (chunker.IsTooLarge(content)) { _activity?.Log("skip", $"{collection}:{path}", "File too large"); return; }

        var hash = ComputeSha256(content);
        var existingHash = _db.GetFileHash(collection, path);
        if (existingHash == hash) { _activity?.Log("skip", $"{collection}:{path}", "Unchanged"); return; }

        var chunks = chunker.Chunk(content);
        if (chunks.Count == 0) return;

        _activity?.Log("file_start", $"{collection}:{path}", $"Chunked into {chunks.Count} pieces, source={source}");
        _db.GetOrCreateCollection(collection, _backend.ModelName, _backend.Dimension);
        // Reserve the file row WITHOUT the new hash. If embedding fails below, the stored
        // hash stays stale/empty so the next pass re-indexes the file instead of seeing a
        // matching hash + zero chunks and skipping it forever.
        var fileId = _db.UpsertFile(collection, path, existingHash ?? string.Empty, source, 0);
        _db.DeleteFileChunks(fileId, collection);

        const int batchSize = 16;
        var batchTasks = new List<Func<Task>>();
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            int batchNum = i / batchSize + 1;

            batchTasks.Add(async () =>
            {
                _activity?.Log("embed_batch", $"{collection}:{path}", $"Embedding batch {batchNum} ({batch.Count} chunks, offset {i})");
                await _embedSemaphore!.WaitAsync(ct);
                try
                {
                    float[][] embeddings = await _backend.EmbedBatchAsync(texts, ct);
                    for (int j = 0; j < batch.Count; j++)
                    {
                        var chunk = batch[j];
                        var embedding = j < embeddings.Length ? embeddings[j] : [];
                        _db.InsertChunk(fileId, collection, chunk.Index, chunk.StartLine, chunk.EndLine, chunk.Text, embedding);
                    }
                }
                finally
                {
                    _embedSemaphore!.Release();
                }
            });
        }
        try
        {
            await Task.WhenAll(batchTasks.Select(f => f())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activity?.Log("error", $"{collection}:{path}", $"Embedding failed: {ex.Message}");
            return; // hash not committed → file is retried on the next pass
        }
        // Every chunk embedded and inserted successfully: commit the new hash now so an
        // unchanged file is skipped next time.
        _db.UpsertFile(collection, path, hash, source, chunks.Count);
        _activity?.Log("file_complete", $"{collection}:{path}", $"Indexed {chunks.Count} chunks");
    }

    private void DeleteFileAsync(string collection, string path)
    {
        _db.DeleteFile(collection, path);
        _activity?.Log("file_deleted", $"{collection}:{path}", "Removed stale file from index");
    }

    /// <summary>
    /// Full re-embedding of a collection (H5): clears the stored collection (hashes + chunks)
    /// then re-walks its registered mirror source(s) and re-enqueues every file. Mirrors the
    /// UI Reindex button so the MCP code_reindex tool actually regenerates embeddings after a
    /// model/chunking change instead of only logging.
    /// </summary>
    private async Task ReindexCollectionAsync(string collection, CancellationToken ct)
    {
        _db.DeleteCollection(collection);
        _activity?.Log("reindex", collection, "Collection cleared; re-indexing from registered sources");
        var reindex = ReindexFromSource;
        if (reindex is null)
        {
            _activity?.Log("reindex", collection, "No mirror source registered; run code_sync_repo to re-index");
            return;
        }
        int queued = await reindex(collection, ct).ConfigureAwait(false);
        _activity?.Log("reindex", collection, $"Reindex queued {queued} file(s)");
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private enum JobType { IndexFile, DeleteFile, DeletePath, DeleteCollection, Reindex }
    private sealed class IndexJob
    {
        public JobType Type { get; init; }
        public string? Collection { get; init; }
        public string? Path { get; init; }
        public string? Content { get; init; }
        public string? Source { get; init; }
        public QueueItemInfo? Info { get; init; }
    }

    internal sealed record QueueItemInfo(string Operation, string Collection, string Path, string Source);
}
