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
    private readonly ConcurrentDictionary<QueueItemInfo, byte> _pendingJobs = new();
    // M14: dedup in-flight/queued IndexFile jobs so repeated syncs don't pile up duplicates.
    private readonly ConcurrentDictionary<(string Collection, string Path), byte> _inFlightIndex = new();
    private Task[]? _workers;
    private SemaphoreSlim? _embedSemaphore;
    // L6: one "current job" slot per worker so parallel workers don't overwrite each other.
    private readonly ConcurrentDictionary<int, QueueItemInfo> _currentJobs = new();
    private volatile string? _stopReason;

    public IndexingEngine(CodeVectorDatabase db, IEmbeddingBackend backend, CodeVectorSettings settings, CodeVectorActivityLogger? activity)
    {
        _db = db;
        _backend = backend;
        _settings = settings;
        _activity = activity;
        // M10: bounded queue — each job holds full file content, so an unbounded channel
        // could exhaust memory on a large mirror. Producers wait or drop explicitly.
        _queue = Channel.CreateBounded<IndexJob>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false
        });
    }

    public bool IsRunning => _workers is { Length: > 0 } && _workers.Any(w => !w.IsCompleted);
    public int QueueDepth => _pendingJobs.Count;
    public int ActiveWorkerCount => _workers is null ? 0 : _workers.Count(w => !w.IsCompleted);
    public QueueItemInfo? CurrentJob => _currentJobs.Values.FirstOrDefault();

    /// <summary>
    /// Optional callback wired by the module to re-walk a collection's registered mirror(s)
    /// and re-enqueue every file. Used by the Reindex job (H5); null when no mirror manager
    /// has been created yet.
    /// </summary>
    internal Func<string, CancellationToken, Task<int>>? ReindexFromSource { get; set; }

    /// <summary>Why the engine last stopped (e.g. "Stopped", "Worker faulted: ..."), or null while running.</summary>
    public string? StopReason => _stopReason;

    public IReadOnlyList<QueueItemInfo> GetQueueSnapshot() => _pendingJobs.Keys.ToArray();

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
            {
                int workerIndex = i;
                _workers[i] = Task.Run(() => ProcessQueueAsync(workerIndex, _cts.Token));
            }
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
        lock (_lifecycleLock)
        {
            // L6: don't leave a cancelled CTS lingering after stop; the next Start gets a fresh one.
            _cts.Dispose();
            _cts = new CancellationTokenSource();
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
        while (_queue.Reader.TryRead(out var job))
        {
            drained++;
            if (job.Info is not null) _pendingJobs.TryRemove(job.Info, out _);
            if (job.Type == JobType.IndexFile) _inFlightIndex.TryRemove((job.Collection!, job.Path!), out _);
        }
        _activity?.Log("queue_clear", "-", $"Cleared {drained} queued job(s)");
        Log.Information("CodeVector indexing queue cleared ({Drained} job(s))", drained);
    }

    /// <summary>
    /// Enqueues a file for indexing. Returns true when the file was queued.
    /// <para>When the engine is not running the file is NOT queued (prevents the queue from
    /// growing unboundedly while the engine is down); it will be picked up on the next sync.</para>
    /// <para>When <paramref name="onlyIfChanged"/> is true the file is only queued if its content
    /// hash differs from the stored hash, so unchanged files are not re-queued on every mirror sync.</para>
    /// <para>M10: agent pushes are non-blocking — when the bounded queue is full the file is
    /// dropped with a warning (it can be re-pushed). Mirror walks pass <paramref name="waitForCapacity"/>
    /// to block until space is available so no source file is silently lost; they run on their
    /// own task, never on a queue worker.</para>
    /// </summary>
    public bool EnqueueIndexFile(string collection, string path, string content, string source = "agent", bool onlyIfChanged = false, bool waitForCapacity = false)
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
        // M14: one queued/in-flight index job per (collection, path).
        if (!_inFlightIndex.TryAdd((collection, path), 0))
        {
            _activity?.Log("skip", $"{collection}:{path}", "Already queued");
            return false;
        }
        var info = new QueueItemInfo("Index", collection, path, source);
        _pendingJobs[info] = 0;
        var job = new IndexJob { Type = JobType.IndexFile, Collection = collection, Path = path, Content = content, Source = source, Info = info };
        bool written;
        if (waitForCapacity)
        {
            try
            {
                _queue.Writer.WriteAsync(job, _cts.Token).AsTask().GetAwaiter().GetResult();
                written = true;
            }
            catch (OperationCanceledException)
            {
                written = false;
            }
        }
        else
        {
            written = _queue.Writer.TryWrite(job);
        }
        if (!written)
        {
            _pendingJobs.TryRemove(info, out _);
            _inFlightIndex.TryRemove((collection, path), out _);
            _activity?.Log("skip", $"{collection}:{path}", "Queue full; not queued");
            Log.Warning("CodeVector queue full; dropped {Collection}/{Path}", collection, path);
            return false;
        }
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
        return TryWriteControlJob(new IndexJob { Type = JobType.DeleteFile, Collection = collection, Path = path, Info = info }, info);
    }

    public bool EnqueueDeletePath(string collection, string pathPrefix)
    {
        if (!IsRunning) { _activity?.Log("skip", collection, "Engine stopped; delete-path not queued"); return false; }
        var info = new QueueItemInfo("DeletePath", collection, pathPrefix, "-");
        return TryWriteControlJob(new IndexJob { Type = JobType.DeletePath, Collection = collection, Path = pathPrefix, Info = info }, info);
    }

    public bool EnqueueDeleteCollection(string collection)
    {
        if (!IsRunning) { _activity?.Log("skip", collection, "Engine stopped; delete-collection not queued"); return false; }
        var info = new QueueItemInfo("DeleteCollection", collection, "(all)", "-");
        return TryWriteControlJob(new IndexJob { Type = JobType.DeleteCollection, Collection = collection, Info = info }, info);
    }

    public bool EnqueueReindex(string collection)
    {
        if (!IsRunning) { _activity?.Log("skip", collection, "Engine stopped; reindex not queued"); return false; }
        var info = new QueueItemInfo("Reindex", collection, "(all)", "-");
        return TryWriteControlJob(new IndexJob { Type = JobType.Reindex, Collection = collection, Info = info }, info);
    }

    /// <summary>Non-blocking write for delete/reindex control jobs; drops with a warning when full.</summary>
    private bool TryWriteControlJob(IndexJob job, QueueItemInfo info)
    {
        _pendingJobs[info] = 0;
        if (!_queue.Writer.TryWrite(job))
        {
            _pendingJobs.TryRemove(info, out _);
            _activity?.Log("skip", job.Collection ?? "", "Queue full; control job not queued");
            Log.Warning("CodeVector queue full; dropped {Operation} for {Collection}", info.Operation, job.Collection);
            return false;
        }
        return true;
    }

    private async Task ProcessQueueAsync(int workerIndex, CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                if (job.Info is not null) _currentJobs[workerIndex] = job.Info;
                try
                {
                    switch (job.Type)
                    {
                        case JobType.IndexFile: await IndexFileAsync(job.Collection!, job.Path!, job.Content!, job.Source!, ct); break;
                        case JobType.DeleteFile: DeleteFileAsync(job.Collection!, job.Path!); break;
                        case JobType.DeletePath: _db.DeleteFilesByPathPrefix(job.Collection!, job.Path!); break;
                        case JobType.DeleteCollection: _db.DeleteCollection(job.Collection!); break;
                        case JobType.Reindex:
                            // M10: reindex produces jobs that can block on the full bounded queue.
                            // Awaiting that on a worker would deadlock a single-worker pool, so
                            // production runs on its own task with a full catch (background rule).
                            string reindexCollection = job.Collection!;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await ReindexCollectionAsync(reindexCollection, ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex)
                                {
                                    _activity?.Log("error", reindexCollection, $"Reindex failed: {ex.Message}");
                                    Log.Warning("CodeVector reindex failed ({Collection}): {Message}", reindexCollection, ex.Message);
                                }
                            }, CancellationToken.None);
                            break;
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
                    if (job.Info is not null) _pendingJobs.TryRemove(job.Info, out _);
                    if (job.Type == JobType.IndexFile) _inFlightIndex.TryRemove((job.Collection!, job.Path!), out _);
                    _currentJobs.TryRemove(workerIndex, out _);
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
            _currentJobs.TryRemove(workerIndex, out _);
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

        // M12: embed FIRST, then create/verify the collection with the real vector dimension
        // (backends may report Dimension = 0 until the first successful embedding call).
        const int batchSize = 16;
        var allEmbeddings = new float[chunks.Count][];
        var batchTasks = new List<Func<Task>>();
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int offset = i;
            var batch = chunks.GetRange(i, Math.Min(batchSize, chunks.Count - i));
            var texts = batch.Select(c => c.Text).ToList();
            int batchNum = i / batchSize + 1;

            batchTasks.Add(async () =>
            {
                _activity?.Log("embed_batch", $"{collection}:{path}", $"Embedding batch {batchNum} ({batch.Count} chunks, offset {offset})");
                await _embedSemaphore!.WaitAsync(ct);
                try
                {
                    float[][] embeddings = await _backend.EmbedBatchAsync(texts, ct);
                    // M4: never pad missing vectors with empty arrays — that silently stores
                    // unsearchable chunks. A short/empty result fails the whole file instead.
                    if (embeddings is null || embeddings.Length != batch.Count)
                        throw new InvalidOperationException($"Embedding backend returned {embeddings?.Length ?? 0} vectors for {batch.Count} chunks");
                    for (int j = 0; j < batch.Count; j++)
                    {
                        if (embeddings[j] is null || embeddings[j].Length == 0)
                            throw new InvalidOperationException("Embedding backend returned an empty vector");
                        allEmbeddings[offset + j] = embeddings[j];
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
            return; // nothing written → file is retried on the next pass
        }

        _db.GetOrCreateCollection(collection, _backend.ModelName, allEmbeddings[0].Length);
        // Reserve the file row WITHOUT the new hash. If the insert below fails, the stored
        // hash stays stale/empty so the next pass re-indexes the file instead of seeing a
        // matching hash + zero chunks and skipping it forever.
        var fileId = _db.UpsertFile(collection, path, existingHash ?? string.Empty, source, 0);
        _db.DeleteFileChunks(fileId, collection);
        // M3: one transaction + one vec-table lookup for the whole file instead of per chunk.
        _db.InsertChunks(fileId, collection, chunks, allEmbeddings);
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
