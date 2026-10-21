using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Events;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Serilog sink that persists log events to the <c>system_logs</c> table in the application
/// SQLite database. When the database is unavailable (locked, corrupted, missing), events
/// are written to a fallback CLEF file instead. The sink periodically retries the database
/// to recover automatically.
/// </summary>
internal sealed class SystemLogDbSink : ILogEventSink, IDisposable
{
    private readonly string _connectionString;
    private readonly string _fallbackFilePath;
    private const string SourceContextKey = "$sourceContext";
    private readonly object _lock = new();

    // DB health tracking
    private int _consecutiveDbFailures;
    private DateTime _lastDbRetryUtc = DateTime.MinValue;
    private const int FailureThreshold = 3;
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
    private bool _disposed;

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
        }.ToString();

        _fallbackFilePath = fallbackFilePath;
    }

    /// <summary>True when events are being routed to the database; false when in file-fallback mode.</summary>
    public bool IsUsingDatabase
    {
        get { lock (_lock) { return _dbHealthy; } }
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed) return;

        string renderedMessage = logEvent.RenderMessage();
        string? exception = logEvent.Exception?.ToString();
        string? sourceContext = GetSourceContext(logEvent);

        lock (_lock)
        {
            if (_dbHealthy)
                TryEmitToDb(logEvent, renderedMessage, exception, sourceContext);
            else
                TryEmitToFallback(logEvent);
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

    private void TryEmitToDb(LogEvent logEvent, string message, string? exception, string? sourceContext)
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
            command.Parameters.AddWithValue("$msg", message);
            command.Parameters.AddWithValue("$ex", (object?)exception ?? DBNull.Value);
            command.Parameters.AddWithValue("$ctx", (object?)sourceContext ?? DBNull.Value);
            command.ExecuteNonQuery();

            // Successful write — reset failure counter and clear table-ensured
            // flag so DDL retries on connection-recovery (e.g. DB recreated on disk).
            _consecutiveDbFailures = 0;
        }
        catch
        {
            _tableEnsured = false;
            _consecutiveDbFailures++;

            if (_consecutiveDbFailures >= FailureThreshold)
            {
                _dbHealthy = false;
                OpenFallbackWriter();
            }
            else
            {
                // Below threshold — write to fallback for this event to avoid data loss
                EmitToFallbackInternal(logEvent);
            }
        }
    }

    private void TryEmitToFallback(LogEvent logEvent)
    {
        // Periodically retry the DB while in fallback mode
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastDbRetryUtc) >= RetryInterval)
        {
            _lastDbRetryUtc = nowUtc;
            if (TryTestDbConnection())
            {
                _dbHealthy = true;
                _consecutiveDbFailures = 0;
                CloseFallbackWriter();

                // Retry this event through the DB path
                string msg = logEvent.RenderMessage();
                TryEmitToDb(logEvent, msg, logEvent.Exception?.ToString(), GetSourceContext(logEvent));
                return;
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
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS system_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                exception TEXT NULL,
                source_context TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_system_logs_timestamp_utc ON system_logs(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_system_logs_level ON system_logs(level);
            """;
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
        if (_fallbackWriter is not null) return;

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
        _fallbackWriter?.Dispose();
        _fallbackWriter = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseFallbackWriter();
        }
    }
}
