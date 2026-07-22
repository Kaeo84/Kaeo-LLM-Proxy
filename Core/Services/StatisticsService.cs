using System.Collections.Concurrent;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Serilog;
using System.Diagnostics;

namespace Kaeo.LlmProxy.Core.Services;

/// <summary>
/// Thread-safe service that tracks request logs and aggregate statistics.
/// Persists every entry to <see cref="AppDatabase"/> (SQLite) when one is supplied.
/// On construction, seeds the in-memory queue from the store so the GUI is populated after restart.
/// Runs a background timer every 15 minutes to prune entries older than the configured retention window.
/// All public members are safe to call from any thread.
/// </summary>
internal sealed class StatisticsService : IDisposable
{
    private readonly ConcurrentQueue<RequestLog> _logs = new();
    private int _maxEntries;
    private int _retentionHours;
    private readonly AppDatabase? _store;

    // Rolling 60-second window of request timestamps for requests-per-second calculation.
    private readonly ConcurrentQueue<long> _requestTimestamps = new();

    private long _totalRequests;
    private long _totalErrors;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;

    private readonly System.Threading.Timer? _cleanupTimer;

    // Cached snapshot of the in-memory queue returned by GetRecentLogs(). Rebuilt only when
    // the underlying queue changes (_snapshotDirty) to avoid allocating a new array on every
    // GUI refresh tick.
    private IReadOnlyList<RequestLog>? _cachedSnapshot;
    private volatile bool _snapshotDirty = true;

    // Per-model heartbeat counters. Key: resolved model name. Updated lock-free via Interlocked.
    private readonly ConcurrentDictionary<string, HeartbeatStat> _heartbeats = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? StatsChanged;
    public event EventHandler? HeartbeatsChanged;

    public StatisticsService(int maxEntries = 500, AppDatabase? store = null, int retentionHours = 72)
    {
        _maxEntries = maxEntries;
        _retentionHours = retentionHours;
        _store = store;

        // Seed in-memory queue from persisted store so the GUI is populated on startup.
        if (store is not null)
        {
            foreach (RequestLog entry in store.LoadRecent(maxEntries))
            {
                _logs.Enqueue(entry);
                Interlocked.Increment(ref _totalRequests);
                if (entry.Status == RequestStatus.Error) Interlocked.Increment(ref _totalErrors);
                Interlocked.Add(ref _totalPromptTokens, entry.PromptTokens);
                Interlocked.Add(ref _totalCompletionTokens, entry.CompletionTokens);
            }

            foreach ((string model, long count, DateTime lastSentUtc) in store.LoadHeartbeatStats())
                SetHeartbeatStat(model, count, lastSentUtc);
        }

        // Background cleanup: prune stale entries every 15 minutes.
        _cleanupTimer = new System.Threading.Timer(_ => PruneExpired(), null,
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    public void UpdateMaxEntries(int max)
    {
        _maxEntries = max;
    }

    /// <summary>Updates the retention window. Pass 0 to keep entries forever.</summary>
    public void UpdateRetentionHours(int hours)
    {
        _retentionHours = hours;
    }

    public void AddLog(RequestLog entry, Exception? ex = null)
    {
        // Enqueue a lightweight copy (no request/response bodies) to keep memory usage low.
        // The full entry — including bodies — is persisted to SQLite on a background thread below.
        _logs.Enqueue(CreateSummary(entry));
        _snapshotDirty = true;

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

        while (_logs.Count > _maxEntries)
            _logs.TryDequeue(out _);

        // Persist to LiteDB on a background thread to avoid blocking the request pipeline.
        if (_store is not null)
        {
            // Capture for closure — exception may carry a large stack trace so we pass it
            // through only when present, letting the store write the separate exceptions table.
            Exception? capturedException = ex;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _store.Insert(entry, capturedException); }
                catch (Exception storeEx) { Log.Warning(storeEx, "Failed to persist request log entry"); }
            });
        }

        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates a lightweight copy of a <see cref="RequestLog"/> without the potentially large
    /// <see cref="RequestLog.RequestBody"/> and <see cref="RequestLog.ResponseBody"/> strings.
    /// The in-memory queue holds only these summaries; full bodies live in SQLite and are
    /// loaded on demand when a detail view is opened.
    /// </summary>
    private static RequestLog CreateSummary(RequestLog source) => new()
    {
        RequestId = source.RequestId,
        Timestamp = source.Timestamp,
        Method = source.Method,
        OllamaPath = source.OllamaPath,
        UpstreamPath = source.UpstreamPath,
        Model = source.Model,
        Streaming = source.Streaming,
        Status = source.Status,
        ErrorMessage = source.ErrorMessage,
        StatusCode = source.StatusCode,
        DurationMs = source.DurationMs,
        PromptTokens = source.PromptTokens,
        CompletionTokens = source.CompletionTokens,
        TokensPerSecond = source.TokensPerSecond,
        ExceptionId = source.ExceptionId,
        RequestBody = null,
        ResponseBody = null,
        RequestBytes = source.RequestBytes,
        ResponseBytes = source.ResponseBytes,
        SummarizationRetries = source.SummarizationRetries,
        OriginalMessageCount = source.OriginalMessageCount,
        SummarizedMessageCount = source.SummarizedMessageCount,
    };

