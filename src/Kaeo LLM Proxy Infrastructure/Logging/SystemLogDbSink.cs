using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Serilog sink that persists log events to the <c>system_logs</c> table in the application
/// SQLite database. Events are handed to a bounded in-memory queue and written by a single
/// background task, so <see cref="Emit"/> never blocks on database IO. When the database is
/// unavailable (locked, corrupted, missing), events are written to a fallback CLEF file instead.
/// The sink periodically retries the database to recover automatically.
/// </summary>
internal sealed class SystemLogDbSink : ILogEventSink, IDisposable
{
    private readonly string _connectionString;
    private readonly string _fallbackFilePath;
    private const string SourceContextKey = "$sourceContext";
    private readonly object _lock = new();

    // Bounded queue + single writer. Emit only enqueues; when the queue is full the newest
    // event is dropped and counted, shedding load instead of blocking the emitting thread.
    private readonly Channel<LogEvent> _channel;
    private readonly Task _writerTask;
    private long _droppedEvents;

    // DB health tracking: the fallback trips on the failure RATE (>= 5 failures within 60s),
    // so recurring transient contention degrades to the file instead of stalling forever.
    private readonly Queue<DateTime> _dbFailureTimesUtc = new();
    private const int FailureRateThreshold = 5;
    private static readonly TimeSpan FailureRateWindow = TimeSpan.FromSeconds(60);
    private DateTime _lastDbRetryUtc = DateTime.MinValue;
    private int _recoveryProbeSuccesses;
    private const int RecoveryProbeThreshold = 2;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    // Fallback file state
    private StreamWriter? _fallbackWriter;
    private long _fallbackBytesWritten;
    private bool _dbHealthy = true;

    /// <summary>
    /// Soft cap on the fallback log file. The file holds rendered log events in plaintext —
    /// potentially including sensitive payload text — and would otherwise grow unbounded while the
    /// database is down. At most one rolled generation (.old) is kept, capping plaintext exposure
    /// at roughly twice this size.
    /// </summary>
    private const long MaxFallbackFileBytes = 5 * 1024 * 1024;
    private bool _tableEnsured;
    private volatile bool _disposed;
    private bool _closed;

    /// <summary>
    /// Creates a new DB-backed system log sink.
    /// </summary>
    /// <param name="dbPath">Absolute path to the application SQLite database file.</param>
    /// <param name="fallbackFilePath">Absolute path for the fallback CLEF file when the DB is unavailable.</param>
    public SystemLogDbSink(string dbPath, string fallbackFilePath)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        ArgumentNullException.ThrowIfNull(fallbackFilePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Short busy timeout: a contended write fails fast into the fallback file instead
            // of parking the writer task for the 30s default.
            DefaultTimeout = 2,
        }.ToString();

