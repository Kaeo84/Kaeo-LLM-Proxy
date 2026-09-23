using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the behaviour of the request-log read and prune queries. These were rewritten for
/// efficiency (removing a redundant derived table, an existence check that scanned the table, and an
/// in-memory id list), so the tests assert the observable contract rather than the SQL shape: the
/// rewrite must be invisible to callers.
/// </summary>
public class RequestLogQueryTests
{
    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left in
    /// the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-reqquery-{Guid.NewGuid():N}.db");

    private static AppDatabase Open(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    /// <summary>Inserts <paramref name="count"/> rows one minute apart, oldest first.</summary>
    private static void SeedMinutes(AppDatabase database, int count)
    {
        DateTime start = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Local);
        for (int i = 0; i < count; i++)
        {
            database.Insert(new RequestLog
            {
                Timestamp = start.AddMinutes(i),
                Method = "GET",
                OllamaPath = $"/entry/{i}",
                StatusCode = 200,
                Status = RequestStatus.Success,
            });
        }
    }

    // ── LoadRecent: most recent rows, returned oldest first ─────────────────

    [Fact]
    public void LoadRecentReturnsTheMostRecentRowsNotTheOldest()
    {
        // The trap this guards: ordering ascending in SQL would apply LIMIT to the *oldest* rows.
        // The limit must select the newest rows and only then be reversed for the caller.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 10);

        using AppDatabase database = Open(path);
        IReadOnlyList<RequestLog> recent = database.LoadRecent(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal(["/entry/7", "/entry/8", "/entry/9"], recent.Select(e => e.OllamaPath));
    }

    [Fact]
    public void LoadRecentReturnsOldestFirstSoTheQueueSeedsChronologically()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 5);

        using AppDatabase database = Open(path);
        IReadOnlyList<RequestLog> recent = database.LoadRecent(5);

        // Ascending by timestamp across the whole result.
        Assert.True(
            recent.Zip(recent.Skip(1)).All(pair => pair.First.Timestamp <= pair.Second.Timestamp),
            "LoadRecent must return entries in ascending timestamp order");
    }

    [Fact]
    public void LoadRecentRespectsTheLimitWhenFewerRowsExistThanRequested()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 2);

        using AppDatabase database = Open(path);
        IReadOnlyList<RequestLog> recent = database.LoadRecent(50);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void LoadRecentIsScopedToItsLogSource()
    {
        // Each source has its own table, so seeding the proxy queue must not pull in MCP rows.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            SeedMinutes(setup, 2);
            setup.Insert(
                new RequestLog
                {
                    Timestamp = new DateTime(2026, 9, 24, 13, 0, 0, DateTimeKind.Local),
                    Method = "POST",
                    OllamaPath = "/mcp/tool",
                    StatusCode = 200,
                    Status = RequestStatus.Success,
                },
                ex: null,
                source: LogSource.Mcp);
        }

        using AppDatabase database = Open(path);

        Assert.Equal(2, database.LoadRecent(50, LogSource.Proxy).Count);
        Assert.Single(database.LoadRecent(50, LogSource.Mcp));
    }

    // ── DeleteOlderThan: prunes requests and the exceptions they linked ─────

    [Fact]
    public void DeleteOlderThanRemovesOnlyRowsBeforeTheCutoff()
    {
        string path = NewDatabasePath();
        DateTime cutoff = new(2026, 9, 24, 12, 5, 0, DateTimeKind.Local);

        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 10);

        using AppDatabase database = Open(path);
        int deleted = database.DeleteOlderThan(cutoff);

        Assert.Equal(5, deleted); // entries 0..4
        IReadOnlyList<RequestLog> remaining = database.QueryRecent(50);
        Assert.Equal(5, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.OllamaPath == "/entry/0");
    }

    [Fact]
    public void DeleteOlderThanPrunesTheExceptionsLinkedToDeletedRequests()
    {
        // Rows deleted by the prune must not leave their exception detail behind, which was the
        // linkage the rewritten query has to preserve now that it no longer collects ids in C#.
        string path = NewDatabasePath();
        DateTime old = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Local);
        DateTime recent = new(2026, 9, 24, 12, 30, 0, DateTimeKind.Local);

        using (AppDatabase setup = Open(path))
        {
            RequestLog failedOld = new()
            {
                Timestamp = old,
                Method = "POST",
                OllamaPath = "/failed/old",
                StatusCode = 500,
                Status = RequestStatus.Error,
            };
            setup.Insert(failedOld, new InvalidOperationException("old failure"));

            RequestLog failedRecent = new()
            {
                Timestamp = recent,
                Method = "POST",
                OllamaPath = "/failed/recent",
                StatusCode = 500,
                Status = RequestStatus.Error,
            };
            setup.Insert(failedRecent, new InvalidOperationException("recent failure"));
        }

        int oldExceptionId;
        int recentExceptionId;
        using (AppDatabase database = Open(path))
        {
            oldExceptionId = database.LoadFullLogEntry(old)!.ExceptionId!.Value;
            recentExceptionId = database.LoadFullLogEntry(recent)!.ExceptionId!.Value;

            database.DeleteOlderThan(new DateTime(2026, 9, 24, 12, 15, 0, DateTimeKind.Local));
        }

        using AppDatabase verify = Open(path);

        // The pruned row's exception is gone; the surviving row's exception is untouched.
        Assert.Null(verify.GetException(oldExceptionId));
        Assert.NotNull(verify.GetException(recentExceptionId));
    }

    [Fact]
    public void DeleteOlderThanDeletesNothingWhenEverythingIsNewerThanTheCutoff()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 3);

        using AppDatabase database = Open(path);
        int deleted = database.DeleteOlderThan(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local));

        Assert.Equal(0, deleted);
        Assert.Equal(3, database.QueryRecent(50).Count);
    }

    [Fact]
    public void DeleteOlderThanHandlesMoreRowsThanWouldFitInABoundParameterList()
    {
        // The previous implementation bound one parameter per distinct exception id, so a large
        // backlog would build a list that eventually exceeds SQLITE_MAX_VARIABLE_NUMBER (32766).
        // The engine-side subquery has no such ceiling, so a few thousand rows must prune cleanly.
        string path = NewDatabasePath();
        DateTime start = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Local);

        using (AppDatabase setup = Open(path))
        {
            for (int i = 0; i < 3000; i++)
            {
                RequestLog log = new()
                {
                    Timestamp = start.AddSeconds(i),
                    Method = "POST",
                    OllamaPath = $"/bulk/{i}",
                    StatusCode = 500,
                    Status = RequestStatus.Error,
                };
                setup.Insert(log, new InvalidOperationException($"failure {i}"));
            }
        }

        using AppDatabase database = Open(path);
        int deleted = database.DeleteOlderThan(start.AddSeconds(2500));

        Assert.Equal(2500, deleted);
        Assert.Equal(500, database.QueryRecent(5000).Count);
    }
}