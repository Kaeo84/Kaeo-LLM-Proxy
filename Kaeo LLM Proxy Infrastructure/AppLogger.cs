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
    /// <remarks>
    /// A rolling file sink is included deliberately. The database sink cannot record a failure that
    /// kills the process before the entry is flushed, and a crash is exactly the case that most needs
    /// a durable record — which is why an unexplained exit could previously leave nothing behind
    /// anywhere. The file sink writes synchronously so the last entry survives process death, and it
    /// is the consumer of <c>AppLogFileSizeLimitMb</c> / <c>AppLogRetainedFileCount</c>, which had no
    /// effect on anything before.
    /// </remarks>
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
            .WriteTo.File(
                Path.Combine(appLogDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: Math.Max(1, settings.AppLogFileSizeLimitMb) * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: Math.Max(1, settings.AppLogRetainedFileCount),
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _initialized = true;
        Log.Information("AppLogger initialized. Level={Level} DbPath={DbPath} Fallback={Fallback} File={File}",
            level, dbPath, fallbackPath, appLogDir);
    }

    /// <summary>Flushes and closes the current logger. Call on application exit.</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
