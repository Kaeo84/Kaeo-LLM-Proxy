using System.Collections.Concurrent;
using System.Threading.Channels;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Diagnostics;

namespace Kaeo.LlmProxy.Services;

/// <summary>
/// Thread-safe service that tracks request logs and aggregate statistics.
/// Persists every entry to <see cref="AppDatabase"/> (SQLite) when one is supplied.
/// On construction, seeds the in-memory queue from the store so the GUI is populated after restart.
/// Runs a background timer every 15 minutes to prune entries older than the configured retention window.
/// All public members are safe to call from any thread.
/// </summary>
internal class StatisticsService : IDisposable
{
    private readonly ConcurrentQueue<RequestLog> _logs = new();
    private int _maxEntries;
    private int _retentionHours;

    // How many of the most recent in-memory entries keep their request/response bodies. Older
    // entries have their bodies trimmed (the SQLite store keeps the full bodies within its own
    // retention). Mirrors LoggingSettings.MaxLogEntriesWithBodies.
    private int _maxEntriesWithBodies;
    private readonly ConcurrentQueue<RequestLog> _bodiedEntries = new();
    private readonly AppDatabase? _store;
    private readonly LogSource _source;

    // Rolling 60-second window of request timestamps for requests-per-second calculation.
    private readonly ConcurrentQueue<long> _requestTimestamps = new();

    private long _totalRequests;
    private long _totalErrors;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;

    private readonly System.Threading.Timer? _cleanupTimer;

    // Bounded channel decoupling the hot request path from SQLite writes. A single dedicated
    // writer task consumes entries so a burst of requests cannot queue unbounded work on the
    // thread pool (the previous ThreadPool.QueueUserWorkItem approach). When the channel is full
    // the newest entry is dropped and a warning is logged, shedding load instead of growing memory.
    private readonly Channel<PersistEntry>? _persistChannel;
    private readonly Task? _persistTask;
    private int _droppedPersistEntries;

    // Cached snapshot of the in-memory queue returned by GetRecentLogs(). Rebuilt only when
    // the underlying queue changes (_snapshotDirty) to avoid allocating a new array on every
    // GUI refresh tick. Published via Volatile.Write; concurrent readers may briefly see the
    // previous snapshot while a rebuild is in progress (display-only, self-healing next tick).
    private IReadOnlyList<RequestLog>? _cachedSnapshot = [];

    // Dirty flag as an int (1 = dirty, 0 = clean) so GetRecentLogs can claim the rebuild atomically
    // via Interlocked.CompareExchange, preventing multiple concurrent refreshes from each
    // allocating a redundant snapshot array.
    private int _snapshotDirty = 1;

    // Per-model connection-health counters, covering BOTH client-facing SSE keep-alive frames and
    // upstream liveness probe results. Key: resolved model name. Updated lock-free via Interlocked.
    private readonly ConcurrentDictionary<string, ConnectionHealthStat> _connectionHealth = new(StringComparer.OrdinalIgnoreCase);

    // Coalesces ConnectionHealthChanged bursts (one keep-alive frame per stream per second would
    // otherwise storm the UI): the first change schedules a single trailing raise; further changes
    // inside the window are folded into it. The UI also polls GetConnectionHealthStats snapshots.
    private static readonly TimeSpan HealthNotifyDelay = TimeSpan.FromMilliseconds(500);
    private readonly System.Threading.Timer _healthNotifyTimer;
    private int _healthNotifyPending;

    // Keep-alive persistence is coalesced through this timer instead of one thread-pool work item
    // per frame: a single flusher upserts the latest snapshot per model, so writes stay ordered and
    // the global database lock is taken at most once per interval instead of once per frame.
    private static readonly TimeSpan KeepAliveFlushInterval = TimeSpan.FromSeconds(5);
    private readonly System.Threading.Timer? _keepAliveFlushTimer;
    private int _keepAliveFlushRunning;

    private int _disposed;

    public event EventHandler? StatsChanged;

    /// <summary>
    /// Raised when any connection-health counter changes (keep-alive frame or probe). Bursts are
    /// coalesced into at most one raise per <see cref="HealthNotifyDelay"/> window.
    /// </summary>
    public event EventHandler? ConnectionHealthChanged;

