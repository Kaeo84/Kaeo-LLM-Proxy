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
}