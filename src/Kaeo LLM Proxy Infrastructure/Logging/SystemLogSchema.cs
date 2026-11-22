namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Single authority for the <c>system_logs</c> schema, shared by <see cref="AppDatabase"/>'s
/// startup DDL and <see cref="SystemLogDbSink"/>'s self-healing table check so the two cannot drift.
/// </summary>
internal static class SystemLogSchema
{
    public const string CreateSql =
        """
        CREATE TABLE IF NOT EXISTS system_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            level TEXT NOT NULL,
            message TEXT NOT NULL,
            exception TEXT NULL,
            source_context TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_system_logs_timestamp_utc ON system_logs(timestamp_utc);
        CREATE INDEX IF NOT EXISTS idx_system_logs_level ON system_logs(level);
        """;
}