using System.Net;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Kaeo.LlmProxy.Services;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins how a post-header passthrough failure is reported: a message the client can act on that
/// leaks nothing internal, and a full exception recorded server-side where the client cannot see it.
/// </summary>
/// <remarks>
/// The failure these exist because of: an upstream error arriving after the SSE headers were committed
/// produced only <c>"Passthrough failed after the SSE headers were committed"</c>. The exception went
/// to Serilog, whose database sink stores the message without the stack trace, and the path never
/// recorded an <c>exception_id</c> — so the cause was unrecoverable from the request log, and the
/// upstream's own error body had been discarded.
/// </remarks>
public class PassthroughFailureReportingTests
{
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-passthrough-{Guid.NewGuid():N}.db");

    private static AppDatabase OpenDatabase(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    // ── The exception is persisted and linked ─────────────────────────────

    [Fact]
    public void AnAttachedExceptionIsPersistedAndLinked()
    {
        // The mechanism the passthrough path now relies on: a handler attaches the exception, and the
        // single persistence point stores the detail and writes back the id.
        string path = NewDatabasePath();

        RequestLog log = new()
        {
            Method = "POST",
            OllamaPath = "/v1/chat/completions",
            Model = "QC Deepseek 4.1 Flash",
        };
        log.Exception = new InvalidOperationException("upstream mid-stream failure");

        using AppDatabase database = OpenDatabase(path);
        database.Insert(log, log.Exception);

        Assert.NotNull(log.ExceptionId);

        ExceptionDetail? detail = database.GetException(log.ExceptionId!.Value);
        Assert.NotNull(detail);
        Assert.Contains("upstream mid-stream failure", detail!.Message);
        Assert.Equal("POST", detail.Method);
    }

    [Fact]
    public void AnEntryWithNoExceptionRecordsNoExceptionId()
    {
        // Only failures that would otherwise be undiagnosable attach one; a normal entry must not
        // acquire an exception row.
        string path = NewDatabasePath();

        RequestLog log = new()
        {
            Method = "POST",
            OllamaPath = "/v1/chat/completions",
            Model = "main",
        };

        using AppDatabase database = OpenDatabase(path);
        database.Insert(log, log.Exception);

        Assert.Null(log.ExceptionId);
    }

    [Fact]
    public void TheExceptionIsTransientAndNotAPersistedColumn()
    {
        // It carries the exception to the persistence point; it is not schema. Storing it would
        // duplicate what the exceptions table already holds.
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);
        List<string> columns = ReadColumnNames(path, "requests");

        Assert.DoesNotContain("exception", columns);
        Assert.Contains("exception_id", columns);
    }

    // ── Nothing internal reaches the client ───────────────────────────────

    [Fact]
    public void NoClientFacingDescriptionEchoesInternalDetail()
    {
        // An HttpRequestException carries the upstream URI, and this text is written into an SSE
        // error frame the client reads. Sweeping the realistic exception shapes guards the whole
        // helper rather than one branch, and would catch a careless future arm.
        Exception[] causes =
        [
            new HttpRequestException("Connection refused (127.0.0.1:8080)"),
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
            new TimeoutException("The operation timed out after 100 seconds."),
            new IOException("Unable to read data from the transport connection: 192.168.101.199:8080."),
            new InvalidOperationException("The response ended prematurely while 10.0.0.5 was streaming."),
        ];

        foreach (Exception cause in causes)
        {
            string description = OllamaProxyHandler.DescribePassthroughFailureForTest(cause);

            Assert.DoesNotContain("127.0.0.1", description);
            Assert.DoesNotContain("192.168.101.199", description);
            Assert.DoesNotContain("10.0.0.5", description);
            Assert.DoesNotContain("http", description);
            Assert.DoesNotContain("8080", description);

            // And it must still say something useful rather than being empty.
            Assert.False(string.IsNullOrWhiteSpace(description));
        }
    }

    [Fact]
    public void ATimeoutIsStillNamedForTheClient()
    {
        // The two cases a client can act on must survive the tightening, or the leak fix would have
        // traded one problem for another.
        Assert.Contains("timed out", OllamaProxyHandler.DescribePassthroughFailureForTest(new TimeoutException("x9")));
        Assert.Contains("timed out", OllamaProxyHandler.DescribePassthroughFailureForTest(new TaskCanceledException("x9")));
    }

    private static List<string> ReadColumnNames(string path, string table)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        List<string> columns = [];
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return columns;
    }
}