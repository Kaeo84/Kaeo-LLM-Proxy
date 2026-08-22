using Kaeo.LlmProxy.Core.Models;
using Serilog;
using Serilog.Events;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Bootstraps Serilog for application diagnostic logging.
/// Primary persistent store is the <c>system_logs</c> table in the application database.
/// Falls back to a CLEF flat file under {LogDirectory}/app/ when the database is unavailable.
/// An in-memory sink provides real-time access for the System Logs GUI tab.
/// </summary>
internal static class AppLogger
{
    private static bool _initialized;

    /// <summary>
    /// In-memory sink for real-time display in the System Logs tab.
    /// Accessible from MainForm without coupling to the Serilog pipeline.
    /// </summary>
    public static SystemLogSink SysLog { get; private set; } = new();

    /// <summary>
    /// The DB-backed sink instance. Exposed so the GUI can check whether the DB is healthy
    /// and whether logs are being written to the fallback file.
    /// </summary>
    public static SystemLogDbSink? DbSink { get; private set; }

    /// <summary>
    /// Configures and assigns <see cref="Log.Logger"/> from the supplied settings.
    /// Safe to call multiple times — reconfigures on subsequent calls.
    /// </summary>
    public static void Initialize(LoggingSettings settings)
    {
        // Close any existing logger before reconfiguring.
        if (_initialized)
        {
            Log.CloseAndFlush();
            DbSink?.Dispose();
        }

        string appLogDir = Path.Combine(settings.LogDirectory, "app");
        Directory.CreateDirectory(appLogDir);

        if (!Enum.TryParse<LogEventLevel>(settings.MinimumLevel, ignoreCase: true, out LogEventLevel level))
            level = LogEventLevel.Information;

        var syslog = new SystemLogSink();
        SysLog = syslog;

        string dbPath = settings.GetApplicationDatabasePath();
        string fallbackPath = Path.Combine(appLogDir, "system-logs.fallback.clef");
        var dbSink = new SystemLogDbSink(dbPath, fallbackPath);
        DbSink = dbSink;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Sink(syslog)
            .WriteTo.Sink(dbSink)
            .CreateLogger();

        _initialized = true;
        Log.Information("AppLogger initialized. Level={Level} DbPath={DbPath} Fallback={Fallback}",
            level, dbPath, fallbackPath);
    }

    /// <summary>Flushes and closes the current logger. Call on application exit.</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