        _fallbackFilePath = fallbackFilePath;

        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(1000)
        {
            // Wait mode + TryWrite gives drop-newest with a detectable false return; TryWrite
            // never blocks, so Emit cannot stall on a slow consumer.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>True when events are being routed to the database; false when in file-fallback mode.</summary>
    public bool IsUsingDatabase
    {
        get { lock (_lock) { return _dbHealthy; } }
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed) return;

        if (!_channel.Writer.TryWrite(logEvent))
        {
            long dropped = Interlocked.Increment(ref _droppedEvents);
            if (dropped % 100 == 1)
                SelfLog.WriteLine("SystemLogDbSink queue full; {0} log events dropped so far.", dropped);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (LogEvent logEvent in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_lock)
                {
                    if (_closed) return;
                    if (_dbHealthy)
                        WriteToDb(logEvent);
                    else
                        WriteToFallback(logEvent);
                }
            }
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("SystemLogDbSink writer loop failed: {0}", ex);
        }
    }

    /// <summary>
    /// Reads the SourceContext property as its raw string value. The rendered-then-unquoted form
    /// is kept only as a fallback for the unexpected non-scalar case, so the common path does not
    /// depend on Serilog's <see cref="LogEventPropertyValue.ToString()"/> format.
    /// </summary>
    private static string? GetSourceContext(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(SourceContextKey, out LogEventPropertyValue? property)
            ? property is ScalarValue { Value: string context } ? context : property?.ToString().Trim('"')
            : null;

    private void WriteToDb(LogEvent logEvent)
    {
        try
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            if (!_tableEnsured)
            {
                EnsureTableExists(connection);
                _tableEnsured = true;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO system_logs (timestamp_utc, level, message, exception, source_context) " +
                "VALUES ($ts, $level, $msg, $ex, $ctx)";
            command.Parameters.AddWithValue("$ts", logEvent.Timestamp.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$level", logEvent.Level.ToString());
            command.Parameters.AddWithValue("$msg", logEvent.RenderMessage());
            command.Parameters.AddWithValue("$ex", (object?)logEvent.Exception?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$ctx", (object?)GetSourceContext(logEvent) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _tableEnsured = false;
            SelfLog.WriteLine("SystemLogDbSink database write failed: {0}", ex.Message);

            DateTime nowUtc = DateTime.UtcNow;
            _dbFailureTimesUtc.Enqueue(nowUtc);
            while (_dbFailureTimesUtc.Count > 0 && nowUtc - _dbFailureTimesUtc.Peek() > FailureRateWindow)
                _dbFailureTimesUtc.Dequeue();

            if (_dbFailureTimesUtc.Count >= FailureRateThreshold)
                _dbHealthy = false;

            // Whether or not the rate tripped, this event still goes to the fallback file so a
            // failed DB write is not lost.
            EmitToFallbackInternal(logEvent);
        }
    }

    private void WriteToFallback(LogEvent logEvent)
    {
        // Periodically probe the DB while in fallback mode.
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastDbRetryUtc) >= RetryInterval)
        {
            _lastDbRetryUtc = nowUtc;
            if (TryTestDbConnection())
            {
                _recoveryProbeSuccesses++;
                // Recover only after sustained success so a flapping database does not bounce
                // every event between the two stores.
                if (_recoveryProbeSuccesses >= RecoveryProbeThreshold)
                {
                    _dbHealthy = true;
                    _dbFailureTimesUtc.Clear();
                    _recoveryProbeSuccesses = 0;
                    CloseFallbackWriter();
                    WriteToDb(logEvent);
                    return;
                }
            }
            else
            {
                _recoveryProbeSuccesses = 0;
            }
        }

        EmitToFallbackInternal(logEvent);
    }

    private bool TryTestDbConnection()
    {
        try
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();
            EnsureTableExists(connection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureTableExists(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SystemLogSchema.CreateSql;
        command.ExecuteNonQuery();
    }

    private void EmitToFallbackInternal(LogEvent logEvent)
    {
        string line = $"[{logEvent.Timestamp:O}] [{logEvent.Level}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null)
            line += " " + logEvent.Exception.ToString();

        try
        {
            OpenFallbackWriter();
            _fallbackWriter?.WriteLine(line);
            _fallbackWriter?.Flush();

            // Approximate byte count (char count is close enough for a soft cap); when the file
            // reaches the cap, close it so the next event reopens and rolls it over.
            _fallbackBytesWritten += line.Length + Environment.NewLine.Length;
            if (_fallbackBytesWritten >= MaxFallbackFileBytes)
                CloseFallbackWriter();
        }
        catch
        {
            try
            {
                File.AppendAllText(_fallbackFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Truly nowhere to go — swallow to avoid crashing the emitting thread
            }
        }
    }

    private void OpenFallbackWriter()
    {
        if (_fallbackWriter is not null || _closed) return;

        try
        {
            string? dir = Path.GetDirectoryName(_fallbackFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            RollOverFallbackFileIfOversized();

            FileStream stream = File.Open(_fallbackFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            try
            {
                _fallbackWriter = new StreamWriter(stream);
            }
            catch
            {
                // Don't leak the raw FileStream if the writer construction ever fails.
                stream.Dispose();
                throw;
            }

            _fallbackBytesWritten = new FileInfo(_fallbackFilePath).Length;
        }
        catch (Exception ex)
        {
            // The fallback itself may be locked by another instance (AllowMultipleInstances);
            // degrade to dropping events rather than throwing from a logging path.
            SelfLog.WriteLine("SystemLogDbSink could not open fallback writer: {0}", ex);
            CloseFallbackWriter();
        }
    }

    /// <summary>
    /// Moves an oversized fallback file to <c>.old</c>, replacing any previous generation. A file
    /// that is locked or otherwise undeletable is simply kept — the sink must never throw from a
    /// logging path, and bounded growth matters more than a hard guarantee.
    /// </summary>
    private void RollOverFallbackFileIfOversized()
    {
        try
        {
            FileInfo info = new(_fallbackFilePath);
            if (!info.Exists || info.Length < MaxFallbackFileBytes)
                return;

            string rolledPath = _fallbackFilePath + ".old";
            File.Delete(rolledPath);
            File.Move(_fallbackFilePath, rolledPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another instance may hold the file open (AllowMultipleInstances) or the disk may
            // refuse the rename; keep appending to the existing file rather than failing the sink.
        }
    }

    private void CloseFallbackWriter()
    {
        try
        {
            _fallbackWriter?.Dispose();
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("SystemLogDbSink could not close fallback writer: {0}", ex);
        }
        finally
        {
            _fallbackWriter = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Bounded drain: let the writer flush what is already queued, then force-close.
        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("SystemLogDbSink drain failed: {0}", ex);
        }

        lock (_lock)
        {
            _closed = true;
            CloseFallbackWriter();
        }

        long dropped = Interlocked.Read(ref _droppedEvents);
        if (dropped > 0)
            SelfLog.WriteLine("SystemLogDbSink dropped {0} log events over its lifetime (queue full).", dropped);
    }
}