    public StatisticsService(int maxEntries = 500, AppDatabase? store = null, int retentionHours = 72,
        LogSource source = LogSource.Proxy, int maxEntriesWithBodies = 100)
    {
        _maxEntries = maxEntries;
        _retentionHours = retentionHours;
        _store = store;
        _source = source;
        _maxEntriesWithBodies = Math.Clamp(maxEntriesWithBodies, 0, Math.Max(0, maxEntries));

        _healthNotifyTimer = new System.Threading.Timer(_ =>
        {
            Interlocked.Exchange(ref _healthNotifyPending, 0);
            if (Volatile.Read(ref _disposed) == 0)
                OnConnectionHealthChanged();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (store is not null)
        {
            _keepAliveFlushTimer = new System.Threading.Timer(_ => FlushKeepAliveStats(), null,
                KeepAliveFlushInterval, KeepAliveFlushInterval);
        }

        // Seed in-memory queue from persisted store so the GUI is populated on startup.
        if (store is not null)
        {
            foreach (RequestLog entry in store.LoadRecent(maxEntries, _source))
            {
                _logs.Enqueue(entry);
                Interlocked.Increment(ref _totalRequests);
                if (entry.Status == RequestStatus.Error) Interlocked.Increment(ref _totalErrors);
                Interlocked.Add(ref _totalPromptTokens, entry.PromptTokens);
                Interlocked.Add(ref _totalCompletionTokens, entry.CompletionTokens);
            }

            // Keep-alive tracking is a proxy concern; the MCP store has no keep-alive rows.
            if (_source == LogSource.Proxy)
            {
                foreach ((string model, long count, DateTime lastSentUtc) in store.LoadSseKeepAliveStats())
                    SetSseKeepAliveCount(model, count, lastSentUtc);
            }
        }

        // Background cleanup: prune stale entries every 15 minutes.
        _cleanupTimer = new System.Threading.Timer(_ => PruneExpired(), null,
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

        // Bounded persistence channel + single dedicated writer, only when a store is present.
        if (store is not null)
        {
            int capacity = Math.Max(16, maxEntries);
            _persistChannel = Channel.CreateBounded<PersistEntry>(new BoundedChannelOptions(capacity)
            {
                // Wait mode + non-blocking TryWrite gives drop-newest semantics WITH a detectable
                // false return when full (DropNewest/DropOldest modes make TryWrite always succeed,
                // hiding overflow). We never call WriteAsync, so TryWrite never actually blocks.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            _persistTask = Task.Run(() => PersistLoopAsync(_persistChannel.Reader));
        }
    }

    /// <summary>
    /// Single consumer that drains the persistence channel and writes each entry to the store.
    /// Runs until the channel is completed during <see cref="Dispose"/>.
    /// </summary>
    private async Task PersistLoopAsync(ChannelReader<PersistEntry> reader)
    {
        try
        {
            await foreach (PersistEntry item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (item.Entry is { } entry)
                        _store!.Insert(entry, item.Exception, _source);
                    else
                        _store!.ClearLogs(_source);
                }
                catch (Exception storeEx)
                {
                    Log.Warning(storeEx, "Failed to persist request log entry");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel completed during shutdown — expected.
        }
    }

    public void UpdateMaxEntries(int max)
    {
        _maxEntries = max;
    }

    /// <summary>
    /// Updates how many of the most recent in-memory entries retain their bodies
    /// (<c>LoggingSettings.MaxLogEntriesWithBodies</c>). Lowering the limit trims older entries
    /// immediately; full bodies remain available from the store via <c>LoadFullLogEntry</c>.
    /// </summary>
    public void UpdateMaxEntriesWithBodies(int max)
    {
        Volatile.Write(ref _maxEntriesWithBodies, Math.Clamp(max, 0, Math.Max(0, Volatile.Read(ref _maxEntries))));
        TrimBodiedEntries();
    }

    /// <summary>Updates the retention window. Pass 0 to keep entries forever.</summary>
    public void UpdateRetentionHours(int hours)
    {
        _retentionHours = hours;
    }

    public void AddLog(RequestLog entry, Exception? ex = null)
    {
        // Enqueue a private copy so later body trimming never mutates the caller's entry (the
        // original is handed to the persistence channel below with its bodies intact). Only the
        // most recent _maxEntriesWithBodies copies keep their bodies; older ones are trimmed.
        RequestLog inMemory = CreateInMemoryCopy(entry);
        _logs.Enqueue(inMemory);
        if (inMemory.RequestBody is not null || inMemory.ResponseBody is not null)
        {
            _bodiedEntries.Enqueue(inMemory);
            TrimBodiedEntries();
        }
        Volatile.Write(ref _snapshotDirty, 1);

        long now = Stopwatch.GetTimestamp();
        _requestTimestamps.Enqueue(now);

        // Prune timestamps older than 60 seconds from the front.
        long cutoff = now - Stopwatch.Frequency * 60;
        while (_requestTimestamps.TryPeek(out long oldest) && oldest < cutoff)
            _requestTimestamps.TryDequeue(out _);

        Interlocked.Increment(ref _totalRequests);

        if (entry.Status == RequestStatus.Error)
            Interlocked.Increment(ref _totalErrors);

        Interlocked.Add(ref _totalPromptTokens, entry.PromptTokens);
        Interlocked.Add(ref _totalCompletionTokens, entry.CompletionTokens);

        // Intentionally lock-free soft cap: Count is a snapshot, so concurrent AddLog calls may
        // trim one extra entry. Precision does not matter for this display-only in-memory cap
        // (the SQLite store stays authoritative), and locking here would hit the request hot path.
        while (_logs.Count > _maxEntries)
            _logs.TryDequeue(out _);

        // Hand the full entry (including bodies and any exception) to the bounded persistence
        // channel. The dedicated writer task persists it without blocking the request pipeline.
        // When the channel is full, DropNewest sheds this entry; log a warning (rate-limited by
        // counting) so sustained overflow is visible without flooding the log.
        if (_store is not null && _persistChannel is not null)
        {
            if (!_persistChannel.Writer.TryWrite(new PersistEntry(entry, ex)))
            {
                int dropped = Interlocked.Increment(ref _droppedPersistEntries);
                if (dropped == 1 || dropped % 100 == 0)
                    Log.Warning("Request log persistence channel is full; {Count} entries dropped so far", dropped);
            }
        }

        OnStatsChanged();
    }

    /// <summary>
    /// Trims bodies from the oldest in-memory entries so only the most recent
    /// <see cref="_maxEntriesWithBodies"/> retain them. Entries already evicted from the log queue
    /// may still sit in this queue; trimming them is harmless and they age out naturally.
    /// </summary>
    private void TrimBodiedEntries()
    {
        int limit = Volatile.Read(ref _maxEntriesWithBodies);
        while (_bodiedEntries.Count > limit && _bodiedEntries.TryDequeue(out RequestLog? oldest))
        {
            oldest.RequestBody = null;
            oldest.ResponseBody = null;
        }
    }

    /// <summary>
    /// Creates the in-memory copy of a <see cref="RequestLog"/>. Bodies are kept initially so the
    /// detail view can show them without a database round-trip; <see cref="TrimBodiedEntries"/>
    /// nulls them once the entry falls out of the most recent <see cref="_maxEntriesWithBodies"/>.
    /// Full bodies always live in SQLite and are loaded on demand via <c>LoadFullLogEntry</c>.
    /// </summary>
    private static RequestLog CreateInMemoryCopy(RequestLog source) => new()
    {
        RequestId = source.RequestId,
        Timestamp = source.Timestamp,
        Method = source.Method,
        OllamaPath = source.OllamaPath,
        UpstreamPath = source.UpstreamPath,
        Model = source.Model,
        UserId = source.UserId,
        // Carried so the log list can show "Original → Effective (compact redirect)" for entries
        // still only in memory; dropping it here made the redirect visible only after a reload.
        OriginalModel = source.OriginalModel,
        Streaming = source.Streaming,
        Status = source.Status,
        ErrorMessage = source.ErrorMessage,
        StatusCode = source.StatusCode,
        DurationMs = source.DurationMs,
        PromptTokens = source.PromptTokens,
        CompletionTokens = source.CompletionTokens,
        TokensPerSecond = source.TokensPerSecond,
        TotalTokens = source.TotalTokens,
        CachedPromptTokens = source.CachedPromptTokens,
        ReasoningTokens = source.ReasoningTokens,
        DraftN = source.DraftN,
        DraftNAccepted = source.DraftNAccepted,
        ExceptionId = source.ExceptionId,
        RequestBody = source.RequestBody,
        ResponseBody = source.ResponseBody,
        RequestBytes = source.RequestBytes,
        ResponseBytes = source.ResponseBytes,
    };

    public IReadOnlyList<RequestLog> GetRecentLogs()
    {
        // Atomically claim the rebuild: only the thread that flips dirty 1 -> 0 rebuilds the
        // snapshot. Concurrent callers see 0 and reuse the previous snapshot (published via
        // Volatile) rather than each allocating a redundant array; they pick up the new one on
        // the next call. Display-only and self-healing.
        if (Interlocked.CompareExchange(ref _snapshotDirty, 0, 1) == 1)
        {
            Volatile.Write(ref _cachedSnapshot, [.. _logs.Reverse()]);
        }

        return Volatile.Read(ref _cachedSnapshot) ?? [];
    }

    /// <summary>
    /// Retrieves the full <see cref="ExceptionDetail"/> for a log entry, or null if
    /// no exception was recorded or the store is unavailable. Exceptions are persisted for every
    /// log source, so the lookup is not gated on <see cref="_source"/>.
    /// </summary>
    public ExceptionDetail? GetException(int exceptionId) =>
        _store?.GetException(exceptionId);

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public long TotalPromptTokens => Interlocked.Read(ref _totalPromptTokens);
    public long TotalCompletionTokens => Interlocked.Read(ref _totalCompletionTokens);

    /// <summary>
    /// Returns the average number of requests per second over the last 60 seconds.
    /// </summary>
    public double RequestsPerSecond
    {
        get
        {
            // Snapshot and prune stale entries.
            long now = Stopwatch.GetTimestamp();
            long cutoff = now - Stopwatch.Frequency * 60;
            while (_requestTimestamps.TryPeek(out long oldest) && oldest < cutoff)
                _requestTimestamps.TryDequeue(out _);

            int count = _requestTimestamps.Count;
            return count == 0 ? 0.0 : count / 60.0;
        }
    }

    public void Reset()
    {
        while (_logs.TryDequeue(out _)) { }
        while (_bodiedEntries.TryDequeue(out _)) { }
        while (_requestTimestamps.TryDequeue(out _)) { }
        Volatile.Write(ref _snapshotDirty, 1);
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        Interlocked.Exchange(ref _totalPromptTokens, 0);
        Interlocked.Exchange(ref _totalCompletionTokens, 0);
        OnStatsChanged();
    }

    /// <summary>
    /// Clears the in-memory log queue, aggregate counters, and snapshot cache, then wipes all
    /// persisted request logs and linked exceptions from the store. The database wipe is routed
    /// through the persistence channel so it runs after entries still queued have been written;
    /// otherwise they would land in the database after the wipe and reappear.
    /// </summary>
    public void ClearLogs()
    {
        Reset();

        if (_store is null || _persistChannel is null)
            return;

        if (_persistChannel.Writer.TryWrite(PersistEntry.ClearLogs))
            return;

        // Channel full or completed — wipe directly so the clear still reaches the database.
        // Must target this service's own source: the default would wipe the Proxy store even
        // when called on the MCP/NonProxied instance.
        try
        {
            _store.ClearLogs(_source);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Log.Warning(ex, "Failed to clear request logs from the database");
        }
    }

    /// <summary>
    /// Records one SSE keep-alive frame emitted to a client for the given model. Thread-safe;
    /// non-blocking, so it is safe to call from the streaming pipeline on every frame.
    /// </summary>
    /// <remarks>
    /// This deliberately does NOT touch the upstream probe fields (<see cref="ConnectionHealthStat.Attempts"/>,
    /// <see cref="ConnectionHealthStat.LastStatus"/>, <see cref="ConnectionHealthStat.LastError"/>).
    /// Emitting a keep-alive to the client says nothing about whether the upstream is reachable, and
    /// mixing the two made the Keep-Alive tab report "Success" for a model whose upstream was down —
    /// which hid exactly the failure mode (silent streaming timeouts) this view exists to surface.
    /// </remarks>
    public void IncrementSseKeepAlive(string? modelName)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        ConnectionHealthStat stat = _connectionHealth.GetOrAdd(key, _ => new ConnectionHealthStat());
        Interlocked.Increment(ref stat.Count);
        Interlocked.Exchange(ref stat.LastSentUtcTicks, DateTime.UtcNow.Ticks);

        // Persistence is coalesced by _keepAliveFlushTimer (latest snapshot per model), not a
        // per-frame work item: ordered writes, one lock acquisition per interval.
        NotifyConnectionHealthChanged();
    }

    /// <summary>
    /// Upserts the latest keep-alive snapshot for each model whose counters changed since the
    /// last flush. Single flusher (re-entrancy guarded), so persisted counts never regress.
    /// </summary>
    private void FlushKeepAliveStats()
    {
        if (_store is null || Volatile.Read(ref _disposed) == 1)
            return;

        if (Interlocked.CompareExchange(ref _keepAliveFlushRunning, 1, 0) != 0)
            return;

        try
        {
            foreach ((string model, ConnectionHealthStat stat) in _connectionHealth)
            {
                long sentTicks = Interlocked.Read(ref stat.LastSentUtcTicks);
                if (sentTicks == 0 || sentTicks == Interlocked.Read(ref stat.LastPersistedSentUtcTicks))
                    continue;

                long count = Interlocked.Read(ref stat.Count);
                try
                {
                    _store.UpsertSseKeepAlive(model, count, new DateTime(sentTicks, DateTimeKind.Utc));
                    Interlocked.Exchange(ref stat.LastPersistedSentUtcTicks, sentTicks);
                }
                catch (Exception storeEx)
                {
                    Log.Warning(storeEx, "Failed to persist SSE keep-alive stat");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _keepAliveFlushRunning, 0);
        }
    }

    /// <summary>
    /// Records one successful upstream liveness probe for the given model. The counterpart to
    /// <see cref="RecordHeartbeatFailure"/>; both describe the periodic <c>/v1/models</c> probe and
    /// neither counts client-facing SSE keep-alive frames.
    /// </summary>
    public void RecordHeartbeatProbeSuccess(string? modelName)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        ConnectionHealthStat stat = _connectionHealth.GetOrAdd(key, _ => new ConnectionHealthStat());
        Interlocked.Increment(ref stat.Attempts);
        Interlocked.Exchange(ref stat.LastAttemptUtcTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref stat.LastStatus, "Success");
        Volatile.Write(ref stat.LastError, string.Empty);
        NotifyConnectionHealthChanged();
    }

    public void RecordHeartbeatFailure(string? modelName, string errorMessage)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        ConnectionHealthStat stat = _connectionHealth.GetOrAdd(key, _ => new ConnectionHealthStat());
        Interlocked.Increment(ref stat.Attempts);
        Interlocked.Increment(ref stat.Failures);
        Interlocked.Exchange(ref stat.LastAttemptUtcTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref stat.LastStatus, "Failed");
        Volatile.Write(ref stat.LastError, string.IsNullOrWhiteSpace(errorMessage) ? "Unknown failure" : errorMessage.Trim());
        NotifyConnectionHealthChanged();
    }

    public void RegisterHeartbeatModel(string? modelName)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        _connectionHealth.GetOrAdd(key, _ => new ConnectionHealthStat());
        NotifyConnectionHealthChanged();
    }

    /// <summary>Restores the persisted SSE keep-alive frame count for a model at startup.</summary>
    public void SetSseKeepAliveCount(string? modelName, long count, DateTime lastSentUtc)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        ConnectionHealthStat stat = _connectionHealth.GetOrAdd(key, _ => new ConnectionHealthStat());
        Interlocked.Exchange(ref stat.Count, count);
        long ticks = lastSentUtc.Kind == DateTimeKind.Utc
            ? lastSentUtc.Ticks
            : lastSentUtc.ToUniversalTime().Ticks;
        Interlocked.Exchange(ref stat.LastSentUtcTicks, ticks);
        // The restored value came from the store; no need to write it back until it changes.
        Interlocked.Exchange(ref stat.LastPersistedSentUtcTicks, ticks);
        NotifyConnectionHealthChanged();
    }

    /// <summary>
    /// Returns a thread-safe snapshot of connection-health counters keyed by model name, covering
    /// both SSE keep-alive frames and upstream liveness probe results.
    /// </summary>
    public IReadOnlyList<ConnectionHealthSnapshot> GetConnectionHealthStats()
    {
        return [.. _connectionHealth.Select(kvp => new ConnectionHealthSnapshot(
            kvp.Key,
            Interlocked.Read(ref kvp.Value.Attempts),
            Interlocked.Read(ref kvp.Value.Count),
            Interlocked.Read(ref kvp.Value.Failures),
            new DateTime(Interlocked.Read(ref kvp.Value.LastAttemptUtcTicks), DateTimeKind.Utc),
            new DateTime(Interlocked.Read(ref kvp.Value.LastSentUtcTicks), DateTimeKind.Utc),
            Volatile.Read(ref kvp.Value.LastStatus),
            Volatile.Read(ref kvp.Value.LastError)))];
    }

    /// <summary>
    /// Clears all in-memory connection-health counters and the persisted SSE keep-alive rows.
    /// Probe results are in-memory only, so clearing the dictionary resets those as well.
    /// </summary>
    public void ResetConnectionHealth()
    {
        _connectionHealth.Clear();
        _store?.ClearSseKeepAlive();
        NotifyConnectionHealthChanged();
    }

    /// <summary>
    /// Raises <see cref="StatsChanged"/> with an exception guard: a throwing UI subscriber must
    /// never propagate out of the request pipeline (AddLog runs inside request finally blocks).
    /// </summary>
    protected virtual void OnStatsChanged()
    {
        try
        {
            StatsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A StatsChanged subscriber threw; the request pipeline is unaffected");
        }
    }

    /// <summary>
    /// Raises <see cref="ConnectionHealthChanged"/> with an exception guard. Call
    /// <see cref="NotifyConnectionHealthChanged"/> from counter paths to coalesce bursts.
    /// </summary>
    protected virtual void OnConnectionHealthChanged()
    {
        try
        {
            ConnectionHealthChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A ConnectionHealthChanged subscriber threw; the request pipeline is unaffected");
        }
    }

    /// <summary>
    /// Schedules a single coalesced <see cref="ConnectionHealthChanged"/> raise. The first change
    /// in a quiet period starts the delay; changes while a raise is pending are folded into it.
    /// </summary>
    private void NotifyConnectionHealthChanged()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        if (Interlocked.Exchange(ref _healthNotifyPending, 1) == 0)
        {
            try
            {
                _healthNotifyTimer.Change(HealthNotifyDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down; the pending notification is dropped.
            }
        }
    }

    /// <summary>
    /// Deletes entries from the SQLite store that are older than
    /// <see cref="_retentionHours"/>. A value of 0 means keep forever.
    /// The in-memory queue is left as-is (eventually consistent) — stale entries
    /// will naturally age out as new entries push them out due to the max-entries limit.
    /// </summary>
    private void PruneExpired()
    {
        if (_store is null)
            return;

        try
        {
            if (_retentionHours > 0)
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-_retentionHours);
                int pruned = _store.DeleteOlderThan(cutoff, _source);

                if (pruned > 0)
                {
                    Volatile.Write(ref _snapshotDirty, 1);
                    OnStatsChanged();
                }
            }

            // system_logs has its own bounded retention (30 days / 100k rows) regardless of the
            // request-log setting; only the proxy instance prunes it — both services share the
            // same database file.
            if (_source == LogSource.Proxy)
                _store.PruneSystemLogs(TimeSpan.FromDays(30), 100_000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Log retention cleanup failed");
        }
    }

    /// <summary>
    /// Waits (briefly) for queued log entries to reach the database. Best effort by design: a
    /// caller shutting down its own logging path wants its last entries persisted, but must never
    /// hang on a stalled writer. No-op when the service has no store.
    /// </summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        if (_persistChannel is null)
            return;

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        long deadline = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        while (_persistChannel.Reader.Count > 0 && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(15).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _healthNotifyTimer.Dispose();
        _keepAliveFlushTimer?.Dispose();

        // Persist the final keep-alive snapshots before marking disposal complete.
        FlushKeepAliveStats();

        if (_persistChannel is not null)
        {
            // Signal no more writes and let the writer drain remaining entries to the store.
            _persistChannel.Writer.TryComplete();
            try
            {
                _persistTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Timed out or failed while draining request log persistence channel");
            }
        }

        Volatile.Write(ref _disposed, 1);

        // Drop subscriber references so a disposed service does not keep UI objects alive.
        StatsChanged = null;
        ConnectionHealthChanged = null;
    }
}

/// <summary>A request log entry plus its optional exception, queued for background persistence.</summary>
internal sealed record PersistEntry(RequestLog? Entry, Exception? Exception)
{
    /// <summary>
    /// Sentinel that instructs the persistence writer to wipe the store instead of inserting.
    /// Routing the wipe through the channel guarantees it executes after every previously
    /// queued entry has been persisted.
    /// </summary>
    public static PersistEntry ClearLogs { get; } = new(null, null);
}

/// <summary>
/// Mutable counter holder used internally by <see cref="StatisticsService"/>. Holds two unrelated
/// groups of fields that must not be updated by each other's code paths:
/// <list type="bullet">
/// <item><see cref="Count"/> and <see cref="LastSentUtcTicks"/> count client-facing SSE keep-alive
/// frames, and are the only fields persisted to the store. Update via Interlocked.</item>
/// <item><see cref="Attempts"/>, <see cref="Failures"/>, <see cref="LastAttemptUtcTicks"/> describe
/// the periodic upstream liveness probe and are in-memory only. All <c>long</c> fields on this class
/// MUST be accessed through Interlocked (Increment / Exchange / Read).</item>
/// <item><see cref="LastStatus"/> and <see cref="LastError"/> are plain reference-type writes; the
/// writer is always a single thread (the heartbeat timer). Reads are eventually consistent.</item>
/// </list>
/// External code should consume the snapshot (<see cref="ConnectionHealthSnapshot"/>) rather than
/// reading fields directly.
/// </summary>
internal sealed class ConnectionHealthStat
{
    /// <summary>Keep-alive frame count for this model. Access via <see cref="Interlocked"/>.</summary>
    internal long Count;