    public IReadOnlyList<RequestLog> GetRecentLogs()
    {
        if (_snapshotDirty)
        {
            _cachedSnapshot = [.. _logs.Reverse()];
            _snapshotDirty = false;
        }

        return _cachedSnapshot ?? [];
    }

    /// <summary>
    /// Retrieves the full <see cref="ExceptionDetail"/> for a log entry, or null if
    /// no exception was recorded or the store is unavailable.
    /// </summary>
    public ExceptionDetail? GetException(int exceptionId) => _store?.GetException(exceptionId);

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
        _snapshotDirty = true;
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        Interlocked.Exchange(ref _totalPromptTokens, 0);
        Interlocked.Exchange(ref _totalCompletionTokens, 0);
        StatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records one heartbeat frame emitted for the given model. Thread-safe; non-blocking.
    /// Safe to call from the streaming pipeline.
    /// </summary>
    public void IncrementHeartbeat(string? modelName)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        HeartbeatStat stat = _heartbeats.GetOrAdd(key, _ => new HeartbeatStat());
        Interlocked.Increment(ref stat.Attempts);
        Interlocked.Increment(ref stat.Count);
        long nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref stat.LastAttemptUtcTicks, nowTicks);
        Interlocked.Exchange(ref stat.LastSentUtcTicks, nowTicks);
        Volatile.Write(ref stat.LastStatus, "Success");
        Volatile.Write(ref stat.LastError, string.Empty);
        long count = Interlocked.Read(ref stat.Count);
        DateTime lastSentUtc = new(nowTicks, DateTimeKind.Utc);

        if (_store is not null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _store.UpsertHeartbeat(key, count, lastSentUtc); }
                catch (Exception storeEx) { Log.Warning(storeEx, "Failed to persist heartbeat stat"); }
            });
        }

        HeartbeatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RecordHeartbeatFailure(string? modelName, string errorMessage)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        HeartbeatStat stat = _heartbeats.GetOrAdd(key, _ => new HeartbeatStat());
        Interlocked.Increment(ref stat.Attempts);
        Interlocked.Increment(ref stat.Failures);
        Interlocked.Exchange(ref stat.LastAttemptUtcTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref stat.LastStatus, "Failed");
        Volatile.Write(ref stat.LastError, string.IsNullOrWhiteSpace(errorMessage) ? "Unknown failure" : errorMessage.Trim());
        HeartbeatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterHeartbeatModel(string? modelName)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        _heartbeats.GetOrAdd(key, _ => new HeartbeatStat());
        HeartbeatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetHeartbeatStat(string? modelName, long count, DateTime lastSentUtc)
    {
        string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName.Trim();
        HeartbeatStat stat = _heartbeats.GetOrAdd(key, _ => new HeartbeatStat());
        Interlocked.Exchange(ref stat.Count, count);
        stat.LastSentUtcTicks = lastSentUtc.Kind == DateTimeKind.Utc
            ? lastSentUtc.Ticks
            : lastSentUtc.ToUniversalTime().Ticks;
        HeartbeatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns a thread-safe snapshot of heartbeat counters keyed by model name.</summary>
    public IReadOnlyList<HeartbeatSnapshot> GetHeartbeatStats()
    {
        return [.. _heartbeats.Select(kvp => new HeartbeatSnapshot(
            kvp.Key,
            Interlocked.Read(ref kvp.Value.Attempts),
            Interlocked.Read(ref kvp.Value.Count),
            Interlocked.Read(ref kvp.Value.Failures),
            new DateTime(Interlocked.Read(ref kvp.Value.LastAttemptUtcTicks), DateTimeKind.Utc),
            new DateTime(Interlocked.Read(ref kvp.Value.LastSentUtcTicks), DateTimeKind.Utc),
            Volatile.Read(ref kvp.Value.LastStatus),
            Volatile.Read(ref kvp.Value.LastError)))];
    }

    /// <summary>Clears all heartbeat counters.</summary>
    public void ResetHeartbeats()
    {
        _heartbeats.Clear();
        _store?.ClearHeartbeats();
        HeartbeatsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Deletes entries from the SQLite store that are older than
    /// <see cref="_retentionHours"/>. A value of 0 means keep forever.
    /// The in-memory queue is left as-is (eventually consistent) — stale entries
    /// will naturally age out as new entries push them out due to the max-entries limit.
    /// </summary>
    private void PruneExpired()
    {
        if (_retentionHours <= 0 || _store is null)
            return;

        DateTime cutoff = DateTime.UtcNow.AddHours(-_retentionHours);

        try
        {
            int pruned = _store.DeleteOlderThan(cutoff);

            if (pruned > 0)
            {
                _snapshotDirty = true;
                StatsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Log retention cleanup failed");
        }
    }

    public void Dispose() => _cleanupTimer?.Dispose();
}

/// <summary>Mutable counter holder used internally by <see cref="StatisticsService"/>.</summary>
internal sealed class HeartbeatStat
{
    public long Attempts;
    public long Count;
    public long Failures;
    public long LastAttemptUtcTicks;
    public long LastSentUtcTicks;
    public string LastStatus = "Not checked";
    public string LastError = string.Empty;
}

/// <summary>Immutable snapshot of heartbeat activity for a single model.</summary>
internal sealed record HeartbeatSnapshot(
    string Model,
    long Attempts,
    long Count,
    long Failures,
    DateTime LastAttemptUtc,
    DateTime LastSentUtc,
    string LastStatus,
    string LastError);
