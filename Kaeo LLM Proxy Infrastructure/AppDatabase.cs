using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Central SQLite application database. Stores application data in tables, including
/// request logs, exceptions, model mappings, instruction sets, and heartbeat counters.
/// </summary>
internal sealed class AppDatabase : IDisposable
{
    private const string RuntimeSettingsId = "current";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _configuredDbPath;
    private readonly Lock _lock = new();
    private readonly string _connectionString;

    public AppDatabase(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _configuredDbPath = settings.GetApplicationDatabasePath();

        string? directory = Path.GetDirectoryName(_configuredDbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        PrepareDatabaseFile();

        // Private cache (default): shared-cache mode is discouraged by SQLite and, combined with
        // connection pooling, pins a shared page cache for the entire process lifetime. Cross-instance
        // concurrency is handled by WAL, not shared cache.
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _configuredDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connectionString = builder.ToString();

        InitializeDatabase();

        Log.Debug("AppDatabase opened {Path}", _configuredDbPath);
    }

    /// <summary>
    /// Absolute path to the configured application database file.
    /// Exposed so modules can derive the application's data directory for module-owned files.
    /// </summary>
    public string DatabasePath => _configuredDbPath;

    /// <summary>
    /// Inserts a request log entry.
    /// If <paramref name="ex"/> is provided, the full exception detail is stored in the
    /// exceptions table and the generated id is linked back onto <paramref name="entry"/>.
    /// </summary>
    public void Insert(RequestLog entry, Exception? ex = null, LogSource source = LogSource.Proxy)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            // Exception details are persisted only for proxy requests; MCP errors are
            // HTTP-level and carried on the entry itself.
            if (ex is not null && source == LogSource.Proxy)
            {
                ExceptionDetail detail = ExceptionDetail.FromException(ex, entry);
                detail.Id = InsertException(connection, transaction, detail);
                entry.ExceptionId = detail.Id;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $$"""
                INSERT INTO {{RequestTable(source)}} (
                    timestamp_utc,
                    method,
                    ollama_path,
                    upstream_path,
                    model,
                    streaming,
                    status,
                    error_message,
                    status_code,
                    duration_ms,
                    prompt_tokens,
                    completion_tokens,
                    tokens_per_second,
                    exception_id,
                    request_body,
                    upstream_request_body,
                    response_body,
                    request_bytes,
                    response_bytes,
                    total_tokens,
                    cached_prompt_tokens,
                    reasoning_tokens,
                    draft_n,
                    draft_n_accepted,
                    debug_summary,
                    upstream_response_body
                )
                VALUES (
                    $timestampUtc,
                    $method,
                    $ollamaPath,
                    $upstreamPath,
                    $model,
                    $streaming,
                    $status,
                    $errorMessage,
                    $statusCode,
                    $durationMs,
                    $promptTokens,
                    $completionTokens,
                    $tokensPerSecond,
                    $exceptionId,
                    $requestBody,
                    $upstreamRequestBody,
                    $responseBody,
                    $requestBytes,
                    $responseBytes,
                    $totalTokens,
                    $cachedPromptTokens,
                    $reasoningTokens,
                    $draftN,
                    $draftNAccepted,
                    $debugSummary,
                    $upstreamResponseBody
                );
                """;

            AddRequestLogParameters(command, entry);
            command.ExecuteNonQuery();

            transaction.Commit();
        }
    }

    public IReadOnlyList<ModelMapping> LoadModelMappings()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    is_enabled,
                    proxy_name,
                    model_name,
                    enable_thinking_compatibility,
                    capabilities,
                    enable_heartbeats,
                    upstream_type,
                    upstream_url,
                    upstream_timeout_seconds,
                    repeat_penalty,
                    temperature,
                    instruction_set_name,
                    redact_request_bodies,
                    redact_response_bodies,
                    redact_sensitive_json_fields,
                    credential_name,
                    thinking_mode,
                    context_window_tokens,
                    synthesize_openai_metadata,
                    temperature_priority,
                    repeat_penalty_priority,
                    reasoning_effort_priority,
                    reasoning_effort,
                    reasoning_effort_values,
                    reasoning_effort_format,
                    proactive_overflow_percent,
                    proactive_overflow_tokens
                FROM model_mappings
                ORDER BY proxy_name;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<ModelMapping> mappings = [];

            while (reader.Read())
                mappings.Add(ReadModelMapping(reader));

