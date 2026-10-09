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
    private bool _dbHealthy = true;
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
        string? sourceContext = logEvent.Properties
            .TryGetValue(SourceContextKey, out var sc)
                ? sc.ToString().Trim('"')
                : null;

        lock (_lock)
        {
            if (_dbHealthy)
                TryEmitToDb(logEvent, renderedMessage, exception, sourceContext);
            else
                TryEmitToFallback(logEvent);
        }
    }

    private void TryEmitToDb(LogEvent logEvent, string message, string? exception, string? sourceContext)
    {
        try
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            EnsureTableExists(connection);

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

            // Successful write — reset failure counter
            _consecutiveDbFailures = 0;
        }
        catch
        {
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
                TryEmitToDb(logEvent, msg, logEvent.Exception?.ToString(),
                    logEvent.Properties.TryGetValue(SourceContextKey, out var sc)
                        ? sc.ToString().Trim('"') : null);
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

        _fallbackWriter = new StreamWriter(
            File.Open(_fallbackFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
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
