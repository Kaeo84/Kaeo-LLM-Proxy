using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the schema migrations that repair databases left behind by an older build. Each test
/// reproduces the exact on-disk state an older version produced and asserts that opening the
/// database through <see cref="AppDatabase"/> heals it, so a regression cannot reappear as a
/// startup crash on an existing install.
/// </summary>
public class AppDatabaseMigrationTests
{
    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left in
    /// the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-migrate-{Guid.NewGuid():N}.db");

    private static AppDatabase OpenDatabase(string path) =>
        new(new LoggingSettings { ApplicationDatabasePath = path });

    private static List<string> ReadColumnNames(string path, string table)
    {
        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        List<string> columns = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return columns;
    }

    [Fact]
    public void LegacyMcpRequestsPathColumnIsRenamedToUpstreamPath()
    {
        // A build that briefly declared the MCP column as "path" left existing databases with that
        // name on disk. CREATE TABLE IF NOT EXISTS never revisits an existing table, so every query
        // naming upstream_path failed at startup with "no such column: upstream_path".
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            // Recreate the legacy on-disk state: a fully valid table whose MCP column is named path.
            using SqliteConnection connection = new($"Data Source={path}");
            connection.Open();

            using SqliteCommand rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE mcp_requests RENAME COLUMN upstream_path TO path;";
            rename.ExecuteNonQuery();
        }

        Assert.Contains("path", ReadColumnNames(path, "mcp_requests"));

        using AppDatabase database = OpenDatabase(path);
        List<string> columns = ReadColumnNames(path, "mcp_requests");

        Assert.Contains("upstream_path", columns);
        Assert.DoesNotContain("path", columns);
    }

    [Fact]
    public void LegacyMcpRequestsDatabaseLoadsRecentWithoutThrowing()
    {
        // The reported crash surfaced from StatisticsService seeding the MCP queue via LoadRecent,
        // so the heal is only real if that exact read succeeds afterward.
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            using SqliteConnection connection = new($"Data Source={path}");
            connection.Open();

            using SqliteCommand rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE mcp_requests RENAME COLUMN upstream_path TO path;";
            rename.ExecuteNonQuery();
        }

        using AppDatabase database = OpenDatabase(path);

        IReadOnlyList<RequestLog> recent = database.LoadRecent(50, LogSource.Mcp);

        Assert.Empty(recent);
    }

    [Fact]
    public void LegacyMcpRequestsPathColumnRenameIsIdempotent()
    {
        // Opening an already-healed database must not undo the migration or throw.
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            using SqliteConnection connection = new($"Data Source={path}");
            connection.Open();

            using SqliteCommand rename = connection.CreateCommand();
            rename.CommandText = "ALTER TABLE mcp_requests RENAME COLUMN upstream_path TO path;";
            rename.ExecuteNonQuery();
        }

        using (AppDatabase first = OpenDatabase(path))
        {
            Assert.Contains("upstream_path", ReadColumnNames(path, "mcp_requests"));
        }

        using AppDatabase second = OpenDatabase(path);

        Assert.Contains("upstream_path", ReadColumnNames(path, "mcp_requests"));
    }
}