            return mappings;
        }
    }

    public void SaveModelMappings(IEnumerable<ModelMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM model_mappings;";
                deleteCommand.ExecuteNonQuery();
            }

            foreach (ModelMapping mapping in mappings)
            {
                using SqliteCommand insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO model_mappings (
                        proxy_name,
                        is_enabled,
                        model_name,
                        enable_thinking_compatibility,
                        capabilities,
                        enable_heartbeats,
                        upstream_type,
                        upstream_url,
                        upstream_timeout_seconds,
                        repeat_penalty,
                        temperature,
                        instruction_set_name,
                        redact_request_bodies,
                        redact_response_bodies,
                        redact_sensitive_json_fields,
                        credential_name,
                        thinking_mode,
                        context_window_tokens,
                        synthesize_openai_metadata,
                        temperature_priority,
                        repeat_penalty_priority,
                        reasoning_effort_priority,
                        reasoning_effort,
                        reasoning_effort_values,
                        reasoning_effort_format,
                        proactive_overflow_percent,
                        proactive_overflow_tokens
                    )
                    VALUES (
                        $proxyName,
                        $isEnabled,
                        $modelName,
                        $enableThinkingCompatibility,
                        $capabilities,
                        $enableHeartbeats,
                        $upstreamType,
                        $upstreamUrl,
                        $upstreamTimeoutSeconds,
                        $repeatPenalty,
                        $temperature,
                        $instructionSetName,
                        $redactRequestBodies,
                        $redactResponseBodies,
                        $redactSensitiveJsonFields,
                        $credentialName,
                        $thinkingMode,
                        $contextWindowTokens,
                        $synthesizeOpenAiMetadata,
                        $temperaturePriority,
                        $repeatPenaltyPriority,
                        $reasoningEffortPriority,
                        $reasoningEffort,
                        $reasoningEffortValues,
                        $reasoningEffortFormat,
                        $proactiveOverflowPercent,
                        $proactiveOverflowTokens
                    );
                    """;

                AddModelMappingParameters(insertCommand, mapping);
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<InstructionSet> LoadInstructionSets()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name, instructions, description
                FROM instruction_sets
                ORDER BY name;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<InstructionSet> instructionSets = [];

            while (reader.Read())
            {
                instructionSets.Add(new InstructionSet
                {
                    Name = reader.GetString(0),
                    Instructions = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                });
            }

            return instructionSets;
        }
    }

    public void SaveInstructionSets(IEnumerable<InstructionSet> instructionSets)
    {
        ArgumentNullException.ThrowIfNull(instructionSets);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM instruction_sets;";
                deleteCommand.ExecuteNonQuery();
            }

            foreach (InstructionSet instructionSet in instructionSets)
            {
                using SqliteCommand insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO instruction_sets (name, instructions, description)
                    VALUES ($name, $instructions, $description);
                    """;
                insertCommand.Parameters.AddWithValue("$name", instructionSet.Name);
                insertCommand.Parameters.AddWithValue("$instructions", instructionSet.Instructions);
                insertCommand.Parameters.AddWithValue("$description", DbValue(instructionSet.Description));
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Loads all stored credentials. Secret values are returned exactly as stored
    /// (encrypted envelopes when a passphrase was used); decryption is handled by the caller.
    /// </summary>
    public IReadOnlyList<StoredCredential> LoadCredentials()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name, secret, description, username, private_key, certificate
                FROM credentials
                ORDER BY name;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<StoredCredential> credentials = [];

            while (reader.Read())
            {
                credentials.Add(new StoredCredential
                {
                    Name = reader.GetString(0),
                    Secret = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Username = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PrivateKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Certificate = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            return credentials;
        }
    }

    /// <summary>
    /// Replaces the stored credentials table with the supplied set. Secrets should already be
    /// encrypted by the caller before being passed in.
    /// </summary>
    public void SaveCredentials(IEnumerable<StoredCredential> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM credentials;";
                deleteCommand.ExecuteNonQuery();
            }

            foreach (StoredCredential credential in credentials)
            {
                using SqliteCommand insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO credentials (name, secret, description, username, private_key, certificate)
                    VALUES ($name, $secret, $description, $username, $privateKey, $certificate);
                    """;
                insertCommand.Parameters.AddWithValue("$name", credential.Name);
                insertCommand.Parameters.AddWithValue("$secret", credential.Secret);
                insertCommand.Parameters.AddWithValue("$description", DbValue(credential.Description));
                insertCommand.Parameters.AddWithValue("$username", DbValue(credential.Username));
                insertCommand.Parameters.AddWithValue("$privateKey", DbValue(credential.PrivateKey));
                insertCommand.Parameters.AddWithValue("$certificate", DbValue(credential.Certificate));
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<(string Model, long Count, DateTime LastSentUtc)> LoadHeartbeatStats()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT model, count, last_sent_utc
                FROM heartbeats
                ORDER BY model;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<(string Model, long Count, DateTime LastSentUtc)> results = [];

            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    ReadUtc(reader, 2)));
            }

            return results;
        }
    }

    public void UpsertHeartbeat(string model, long count, DateTime lastSentUtc)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Heartbeat model is required.", nameof(model));

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO heartbeats (model, count, last_sent_utc)
                VALUES ($model, $count, $lastSentUtc)
                ON CONFLICT(model) DO UPDATE SET
                    count = excluded.count,
                    last_sent_utc = excluded.last_sent_utc;
                """;
            command.Parameters.AddWithValue("$model", model.Trim());
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$lastSentUtc", ToUtcText(lastSentUtc));
            command.ExecuteNonQuery();
        }
    }

    public void ClearHeartbeats()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM heartbeats;";
            command.ExecuteNonQuery();
        }
    }

    public RuntimeSettings LoadRuntimeSettings()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    auto_start_proxy,
                    start_with_dashboard_open,
                    allow_multiple_instances,
                    show_close_to_tray_notification,
                    collect_request_details,
                    collect_response_details,
                    debug_mode,
                    enable_streaming_heartbeats,
                    streaming_heartbeat_interval_seconds,
                    enable_performance_sampling,
                    enable_api_explorer,
                    run_as_administrator
                FROM runtime_settings
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", RuntimeSettingsId);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return new RuntimeSettings();

            return new RuntimeSettings
            {
                AutoStartProxy = ReadBoolean(reader, 0),
                StartWithDashboardOpen = ReadBoolean(reader, 1),
                AllowMultipleInstances = ReadBoolean(reader, 2),
                ShowCloseToTrayNotification = ReadBoolean(reader, 3),
                CollectRequestDetails = ReadBoolean(reader, 4),
                CollectResponseDetails = ReadBoolean(reader, 5),
                DebugMode = ReadBoolean(reader, 6),
                EnableStreamingHeartbeats = ReadBoolean(reader, 7),
                StreamingHeartbeatIntervalSeconds = reader.GetInt32(8),
                EnablePerformanceSampling = ReadBoolean(reader, 9),
                EnableApiExplorer = ReadBoolean(reader, 10),
                RunAsAdministrator = ReadBoolean(reader, 11),
            };
        }
    }

    public void SaveRuntimeSettings(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO runtime_settings (
                    id,
                    auto_start_proxy,
                    start_with_dashboard_open,
                    allow_multiple_instances,
                    show_close_to_tray_notification,
                    collect_request_details,
                    collect_response_details,
                    debug_mode,
                    enable_streaming_heartbeats,
                    streaming_heartbeat_interval_seconds,
                    enable_performance_sampling,
                    enable_api_explorer,
                    run_as_administrator
                )
                VALUES (
                    $id,
                    $autoStartProxy,
                    $startWithDashboardOpen,
                    $allowMultipleInstances,
                    $showCloseToTrayNotification,
                    $collectRequestDetails,
                    $collectResponseDetails,
                    $debugMode,
                    $enableStreamingHeartbeats,
                    $streamingHeartbeatIntervalSeconds,
                    $enablePerformanceSampling,
                    $enableApiExplorer,
                    $runAsAdministrator
                )
                ON CONFLICT(id) DO UPDATE SET
                    auto_start_proxy = excluded.auto_start_proxy,
                    start_with_dashboard_open = excluded.start_with_dashboard_open,
                    allow_multiple_instances = excluded.allow_multiple_instances,
                    show_close_to_tray_notification = excluded.show_close_to_tray_notification,
                    collect_request_details = excluded.collect_request_details,
                    collect_response_details = excluded.collect_response_details,
                    debug_mode = excluded.debug_mode,
                    enable_streaming_heartbeats = excluded.enable_streaming_heartbeats,
                    streaming_heartbeat_interval_seconds = excluded.streaming_heartbeat_interval_seconds,
                    enable_performance_sampling = excluded.enable_performance_sampling,
                    enable_api_explorer = excluded.enable_api_explorer,
                    run_as_administrator = excluded.run_as_administrator;
                """;

            command.Parameters.AddWithValue("$id", RuntimeSettingsId);
            command.Parameters.AddWithValue("$autoStartProxy", ToSqliteBoolean(settings.AutoStartProxy));
            command.Parameters.AddWithValue("$startWithDashboardOpen", ToSqliteBoolean(settings.StartWithDashboardOpen));
            command.Parameters.AddWithValue("$allowMultipleInstances", ToSqliteBoolean(settings.AllowMultipleInstances));
            command.Parameters.AddWithValue("$showCloseToTrayNotification", ToSqliteBoolean(settings.ShowCloseToTrayNotification));
            command.Parameters.AddWithValue("$collectRequestDetails", ToSqliteBoolean(settings.CollectRequestDetails));
            command.Parameters.AddWithValue("$collectResponseDetails", ToSqliteBoolean(settings.CollectResponseDetails));
            command.Parameters.AddWithValue("$debugMode", ToSqliteBoolean(settings.DebugMode));
            command.Parameters.AddWithValue("$enableStreamingHeartbeats", ToSqliteBoolean(settings.EnableStreamingHeartbeats));
            command.Parameters.AddWithValue("$streamingHeartbeatIntervalSeconds", settings.StreamingHeartbeatIntervalSeconds);
            command.Parameters.AddWithValue("$enablePerformanceSampling", ToSqliteBoolean(settings.EnablePerformanceSampling));
            command.Parameters.AddWithValue("$enableApiExplorer", ToSqliteBoolean(settings.EnableApiExplorer));
            command.Parameters.AddWithValue("$runAsAdministrator", ToSqliteBoolean(settings.RunAsAdministrator));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Returns the <see cref="ExceptionDetail"/> linked to a request log, or null.</summary>
    public ExceptionDetail? GetException(int exceptionId)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, timestamp_utc, exception_type, message, stack_trace, inner_exceptions_json, method, path, model
                FROM exceptions
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", exceptionId);

            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadExceptionDetail(reader) : null;
        }
    }

    /// <summary>
    /// Returns the most recent <paramref name="count"/> log entries from the active database,
    /// newest first.
    /// </summary>
    public IReadOnlyList<RequestLog> QueryRecent(int count)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    timestamp_utc,
                    method,
                    ollama_path,
                    upstream_path,
                    model,
                    streaming,
                    status,
                    error_message,
                    status_code,
                    duration_ms,
                    prompt_tokens,
                    completion_tokens,
                    tokens_per_second,
                    exception_id,
                    request_body,
                    upstream_request_body,
                    response_body,
                    request_bytes,
                    response_bytes,
                    total_tokens,
                    cached_prompt_tokens,
                    reasoning_tokens,
                    draft_n,
                    draft_n_accepted
                FROM requests
                ORDER BY timestamp_utc DESC
                LIMIT $count;
                """;
            command.Parameters.AddWithValue("$count", count);

            using SqliteDataReader reader = command.ExecuteReader();
            List<RequestLog> entries = [];
            while (reader.Read())
                entries.Add(ReadRequestLog(reader));

            return entries;
        }
    }

    /// <summary>
    /// Loads up to <paramref name="count"/> recent entries from the active database into
    /// the supplied list, oldest first (so callers can enqueue them in chronological order).
    /// Used to seed the in-memory queue on startup.
    /// </summary>
    public IReadOnlyList<RequestLog> LoadRecent(int count, LogSource source = LogSource.Proxy)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $$"""
                SELECT
                    timestamp_utc,
                    method,
                    ollama_path,
                    upstream_path,
                    model,
                    streaming,
                    status,
                    error_message,
                    status_code,
                    duration_ms,
                    prompt_tokens,
                    completion_tokens,
                    tokens_per_second,
                    exception_id,
                    request_bytes,
                    response_bytes,
                    total_tokens,
                    cached_prompt_tokens,
                    reasoning_tokens,
                    draft_n,
                    draft_n_accepted
                FROM (
                    SELECT
                        timestamp_utc,
                        method,
                        ollama_path,
                        upstream_path,
                        model,
                        streaming,
                        status,
                        error_message,
                        status_code,
                        duration_ms,
                        prompt_tokens,
                        completion_tokens,
                        tokens_per_second,
                        exception_id,
                        request_bytes,
                        response_bytes,
                        total_tokens,
                        cached_prompt_tokens,
                        reasoning_tokens,
                        draft_n,
                        draft_n_accepted
                    FROM {{RequestTable(source)}}
                    ORDER BY timestamp_utc DESC
                    LIMIT $count
                ) recent
                ORDER BY timestamp_utc ASC;
                """;
            command.Parameters.AddWithValue("$count", count);

            using SqliteDataReader reader = command.ExecuteReader();
            List<RequestLog> entries = [];
            while (reader.Read())
                entries.Add(ReadRequestLogSummary(reader));

            return entries;
        }
    }

    /// <summary>
    /// Loads a single full request log entry — including <c>request_body</c> and
    /// <c>response_body</c> — matching the supplied local timestamp. Used to populate the
    /// detail view on demand so large bodies do not need to live in memory. Returns null if
    /// no matching entry exists. When multiple rows share a timestamp, the most recently
    /// inserted row is returned.
    /// </summary>
    public RequestLog? LoadFullLogEntry(DateTime localTimestamp, LogSource source = LogSource.Proxy)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $$"""
                SELECT
                    timestamp_utc,
                    method,
                    ollama_path,
                    upstream_path,
                    model,
                    streaming,
                    status,
                    error_message,
                    status_code,
                    duration_ms,
                    prompt_tokens,
                    completion_tokens,
                    tokens_per_second,
                    exception_id,
                    request_body,
                    upstream_request_body,
                    response_body,
                    request_bytes,
                    response_bytes,
                    total_tokens,
                    cached_prompt_tokens,
                    reasoning_tokens,
                    draft_n,
                    draft_n_accepted,
                    debug_summary,
                    upstream_response_body
                FROM {{RequestTable(source)}}
                WHERE timestamp_utc = $timestampUtc
                ORDER BY id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$timestampUtc", ToUtcText(localTimestamp));

            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadRequestLog(reader) : null;
        }
    }

    /// <summary>
    /// Deletes all request log entries (and their linked exception records) with a
    /// <see cref="RequestLog.Timestamp"/> older than <paramref name="cutoff"/>.
    /// Returns the number of rows deleted.
    /// </summary>
    public int DeleteOlderThan(DateTime cutoff, LogSource source = LogSource.Proxy)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            List<int> exceptionIds = [];

            using (SqliteCommand selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText =
                    $$"""
                    SELECT exception_id
                    FROM {{RequestTable(source)}}
                    WHERE timestamp_utc < $cutoffUtc
                      AND exception_id IS NOT NULL;
                    """;
                selectCommand.Parameters.AddWithValue("$cutoffUtc", ToUtcText(cutoff));

                using SqliteDataReader reader = selectCommand.ExecuteReader();
                while (reader.Read())
                    exceptionIds.Add(reader.GetInt32(0));
            }

            int deleted;

            using (SqliteCommand deleteRequests = connection.CreateCommand())
            {
                deleteRequests.Transaction = transaction;
                deleteRequests.CommandText =
                    $$"""
                    DELETE FROM {{RequestTable(source)}}
                    WHERE timestamp_utc < $cutoffUtc;
                    """;
                deleteRequests.Parameters.AddWithValue("$cutoffUtc", ToUtcText(cutoff));
                deleted = deleteRequests.ExecuteNonQuery();
            }

            if (exceptionIds.Count > 0)
            {
                var distinctIds = exceptionIds.Distinct().ToList();
                var parameters = new List<string>();
                using SqliteCommand deleteExceptions = connection.CreateCommand();
                deleteExceptions.Transaction = transaction;

                for (int i = 0; i < distinctIds.Count; i++)
                {
                    string paramName = $"$id{i}";
                    parameters.Add(paramName);
                    deleteExceptions.Parameters.AddWithValue(paramName, distinctIds[i]);
                }

                deleteExceptions.CommandText = $"DELETE FROM exceptions WHERE id IN ({string.Join(", ", parameters)});";
                deleteExceptions.ExecuteNonQuery();
            }

            transaction.Commit();

            if (deleted > 0)
                Log.Debug("AppDatabase pruned {Count} request entries older than {Cutoff:u}", deleted, cutoff);

            return deleted;
        }
    }

    /// <summary>
    /// Deletes all request log entries and their linked exception records, and resets the
    /// auto-increment counters so ids restart at 1. SQLite has no TRUNCATE statement; an
    /// unfiltered DELETE FROM is the equivalent. Returns the number of request rows deleted.
    /// </summary>
    public int ClearLogs(LogSource source = LogSource.Proxy)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            int deleted;

            using (SqliteCommand deleteRequests = connection.CreateCommand())
            {
                deleteRequests.Transaction = transaction;
                deleteRequests.CommandText = $"DELETE FROM {RequestTable(source)};";
                deleted = deleteRequests.ExecuteNonQuery();
            }

            if (source == LogSource.Proxy)
            {
                using SqliteCommand deleteExceptions = connection.CreateCommand();
                deleteExceptions.Transaction = transaction;
                deleteExceptions.CommandText = "DELETE FROM exceptions;";
                deleteExceptions.ExecuteNonQuery();
            }

            // sqlite_sequence holds the AUTOINCREMENT counters; clearing the rows restarts ids
            // at 1 like a truncate would. The table only exists once an AUTOINCREMENT table has
            // been created, so verify it is present before deleting from it.
            bool sequenceTableExists;
            using (SqliteCommand checkSequence = connection.CreateCommand())
            {
                checkSequence.Transaction = transaction;
                checkSequence.CommandText =
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM sqlite_master
                        WHERE type = 'table' AND name = 'sqlite_sequence'
                    );
                    """;
                sequenceTableExists = Convert.ToInt64(checkSequence.ExecuteScalar()) != 0;
            }

            if (sequenceTableExists)
            {
                using SqliteCommand resetSequence = connection.CreateCommand();
                resetSequence.Transaction = transaction;
                resetSequence.CommandText = source == LogSource.Proxy
                    ? "DELETE FROM sqlite_sequence WHERE name IN ('requests', 'exceptions');"
                    : "DELETE FROM sqlite_sequence WHERE name = 'mcp_requests';";
                resetSequence.ExecuteNonQuery();
            }

            transaction.Commit();

            if (deleted > 0)
                Log.Debug("AppDatabase cleared {Count} request entries", deleted);

            return deleted;
        }
    }

    /// <summary>Maps a log source to its backing request log table.</summary>
    private static string RequestTable(LogSource source) =>
        source == LogSource.Mcp ? "mcp_requests" : "requests";

    /// <summary>Returns aggregate stats from the active database file.</summary>
    public (long total, long errors, long promptTokens, long completionTokens) QueryTotals()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    COUNT(*) AS total,
                    COALESCE(SUM(CASE WHEN status = $errorStatus THEN 1 ELSE 0 END), 0) AS errors,
                    COALESCE(SUM(prompt_tokens), 0) AS prompt_tokens,
                    COALESCE(SUM(completion_tokens), 0) AS completion_tokens
                FROM requests;
                """;
            command.Parameters.AddWithValue("$errorStatus", (int)RequestStatus.Error);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return (0, 0, 0, 0);

            return (
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3));
        }
    }

    /// <summary>
    /// Enables WAL journal mode if it is not already active. WAL mode is persistent on the
    /// database file across connections, so the pragma only runs when the mode differs,
    /// avoiding a redundant write on every startup.
    /// </summary>
    private static void EnsureWalJournalMode(SqliteConnection connection)
    {
        try
        {
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = "PRAGMA journal_mode;";
            string currentMode = query.ExecuteScalar() as string ?? string.Empty;
            if (currentMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
                return;

            using SqliteCommand setWal = connection.CreateCommand();
            setWal.CommandText = "PRAGMA journal_mode = WAL;";
            setWal.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // Switching journal modes needs brief exclusive access; a concurrent instance or
            // sharing violation must not block startup. The database works with any journal mode.
            Log.Warning(ex, "Could not enable WAL journal mode; continuing with the current mode");
        }
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();

            EnsureWalJournalMode(connection);

            MigrateLegacyModelMappingsTable(connection);

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = OFF;

                CREATE TABLE IF NOT EXISTS exceptions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    exception_type TEXT NOT NULL,
                    message TEXT NOT NULL,
                    stack_trace TEXT NULL,
                    inner_exceptions_json TEXT NOT NULL,
                    method TEXT NOT NULL,
                    path TEXT NOT NULL,
                    model TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    method TEXT NOT NULL,
                    ollama_path TEXT NOT NULL,
                    upstream_path TEXT NOT NULL,
                    model TEXT NOT NULL,
                    streaming INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    error_message TEXT NULL,
                    status_code INTEGER NOT NULL,
                    duration_ms REAL NOT NULL,
                    prompt_tokens INTEGER NOT NULL,
                    completion_tokens INTEGER NOT NULL,
                    tokens_per_second REAL NOT NULL,
                    exception_id INTEGER NULL,
                    request_body TEXT NULL,
                    upstream_request_body TEXT NULL,
                    response_body TEXT NULL,
                    request_bytes INTEGER NOT NULL,
                    response_bytes INTEGER NOT NULL,
                    total_tokens INTEGER NOT NULL DEFAULT 0,
                    cached_prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                    draft_n INTEGER NOT NULL DEFAULT 0,
                    draft_n_accepted INTEGER NOT NULL DEFAULT 0,
                    debug_summary TEXT NULL,
                    upstream_response_body TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_requests_timestamp_utc ON requests(timestamp_utc);
                CREATE INDEX IF NOT EXISTS idx_requests_exception_id ON requests(exception_id);
                CREATE INDEX IF NOT EXISTS idx_exceptions_timestamp_utc ON exceptions(timestamp_utc);

                CREATE TABLE IF NOT EXISTS mcp_requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    method TEXT NOT NULL,
                    ollama_path TEXT NOT NULL,
                    upstream_path TEXT NOT NULL,
                    model TEXT NOT NULL,
                    streaming INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    error_message TEXT NULL,
                    status_code INTEGER NOT NULL,
                    duration_ms REAL NOT NULL,
                    prompt_tokens INTEGER NOT NULL,
                    completion_tokens INTEGER NOT NULL,
                    tokens_per_second REAL NOT NULL,
                    exception_id INTEGER NULL,
                    request_body TEXT NULL,
                    upstream_request_body TEXT NULL,
                    response_body TEXT NULL,
                    request_bytes INTEGER NOT NULL,
                    response_bytes INTEGER NOT NULL,
                    total_tokens INTEGER NOT NULL DEFAULT 0,
                    cached_prompt_tokens INTEGER NOT NULL DEFAULT 0,
                    reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                    draft_n INTEGER NOT NULL DEFAULT 0,
                    draft_n_accepted INTEGER NOT NULL DEFAULT 0,
                    debug_summary TEXT NULL,
                    upstream_response_body TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_mcp_requests_timestamp_utc ON mcp_requests(timestamp_utc);

                CREATE TABLE IF NOT EXISTS model_mappings (
                    proxy_name TEXT PRIMARY KEY,
                    is_enabled INTEGER NOT NULL,
                    model_name TEXT NOT NULL,
                    enable_thinking_compatibility INTEGER NOT NULL,
                    capabilities TEXT NULL,
                    supports_reasoning_effort INTEGER NULL,
                    adaptive_thinking TEXT NULL,
                    enable_heartbeats INTEGER NOT NULL,
                    upstream_type INTEGER NOT NULL,
                    upstream_url TEXT NOT NULL,
                    upstream_timeout_seconds INTEGER NOT NULL,
                    repeat_penalty REAL NOT NULL,
                    temperature REAL NOT NULL,
                    instruction_set_name TEXT NULL,
                    redact_request_bodies INTEGER NOT NULL,
                    redact_response_bodies INTEGER NOT NULL,
                    redact_sensitive_json_fields INTEGER NOT NULL,
                    credential_name TEXT NULL,
                    thinking_mode INTEGER NOT NULL DEFAULT 0,
                    context_window_tokens INTEGER NOT NULL DEFAULT 0,
                    synthesize_openai_metadata INTEGER NOT NULL DEFAULT 0,
                    temperature_priority INTEGER NOT NULL DEFAULT 0,
                    repeat_penalty_priority INTEGER NOT NULL DEFAULT 0,
                    reasoning_effort_priority INTEGER NOT NULL DEFAULT 0,
                    reasoning_effort TEXT NULL,
                    reasoning_effort_values TEXT NULL,
                    reasoning_effort_format INTEGER NOT NULL DEFAULT 1,
                    proactive_overflow_percent INTEGER NOT NULL DEFAULT 0,
                    proactive_overflow_tokens INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS idx_model_mappings_model_name ON model_mappings(model_name);

                CREATE TABLE IF NOT EXISTS instruction_sets (
                    name TEXT PRIMARY KEY,
                    instructions TEXT NOT NULL,
                    description TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS credentials (
                    name TEXT PRIMARY KEY,
                    secret TEXT NOT NULL,
                    description TEXT NULL,
                    username TEXT NULL,
                    private_key TEXT NULL,
                    certificate TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS heartbeats (
                    model TEXT PRIMARY KEY,
                    count INTEGER NOT NULL,
                    last_sent_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS runtime_settings (
                    id TEXT PRIMARY KEY,
                    auto_start_proxy INTEGER NOT NULL,
                    start_with_dashboard_open INTEGER NOT NULL,
                    allow_multiple_instances INTEGER NOT NULL,
                    show_close_to_tray_notification INTEGER NOT NULL,
                    collect_request_details INTEGER NOT NULL,
                    collect_response_details INTEGER NOT NULL,
                    debug_mode INTEGER NOT NULL DEFAULT 0,
                    enable_streaming_heartbeats INTEGER NOT NULL,
                    streaming_heartbeat_interval_seconds INTEGER NOT NULL,
                    enable_performance_sampling INTEGER NOT NULL DEFAULT 1,
                    enable_api_explorer INTEGER NOT NULL DEFAULT 0,
                    run_as_administrator INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS module_registry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    assembly_path TEXT NOT NULL UNIQUE,
                    module_id TEXT NULL,
                    name TEXT NULL,
                    version TEXT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    registered_utc TEXT NOT NULL,
                    last_error TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS mcp_server_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

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
            command.ExecuteNonQuery();

            MigrateRuntimeSettingsTable(connection);
            MigrateModelMappingsTable(connection);
            MigrateRequestsTable(connection);
            MigrateCredentialsTable(connection);
        }
    }

    /// <summary>
    /// Adds the token-detail columns to pre-existing <c>requests</c> tables that were created
    /// before they were introduced: <c>total_tokens</c>, <c>cached_prompt_tokens</c>, and
    /// <c>reasoning_tokens</c>.
    /// </summary>
    private static void MigrateRequestsTable(SqliteConnection connection)
    {
        if (!TableExists(connection, "requests"))
            return;

        if (!ColumnExists(connection, "requests", "total_tokens"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE requests ADD COLUMN total_tokens INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated requests table: added total_tokens column.");
        }

        if (!ColumnExists(connection, "requests", "cached_prompt_tokens"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE requests ADD COLUMN cached_prompt_tokens INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated requests table: added cached_prompt_tokens column.");
        }

        if (!ColumnExists(connection, "requests", "reasoning_tokens"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE requests ADD COLUMN reasoning_tokens INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated requests table: added reasoning_tokens column.");
        }

        AddColumnIfMissing(connection, "requests", "upstream_request_body",
            "ALTER TABLE requests ADD COLUMN upstream_request_body TEXT NULL;");
        AddColumnIfMissing(connection, "mcp_requests", "upstream_request_body",
            "ALTER TABLE mcp_requests ADD COLUMN upstream_request_body TEXT NULL;");
        AddColumnIfMissing(connection, "requests", "draft_n",
            "ALTER TABLE requests ADD COLUMN draft_n INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(connection, "requests", "draft_n_accepted",
            "ALTER TABLE requests ADD COLUMN draft_n_accepted INTEGER NOT NULL DEFAULT 0;");
        AddColumnIfMissing(connection, "mcp_requests", "draft_n",
            "ALTER TABLE mcp_requests ADD COLUMN draft_n INTEGER NOT NULL DEFAULT 0;");
            AddColumnIfMissing(connection, "mcp_requests", "draft_n_accepted",
                "ALTER TABLE mcp_requests ADD COLUMN draft_n_accepted INTEGER NOT NULL DEFAULT 0;");
            AddColumnIfMissing(connection, "requests", "debug_summary",
                "ALTER TABLE requests ADD COLUMN debug_summary TEXT NULL;");
            AddColumnIfMissing(connection, "requests", "upstream_response_body",
                "ALTER TABLE requests ADD COLUMN upstream_response_body TEXT NULL;");
            AddColumnIfMissing(connection, "mcp_requests", "debug_summary",
                "ALTER TABLE mcp_requests ADD COLUMN debug_summary TEXT NULL;");
            AddColumnIfMissing(connection, "mcp_requests", "upstream_response_body",
                "ALTER TABLE mcp_requests ADD COLUMN upstream_response_body TEXT NULL;");
        }

    /// <summary>Adds a column to a table when it does not exist yet, logging the migration.</summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string tableName, string columnName, string alterStatement)
    {
        if (!TableExists(connection, tableName) || ColumnExists(connection, tableName, columnName))
            return;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = alterStatement;
        command.ExecuteNonQuery();

        Log.Information("Migrated {Table} table: added {Column} column.", tableName, columnName);
    }

    /// <summary>
    /// Adds the SSH-style credential columns to pre-existing <c>credentials</c> tables that were
    /// created before they were introduced: <c>username</c>, <c>private_key</c>, and
    /// <c>certificate</c>.
    /// </summary>
    private static void MigrateCredentialsTable(SqliteConnection connection)
    {
        if (!TableExists(connection, "credentials"))
            return;

        if (!ColumnExists(connection, "credentials", "username"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE credentials ADD COLUMN username TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated credentials table: added username column.");
        }

        if (!ColumnExists(connection, "credentials", "private_key"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE credentials ADD COLUMN private_key TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated credentials table: added private_key column.");
        }

        if (!ColumnExists(connection, "credentials", "certificate"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE credentials ADD COLUMN certificate TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated credentials table: added certificate column.");
        }
    }

    /// <summary>
    /// Adds columns to pre-existing runtime_settings tables that were created before they
    /// were introduced: <c>enable_performance_sampling</c>, <c>enable_api_explorer</c>,
    /// and <c>run_as_administrator</c>.
    /// </summary>
    private static void MigrateRuntimeSettingsTable(SqliteConnection connection)
    {
        if (!TableExists(connection, "runtime_settings"))
            return;

        if (!ColumnExists(connection, "runtime_settings", "enable_performance_sampling"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE runtime_settings ADD COLUMN enable_performance_sampling INTEGER NOT NULL DEFAULT 1;";
            command.ExecuteNonQuery();

            Log.Information("Migrated runtime_settings table: added enable_performance_sampling column.");
        }

        if (!ColumnExists(connection, "runtime_settings", "enable_api_explorer"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE runtime_settings ADD COLUMN enable_api_explorer INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated runtime_settings table: added enable_api_explorer column.");
        }

        if (!ColumnExists(connection, "runtime_settings", "run_as_administrator"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE runtime_settings ADD COLUMN run_as_administrator INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated runtime_settings table: added run_as_administrator column.");
        }

        if (!ColumnExists(connection, "runtime_settings", "debug_mode"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "ALTER TABLE runtime_settings ADD COLUMN debug_mode INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated runtime_settings table: added debug_mode column.");
        }
    }

    /// <summary>
    /// Adds columns to pre-existing <c>model_mappings</c> tables that were created before they
    /// were introduced: <c>capabilities</c>, <c>credential_name</c>, <c>thinking_mode</c>,
    /// <c>context_window_tokens</c>, <c>synthesize_openai_metadata</c>,
    /// <c>temperature_priority</c>, <c>repeat_penalty_priority</c>,
    /// <c>reasoning_effort_priority</c>, <c>reasoning_effort</c>,
    /// <c>reasoning_effort_values</c>, and <c>reasoning_effort_format</c>.
    /// </summary>
    private static void MigrateModelMappingsTable(SqliteConnection connection)
    {
        if (!TableExists(connection, "model_mappings"))
            return;

        if (!ColumnExists(connection, "model_mappings", "capabilities"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN capabilities TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added capabilities column.");
        }

        if (!ColumnExists(connection, "model_mappings", "supports_reasoning_effort"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN supports_reasoning_effort INTEGER NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added supports_reasoning_effort column.");
        }

        if (!ColumnExists(connection, "model_mappings", "adaptive_thinking"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN adaptive_thinking TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added adaptive_thinking column.");
        }

        if (!ColumnExists(connection, "model_mappings", "credential_name"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN credential_name TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added credential_name column.");
        }

        if (!ColumnExists(connection, "model_mappings", "thinking_mode"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN thinking_mode INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added thinking_mode column.");
        }

        if (!ColumnExists(connection, "model_mappings", "context_window_tokens"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN context_window_tokens INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added context_window_tokens column.");
        }

        if (!ColumnExists(connection, "model_mappings", "synthesize_openai_metadata"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN synthesize_openai_metadata INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added synthesize_openai_metadata column.");
        }

        if (!ColumnExists(connection, "model_mappings", "temperature_priority"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN temperature_priority INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added temperature_priority column.");
        }

        if (!ColumnExists(connection, "model_mappings", "repeat_penalty_priority"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN repeat_penalty_priority INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added repeat_penalty_priority column.");
        }

        if (!ColumnExists(connection, "model_mappings", "reasoning_effort_priority"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN reasoning_effort_priority INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added reasoning_effort_priority column.");
        }

        if (!ColumnExists(connection, "model_mappings", "reasoning_effort"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN reasoning_effort TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added reasoning_effort column.");
        }

        if (!ColumnExists(connection, "model_mappings", "reasoning_effort_values"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN reasoning_effort_values TEXT NULL;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added reasoning_effort_values column.");
        }

        if (!ColumnExists(connection, "model_mappings", "reasoning_effort_format"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE model_mappings ADD COLUMN reasoning_effort_format INTEGER NOT NULL DEFAULT 1;";
            command.ExecuteNonQuery();

            Log.Information("Migrated model_mappings table: added reasoning_effort_format column.");
        }
    }

    /// <summary>
    /// Handles pre-existing <c>model_mappings</c> tables from older database schemas that
    /// predate the <c>proxy_name</c> primary key column. Rather than silently losing the
    /// old data, the legacy table is renamed out of the way so a fresh, up-to-date
    /// <c>model_mappings</c> table can be created by the schema script.
    /// </summary>
    private static void MigrateLegacyModelMappingsTable(SqliteConnection connection)
    {
        const string tableName = "model_mappings";

        if (!TableExists(connection, tableName))
            return;

        if (ColumnExists(connection, tableName, "proxy_name"))
            return;

        string legacyTableName = $"{tableName}_legacy_{DateTime.UtcNow:yyyyMMddHHmmss}";

        using SqliteCommand renameCommand = connection.CreateCommand();
        renameCommand.CommandText = $"ALTER TABLE {tableName} RENAME TO {legacyTableName};";
        renameCommand.ExecuteNonQuery();

        Log.Warning(
            "The existing {Table} table used an outdated schema without a {Column} column. " +
            "It was renamed to {LegacyTable} and a new {Table} table will be created. " +
            "Model mappings must be re-entered.",
            tableName,
            "proxy_name",
            legacyTableName);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void PrepareDatabaseFile()
    {
        if (!File.Exists(_configuredDbPath))
            return;

        FileInfo fileInfo = new(_configuredDbPath);
        if (fileInfo.Length == 0)
            return;

        try
        {
            if (IsSqliteDatabaseFile(_configuredDbPath))
                return;
        }
        catch (IOException ex)
        {
            // The file is locked by another process (e.g. another running instance of this
            // application with AllowMultipleInstances enabled). Assume it is a valid SQLite
            // database rather than crashing; the shared-cache connection below will fail with
            // a clearer error if that assumption turns out to be wrong.
            Log.Warning(
                ex,
                "Could not verify database file {Path} because it is in use by another process. Assuming it is a valid SQLite database.",
                _configuredDbPath);
            return;
        }

        try
        {
            // Preserve the unrecognized file by renaming it to a timestamped *.bak instead of
            // permanently deleting it, so no user data is lost if the file was misidentified.
            string backupPath = CreateUniqueBackupPath(_configuredDbPath);
            File.Move(_configuredDbPath, backupPath);

            Log.Warning(
                "Existing database file at {Path} is not a SQLite database. It was renamed to {BackupPath} and a new SQLite database will be created.",
                _configuredDbPath, backupPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"The legacy non-SQLite database file '{_configuredDbPath}' could not be renamed because it is in use by another process. Close the process that is locking the file and try again. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"The legacy non-SQLite database file '{_configuredDbPath}' could not be renamed due to access restrictions. Fix the file permissions and try again. {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a collision-free backup path for a database file being set aside, of the form
    /// <c>{path}.{yyyyMMdd-HHmmss}.bak</c> (with a numeric suffix appended if that already exists).
    /// </summary>
    private static string CreateUniqueBackupPath(string path)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string candidate = $"{path}.{timestamp}.bak";

        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{path}.{timestamp}-{suffix}.bak";
            suffix++;
        }

        return candidate;
    }

    private static bool IsSqliteDatabaseFile(string path)
    {
        byte[] header = new byte[16];

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < header.Length)
            return false;

        int bytesRead = stream.Read(header, 0, header.Length);
        if (bytesRead < header.Length)
            return false;

        return Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddRequestLogParameters(SqliteCommand command, RequestLog entry)
    {
        command.Parameters.AddWithValue("$timestampUtc", ToUtcText(entry.Timestamp));
        command.Parameters.AddWithValue("$method", entry.Method);
        command.Parameters.AddWithValue("$ollamaPath", entry.OllamaPath);
        command.Parameters.AddWithValue("$upstreamPath", entry.UpstreamPath);
        command.Parameters.AddWithValue("$model", entry.Model);
        command.Parameters.AddWithValue("$streaming", ToSqliteBoolean(entry.Streaming));
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$errorMessage", DbValue(entry.ErrorMessage));
        command.Parameters.AddWithValue("$statusCode", entry.StatusCode);
        command.Parameters.AddWithValue("$durationMs", entry.DurationMs);
        command.Parameters.AddWithValue("$promptTokens", entry.PromptTokens);
        command.Parameters.AddWithValue("$completionTokens", entry.CompletionTokens);
        command.Parameters.AddWithValue("$tokensPerSecond", entry.TokensPerSecond);
        command.Parameters.AddWithValue("$exceptionId", entry.ExceptionId.HasValue ? entry.ExceptionId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$requestBody", DbValue(entry.RequestBody));
        command.Parameters.AddWithValue("$upstreamRequestBody", DbValue(entry.UpstreamRequestBody));
        command.Parameters.AddWithValue("$responseBody", DbValue(entry.ResponseBody));
        command.Parameters.AddWithValue("$requestBytes", entry.RequestBytes);
        command.Parameters.AddWithValue("$responseBytes", entry.ResponseBytes);
        command.Parameters.AddWithValue("$totalTokens", entry.TotalTokens);
            command.Parameters.AddWithValue("$cachedPromptTokens", entry.CachedPromptTokens);
            command.Parameters.AddWithValue("$reasoningTokens", entry.ReasoningTokens);
            command.Parameters.AddWithValue("$draftN", entry.DraftN);
            command.Parameters.AddWithValue("$draftNAccepted", entry.DraftNAccepted);
            command.Parameters.AddWithValue("$debugSummary", DbValue(entry.DebugSummary));
            command.Parameters.AddWithValue("$upstreamResponseBody", DbValue(entry.UpstreamResponseBody));
        }

    private static void AddModelMappingParameters(SqliteCommand command, ModelMapping mapping)
    {
        command.Parameters.AddWithValue("$proxyName", mapping.ProxyName);
        command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(mapping.IsEnabled));
        command.Parameters.AddWithValue("$modelName", mapping.ModelName);
        command.Parameters.AddWithValue("$enableThinkingCompatibility", ToSqliteBoolean(mapping.EnableThinkingCompatibility));
        command.Parameters.AddWithValue("$capabilities", mapping.Capabilities.Count > 0
            ? DbValue(string.Join(",", mapping.Capabilities))
            : DBNull.Value);
        command.Parameters.AddWithValue("$enableHeartbeats", ToSqliteBoolean(mapping.EnableHeartbeats));
        command.Parameters.AddWithValue("$upstreamType", (int)mapping.UpstreamType);
        command.Parameters.AddWithValue("$upstreamUrl", mapping.UpstreamUrl);
        command.Parameters.AddWithValue("$upstreamTimeoutSeconds", mapping.UpstreamTimeoutSeconds);
        command.Parameters.AddWithValue("$repeatPenalty", mapping.RepeatPenalty);
        command.Parameters.AddWithValue("$temperature", mapping.Temperature);
        command.Parameters.AddWithValue("$instructionSetName", DbValue(mapping.InstructionSetName));
        command.Parameters.AddWithValue("$redactRequestBodies", ToSqliteBoolean(mapping.RedactRequestBodies));
        command.Parameters.AddWithValue("$redactResponseBodies", ToSqliteBoolean(mapping.RedactResponseBodies));
        command.Parameters.AddWithValue("$redactSensitiveJsonFields", ToSqliteBoolean(mapping.RedactSensitiveJsonFields));
        command.Parameters.AddWithValue("$credentialName", DbValue(mapping.CredentialName));
        command.Parameters.AddWithValue("$thinkingMode", (int)mapping.ThinkingMode);
        command.Parameters.AddWithValue("$contextWindowTokens", mapping.ContextWindowTokens);
        command.Parameters.AddWithValue("$synthesizeOpenAiMetadata", ToSqliteBoolean(mapping.SynthesizeOpenAiMetadata));
        command.Parameters.AddWithValue("$temperaturePriority", (int)mapping.TemperaturePriority);
        command.Parameters.AddWithValue("$repeatPenaltyPriority", (int)mapping.RepeatPenaltyPriority);
        command.Parameters.AddWithValue("$reasoningEffortPriority", (int)mapping.ReasoningEffortPriority);
        command.Parameters.AddWithValue("$reasoningEffort", DbValue(mapping.ReasoningEffort));
        command.Parameters.AddWithValue("$reasoningEffortValues", mapping.ReasoningEffortValues.Count > 0
            ? DbValue(string.Join(", ", mapping.ReasoningEffortValues))
            : DBNull.Value);
        command.Parameters.AddWithValue("$reasoningEffortFormat", (int)mapping.ReasoningEffortFormat);
        command.Parameters.AddWithValue("$proactiveOverflowPercent", mapping.ProactiveOverflowPercent);
        command.Parameters.AddWithValue("$proactiveOverflowTokens", mapping.ProactiveOverflowTokens);
    }

    private static ModelMapping ReadModelMapping(SqliteDataReader reader) => new()
    {
        IsEnabled = ReadBoolean(reader, 0),
        ProxyName = reader.GetString(1),
        ModelName = reader.GetString(2),
        EnableThinkingCompatibility = ReadBoolean(reader, 3),
        Capabilities = reader.IsDBNull(4)
            ? []
            : [.. reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        EnableHeartbeats = ReadBoolean(reader, 5),
        UpstreamType = Enum.IsDefined(typeof(UpstreamType), reader.GetInt32(6))
            ? (UpstreamType)reader.GetInt32(6)
            : UpstreamType.OpenAI,
        UpstreamUrl = reader.GetString(7),
        UpstreamTimeoutSeconds = reader.GetInt32(8),
        RepeatPenalty = reader.GetDouble(9),
        Temperature = reader.GetDouble(10),
        InstructionSetName = reader.IsDBNull(11) ? null : reader.GetString(11),
        RedactRequestBodies = ReadBoolean(reader, 12),
        RedactResponseBodies = ReadBoolean(reader, 13),
        RedactSensitiveJsonFields = ReadBoolean(reader, 14),
        CredentialName = reader.IsDBNull(15) ? null : reader.GetString(15),
        ThinkingMode = Enum.IsDefined(typeof(ThinkingMode), reader.GetInt32(16))
            ? (ThinkingMode)reader.GetInt32(16)
            : ThinkingMode.Off,
        ContextWindowTokens = reader.GetInt32(17),
        SynthesizeOpenAiMetadata = ReadBoolean(reader, 18),
        TemperaturePriority = Enum.IsDefined(typeof(SamplingPriority), reader.GetInt32(19))
            ? (SamplingPriority)reader.GetInt32(19)
            : SamplingPriority.ClientApp,
        RepeatPenaltyPriority = Enum.IsDefined(typeof(SamplingPriority), reader.GetInt32(20))
            ? (SamplingPriority)reader.GetInt32(20)
            : SamplingPriority.ClientApp,
        ReasoningEffortPriority = Enum.IsDefined(typeof(SamplingPriority), reader.GetInt32(21))
            ? (SamplingPriority)reader.GetInt32(21)
            : SamplingPriority.ClientApp,
        ReasoningEffort = reader.IsDBNull(22) ? null : reader.GetString(22),
        ReasoningEffortValues = reader.IsDBNull(23)
            ? []
            : [.. reader.GetString(23).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        ReasoningEffortFormat = ToReasoningEffortFormat(reader.GetInt32(24)),
        ProactiveOverflowPercent = reader.GetInt32(25),
        ProactiveOverflowTokens = reader.GetInt32(26),
    };

    /// <summary>
    /// Interprets the stored bitmask as a <see cref="ReasoningEffortFormat"/>, discarding unknown
    /// bits and falling back to <see cref="ReasoningEffortFormat.Legacy"/> when nothing is
    /// selected (e.g. the historical column default) so Proxy-priority mappings keep injecting.
    /// </summary>
    private static ReasoningEffortFormat ToReasoningEffortFormat(int value)
    {
        const ReasoningEffortFormat allFormats = ReasoningEffortFormat.Legacy
            | ReasoningEffortFormat.Modern
            | ReasoningEffortFormat.QwenCloud
            | ReasoningEffortFormat.ChatTemplateKwargs;

        ReasoningEffortFormat format = (ReasoningEffortFormat)value & allFormats;
        return format == default ? ReasoningEffortFormat.Legacy : format;
    }

    private static RequestLog ReadRequestLog(SqliteDataReader reader) => new()
    {
        Timestamp = ReadUtc(reader, 0).ToLocalTime(),
        Method = reader.GetString(1),
        OllamaPath = reader.GetString(2),
        UpstreamPath = reader.GetString(3),
        Model = reader.GetString(4),
        Streaming = ReadBoolean(reader, 5),
        Status = Enum.IsDefined(typeof(RequestStatus), reader.GetInt32(6))
            ? (RequestStatus)reader.GetInt32(6)
            : RequestStatus.Error,
        ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
        StatusCode = reader.GetInt32(8),
        DurationMs = reader.GetDouble(9),
        PromptTokens = reader.GetInt32(10),
        CompletionTokens = reader.GetInt32(11),
        TokensPerSecond = reader.GetDouble(12),
        ExceptionId = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        RequestBody = reader.IsDBNull(14) ? null : reader.GetString(14),
        UpstreamRequestBody = reader.IsDBNull(15) ? null : reader.GetString(15),
        ResponseBody = reader.IsDBNull(16) ? null : reader.GetString(16),
        RequestBytes = reader.GetInt64(17),
        ResponseBytes = reader.GetInt64(18),
        TotalTokens = reader.GetInt32(19),
        CachedPromptTokens = reader.GetInt32(20),
        ReasoningTokens = reader.GetInt32(21),
        DraftN = reader.GetInt32(22),
        DraftNAccepted = reader.GetInt32(23),
        DebugSummary = reader.IsDBNull(24) ? null : reader.GetString(24),
        UpstreamResponseBody = reader.IsDBNull(25) ? null : reader.GetString(25),
    };

    /// <summary>
    /// Reads a <see cref="RequestLog"/> from a result set that excludes the
    /// <c>request_body</c> and <c>response_body</c> columns (see <c>LoadRecent</c>).
    /// Column ordinals after <c>exception_id</c> shift down by two accordingly.
    /// </summary>
    private static RequestLog ReadRequestLogSummary(SqliteDataReader reader) => new()
    {
        Timestamp = ReadUtc(reader, 0).ToLocalTime(),
        Method = reader.GetString(1),
        OllamaPath = reader.GetString(2),
        UpstreamPath = reader.GetString(3),
        Model = reader.GetString(4),
        Streaming = ReadBoolean(reader, 5),
        Status = Enum.IsDefined(typeof(RequestStatus), reader.GetInt32(6))
            ? (RequestStatus)reader.GetInt32(6)
            : RequestStatus.Error,
        ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
        StatusCode = reader.GetInt32(8),
        DurationMs = reader.GetDouble(9),
        PromptTokens = reader.GetInt32(10),
        CompletionTokens = reader.GetInt32(11),
        TokensPerSecond = reader.GetDouble(12),
        ExceptionId = reader.IsDBNull(13) ? null : reader.GetInt32(13),
        RequestBody = null,
        ResponseBody = null,
        RequestBytes = reader.GetInt64(14),
        ResponseBytes = reader.GetInt64(15),
        TotalTokens = reader.GetInt32(16),
        CachedPromptTokens = reader.GetInt32(17),
        ReasoningTokens = reader.GetInt32(18),
        DraftN = reader.GetInt32(19),
        DraftNAccepted = reader.GetInt32(20),
    };

    private static ExceptionDetail ReadExceptionDetail(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Timestamp = ReadUtc(reader, 1),
        ExceptionType = reader.GetString(2),
        Message = reader.GetString(3),
        StackTrace = reader.IsDBNull(4) ? null : reader.GetString(4),
        InnerExceptions = DeserializeInnerExceptions(reader.IsDBNull(5) ? null : reader.GetString(5)),
        Method = reader.GetString(6),
        Path = reader.GetString(7),
        Model = reader.GetString(8),
    };

    private static int InsertException(SqliteConnection connection, SqliteTransaction transaction, ExceptionDetail detail)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO exceptions (
                timestamp_utc,
                exception_type,
                message,
                stack_trace,
                inner_exceptions_json,
                method,
                path,
                model
            )
            VALUES (
                $timestampUtc,
                $exceptionType,
                $message,
                $stackTrace,
                $innerExceptionsJson,
                $method,
                $path,
                $model
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestampUtc", ToUtcText(detail.Timestamp));
        command.Parameters.AddWithValue("$exceptionType", detail.ExceptionType);
        command.Parameters.AddWithValue("$message", detail.Message);
        command.Parameters.AddWithValue("$stackTrace", DbValue(detail.StackTrace));
        command.Parameters.AddWithValue("$innerExceptionsJson", JsonSerializer.Serialize(detail.InnerExceptions, _jsonOptions));
        command.Parameters.AddWithValue("$method", detail.Method);
        command.Parameters.AddWithValue("$path", detail.Path);
        command.Parameters.AddWithValue("$model", detail.Model);

        object? scalar = command.ExecuteScalar();
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> DeserializeInnerExceptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, _jsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ToUtcText(DateTime value) =>
        (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime()).ToString("O");

    private static DateTime ReadUtc(SqliteDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);
        DateTime parsed = DateTime.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal) != 0;

    private static int ToSqliteBoolean(bool value) => value ? 1 : 0;

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    // ── Module registry + module database gateway support ───────────────────

    /// <summary>Loads all registered modules ordered by registration.</summary>
    public IReadOnlyList<ModuleRegistryEntry> LoadModuleRegistry()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    id,
                    assembly_path,
                    module_id,
                    name,
                    version,
                    is_enabled,
                    registered_utc,
                    last_error
                FROM module_registry
                ORDER BY id;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<ModuleRegistryEntry> entries = [];

            while (reader.Read())
            {
                entries.Add(new ModuleRegistryEntry
                {
                    Id = reader.GetInt32(0),
                    AssemblyPath = reader.GetString(1),
                    ModuleId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Version = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IsEnabled = ReadBoolean(reader, 5),
                    RegisteredUtc = ReadUtc(reader, 6),
                    LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }

            return entries;
        }
    }

    /// <summary>Finds a registered module by its assembly path, or null when not registered.</summary>
    public ModuleRegistryEntry? FindModuleRegistryByPath(string assemblyPath)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    id,
                    assembly_path,
                    module_id,
                    name,
                    version,
                    is_enabled,
                    registered_utc,
                    last_error
                FROM module_registry
                WHERE assembly_path = $assemblyPath;
                """;
            command.Parameters.AddWithValue("$assemblyPath", assemblyPath);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new ModuleRegistryEntry
            {
                Id = reader.GetInt32(0),
                AssemblyPath = reader.GetString(1),
                ModuleId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Name = reader.IsDBNull(3) ? null : reader.GetString(3),
                Version = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsEnabled = ReadBoolean(reader, 5),
                RegisteredUtc = ReadUtc(reader, 6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
        }
    }

    /// <summary>Registers a new module. The generated row id is written back to <paramref name="entry"/>.</summary>
    public void InsertModuleRegistry(ModuleRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO module_registry (
                    assembly_path,
                    module_id,
                    name,
                    version,
                    is_enabled,
                    registered_utc,
                    last_error
                )
                VALUES (
                    $assemblyPath,
                    $moduleId,
                    $name,
                    $version,
                    $isEnabled,
                    $registeredUtc,
                    $lastError
                );
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$assemblyPath", entry.AssemblyPath);
            command.Parameters.AddWithValue("$moduleId", DbValue(entry.ModuleId));
            command.Parameters.AddWithValue("$name", DbValue(entry.Name));
            command.Parameters.AddWithValue("$version", DbValue(entry.Version));
            command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(entry.IsEnabled));
            command.Parameters.AddWithValue("$registeredUtc", ToUtcText(entry.RegisteredUtc));
            command.Parameters.AddWithValue("$lastError", DbValue(entry.LastError));

            object? scalar = command.ExecuteScalar();
            entry.Id = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Updates mutable registry fields (metadata, enabled state, last error) for a module.</summary>
    public void UpdateModuleRegistry(ModuleRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE module_registry
                SET
                    module_id = $moduleId,
                    name = $name,
                    version = $version,
                    is_enabled = $isEnabled,
                    last_error = $lastError
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$moduleId", DbValue(entry.ModuleId));
            command.Parameters.AddWithValue("$name", DbValue(entry.Name));
            command.Parameters.AddWithValue("$version", DbValue(entry.Version));
            command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(entry.IsEnabled));
            command.Parameters.AddWithValue("$lastError", DbValue(entry.LastError));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Removes a module from the registry.</summary>
    public void DeleteModuleRegistry(int id)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM module_registry WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Executes a module-provided baseline schema script (DDL) against the application
    /// database. Scripts must be idempotent (CREATE TABLE IF NOT EXISTS / CREATE INDEX IF
    /// NOT EXISTS). Called through <c>ModuleDatabaseGateway</c>.
    /// </summary>
    internal void ExecuteModuleSchemaScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Executes a module-provided non-query command. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal int ExecuteModuleNonQuery(string commandText, Action<DbCommand>? configureCommand)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            configureCommand?.Invoke(command);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Executes a module-provided scalar command. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal object? ExecuteModuleScalar(string commandText, Action<DbCommand>? configureCommand)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            configureCommand?.Invoke(command);
            return command.ExecuteScalar();
        }
    }

    /// <summary>Executes a module-provided query and maps every row. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal IReadOnlyList<T> ExecuteModuleQuery<T>(
        string commandText,
        Func<DbDataReader, T> map,
        Action<DbCommand>? configureCommand)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            configureCommand?.Invoke(command);

            using SqliteDataReader reader = command.ExecuteReader();
            List<T> results = [];

            while (reader.Read())
                results.Add(map(reader));

            return results;
        }
    }

    /// <summary>
    /// Returns the most recent system log entries (newest first).
    /// </summary>
    /// <param name="levelFilter">Optional level name to filter by (e.g. "Error"). Null returns all.</param>
    /// <param name="limit">Maximum number of entries to return. Default 500.</param>
    public IReadOnlyList<SystemLogEntry> GetSystemLogs(string? levelFilter = null, int limit = 500)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();

            if (string.IsNullOrEmpty(levelFilter))
            {
                command.CommandText =
                    "SELECT timestamp_utc, level, message, exception, source_context " +
                    "FROM system_logs ORDER BY id DESC LIMIT $limit";
                command.Parameters.AddWithValue("$limit", limit);
            }
            else
            {
                command.CommandText =
                    "SELECT timestamp_utc, level, message, exception, source_context " +
                    "FROM system_logs WHERE level = $level ORDER BY id DESC LIMIT $limit";
                command.Parameters.AddWithValue("$level", levelFilter);
                command.Parameters.AddWithValue("$limit", limit);
            }

            using SqliteDataReader reader = command.ExecuteReader();
            List<SystemLogEntry> results = [];

            while (reader.Read())
            {
                DateTime timestamp = DateTime.Parse(reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                string level = reader.GetString(1);
                string message = reader.GetString(2);
                string? exception = reader.IsDBNull(3) ? null : reader.GetString(3);
                string? sourceContext = reader.IsDBNull(4) ? null : reader.GetString(4);

                results.Add(new SystemLogEntry(timestamp, level, message, exception, sourceContext));
            }

            return results;
        }
    }

    /// <summary>Removes all system log entries from the database.</summary>
    public void ClearSystemLogs()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM system_logs";
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        // Connections are opened per operation and disposed immediately.
    }
}