    /// <summary>Ticks of the last keep-alive frame sent. Access via <see cref="Interlocked"/>.</summary>
    internal long LastSentUtcTicks;

    /// <summary>LastSentUtcTicks value already written to the store. Access via <see cref="Interlocked"/>.</summary>
    internal long LastPersistedSentUtcTicks;

    /// <summary>Number of liveness-probe attempts. Access via <see cref="Interlocked"/>.</summary>
    internal long Attempts;

    /// <summary>Number of liveness-probe failures. Access via <see cref="Interlocked"/>.</summary>
    internal long Failures;

    /// <summary>Ticks of the last liveness-probe attempt. Access via <see cref="Interlocked"/>.</summary>
    internal long LastAttemptUtcTicks;

    /// <summary>Last probe result label. Written from timer thread; read eventually consistent.</summary>
    internal string LastStatus = "Not checked";

    /// <summary>Last probe error text. Written from timer thread; read eventually consistent.</summary>
    internal string LastError = string.Empty;
}

/// <summary>Immutable snapshot of connection-health activity for a single model.</summary>
internal sealed record ConnectionHealthSnapshot(
    string Model,
    long Attempts,
    long Count,
    long Failures,
    DateTime LastAttemptUtc,
    DateTime LastSentUtc,
    string LastStatus,
    string LastError);
