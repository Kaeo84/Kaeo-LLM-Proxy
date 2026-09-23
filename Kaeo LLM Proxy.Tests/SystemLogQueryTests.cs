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
    /// Inserts rows directly through SQLite so the test controls the exact level, message,
    /// exception, and source values the query has to filter on.
    /// </summary>
    private static void Seed(string path, params (string Level, string Message, string? Exception, string? Source)[] rows)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        foreach ((string level, string message, string? exception, string? source) in rows)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO system_logs (timestamp_utc, level, message, exception, source_context) " +
                "VALUES ($ts, $level, $message, $exception, $source);";
            command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
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
}