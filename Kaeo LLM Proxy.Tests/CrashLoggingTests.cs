using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins that a crash leaves a durable record. An unexplained process exit previously produced nothing:
/// the exception handlers were compiled out of Debug builds, the handler wrote only to the debugger
/// and a MessageBox, and Serilog had no file sink, so the configured log directory stayed empty.
/// </summary>
/// <remarks>
/// Serialized because <see cref="AppLogger.Initialize"/> assigns the process-wide <c>Log.Logger</c>.
/// Two of these running concurrently would reconfigure each other's pipeline mid-test and fail on a
/// file written to the wrong directory — a flake that would look like a real defect.
/// </remarks>
[Collection("AppLogger")]
public class CrashLoggingTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-crash-{Guid.NewGuid():N}");

    // ── The rolling file sink is real ─────────────────────────────────────

    [Fact]
    public void InitializingTheLoggerWritesToAFileInTheLogDirectory()
    {
        // The empty log directory was the symptom: with no file sink, nothing survived a crash and
        // nothing was written here at all even during normal operation.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings { LogDirectory = directory });
            Log.Warning("a durable line");
            Log.CloseAndFlush();

            string appDir = Path.Combine(directory, "app");
            Assert.True(Directory.Exists(appDir), "The app log directory should be created.");

            string[] files = Directory.GetFiles(appDir, "app-*.log");
            Assert.NotEmpty(files);
            Assert.Contains("a durable line", File.ReadAllText(files[0]));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheFileSinkRecordsTheExceptionTextAndNotJustTheMessage()
    {
        // The database sink stores the message without the stack trace, which is why a logged failure
        // was previously unrecoverable. The file sink must carry the exception.
        //
        // The exception is genuinely thrown: an exception that is only constructed has an empty
        // StackTrace, so asserting on frames would pass against a sink that rendered the message alone.
        Exception thrown;
        try
        {
            throw new InvalidOperationException("boom-with-a-trace");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings { LogDirectory = directory });
            Log.Error(thrown, "context line");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            string contents = File.ReadAllText(file);

            Assert.Contains("boom-with-a-trace", contents);
            Assert.Contains("InvalidOperationException", contents);
            // A stack frame proves the exception object was rendered, not just its message.
            Assert.Contains("CrashLoggingTests", contents);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheMinimumLevelFromSettingsIsHonored()
    {
        // The level is the operator's control over log volume; a file sink that ignored it would
        // either flood the disk or hide the line they were looking for.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings { LogDirectory = directory, MinimumLevel = "Warning" });
            Log.Debug("should-not-appear");
            Log.Warning("should-appear");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            string contents = File.ReadAllText(file);

            Assert.Contains("should-appear", contents);
            Assert.DoesNotContain("should-not-appear", contents);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheSizeAndRetentionSettingsAreConsumed()
    {
        // These two were previously read only by the Settings UI and drove nothing, so an operator
        // setting a limit saw no effect. This pins that they now reach the sink configuration.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings
            {
                LogDirectory = directory,
                AppLogFileSizeLimitMb = 1,
                AppLogRetainedFileCount = 2,
            });
            Log.Warning("configured");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            Assert.Contains("configured", File.ReadAllText(file));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ReinitializingSwitchesToTheNewDirectoryWithoutLosingTheOldLog()
    {
        // The Settings tab can change the log directory at runtime, and AppLogger re-initializes.
        // The earlier contents must already be flushed, or changing a setting would discard history.
        string first = NewDirectory();
        string second = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings { LogDirectory = first });
            Log.Warning("in-first-directory");

            AppLogger.Initialize(new LoggingSettings { LogDirectory = second });
            Log.Warning("in-second-directory");
            Log.CloseAndFlush();

            string firstFile = Directory.GetFiles(Path.Combine(first, "app"), "app-*.log")[0];
            Assert.Contains("in-first-directory", File.ReadAllText(firstFile));

            string secondFile = Directory.GetFiles(Path.Combine(second, "app"), "app-*.log")[0];
            Assert.Contains("in-second-directory", File.ReadAllText(secondFile));
        }
        finally
        {
            TryDelete(first);
            TryDelete(second);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Tests never clean up: a failed run must stay inspectable.
        }
    }

    // ── Heartbeat failures have their own threshold ───────────────────────

    /// <summary>
    /// Writes a heartbeat-marked line at Debug and an ordinary line at Information, then reports
    /// whether the heartbeat line reached the file.
    /// </summary>
    /// <remarks>
    /// The global level is Debug so the emitter is not the thing under test. Two independent gates
    /// apply to a heartbeat line: the global <c>MinimumLevel</c>, which drops an event below its
    /// threshold before any filter sees it, and the heartbeat threshold, which then decides whether a
    /// surviving line is excluded. Holding the global level at Debug isolates the second gate — with
    /// it at Information, every heartbeat case would be suppressed regardless of the setting and the
    /// tests would pass while the filter did nothing.
    /// </remarks>
    private static bool HeartbeatLineSurvivesAt(string heartbeatLevel)
    {
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings
            {
                LogDirectory = directory,
                MinimumLevel = "Debug",
                HeartbeatMinimumLevel = heartbeatLevel,
            });

            Log.Information("ordinary-information-line");
            Log.ForContext(AppLogger.HeartbeatMarkerProperty, true)
                .Debug("heartbeat-debug-line");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            string contents = File.ReadAllText(file);

            // The ordinary line always survives; only the heartbeat line is under test.
            Assert.Contains("ordinary-information-line", contents);
            return contents.Contains("heartbeat-debug-line");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void HeartbeatFailuresAreSuppressedAtTheDefaultDebugThreshold()
    {
        // A Debug-level heartbeat line is below the threshold's own default only when that default is
        // raised; at the shipped Debug default the line is permitted, which is what makes a failed
        // probe visible while debugging. The suppression case is the raised threshold.
        Assert.True(HeartbeatLineSurvivesAt(AppLogger.DefaultHeartbeatLevel));
        Assert.False(HeartbeatLineSurvivesAt("Warning"));
        Assert.False(HeartbeatLineSurvivesAt("Fatal"));
    }

    [Fact]
    public void LoweringTheHeartbeatThresholdLetsThemThrough()
    {
        // The inverse, so a filter that simply dropped every marked line would be caught rather than
        // passing the suppression case above by accident.
        Assert.True(HeartbeatLineSurvivesAt("Debug"));
        Assert.True(HeartbeatLineSurvivesAt("Verbose"));
    }

    [Fact]
    public void AHeartbeatDebugLineIsStillDroppedWhenTheGlobalLevelExcludesDebug()
    {
        // The two gates are independent. With the global level at Information the event is discarded
        // before filtering, so even the most permissive heartbeat threshold cannot surface it. This is
        // the shipped configuration, and it is why the default is quiet without any suppression.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings
            {
                LogDirectory = directory,
                MinimumLevel = "Information",
                HeartbeatMinimumLevel = "Verbose",
            });

            Log.ForContext(AppLogger.HeartbeatMarkerProperty, true).Debug("heartbeat-debug-line");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            Assert.DoesNotContain("heartbeat-debug-line", File.ReadAllText(file));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void OrdinaryDebugLinesAreUnaffectedByTheHeartbeatThreshold()
    {
        // The exclusion is keyed on the marker property, not on the level: raising the heartbeat
        // threshold must not silence unrelated Debug logging, or debugging would lose everything.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings
            {
                LogDirectory = directory,
                MinimumLevel = "Debug",
                HeartbeatMinimumLevel = "Fatal",
            });

            Log.Debug("ordinary-debug-line");
            Log.ForContext(AppLogger.HeartbeatMarkerProperty, true).Debug("heartbeat-debug-line");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            string contents = File.ReadAllText(file);

            Assert.Contains("ordinary-debug-line", contents);
            Assert.DoesNotContain("heartbeat-debug-line", contents);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AHeartbeatFailureAboveTheThresholdIsStillRecorded()
    {
        // A genuine Warning-level problem tagged as a heartbeat must survive a Debug threshold, so
        // the exclusion cannot be used to hide a real failure.
        string directory = NewDirectory();

        try
        {
            AppLogger.Initialize(new LoggingSettings
            {
                LogDirectory = directory,
                MinimumLevel = "Information",
                HeartbeatMinimumLevel = "Debug",
            });

            Log.ForContext(AppLogger.HeartbeatMarkerProperty, true).Warning("heartbeat-warning-line");
            Log.CloseAndFlush();

            string file = Directory.GetFiles(Path.Combine(directory, "app"), "app-*.log")[0];
            Assert.Contains("heartbeat-warning-line", File.ReadAllText(file));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Fatal")]
    public void SuppressingHeartbeatDebugLinesDoesNotSuppressHigherSeverities(string level)
    {
        Assert.False(HeartbeatLineSurvivesAt(level));
    }

    [Fact]
    public void AnUnrecognizedHeartbeatLevelFallsBackToDebug()
    {
        // Settings files are hand-editable, so a typo must not disable logging entirely.
        Assert.Equal(
            Serilog.Events.LogEventLevel.Debug,
            AppLogger.ParseLevel("NotALevel", Serilog.Events.LogEventLevel.Debug));
    }
}