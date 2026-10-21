using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the System Logs query filters added for the Logs tab: a multi-select level set and a
/// free-text search. Both are built into dynamic SQL, so the risk these guard against is an
/// incorrectly assembled WHERE clause — a missing clause, a wildcard leaking through the search
/// term, or a level filter that silently matches nothing.
/// </summary>
public class SystemLogQueryTests
{
    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left in
    /// the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-syslog-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Deterministic seed epoch: rows are stamped with strictly increasing timestamps derived from
    /// it instead of <see cref="DateTime.UtcNow"/>, so the ORDER BY in the queries under test never
    /// depends on clock resolution or on rows landing inside the same tick.
    /// </summary>
    private static readonly DateTime SeedEpoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Inserts rows directly through SQLite so the test controls the exact level, message,
    /// exception, and source values the query has to filter on.
    /// </summary>
    private static void Seed(string path, params (string Level, string Message, string? Exception, string? Source)[] rows)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        for (int i = 0; i < rows.Length; i++)
        {
            (string level, string message, string? exception, string? source) = rows[i];

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO system_logs (timestamp_utc, level, message, exception, source_context) " +
                "VALUES ($ts, $level, $message, $exception, $source);";
            command.Parameters.AddWithValue("$ts", SeedEpoch.AddSeconds(i).ToString("O"));
            command.Parameters.AddWithValue("$level", level);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$exception", (object?)exception ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static AppDatabase Open(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    [Fact]
    public void NoFiltersReturnsEverySeededRow()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "started", null, "Proxy"),
                ("Warning", "slow response", null, "Proxy"),
                ("Error", "failed", "boom", "Proxy"));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs();

        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void MultipleLevelsReturnsOnlyTheSelectedLevels()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "started", null, null),
                ("Warning", "slow response", null, null),
                ("Error", "failed", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(
            levelFilters: ["Warning", "Error"]);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Contains(e.Level, new[] { "Warning", "Error" }));
    }

    [Fact]
    public void SingleLevelFilterReturnsOnlyThatLevel()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "started", null, null),
                ("Error", "failed", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(
            levelFilters: ["Error"]);

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("Error", only.Level);
    }

    [Fact]
    public void SearchMatchesTheMessage()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "handler registered", null, null),
                ("Information", "proxy started", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(searchText: "handler");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("handler registered", only.Message);
    }

    [Fact]
    public void SearchMatchesTheExceptionText()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Error", "request failed", "System.TimeoutException: timed out", null),
                ("Error", "request failed", "System.IO.IOException: broken pipe", null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(searchText: "Timeout");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Contains("Timeout", only.Exception);
    }

    [Fact]
    public void SearchMatchesTheSourceContext()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "connected", null, "Kaeo.LlmProxy.Heartbeat"),
                ("Information", "connected", null, "Kaeo.LlmProxy.Proxy"));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(searchText: "Heartbeat");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("Kaeo.LlmProxy.Heartbeat", only.SourceContext);
    }

    [Fact]
    public void SearchAndLevelFilterCombineWithAnd()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "proxy started", null, null),
                ("Error", "proxy failed", null, null),
                ("Warning", "proxy slow", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(
            levelFilters: ["Error"], searchText: "proxy");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("Error", only.Level);
    }

    [Fact]
    public void PercentInTheSearchTermIsTreatedAsALiteralNotAWildcard()
    {
        // The search term is escaped and the LIKE carries an ESCAPE clause, so a user pasting a
        // value containing % or _ searches for those characters rather than matching everything.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "usage is 50% of quota", null, null),
                ("Information", "unrelated line", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(searchText: "50%");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Contains("50%", only.Message);
    }

    [Fact]
    public void UnderscoreInTheSearchTermIsTreatedAsALiteralNotAWildcard()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "column is collect_all_traffic", null, null),
                ("Information", "totally different", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(searchText: "collect_all_traffic");

        SystemLogEntry only = Assert.Single(entries);
        Assert.Contains("collect_all_traffic", only.Message);
    }

    [Fact]
    public void LimitCapsTheNumberOfRowsReturned()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Information", "one", null, null),
                ("Information", "two", null, null),
                ("Information", "three", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(limit: 2);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void ExcludingHeartbeatsDropsOnlyTheMarkedRows()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} Periodic heartbeat failed for model llama", "boom", null),
                ("Information", "proxy started", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(excludeHeartbeats: true);

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("proxy started", only.Message);
    }

    [Fact]
    public void HeartbeatRowsAreMarkedOnTheEntrySoACallerCanRenderThemDifferently()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} Periodic heartbeat failed for model llama", "boom", null),
                ("Information", "proxy started", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs();

        Assert.True(entries.Single(e => e.Message.Contains("heartbeat failed")).IsHeartbeat);
        Assert.False(entries.Single(e => e.Message == "proxy started").IsHeartbeat);
    }

    [Fact]
    public void ExcludingHeartbeatsAppliesTheLimitAfterTheHeartbeatsAreRemoved()
    {
        // A failed ping repeats for every enabled model, so if the limit were applied before the
        // exclusion it could be consumed entirely by heartbeat rows and leave nothing to show.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} heartbeat one", null, null),
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} heartbeat two", null, null),
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} heartbeat three", null, null),
                ("Information", "proxy started", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(limit: 1, excludeHeartbeats: true);

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("proxy started", only.Message);
    }

    [Fact]
    public void ExcludingHeartbeatsLeavesAnUnmarkedDebugLineAlone()
    {
        // The split keys on the marker, so an ordinary Debug line at the same level survives; this is
        // what keeps the filter from being a second, silent threshold on everything at that severity.
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Debug", "ordinary debug detail", null, null),
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} heartbeat line", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(excludeHeartbeats: true);

        SystemLogEntry only = Assert.Single(entries);
        Assert.Equal("ordinary debug detail", only.Message);
    }

    [Fact]
    public void HeartbeatMarkerInTheMessageIsNotTreatedAsAWildcard()
    {
        string path = NewDatabasePath();
        using (AppDatabase setup = Open(path))
        {
            Seed(path,
                ("Debug", $"{AppLogger.HeartbeatMessageMarker} heartbeat one", null, null),
                ("Information", "unrelated", null, null));
        }

        using AppDatabase database = Open(path);
        IReadOnlyList<SystemLogEntry> entries = database.GetSystemLogs(excludeHeartbeats: true);

        Assert.DoesNotContain(entries, e => e.IsHeartbeat);
    }
}