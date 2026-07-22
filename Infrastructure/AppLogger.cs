using Kaeo.LlmProxy.Core.Models;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Bootstraps Serilog for application diagnostic logging.
/// Writes CLEF (Compact Log Event Format) files under {LogDirectory}/app/.
/// Files roll when they exceed the configured size limit; oldest retained files are pruned.
/// </summary>
internal static class AppLogger
{
    private static bool _initialized;

    /// <summary>
    /// Configures and assigns <see cref="Log.Logger"/> from the supplied settings.
    /// Safe to call multiple times — reconfigures on subsequent calls.
    /// </summary>
    public static void Initialize(LoggingSettings settings)
    {
        // Close any existing logger before reconfiguring.
        if (_initialized)
            Log.CloseAndFlush();

        string appLogDir = Path.Combine(settings.LogDirectory, "app");
        Directory.CreateDirectory(appLogDir);

        string logFilePath = Path.Combine(appLogDir, "app-.clef");

        if (!Enum.TryParse<LogEventLevel>(settings.MinimumLevel, ignoreCase: true, out LogEventLevel level))
            level = LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: logFilePath,
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: (long)settings.AppLogFileSizeLimitMb * 1024 * 1024,
                retainedFileCountLimit: settings.AppLogRetainedFileCount,
                // shared: true allows multiple application instances (AllowMultipleInstances) to
                // write to the same log file concurrently via mutex-protected IO instead of each
                // instance throwing IOException and silently losing its diagnostic logs.
                shared: true,
                buffered: false)
            .CreateLogger();

        _initialized = true;
        Log.Information("AppLogger initialized. Level={Level} LogDir={LogDir}", level, appLogDir);
    }

    /// <summary>Flushes and closes the current logger. Call on application exit.</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
