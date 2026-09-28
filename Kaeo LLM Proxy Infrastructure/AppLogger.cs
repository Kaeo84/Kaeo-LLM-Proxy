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

    /// <summary>Default threshold for heartbeat-failure logging when settings cannot be read.</summary>
    internal const string DefaultHeartbeatLevel = "Debug";

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
    /// Minimum level for periodic heartbeat failures, from
    /// <see cref="LoggingSettings.HeartbeatMinimumLevel"/>.
    /// </summary>
    /// <remarks>
    /// Applied as a logger-wide exclusion in <see cref="Initialize"/> so it governs every sink at
    /// once. The in-memory and database sinks cannot take their own Serilog filter, so a per-sink
    /// threshold would let a heartbeat failure vanish from the file while still appearing in the
    /// System Logs tab, or the reverse.
    /// </remarks>
    private static LogEventLevel _heartbeatMinimumLevel = LogEventLevel.Debug;

    /// <summary>
    /// Event-property name marking a periodic heartbeat failure, so the logger-wide exclusion can
    /// recognize one without matching on message text.
    /// </summary>
    internal const string HeartbeatMarkerProperty = "IsHeartbeat";

    /// <summary>
    /// Parses a Serilog level name, falling back when the value is missing or unrecognized.
    /// </summary>
    internal static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse(value, ignoreCase: true, out LogEventLevel level) ? level : fallback;

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

        _heartbeatMinimumLevel = ParseLevel(settings.HeartbeatMinimumLevel, LogEventLevel.Debug);

        var syslog = new SystemLogSink();
        SysLog = syslog;

        string dbPath = settings.GetApplicationDatabasePath();
        string fallbackPath = Path.Combine(appLogDir, "system-logs.fallback.clef");
        var dbSink = new SystemLogDbSink(dbPath, fallbackPath);
        DbSink = dbSink;

        // A single event-property marker identifies heartbeat failures so the logger-wide exclusion below
        // can apply its own threshold without a brittle prefix match on the message. Trim() gives
        // middle-of-path matching without pulling in Regex.
        const string heartbeatProperty = HeartbeatMarkerProperty;

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
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                // The file sink's own floor is Verbose so nothing is dropped by the level check;
                // the exclusion below is what implements the heartbeat threshold.
                restrictedToMinimumLevel: LogEventLevel.Verbose)
            .Filter.ByExcluding(logEvent =>
                logEvent.Properties.TryGetValue(heartbeatProperty, out LogEventPropertyValue? marker)
                && marker is ScalarValue { Value: bool isHeartbeat }
                && isHeartbeat
                && logEvent.Level < _heartbeatMinimumLevel)
            .CreateLogger();

        _initialized = true;
        Log.Information(
            "AppLogger initialized. Level={Level} HeartbeatLevel={HeartbeatLevel} DbPath={DbPath} Fallback={Fallback} File={File}",
            level, _heartbeatMinimumLevel, dbPath, fallbackPath, appLogDir);
    }

    /// <summary>Flushes and closes the current logger. Call on application exit.</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
