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

    // ── QueryRecent: newest first, optional user/source scoping ─────────────

    [Fact]
    public void QueryRecentReturnsNewestFirst()
    {
        // The web dashboard's fresh load renders these rows top-down, so the newest entry must
        // come first — the opposite of LoadRecent's chronological queue seeding.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
            SeedMinutes(setup, 10);

        using AppDatabase database = Open(path);
        IReadOnlyList<RequestLog> recent = database.QueryRecent(3);

        Assert.Equal(["/entry/9", "/entry/8", "/entry/7"], recent.Select(e => e.OllamaPath));
    }

    [Fact]
    public void QueryRecentFiltersByUserAndExcludesUnattributedRows()
    {
        string path = NewDatabasePath();
        DateTime start = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Local);
        using (AppDatabase setup = Open(path))
        {
            setup.Insert(new RequestLog { Timestamp = start, Method = "GET", OllamaPath = "/mine", StatusCode = 200, Status = RequestStatus.Success, UserId = 7 });
            setup.Insert(new RequestLog { Timestamp = start.AddMinutes(1), Method = "GET", OllamaPath = "/theirs", StatusCode = 200, Status = RequestStatus.Success, UserId = 8 });
            setup.Insert(new RequestLog { Timestamp = start.AddMinutes(2), Method = "GET", OllamaPath = "/open", StatusCode = 200, Status = RequestStatus.Success });
        }

        using AppDatabase database = Open(path);
        Assert.Equal("/mine", Assert.Single(database.QueryRecent(50, LogSource.Proxy, userId: 7)).OllamaPath);
        Assert.Equal(3, database.QueryRecent(50).Count);
    }

    [Fact]
    public void QueryRecentIsScopedToItsLogSource()
    {
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

        Assert.Equal(2, database.QueryRecent(50, LogSource.Proxy).Count);
        Assert.Equal("/mcp/tool", Assert.Single(database.QueryRecent(50, LogSource.Mcp)).OllamaPath);
    }

    [Fact]
    public void LoadRecentCarriesUserAttribution()
    {
        // Startup queue seeding must preserve user_id so read-only incremental polls can filter
        // entries that predate the current process.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            setup.Insert(new RequestLog
            {
                Timestamp = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Local),
                Method = "GET",
                OllamaPath = "/attributed",
                StatusCode = 200,
                Status = RequestStatus.Success,
                UserId = 42,
            });
        }

        using AppDatabase database = Open(path);
        Assert.Equal(42, Assert.Single(database.LoadRecent(10)).UserId);
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
    public void DeleteOlderThanPrunesALargeBacklogAndItsLinkedExceptions()
    {
        // The previous implementation collected the distinct exception ids in memory and rebound them
        // as one parameter per id, so a large backlog built a list that would eventually exceed
        // SQLITE_MAX_VARIABLE_NUMBER (32766). The delete is now driven by an engine-side subquery
        // with a single bound parameter, so the ceiling is gone structurally rather than by row count
        // — which is why this no longer seeds thousands of rows to chase it. Seeding 3000 did not
        // reach the ceiling anyway; it only made the fixture cost 2.8s of every run, since each
        // Insert opens its own connection and transaction.
        //
        // What is still pinned: a backlog far larger than a normal test seeds prunes to the exact
        // count, and the linked exception rows go with it.
        string path = NewDatabasePath();
        DateTime start = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Local);

        using (AppDatabase setup = Open(path))
        {
            for (int i = 0; i < 300; i++)
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
        int deleted = database.DeleteOlderThan(start.AddSeconds(250));

        Assert.Equal(250, deleted);
        Assert.Equal(50, database.QueryRecent(500).Count);
    }
}