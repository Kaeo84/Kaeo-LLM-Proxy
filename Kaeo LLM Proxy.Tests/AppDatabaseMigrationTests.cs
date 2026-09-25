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

    // ── runtime_settings: enable_ir_translation ───────────────────────────
    //
    // The IR translation switch is the first runtime setting added since the per-category capture
    // set. It differs from its neighbours in one way that matters: its reader ordinal is appended
    // LAST because LoadRuntimeSettings reads by position and deliberately keeps retired columns in
    // the SELECT list, so a column inserted anywhere earlier would silently shift every field after
    // it. These tests pin both halves of that: an existing database gains the column without moving
    // its neighbours, and the value survives a save and reload.

    [Fact]
    public void ExistingDatabaseGainsTheIrTranslationColumn()
    {
        string path = NewDatabasePath();

        // Simulate a database created before the column existed: build it, drop the column's value
        // by recreating the table without it, then reopen through AppDatabase and let the migration
        // repair it.
        using (SqliteConnection connection = new($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE runtime_settings (
                    id TEXT PRIMARY KEY,
                    auto_start_proxy INTEGER NOT NULL,
                    start_with_dashboard_open INTEGER NOT NULL,
                    allow_multiple_instances INTEGER NOT NULL,
                    show_close_to_tray_notification INTEGER NOT NULL,
                    collect_request_details INTEGER NOT NULL,
                    collect_response_details INTEGER NOT NULL,
                    debug_mode INTEGER NOT NULL DEFAULT 0,
                    enable_sse_keep_alive INTEGER NOT NULL,
                    sse_keep_alive_interval_seconds INTEGER NOT NULL,
                    enable_performance_sampling INTEGER NOT NULL DEFAULT 1,
                    enable_api_explorer INTEGER NOT NULL DEFAULT 0,
                    run_as_administrator INTEGER NOT NULL DEFAULT 0,
                    collect_all_traffic INTEGER NOT NULL DEFAULT 0,
                    heartbeat_interval_seconds INTEGER NOT NULL DEFAULT 300,
                    compaction_fallback_context_tokens INTEGER NOT NULL DEFAULT 8192,
                    collect_non_proxied_categories TEXT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        Assert.DoesNotContain("enable_ir_translation", ReadColumnNames(path, "runtime_settings"));

        using (AppDatabase database = OpenDatabase(path))
        {
            List<string> columns = ReadColumnNames(path, "runtime_settings");

            Assert.Contains("enable_ir_translation", columns);

            // The other columns must be untouched by the migration.
            Assert.Contains("collect_non_proxied_categories", columns);
            Assert.Contains("compaction_fallback_context_tokens", columns);
        }
    }

    [Fact]
    public void IrTranslationDefaultsToOffForADatabaseThatPredatesTheColumn()
    {
        string path = NewDatabasePath();

        // A pre-existing row with every legacy column populated.
        using (SqliteConnection connection = new($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE runtime_settings (
                    id TEXT PRIMARY KEY,
                    auto_start_proxy INTEGER NOT NULL,
                    start_with_dashboard_open INTEGER NOT NULL,
                    allow_multiple_instances INTEGER NOT NULL,
                    show_close_to_tray_notification INTEGER NOT NULL,
                    collect_request_details INTEGER NOT NULL,
                    collect_response_details INTEGER NOT NULL,
                    debug_mode INTEGER NOT NULL DEFAULT 0,
                    enable_sse_keep_alive INTEGER NOT NULL,
                    sse_keep_alive_interval_seconds INTEGER NOT NULL,
                    enable_performance_sampling INTEGER NOT NULL DEFAULT 1,
                    enable_api_explorer INTEGER NOT NULL DEFAULT 0,
                    run_as_administrator INTEGER NOT NULL DEFAULT 0,
                    collect_all_traffic INTEGER NOT NULL DEFAULT 0,
                    heartbeat_interval_seconds INTEGER NOT NULL DEFAULT 300,
                    compaction_fallback_context_tokens INTEGER NOT NULL DEFAULT 8192,
                    collect_non_proxied_categories TEXT NULL
                );

                INSERT INTO runtime_settings (
                    id, auto_start_proxy, start_with_dashboard_open, allow_multiple_instances,
                    show_close_to_tray_notification, collect_request_details, collect_response_details,
                    debug_mode, enable_sse_keep_alive, sse_keep_alive_interval_seconds,
                    enable_performance_sampling, enable_api_explorer, run_as_administrator,
                    collect_all_traffic, heartbeat_interval_seconds,
                    compaction_fallback_context_tokens, collect_non_proxied_categories)
                VALUES (
                    'current', 1, 0, 0, 1, 1, 0, 1, 1, 45, 1, 0, 1, 0, 333, 4096, NULL);
                """;
            create.ExecuteNonQuery();
        }

        using AppDatabase database = OpenDatabase(path);
        RuntimeSettings loaded = database.LoadRuntimeSettings();

        // Off by default: an upgrade must never silently route live traffic through the IR path.
        Assert.False(loaded.EnableIrTranslation);

        // And the neighbours the migration appended past must still read correctly, which is what
        // catches an ordinal shift.
        Assert.True(loaded.DebugMode);
        Assert.Equal(45, loaded.SseKeepAliveIntervalSeconds);
        Assert.Equal(333, loaded.HeartbeatIntervalSeconds);
        Assert.Equal(4096, loaded.CompactionFallbackContextTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IrTranslationSurvivesASaveAndReload(bool enabled)
    {
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            RuntimeSettings settings = new()
            {
                DebugMode = true,
                SseKeepAliveIntervalSeconds = 45,
                HeartbeatIntervalSeconds = 333,
                CompactionFallbackContextTokens = 4096,
                EnableIrTranslation = enabled,
            };
            setup.SaveRuntimeSettings(settings);
        }

        using AppDatabase database = OpenDatabase(path);
        RuntimeSettings reloaded = database.LoadRuntimeSettings();

        Assert.Equal(enabled, reloaded.EnableIrTranslation);

        // Reload through the settings facade too, since that is the hop the proxy actually reads.
        AppSettings app = new();
        app.ApplyRuntimeSettings(reloaded);
        Assert.Equal(enabled, app.UseIrTranslation);
    }

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
