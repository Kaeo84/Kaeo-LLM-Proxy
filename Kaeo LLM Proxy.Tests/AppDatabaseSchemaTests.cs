using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Pins the application schema baseline. The schema is no longer built up by incremental
/// migrations: a single baseline DDL creates the complete current schema, and every statement in it
/// is <c>IF NOT EXISTS</c> so opening an existing database leaves its data alone.
/// </summary>
/// <remarks>
/// These tests exist because the baseline is now the only schema authority. Anything reached solely
/// through a migration would be invisible, and a column that a reader needs but the baseline omits
/// would only surface at runtime as "no such column". The round-trip tests therefore cover every
/// setting the runtime reader consumes, which is what makes an ordinal shift or a missing column a
/// test failure rather than a launch-time crash.
/// </remarks>
public class AppDatabaseSchemaTests
{
    /// <summary>
    /// Randomized per test so runs never contend over a file. Databases are intentionally left in
    /// the temp directory rather than deleted, so a failed run can still be inspected.
    /// </summary>
    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"kaeo-schema-{Guid.NewGuid():N}.db");

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

    // ── The baseline creates the complete schema ──────────────────────────

    [Fact]
    public void FreshDatabaseCreatesEveryTable()
    {
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);

        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        List<string> tables = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        foreach (string expected in new[]
        {
            "exceptions", "requests", "mcp_requests", "non_proxied_requests",
            "model_mappings", "instruction_sets", "credentials", "sse_keep_alive",
            "runtime_settings", "module_registry", "mcp_server_settings", "system_logs",
        })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void RuntimeSettingsBaselineCarriesEveryColumnTheReaderConsumes()
    {
        // The regression this guards: enable_copilot_compaction_routing was added to the SELECT, the
        // INSERT and a migration but was omitted from the baseline CREATE TABLE, so a brand-new
        // database failed at LoadRuntimeSettings with "no such column".
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);
        List<string> columns = ReadColumnNames(path, "runtime_settings");

        Assert.Contains("enable_copilot_compaction_routing", columns);
        Assert.Contains("copilot_compaction_model_name", columns);
        Assert.Contains("collect_non_proxied_categories", columns);
        Assert.Contains("enable_ir_translation", columns);
        Assert.Contains("compaction_fallback_context_tokens", columns);
    }

    [Fact]
    public void ModelMappingsBaselineCarriesEveryColumnTheReaderConsumes()
    {
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);
        List<string> columns = ReadColumnNames(path, "model_mappings");

        Assert.Contains("hidden", columns);
        Assert.Contains("context_summarize_model_name", columns);
        Assert.Contains("context_summarize_model_id", columns);
        Assert.Contains("enable_copilot_compatibility", columns);
    }

    [Fact]
    public void ModelMappingsBaselineDropsTheRetiredColumns()
    {
        // These are gone from the schema entirely: supports_reasoning_effort and adaptive_thinking
        // were never read by anything, and copilot_compatible_compaction was retired when Copilot
        // compaction became an installation-wide setting.
        string path = NewDatabasePath();

        using AppDatabase database = OpenDatabase(path);
        List<string> columns = ReadColumnNames(path, "model_mappings");

        Assert.DoesNotContain("copilot_compatible_compaction", columns);
        Assert.DoesNotContain("supports_reasoning_effort", columns);
        Assert.DoesNotContain("adaptive_thinking", columns);
    }

    // ── Settings survive a save and reload ────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopilotCompactionRoutingSurvivesASaveAndReload(bool enabled)
    {
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            setup.SaveRuntimeSettings(new RuntimeSettings
            {
                CopilotCompactionModelName = "global-compact",
                EnableCopilotCompactionRouting = enabled,
            });
        }

        using AppDatabase database = OpenDatabase(path);
        RuntimeSettings reloaded = database.LoadRuntimeSettings();

        Assert.Equal(enabled, reloaded.EnableCopilotCompactionRouting);
        Assert.Equal("global-compact", reloaded.CopilotCompactionModelName);

        // Reload through the settings facade too, since that is the hop the proxy actually reads.
        AppSettings app = new();
        app.ApplyRuntimeSettings(reloaded);

        Assert.Equal(enabled, app.EnableCopilotCompactionRouting);
        Assert.Equal("global-compact", app.CopilotCompactionModelName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IrTranslationSurvivesASaveAndReload(bool enabled)
    {
        string path = NewDatabasePath();

        using (AppDatabase setup = OpenDatabase(path))
        {
            setup.SaveRuntimeSettings(new RuntimeSettings
            {
                DebugMode = true,
                SseKeepAliveIntervalSeconds = 45,
                HeartbeatIntervalSeconds = 333,
                CompactionFallbackContextTokens = 4096,
                EnableIrTranslation = enabled,
            });
        }

        using AppDatabase database = OpenDatabase(path);
        RuntimeSettings reloaded = database.LoadRuntimeSettings();

        Assert.Equal(enabled, reloaded.EnableIrTranslation);

        // The neighbours must read correctly too, which is what catches an ordinal shift.
        Assert.True(reloaded.DebugMode);
        Assert.Equal(45, reloaded.SseKeepAliveIntervalSeconds);
        Assert.Equal(333, reloaded.HeartbeatIntervalSeconds);
        Assert.Equal(4096, reloaded.CompactionFallbackContextTokens);

        AppSettings app = new();
        app.ApplyRuntimeSettings(reloaded);
        Assert.Equal(enabled, app.UseIrTranslation);
    }

    [Fact]
    public void ModelMappingSurfaceSurvivesASaveAndReload()
    {
        string path = NewDatabasePath();

        ModelMapping mapping = new()
        {
            ProxyName = "main",
            ModelName = "main-upstream",
            UpstreamUrl = "http://localhost:8080",
            Hidden = true,
            ContextWindowTokens = 65536,
            AutoCompactPaths = AutoCompactPaths.ProxyOnly,
            RedirectManualCompaction = true,
            ContextSummarizeModelName = "compactor",
            ContextSummarizeModelId = 42,
            ProactiveOverflowPercent = 85,
            ProactiveOverflowTokens = 12000,
            EnableCopilotCompatibility = false,
        };
        mapping.EnsureId();

        using (AppDatabase setup = OpenDatabase(path))
            setup.SaveModelMappings([mapping]);

        using AppDatabase database = OpenDatabase(path);
        IReadOnlyList<ModelMapping> loaded = database.LoadModelMappings();

        ModelMapping reloaded = Assert.Single(loaded);
        Assert.Equal("main", reloaded.ProxyName);
        Assert.True(reloaded.Hidden);
        Assert.Equal(65536, reloaded.ContextWindowTokens);
        Assert.Equal(AutoCompactPaths.ProxyOnly, reloaded.AutoCompactPaths);
        Assert.True(reloaded.RedirectManualCompaction);
        Assert.Equal("compactor", reloaded.ContextSummarizeModelName);
        Assert.Equal(42, reloaded.ContextSummarizeModelId);
        Assert.Equal(85, reloaded.ProactiveOverflowPercent);
        Assert.Equal(12000, reloaded.ProactiveOverflowTokens);
        Assert.False(reloaded.EnableCopilotCompatibility);
    }

    [Fact]
    public void ReopeningAPopulatedDatabasePreservesItsRows()
    {
        // Every baseline statement is IF NOT EXISTS, so reopening must never recreate or clear a
        // table. A mapping that survived two opens proves the DDL is genuinely additive.
        string path = NewDatabasePath();

        ModelMapping mapping = new()
        {
            ProxyName = "persisted",
            ModelName = "persisted-upstream",
            UpstreamUrl = "http://localhost:8080",
        };
        mapping.EnsureId();

        using (AppDatabase first = OpenDatabase(path))
            first.SaveModelMappings([mapping]);

        using (AppDatabase second = OpenDatabase(path))
            Assert.Single(second.LoadModelMappings());

        using AppDatabase third = OpenDatabase(path);
        ModelMapping reloaded = Assert.Single(third.LoadModelMappings());
        Assert.Equal("persisted", reloaded.ProxyName);
    }

    // ── A pre-rebaseline database is reported, not patched ────────────────

    [Fact]
    public void APreRebaselineDatabaseIsLeftAloneRatherThanMigrated()
    {
        // With migrations gone, opening an older file must not attempt to repair it: every baseline
        // statement is IF NOT EXISTS, so the old table survives untouched and VerifyBaselineSchema
        // is what reports the problem. This pins that no column is silently added.
        string path = NewDatabasePath();

        using (SqliteConnection connection = new($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE model_mappings (
                    id INTEGER NOT NULL DEFAULT 0,
                    proxy_name TEXT PRIMARY KEY,
                    is_enabled INTEGER NOT NULL,
                    model_name TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using AppDatabase database = OpenDatabase(path);

        List<string> columns = ReadColumnNames(path, "model_mappings");

        // Still the old shape: no repair happened.
        Assert.Contains("proxy_name", columns);
        Assert.DoesNotContain("hidden", columns);
        Assert.DoesNotContain("context_window_tokens", columns);
    }
}