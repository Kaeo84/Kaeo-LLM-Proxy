using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;
using Kaeo.LlmProxy.Core.Modules;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Central SQLite application database. Stores application data in tables, including
/// request logs, exceptions, model mappings, instruction sets, and SSE keep-alive counters.
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
    private string _connectionString;
    private SqliteConnection? _degradedAnchorConnection;

    /// <summary>
    /// True when the on-disk database could not be opened and this instance fell back to a
    /// private in-memory database. Reads return empty/seed data and writes will NOT persist.
    /// </summary>
    public bool IsDegraded { get; private set; }

        /// <summary>
        /// Raised after <see cref="SaveModelMappings"/> rewrites the mapping table. The Windows GUI
        /// renders mappings into a grid and rebuilds <c>AppSettings.ModelMappings</c> from those rows on
        /// every edit, so it must re-sync when another in-process caller (the web dashboard) changes
        /// them — otherwise the next GUI edit silently discards the dashboard's change.
        /// </summary>
        public event Action? ModelMappingsSaved;

    public AppDatabase(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _configuredDbPath = settings.GetApplicationDatabasePath();

        // Private cache (default): shared-cache mode is discouraged by SQLite and, combined with
        // connection pooling, pins a shared page cache for the entire process lifetime. Cross-instance
        // concurrency is handled by WAL, not shared cache.
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _configuredDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connectionString = builder.ToString();

        // A concurrent instance (AllowMultipleInstances) may briefly hold the file; retry with
        // backoff, then degrade to an explicitly flagged in-memory database instead of throwing
        // out of the constructor and taking the whole application down.
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_configuredDbPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                PrepareDatabaseFile();
                InitializeDatabase();
                SeedDefaultsIfEmpty();
                SeedCompactionInstructionSetIfMissing();
                break;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    EnterDegradedMode(ex);
                    break;
                }

                Log.Warning(ex, "AppDatabase initialization attempt {Attempt}/{Max} failed for {Path}; retrying", attempt, maxAttempts, _configuredDbPath);
                Thread.Sleep(500 * attempt);
            }
        }

        Log.Debug("AppDatabase opened {Path} (degraded={Degraded})", _configuredDbPath, IsDegraded);
    }

    /// <summary>
    /// Switches to a named shared in-memory database after the on-disk file proved unusable.
    /// Every operation keeps working against the empty schema (reads return nothing, writes are
    /// accepted but lost on exit); the startup warning is the explicit notice that writes will
    /// not persist.
    /// </summary>
    private void EnterDegradedMode(Exception cause)
    {
        Log.Warning(cause, "AppDatabase could not open {Path} after retries; degrading to a private in-memory database. Writes will NOT persist.", _configuredDbPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "kaeo-degraded-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        IsDegraded = true;

        try
        {
            // The anchor connection keeps the named shared in-memory database alive for this
            // instance's lifetime; pooled per-operation connections attach to the same store.
            _degradedAnchorConnection = new SqliteConnection(_connectionString);
            _degradedAnchorConnection.Open();

            InitializeDatabase();
            SeedDefaultsIfEmpty();
            SeedCompactionInstructionSetIfMissing();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AppDatabase degraded-mode initialization failed; database operations may be unavailable.");
        }
    }

    /// <summary>
    /// Ensures the seeded <c>Compaction</c> instruction set exists, so the Settings tab's compaction
    /// instructions dropdown has a preselected, editable prompt on any install.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="SeedDefaultsIfEmpty"/>, which returns early once
    /// <c>model_mappings</c> holds a row. Riding on that method would mean an existing database never
    /// received this set, which is exactly the case that matters — a fresh database would have it and
    /// an established one would show an empty dropdown.
    /// <para>
    /// Inserts only when the name is absent, so an edited or renamed set is never overwritten. The
    /// existence check and the insert share one connection and are not a transaction because the
    /// worst case is a duplicate-name insert failing a primary-key constraint, which is logged and
    /// skipped.
    /// </para>
    /// </remarks>
    private void SeedCompactionInstructionSetIfMissing()
    {
        try
        {
            using SqliteConnection connection = OpenConnection();

            using (SqliteCommand existsCommand = connection.CreateCommand())
            {
                existsCommand.CommandText = "SELECT COUNT(*) FROM instruction_sets WHERE name = $name;";
                existsCommand.Parameters.AddWithValue("$name", SeedData.CompactionInstructionSetName);

                if (Convert.ToInt64(existsCommand.ExecuteScalar()) > 0)
                    return;
            }

            InstructionSet instructionSet = SeedData.CreateCompactionInstructionSet();

            using SqliteCommand insertCommand = connection.CreateCommand();
            insertCommand.CommandText =
                """
                INSERT INTO instruction_sets (name, instructions, description)
                VALUES ($name, $instructions, $description);
                """;
            insertCommand.Parameters.AddWithValue("$name", instructionSet.Name);
            insertCommand.Parameters.AddWithValue("$instructions", instructionSet.Instructions);
            insertCommand.Parameters.AddWithValue("$description", DbValue(instructionSet.Description));
            insertCommand.ExecuteNonQuery();

            Log.Information(
                "Seeded the default compaction instruction set '{Name}' into {Path}",
                instructionSet.Name, _configuredDbPath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // Optional seed data must never prevent startup, and a concurrent instance may hold the
            // file. The dropdown simply shows no selection until the next successful start.
            Log.Warning(ex, "Skipped seeding the default compaction instruction set for {Path}", _configuredDbPath);
        }
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
                    original_model,
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
                    upstream_response_body,
                    stop_reason,
                    client_address,
                    user_agent,
                    request_headers,
                    response_headers,
                    user_id
                )
                VALUES (
                    $timestampUtc,
                    $method,
                    $ollamaPath,
                    $upstreamPath,
                    $model,
                    $originalModel,
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
                    $upstreamResponseBody,
                    $stopReason,
                    $clientAddress,
                    $userAgent,
                    $requestHeaders,
                    $responseHeaders,
                    $userId
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
                    id,
                    is_enabled,
                    hidden,
                    proxy_name,
                    model_name,
                    enable_thinking_compatibility,
                    capabilities,
                    enable_sse_keep_alive,
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
                    temperature_priority,
                    repeat_penalty_priority,
                    reasoning_effort_priority,
                    reasoning_effort,
                    reasoning_effort_values,
                    reasoning_effort_format,
                    proactive_overflow_percent,
                    proactive_overflow_tokens,
                    context_summarize_model_id,
                    context_summarize_model_name,
                    auto_compact_paths,
                    redirect_manual_compaction,
                    enable_heartbeats,
                    enable_copilot_compatibility,
                    forward_client_authorization,
                    enable_api_translation,
                    anthropic_max_tokens,
                    anthropic_api_version
                FROM model_mappings
                ORDER BY proxy_name;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<ModelMapping> mappings = [];

            while (reader.Read())
            {
                ModelMapping mapping = ReadModelMapping(reader);
                ModelMapping.TrackMaxId(mapping.Id);
                mappings.Add(mapping);
            }

            // Ensure any mappings loaded with id=0 get a fresh ID.
            foreach (ModelMapping m in mappings)
                m.EnsureId();

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
                        id,
                        proxy_name,
                        is_enabled,
                        hidden,
                        model_name,
                        enable_thinking_compatibility,
                        capabilities,
                        enable_sse_keep_alive,
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
                        temperature_priority,
                        repeat_penalty_priority,
                        reasoning_effort_priority,
                        reasoning_effort,
                        reasoning_effort_values,
                        reasoning_effort_format,
                        proactive_overflow_percent,
                        proactive_overflow_tokens,
                        context_summarize_model_id,
                        context_summarize_model_name,
                        auto_compact_paths,
                        redirect_manual_compaction,
                        enable_heartbeats,
                        enable_copilot_compatibility,
                        forward_client_authorization,
                        enable_api_translation,
                        anthropic_max_tokens,
                        anthropic_api_version
                    )
                    VALUES (
                        $id,
                        $proxyName,
                        $isEnabled,
                        $hidden,
                        $modelName,
                        $enableThinkingCompatibility,
                        $capabilities,
                        $enableSseKeepAlive,
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
                        $temperaturePriority,
                        $repeatPenaltyPriority,
                        $reasoningEffortPriority,
                        $reasoningEffort,
                        $reasoningEffortValues,
                        $reasoningEffortFormat,
                        $proactiveOverflowPercent,
                        $proactiveOverflowTokens,
                        $contextSummarizeModelId,
                        $contextSummarizeModelName,
                        $autoCompactPaths,
                        $redirectManualCompaction,
                        $enableHeartbeats,
                        $enableCopilotCompatibility,
                        $forwardClientAuthorization,
                        $enableApiTranslation,
                        $anthropicMaxTokens,
                        $anthropicApiVersion
                    );
                    """;

                AddModelMappingParameters(insertCommand, mapping);
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Raised outside the lock: the Windows GUI keeps its own grid of mappings and rebuilds
        // AppSettings.ModelMappings from those rows on every edit, so it has to re-sync whenever
        // another in-process caller (the web dashboard) rewrites the table.
        ModelMappingsSaved?.Invoke();
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
                // secret is NOT NULL: bind empty string rather than letting DbValue map it to NULL.
                insertCommand.Parameters.AddWithValue("$secret", credential.Secret ?? string.Empty);
                insertCommand.Parameters.AddWithValue("$description", DbValue(credential.Description));
                insertCommand.Parameters.AddWithValue("$username", DbValue(credential.Username));
                insertCommand.Parameters.AddWithValue("$privateKey", DbValue(credential.PrivateKey));
                insertCommand.Parameters.AddWithValue("$certificate", DbValue(credential.Certificate));
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Loads the persisted per-model SSE keep-alive frame counters. Only the client-facing frame
    /// count is persisted; upstream liveness probe results are in-memory only.
    /// </summary>
    public IReadOnlyList<(string Model, long Count, DateTime LastSentUtc)> LoadSseKeepAliveStats()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT model, count, last_sent_utc
                FROM sse_keep_alive
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

    public void UpsertSseKeepAlive(string model, long count, DateTime lastSentUtc)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("SSE keep-alive model is required.", nameof(model));

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sse_keep_alive (model, count, last_sent_utc)
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

    public void ClearSseKeepAlive()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sse_keep_alive;";
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
                    enable_sse_keep_alive,
                    sse_keep_alive_interval_seconds,
                    enable_performance_sampling,
                    enable_api_explorer,
                    run_as_administrator,
                    collect_all_traffic,
                    heartbeat_interval_seconds,
                    compaction_fallback_context_tokens,
                    collect_non_proxied_categories,
                    enable_ir_translation,
                    copilot_compaction_model_name,
                    enable_copilot_compaction_routing,
                    compaction_instruction_set_name,
                    compaction_target_tokens,
                    max_concurrent_compactions,
                    hide_heartbeat_logs,
                    allow_multiple_instances_v2
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
                // Ordinal 2 (allow_multiple_instances) is superseded by the nullable v2 column:
                // NULL means the preference was never set, which reads as the new default (true).
                // The legacy column stays in the SELECT to keep the ordinals below stable.
                ShowCloseToTrayNotification = ReadBoolean(reader, 3),
                CollectRequestDetails = ReadBoolean(reader, 4),
                CollectResponseDetails = ReadBoolean(reader, 5),
                DebugMode = ReadBoolean(reader, 6),
                EnableSseKeepAlive = ReadBoolean(reader, 7),
                SseKeepAliveIntervalSeconds = reader.GetInt32(8),
                EnablePerformanceSampling = ReadBoolean(reader, 9),
                EnableApiExplorer = ReadBoolean(reader, 10),
                RunAsAdministrator = ReadBoolean(reader, 11),
                // Ordinal 12 (collect_all_traffic) is intentionally no longer read: the per-category
                // set below replaced it. The column stays in the SELECT to keep the ordinals that
                // follow it stable, and in the schema so an older build can still open the file.
                HeartbeatIntervalSeconds = reader.GetInt32(13),
                CompactionFallbackContextTokens = reader.GetInt32(14),
                CollectNonProxiedCategories = NonProxiedCategorySet.Parse(
                    reader.IsDBNull(15) ? null : reader.GetString(15)),
                // Appended last so every existing ordinal above stays stable.
                EnableIrTranslation = ReadBoolean(reader, 16),
                CopilotCompactionModelName = reader.IsDBNull(17) ? null : reader.GetString(17),
                EnableCopilotCompactionRouting = ReadBoolean(reader, 18),
                CompactionInstructionSetName = reader.IsDBNull(19) ? null : reader.GetString(19),
                CompactionTargetTokens = reader.GetInt32(20),
                MaxConcurrentCompactions = reader.GetInt32(21),
                HideHeartbeatLogs = ReadBoolean(reader, 22),
                AllowMultipleInstances = reader.IsDBNull(23) || ReadBoolean(reader, 23),
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
                    allow_multiple_instances_v2,
                    show_close_to_tray_notification,
                    collect_request_details,
                    collect_response_details,
                    debug_mode,
                    enable_sse_keep_alive,
                    sse_keep_alive_interval_seconds,
                    enable_performance_sampling,
                    enable_api_explorer,
                    run_as_administrator,
                    collect_all_traffic,
                    heartbeat_interval_seconds,
                    compaction_fallback_context_tokens,
                    collect_non_proxied_categories,
                    enable_ir_translation,
                    copilot_compaction_model_name,
                    enable_copilot_compaction_routing,
                    compaction_instruction_set_name,
                    compaction_target_tokens,
                    max_concurrent_compactions,
                    hide_heartbeat_logs
                )
                VALUES (
                    $id,
                    $autoStartProxy,
                    $startWithDashboardOpen,
                    $allowMultipleInstances,
                    $allowMultipleInstancesV2,
                    $showCloseToTrayNotification,
                    $collectRequestDetails,
                    $collectResponseDetails,
                    $debugMode,
                    $enableSseKeepAlive,
                    $sseKeepAliveIntervalSeconds,
                    $enablePerformanceSampling,
                    $enableApiExplorer,
                    $runAsAdministrator,
                    $collectAllTraffic,
                    $heartbeatIntervalSeconds,
                    $compactionFallbackContextTokens,
                    $collectNonProxiedCategories,
                    $enableIrTranslation,
                    $copilotCompactionModelName,
                    $enableCopilotCompactionRouting,
                    $compactionInstructionSetName,
                    $compactionTargetTokens,
                    $maxConcurrentCompactions,
                    $hideHeartbeatLogs
                )
                ON CONFLICT(id) DO UPDATE SET
                    auto_start_proxy = excluded.auto_start_proxy,
                    start_with_dashboard_open = excluded.start_with_dashboard_open,
                    allow_multiple_instances = excluded.allow_multiple_instances,
                    allow_multiple_instances_v2 = excluded.allow_multiple_instances_v2,
                    show_close_to_tray_notification = excluded.show_close_to_tray_notification,
                    collect_request_details = excluded.collect_request_details,
                    collect_response_details = excluded.collect_response_details,
                    debug_mode = excluded.debug_mode,
                    enable_sse_keep_alive = excluded.enable_sse_keep_alive,
                    sse_keep_alive_interval_seconds = excluded.sse_keep_alive_interval_seconds,
                    enable_performance_sampling = excluded.enable_performance_sampling,
                    enable_api_explorer = excluded.enable_api_explorer,
                    run_as_administrator = excluded.run_as_administrator,
                    collect_all_traffic = excluded.collect_all_traffic,
                    heartbeat_interval_seconds = excluded.heartbeat_interval_seconds,
                    compaction_fallback_context_tokens = excluded.compaction_fallback_context_tokens,
                    collect_non_proxied_categories = excluded.collect_non_proxied_categories,
                    enable_ir_translation = excluded.enable_ir_translation,
                    copilot_compaction_model_name = excluded.copilot_compaction_model_name,
                    enable_copilot_compaction_routing = excluded.enable_copilot_compaction_routing,
                    compaction_instruction_set_name = excluded.compaction_instruction_set_name,
                    compaction_target_tokens = excluded.compaction_target_tokens,
                    max_concurrent_compactions = excluded.max_concurrent_compactions,
                    hide_heartbeat_logs = excluded.hide_heartbeat_logs;
                """;

            command.Parameters.AddWithValue("$id", RuntimeSettingsId);
            command.Parameters.AddWithValue("$autoStartProxy", ToSqliteBoolean(settings.AutoStartProxy));
            command.Parameters.AddWithValue("$startWithDashboardOpen", ToSqliteBoolean(settings.StartWithDashboardOpen));
            // Both columns are written: v2 is authoritative for this build; the legacy column keeps
            // a sensible value for an older build reading the same file.
            command.Parameters.AddWithValue("$allowMultipleInstances", ToSqliteBoolean(settings.AllowMultipleInstances));
            command.Parameters.AddWithValue("$allowMultipleInstancesV2", ToSqliteBoolean(settings.AllowMultipleInstances));
            command.Parameters.AddWithValue("$showCloseToTrayNotification", ToSqliteBoolean(settings.ShowCloseToTrayNotification));
            command.Parameters.AddWithValue("$collectRequestDetails", ToSqliteBoolean(settings.CollectRequestDetails));
            command.Parameters.AddWithValue("$collectResponseDetails", ToSqliteBoolean(settings.CollectResponseDetails));
            command.Parameters.AddWithValue("$debugMode", ToSqliteBoolean(settings.DebugMode));
            command.Parameters.AddWithValue("$enableSseKeepAlive", ToSqliteBoolean(settings.EnableSseKeepAlive));
            command.Parameters.AddWithValue("$sseKeepAliveIntervalSeconds", settings.SseKeepAliveIntervalSeconds);
            command.Parameters.AddWithValue("$enablePerformanceSampling", ToSqliteBoolean(settings.EnablePerformanceSampling));
            command.Parameters.AddWithValue("$enableApiExplorer", ToSqliteBoolean(settings.EnableApiExplorer));
            command.Parameters.AddWithValue("$runAsAdministrator", ToSqliteBoolean(settings.RunAsAdministrator));
            // The legacy switch is no longer written: the per-category set replaces it. It is still
            // bound so the column keeps a value for an older build reading the same file.
            command.Parameters.AddWithValue("$collectAllTraffic", ToSqliteBoolean(false));
            command.Parameters.AddWithValue("$heartbeatIntervalSeconds", settings.HeartbeatIntervalSeconds);
            command.Parameters.AddWithValue("$compactionFallbackContextTokens", settings.CompactionFallbackContextTokens);
            command.Parameters.AddWithValue(
                "$collectNonProxiedCategories",
                NonProxiedCategorySet.Format(settings.CollectNonProxiedCategories));
            command.Parameters.AddWithValue("$enableIrTranslation", ToSqliteBoolean(settings.EnableIrTranslation));
            command.Parameters.AddWithValue("$copilotCompactionModelName", DbValue(settings.CopilotCompactionModelName));
            command.Parameters.AddWithValue("$enableCopilotCompactionRouting", ToSqliteBoolean(settings.EnableCopilotCompactionRouting));
            command.Parameters.AddWithValue("$compactionInstructionSetName", DbValue(settings.CompactionInstructionSetName));
            command.Parameters.AddWithValue("$compactionTargetTokens", settings.CompactionTargetTokens);
            command.Parameters.AddWithValue("$maxConcurrentCompactions", settings.MaxConcurrentCompactions);
            command.Parameters.AddWithValue("$hideHeartbeatLogs", ToSqliteBoolean(settings.HideHeartbeatLogs));
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
    /// newest first. Summary columns only — bodies and headers are loaded on demand via
    /// <see cref="LoadFullLogEntry"/>. <paramref name="userId"/> narrows to one user's attributed
    /// requests (unattributed rows are excluded); null returns everyone's.
    /// </summary>
    public IReadOnlyList<RequestLog> QueryRecent(int count, LogSource source = LogSource.Proxy, int? userId = null)
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
                    draft_n_accepted,
                    original_model,
                    user_id
                FROM {{RequestTable(source)}}
                WHERE ($userId IS NULL OR user_id = $userId)
                ORDER BY timestamp_utc DESC, id DESC
                LIMIT $count;
                """;
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$userId", userId.HasValue ? userId.Value : DBNull.Value);

            using SqliteDataReader reader = command.ExecuteReader();
            List<RequestLog> entries = [];
            while (reader.Read())
                entries.Add(ReadRequestLogSummary(reader));

            return entries;
        }
    }

    /// <summary>
    /// Aggregates token-bearing proxy requests into (local day, model, user) rows for the
    /// dashboard Usage view. The range is UTC inclusive/exclusive; days are server-local calendar
    /// days so the grouping matches the timestamps shown in the GUI. Rows where no tokens were
    /// reported at all are excluded — usage means token consumption, and zero-token probes would
    /// otherwise inflate every request count. <paramref name="userId"/> narrows to one user;
    /// null aggregates everyone (including unattributed rows).
    /// </summary>
    public IReadOnlyList<TokenUsageRow> QueryTokenUsage(DateTime fromUtcInclusive, DateTime toUtcExclusive, int? userId = null)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    date(timestamp_utc, 'localtime') AS day,
                    model,
                    user_id,
                    COUNT(*),
                    SUM(prompt_tokens),
                    SUM(completion_tokens),
                    SUM(CASE WHEN total_tokens > 0 THEN total_tokens ELSE prompt_tokens + completion_tokens END),
                    SUM(cached_prompt_tokens),
                    SUM(reasoning_tokens)
                FROM requests
                WHERE timestamp_utc >= $from AND timestamp_utc < $to
                    AND (prompt_tokens > 0 OR completion_tokens > 0 OR total_tokens > 0)
                    AND ($userId IS NULL OR user_id = $userId)
                GROUP BY day, model, user_id
                ORDER BY day;
                """;
            // "O"-format UTC strings are fixed-length, so a lexicographic range is a time range.
            command.Parameters.AddWithValue("$from", ToUtcText(fromUtcInclusive));
            command.Parameters.AddWithValue("$to", ToUtcText(toUtcExclusive));
            command.Parameters.AddWithValue("$userId", userId.HasValue ? userId.Value : DBNull.Value);

            using SqliteDataReader reader = command.ExecuteReader();
            List<TokenUsageRow> rows = [];
            while (reader.Read())
            {
                rows.Add(new TokenUsageRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8)));
            }

            return rows;
        }
    }

    /// <summary>
    /// Loads up to <paramref name="count"/> recent entries from the active database, oldest first
    /// (so callers can enqueue them in chronological order). Used to seed the in-memory queue on
    /// startup.
    /// </summary>
    /// <remarks>
    /// A single ordered query with the limit, then reversed in memory. Ordering newest-first is what
    /// makes <c>LIMIT</c> select the *most recent* rows — SQLite applies the limit after the sort —
    /// so asking for ascending order directly would return the oldest rows in the table instead.
    /// Reversing here is cheaper than the previous derived-table form (<c>SELECT ... FROM (SELECT ...
    /// LIMIT) ORDER BY</c>), which made SQLite materialize and re-sort the whole derived table.
    /// </remarks>
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
                    draft_n_accepted,
                    original_model,
                    user_id
                FROM {{RequestTable(source)}}
                ORDER BY timestamp_utc DESC, id DESC
                LIMIT $count;
                """;
            command.Parameters.AddWithValue("$count", count);

            using SqliteDataReader reader = command.ExecuteReader();
            List<RequestLog> entries = [];
            while (reader.Read())
                entries.Add(ReadRequestLogSummary(reader));

            // Newest-first from the query; reverse to the chronological order callers enqueue in.
            entries.Reverse();
            return entries;
        }
    }

    /// <summary>
    /// Loads a single full request log entry — including <c>request_body</c> and
    /// <c>response_body</c> — matching the supplied local timestamp within a small tolerance
    /// (±1 ms), so sub-millisecond rounding between the in-memory entry and the stored "O"-format
    /// text does not hide the row. Used to populate the detail view on demand so large bodies do
    /// not need to live in memory. Returns null if no matching entry exists. When multiple rows
    /// fall in the window, the most recently inserted row is returned.
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
                    upstream_response_body,
                    stop_reason,
                    original_model,
                    client_address,
                    user_agent,
                    request_headers,
                    response_headers,
                    user_id
                FROM {{RequestTable(source)}}
                WHERE timestamp_utc BETWEEN $timestampLo AND $timestampHi
                ORDER BY id DESC
                LIMIT 1;
                """;
            // "O"-format UTC strings are fixed-length, so a lexicographic BETWEEN is a time range.
            command.Parameters.AddWithValue("$timestampLo", ToUtcText(localTimestamp.AddMilliseconds(-1)));
            command.Parameters.AddWithValue("$timestampHi", ToUtcText(localTimestamp.AddMilliseconds(1)));

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

            // Exceptions are pruned first, while the request rows that reference them still
            // exist, so the delete is driven by the exact same predicate that decides which
            // requests go. That is equivalent to the previous "delete the exceptions whose ids
            // I just collected" behaviour, but the id set is evaluated by SQLite instead of
            // being materialized in memory and rebuilt as one bound parameter per id — a list
            // that grows unbounded with the backlog and would eventually exceed
            // SQLITE_MAX_VARIABLE_NUMBER.
            //
            // Only the proxy log links exception rows; the MCP and non-proxied paths carry
            // HTTP-level errors on the entry itself.
            if (source == LogSource.Proxy)
            {
                using SqliteCommand deleteExceptions = connection.CreateCommand();
                deleteExceptions.Transaction = transaction;
                deleteExceptions.CommandText =
                    """
                    DELETE FROM exceptions
                    WHERE id IN (
                        SELECT exception_id
                        FROM requests
                        WHERE timestamp_utc < $cutoffUtc
                          AND exception_id IS NOT NULL
                    );
                    """;
                deleteExceptions.Parameters.AddWithValue("$cutoffUtc", ToUtcText(cutoff));
                deleteExceptions.ExecuteNonQuery();
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

            transaction.Commit();

            if (deleted > 0)
                Log.Debug("AppDatabase pruned {Count} request entries older than {Cutoff:u}", deleted, cutoff);

            return deleted;
        }
    }

    /// <summary>
    /// Deletes all request log entries and their linked exception records. SQLite has no
    /// TRUNCATE statement; an unfiltered DELETE FROM is the equivalent. Auto-increment counters
    /// are deliberately left alone so ids stay monotonic across clears. Returns the number of
    /// request rows deleted.
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

            transaction.Commit();

            if (deleted > 0)
                Log.Debug("AppDatabase cleared {Count} request entries", deleted);

            return deleted;
        }
    }

    /// <summary>Maps a log source to its backing request log table.</summary>
    private static string RequestTable(LogSource source) => source switch
    {
        LogSource.Mcp => "mcp_requests",
        LogSource.NonProxied => "non_proxied_requests",
        _ => "requests",
    };

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

    /// <summary>
    /// Creates the application schema on every startup, reconciling existing tables with it.
    /// </summary>
    /// <remarks>
    /// This is the sole schema authority: one baseline DDL that describes the complete current
    /// schema. There are deliberately no dated migration scripts — the schema was rebaselined, and
    /// <see cref="ReconcileExistingTables"/> brings a table that predates a column up to the
    /// baseline instead.
    /// <para>
    /// Reconciliation is required, not optional. <c>CREATE TABLE IF NOT EXISTS</c> never revisits a
    /// table that already exists, so without it a database written by an earlier build keeps its
    /// older columns and the readers fail much later with "no such column" from whichever query
    /// runs first — the reported symptom was a crash in <see cref="LoadRuntimeSettings"/>. Adding
    /// the missing columns is additive and preserves every existing row.
    /// </para>
    /// </remarks>
    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();

            EnsureWalJournalMode(connection);

            // One transaction for the whole DDL batch: a concurrent instance sees either the old
            // schema or the new one, never a half-created set of tables.
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
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
                    original_model TEXT NULL,
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
                    upstream_response_body TEXT NULL,
                    stop_reason TEXT NULL,
                    client_address TEXT NULL,
                    user_agent TEXT NULL,
                    request_headers TEXT NULL,
                    response_headers TEXT NULL,
                    user_id INTEGER NULL
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
                    original_model TEXT NULL,
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
                    upstream_response_body TEXT NULL,
                    stop_reason TEXT NULL,
                    client_address TEXT NULL,
                    user_agent TEXT NULL,
                    request_headers TEXT NULL,
                    response_headers TEXT NULL,
                    user_id INTEGER NULL
                );

                CREATE INDEX IF NOT EXISTS idx_mcp_requests_timestamp_utc ON mcp_requests(timestamp_utc);

                                CREATE TABLE IF NOT EXISTS non_proxied_requests (
                                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    timestamp_utc TEXT NOT NULL,
                                    method TEXT NOT NULL,
                                    ollama_path TEXT NOT NULL,
                                    upstream_path TEXT NOT NULL,
                                    model TEXT NOT NULL,
                                    original_model TEXT NULL,
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
                                    upstream_response_body TEXT NULL,
                                    stop_reason TEXT NULL,
                                    client_address TEXT NULL,
                                    user_agent TEXT NULL,
                                    request_headers TEXT NULL,
                                    response_headers TEXT NULL,
                                    user_id INTEGER NULL
                                );

                                CREATE INDEX IF NOT EXISTS idx_non_proxied_requests_timestamp_utc ON non_proxied_requests(timestamp_utc);

                CREATE TABLE IF NOT EXISTS model_mappings (
                    id INTEGER NOT NULL DEFAULT 0,
                    proxy_name TEXT PRIMARY KEY,
                    is_enabled INTEGER NOT NULL,
                    hidden INTEGER NOT NULL DEFAULT 0,
                    model_name TEXT NOT NULL,
                    enable_thinking_compatibility INTEGER NOT NULL,
                    capabilities TEXT NULL,
                    enable_sse_keep_alive INTEGER NOT NULL,
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
                    temperature_priority INTEGER NOT NULL DEFAULT 0,
                    repeat_penalty_priority INTEGER NOT NULL DEFAULT 0,
                    reasoning_effort_priority INTEGER NOT NULL DEFAULT 0,
                    reasoning_effort TEXT NULL,
                    reasoning_effort_values TEXT NULL,
                    reasoning_effort_format INTEGER NOT NULL DEFAULT 1,
                    proactive_overflow_percent INTEGER NOT NULL DEFAULT 0,
                    proactive_overflow_tokens INTEGER NOT NULL DEFAULT 0,
                    context_summarize_model_name TEXT NULL,
                    context_summarize_model_id INTEGER NULL,
                    auto_compact_paths INTEGER NOT NULL DEFAULT 0,
                    redirect_manual_compaction INTEGER NOT NULL DEFAULT 0,
                    enable_heartbeats INTEGER NOT NULL DEFAULT 1,
                    enable_copilot_compatibility INTEGER NOT NULL DEFAULT 1,
                    forward_client_authorization INTEGER NOT NULL DEFAULT 0,
                    enable_api_translation INTEGER NOT NULL DEFAULT 1,
                    anthropic_max_tokens INTEGER NOT NULL DEFAULT 4096,
                    anthropic_api_version TEXT NOT NULL DEFAULT '2023-06-01'
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

                CREATE TABLE IF NOT EXISTS sse_keep_alive (
                    model TEXT PRIMARY KEY,
                    count INTEGER NOT NULL,
                    last_sent_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS runtime_settings (
                    id TEXT PRIMARY KEY,
                    auto_start_proxy INTEGER NOT NULL,
                    start_with_dashboard_open INTEGER NOT NULL,
                    allow_multiple_instances INTEGER NOT NULL,
                    allow_multiple_instances_v2 INTEGER NULL,
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
                    collect_non_proxied_categories TEXT NULL,
                    enable_ir_translation INTEGER NOT NULL DEFAULT 0,
                    copilot_compaction_model_name TEXT NULL,
                    enable_copilot_compaction_routing INTEGER NOT NULL DEFAULT 1,
                    compaction_instruction_set_name TEXT NULL,
                    compaction_target_tokens INTEGER NOT NULL DEFAULT 0,
                    max_concurrent_compactions INTEGER NOT NULL DEFAULT 1,
                    hide_heartbeat_logs INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS module_registry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    assembly_path TEXT NOT NULL UNIQUE,
                    module_id TEXT NULL,
                    name TEXT NULL,
                    version TEXT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    registered_utc TEXT NOT NULL,
                    last_error TEXT NULL,
                    signature_state TEXT NOT NULL DEFAULT 'Unsigned',
                    signer_key_id TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS mcp_server_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS proxy_users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    email TEXT NOT NULL DEFAULT '',
                    created_utc TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    dashboard_login_enabled INTEGER NOT NULL DEFAULT 0,
                    dashboard_username TEXT NOT NULL DEFAULT '',
                    dashboard_password_hash TEXT NOT NULL DEFAULT '',
                    dashboard_role TEXT NOT NULL DEFAULT 'readOnly'
                );

                CREATE TABLE IF NOT EXISTS proxy_api_keys (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    label TEXT NOT NULL DEFAULT '',
                    key_hash TEXT NOT NULL UNIQUE,
                    key_prefix TEXT NOT NULL DEFAULT '',
                    created_utc TEXT NOT NULL,
                    last_used_utc TEXT NULL,
                    allowed_models TEXT NOT NULL DEFAULT '[]'
                );

                CREATE INDEX IF NOT EXISTS idx_proxy_api_keys_user_id ON proxy_api_keys(user_id);
                """ + SystemLogSchema.CreateSql;
            command.ExecuteNonQuery();
            transaction.Commit();

                        ReconcileExistingTables(connection);
                        EnsureModuleIdUniqueIndex(connection);
                        EnsureDashboardUsernameUniqueIndex(connection);
                    }
                }

                /// <summary>
                /// L19: enforces one registry row per logical module id. A partial unique index keeps
                /// NULL module_id rows (legacy/unresolved imports) unconstrained. Creation is guarded so a
                /// pre-existing database that already holds duplicate ids keeps working — the conflict is
                /// logged and the constraint is simply not applied rather than failing schema init.
                /// </summary>
                private static void EnsureModuleIdUniqueIndex(SqliteConnection connection)
                {
                    try
                    {
                        using SqliteCommand command = connection.CreateCommand();
                        command.CommandText =
                            "CREATE UNIQUE INDEX IF NOT EXISTS ux_module_registry_module_id ON module_registry(module_id) WHERE module_id IS NOT NULL;";
                        command.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        Log.Warning(ex, "Could not enforce unique module_id index; existing duplicate module registrations were left intact");
                    }
                }

                private static void EnsureDashboardUsernameUniqueIndex(SqliteConnection connection)
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_proxy_users_dashboard_username ON proxy_users(dashboard_username COLLATE NOCASE) WHERE dashboard_login_enabled = 1;";
                    command.ExecuteNonQuery();
                }

                /// <summary>
                /// Adds any baseline column that is missing from a table that already exists.
                /// </summary>
                /// <remarks>
                /// <c>CREATE TABLE IF NOT EXISTS</c> cannot add a column to a table that exists, so a database
                /// written by an earlier build would keep a short column set and the readers that name those
                /// columns would throw "no such column" at runtime. This closes that gap without dated
                /// migration scripts: the baseline column list below is the single declaration of what each
                /// table must contain, and anything absent is added.
                /// <para>
                /// Everything here is additive and preserves existing rows. It is idempotent — a table already
                /// at the baseline is untouched — and it never drops or renames a column, so a database cannot
                /// lose data by being opened. A failure on one column is logged and skipped rather than thrown,
                /// matching the rest of the startup path under a concurrent instance.
                /// </para>
                /// </remarks>
                private static void ReconcileExistingTables(SqliteConnection connection)
                {
                    // Every column the baseline DDL declares, with the exact declaration used when adding it to an
                            // existing table. Kept beside the DDL above so the two are read together, and deliberately
                            // complete rather than a list of only the columns some past build happened to add: a column
                            // that has always existed in the baseline is just as absent from a table created before it
                            // was introduced, and omitting it here would reproduce the very bug this method fixes.
                            //
                            // A NOT NULL column with no default in the DDL is given one here, because SQLite refuses to
                            // add a NOT NULL column without a default to a table that already has rows. The default is
                            // the zero value for the type, so existing rows read as "unset" rather than crashing.
                            //
                            // Primary keys are omitted: a table missing its own primary key is a different shape of
                            // problem that ALTER TABLE cannot express.
                            (string Table, string Column, string Declaration)[] columns =
                            [
                                // ── requests ──────────────────────────────────────────────────
                                ("requests", "timestamp_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("requests", "method", "TEXT NOT NULL DEFAULT ''"),
                                ("requests", "ollama_path", "TEXT NOT NULL DEFAULT ''"),
                                ("requests", "upstream_path", "TEXT NOT NULL DEFAULT ''"),
                                ("requests", "model", "TEXT NOT NULL DEFAULT ''"),
                                ("requests", "original_model", "TEXT NULL"),
                                ("requests", "streaming", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "status", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "error_message", "TEXT NULL"),
                                ("requests", "status_code", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "duration_ms", "REAL NOT NULL DEFAULT 0"),
                                ("requests", "prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "completion_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "tokens_per_second", "REAL NOT NULL DEFAULT 0"),
                                ("requests", "exception_id", "INTEGER NULL"),
                                ("requests", "request_body", "TEXT NULL"),
                                ("requests", "upstream_request_body", "TEXT NULL"),
                                ("requests", "response_body", "TEXT NULL"),
                                ("requests", "request_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "response_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "total_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "cached_prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "reasoning_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "draft_n", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "draft_n_accepted", "INTEGER NOT NULL DEFAULT 0"),
                                ("requests", "debug_summary", "TEXT NULL"),
                                ("requests", "upstream_response_body", "TEXT NULL"),
                                ("requests", "stop_reason", "TEXT NULL"),
                                ("requests", "client_address", "TEXT NULL"),
                                ("requests", "user_agent", "TEXT NULL"),
                                ("requests", "request_headers", "TEXT NULL"),
                                ("requests", "response_headers", "TEXT NULL"),
                                ("requests", "user_id", "INTEGER NULL"),

                                // ── mcp_requests: the same log schema ─────────────────────────
                                ("mcp_requests", "timestamp_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("mcp_requests", "method", "TEXT NOT NULL DEFAULT ''"),
                                ("mcp_requests", "ollama_path", "TEXT NOT NULL DEFAULT ''"),
                                ("mcp_requests", "upstream_path", "TEXT NOT NULL DEFAULT ''"),
                                ("mcp_requests", "model", "TEXT NOT NULL DEFAULT ''"),
                                ("mcp_requests", "original_model", "TEXT NULL"),
                                ("mcp_requests", "streaming", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "status", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "error_message", "TEXT NULL"),
                                ("mcp_requests", "status_code", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "duration_ms", "REAL NOT NULL DEFAULT 0"),
                                ("mcp_requests", "prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "completion_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "tokens_per_second", "REAL NOT NULL DEFAULT 0"),
                                ("mcp_requests", "exception_id", "INTEGER NULL"),
                                ("mcp_requests", "request_body", "TEXT NULL"),
                                ("mcp_requests", "upstream_request_body", "TEXT NULL"),
                                ("mcp_requests", "response_body", "TEXT NULL"),
                                ("mcp_requests", "request_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "response_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "total_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "cached_prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "reasoning_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "draft_n", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "draft_n_accepted", "INTEGER NOT NULL DEFAULT 0"),
                                ("mcp_requests", "debug_summary", "TEXT NULL"),
                                ("mcp_requests", "upstream_response_body", "TEXT NULL"),
                                ("mcp_requests", "stop_reason", "TEXT NULL"),
                                ("mcp_requests", "client_address", "TEXT NULL"),
                                ("mcp_requests", "user_agent", "TEXT NULL"),
                                ("mcp_requests", "request_headers", "TEXT NULL"),
                                ("mcp_requests", "response_headers", "TEXT NULL"),
                                ("mcp_requests", "user_id", "INTEGER NULL"),

                                // ── non_proxied_requests ──────────────────────────────────────
                                ("non_proxied_requests", "timestamp_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("non_proxied_requests", "method", "TEXT NOT NULL DEFAULT ''"),
                                ("non_proxied_requests", "ollama_path", "TEXT NOT NULL DEFAULT ''"),
                                ("non_proxied_requests", "upstream_path", "TEXT NOT NULL DEFAULT ''"),
                                ("non_proxied_requests", "model", "TEXT NOT NULL DEFAULT ''"),
                                ("non_proxied_requests", "original_model", "TEXT NULL"),
                                ("non_proxied_requests", "streaming", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "status", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "error_message", "TEXT NULL"),
                                ("non_proxied_requests", "status_code", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "duration_ms", "REAL NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "completion_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "tokens_per_second", "REAL NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "exception_id", "INTEGER NULL"),
                                ("non_proxied_requests", "request_body", "TEXT NULL"),
                                ("non_proxied_requests", "upstream_request_body", "TEXT NULL"),
                                ("non_proxied_requests", "response_body", "TEXT NULL"),
                                ("non_proxied_requests", "request_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "response_bytes", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "total_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "cached_prompt_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "reasoning_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "draft_n", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "draft_n_accepted", "INTEGER NOT NULL DEFAULT 0"),
                                ("non_proxied_requests", "debug_summary", "TEXT NULL"),
                                ("non_proxied_requests", "upstream_response_body", "TEXT NULL"),
                                ("non_proxied_requests", "stop_reason", "TEXT NULL"),
                                ("non_proxied_requests", "client_address", "TEXT NULL"),
                                ("non_proxied_requests", "user_agent", "TEXT NULL"),
                                ("non_proxied_requests", "request_headers", "TEXT NULL"),
                                ("non_proxied_requests", "response_headers", "TEXT NULL"),
                                ("non_proxied_requests", "user_id", "INTEGER NULL"),

                                // ── model_mappings ────────────────────────────────────────────
                                ("model_mappings", "id", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "is_enabled", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "hidden", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "model_name", "TEXT NOT NULL DEFAULT ''"),
                                ("model_mappings", "enable_thinking_compatibility", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "capabilities", "TEXT NULL"),
                                ("model_mappings", "enable_sse_keep_alive", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "upstream_type", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "upstream_url", "TEXT NOT NULL DEFAULT ''"),
                                ("model_mappings", "upstream_timeout_seconds", "INTEGER NOT NULL DEFAULT 300"),
                                ("model_mappings", "repeat_penalty", "REAL NOT NULL DEFAULT 1"),
                                ("model_mappings", "temperature", "REAL NOT NULL DEFAULT 0.7"),
                                ("model_mappings", "instruction_set_name", "TEXT NULL"),
                                ("model_mappings", "redact_request_bodies", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "redact_response_bodies", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "redact_sensitive_json_fields", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "credential_name", "TEXT NULL"),
                                ("model_mappings", "thinking_mode", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "context_window_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "temperature_priority", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "repeat_penalty_priority", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "reasoning_effort_priority", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "reasoning_effort", "TEXT NULL"),
                                ("model_mappings", "reasoning_effort_values", "TEXT NULL"),
                                ("model_mappings", "reasoning_effort_format", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "proactive_overflow_percent", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "proactive_overflow_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "context_summarize_model_name", "TEXT NULL"),
                                ("model_mappings", "context_summarize_model_id", "INTEGER NULL"),
                                ("model_mappings", "auto_compact_paths", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "redirect_manual_compaction", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "enable_heartbeats", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "enable_copilot_compatibility", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "forward_client_authorization", "INTEGER NOT NULL DEFAULT 0"),
                                ("model_mappings", "enable_api_translation", "INTEGER NOT NULL DEFAULT 1"),
                                ("model_mappings", "anthropic_max_tokens", "INTEGER NOT NULL DEFAULT 4096"),
                                ("model_mappings", "anthropic_api_version", "TEXT NOT NULL DEFAULT '2023-06-01'"),

                                // ── runtime_settings ──────────────────────────────────────────
                                ("runtime_settings", "auto_start_proxy", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "start_with_dashboard_open", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "allow_multiple_instances", "INTEGER NOT NULL DEFAULT 0"),
                                // NULL means "never chosen by the user": older builds could not actually
                                // persist this preference (the single-instance check ran before runtime
                                // settings were loaded and no GUI exposed it), so legacy rows carry only
                                // the old default. NULL reads as the new default: multiple instances allowed.
                                ("runtime_settings", "allow_multiple_instances_v2", "INTEGER NULL"),
                                ("runtime_settings", "show_close_to_tray_notification", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "collect_request_details", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "collect_response_details", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "debug_mode", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "enable_sse_keep_alive", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "sse_keep_alive_interval_seconds", "INTEGER NOT NULL DEFAULT 60"),
                                ("runtime_settings", "enable_performance_sampling", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "enable_api_explorer", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "run_as_administrator", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "collect_all_traffic", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "heartbeat_interval_seconds", "INTEGER NOT NULL DEFAULT 300"),
                                ("runtime_settings", "compaction_fallback_context_tokens", "INTEGER NOT NULL DEFAULT 8192"),
                                ("runtime_settings", "collect_non_proxied_categories", "TEXT NULL"),
                                ("runtime_settings", "enable_ir_translation", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "copilot_compaction_model_name", "TEXT NULL"),
                                ("runtime_settings", "enable_copilot_compaction_routing", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "compaction_instruction_set_name", "TEXT NULL"),
                                ("runtime_settings", "compaction_target_tokens", "INTEGER NOT NULL DEFAULT 0"),
                                ("runtime_settings", "max_concurrent_compactions", "INTEGER NOT NULL DEFAULT 1"),
                                ("runtime_settings", "hide_heartbeat_logs", "INTEGER NOT NULL DEFAULT 0"),

                                // ── credentials ───────────────────────────────────────────────
                                ("credentials", "secret", "TEXT NOT NULL DEFAULT ''"),
                                ("credentials", "description", "TEXT NULL"),
                                ("credentials", "username", "TEXT NULL"),
                                ("credentials", "private_key", "TEXT NULL"),
                                ("credentials", "certificate", "TEXT NULL"),

                                // ── sse_keep_alive ────────────────────────────────────────────
                                ("sse_keep_alive", "count", "INTEGER NOT NULL DEFAULT 0"),
                                ("sse_keep_alive", "last_sent_utc", "TEXT NOT NULL DEFAULT ''"),

                                // ── instruction_sets / system_logs ────────────────────────────
                                ("instruction_sets", "instructions", "TEXT NOT NULL DEFAULT ''"),
                                ("instruction_sets", "description", "TEXT NULL"),

                                ("system_logs", "timestamp_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("system_logs", "level", "TEXT NOT NULL DEFAULT ''"),
                                ("system_logs", "message", "TEXT NOT NULL DEFAULT ''"),
                                ("system_logs", "exception", "TEXT NULL"),
                                ("system_logs", "source_context", "TEXT NULL"),

                                // ── exceptions ────────────────────────────────────────────────
                                ("exceptions", "timestamp_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("exceptions", "exception_type", "TEXT NOT NULL DEFAULT ''"),
                                ("exceptions", "message", "TEXT NOT NULL DEFAULT ''"),
                                ("exceptions", "stack_trace", "TEXT NULL"),
                                ("exceptions", "inner_exceptions_json", "TEXT NOT NULL DEFAULT '[]'"),
                                ("exceptions", "method", "TEXT NOT NULL DEFAULT ''"),
                                ("exceptions", "path", "TEXT NOT NULL DEFAULT ''"),
                                ("exceptions", "model", "TEXT NOT NULL DEFAULT ''"),

                                // ── module_registry / mcp_server_settings ─────────────────────
                                ("module_registry", "assembly_path", "TEXT NOT NULL DEFAULT ''"),
                                ("module_registry", "module_id", "TEXT NULL"),
                                ("module_registry", "name", "TEXT NULL"),
                                ("module_registry", "version", "TEXT NULL"),
                                ("module_registry", "is_enabled", "INTEGER NOT NULL DEFAULT 1"),
                                ("module_registry", "registered_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("module_registry", "last_error", "TEXT NULL"),
                                ("module_registry", "signature_state", "TEXT NOT NULL DEFAULT 'Unsigned'"),
                                ("module_registry", "signer_key_id", "TEXT NULL"),

                                ("mcp_server_settings", "value", "TEXT NOT NULL DEFAULT ''"),

                                // ── proxy access security ────────────────────────────────────
                                ("proxy_users", "name", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_users", "email", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_users", "created_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_users", "is_enabled", "INTEGER NOT NULL DEFAULT 1"),
                                ("proxy_users", "dashboard_login_enabled", "INTEGER NOT NULL DEFAULT 0"),
                                ("proxy_users", "dashboard_username", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_users", "dashboard_password_hash", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_users", "dashboard_role", "TEXT NOT NULL DEFAULT 'readOnly'"),
                                ("proxy_api_keys", "user_id", "INTEGER NOT NULL DEFAULT 0"),
                                ("proxy_api_keys", "label", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_api_keys", "key_hash", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_api_keys", "key_prefix", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_api_keys", "created_utc", "TEXT NOT NULL DEFAULT ''"),
                                ("proxy_api_keys", "last_used_utc", "TEXT NULL"),
                                ("proxy_api_keys", "allowed_models", "TEXT NOT NULL DEFAULT '[]'"),
                            ];

                    // Read the existing shape once — table names from sqlite_master, then each
                    // table's columns from PRAGMA table_info — and check membership in memory
                    // instead of issuing two queries per baseline column.
                    HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);
                    using (SqliteCommand tablesCommand = connection.CreateCommand())
                    {
                        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                        using SqliteDataReader tablesReader = tablesCommand.ExecuteReader();
                        while (tablesReader.Read())
                            tables.Add(tablesReader.GetString(0));
                    }

                    Dictionary<string, HashSet<string>> tableColumns = new(StringComparer.OrdinalIgnoreCase);
                    foreach (string existingTable in tables)
                    {
                        HashSet<string> columnSet = new(StringComparer.OrdinalIgnoreCase);
                        using SqliteCommand pragmaCommand = connection.CreateCommand();
                        pragmaCommand.CommandText = $"PRAGMA table_info(\"{existingTable}\");";
                        using SqliteDataReader pragmaReader = pragmaCommand.ExecuteReader();
                        while (pragmaReader.Read())
                            columnSet.Add(pragmaReader.GetString(1));
                        tableColumns[existingTable] = columnSet;
                    }

                    foreach ((string table, string column, string declaration) in columns)
                    {
                        if (!tableColumns.TryGetValue(table, out HashSet<string>? existingColumns) || existingColumns.Contains(column))
                            continue;

                        try
                        {
                            using SqliteCommand command = connection.CreateCommand();
                            command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {declaration};";
                            command.ExecuteNonQuery();
                            existingColumns.Add(column);

                            Log.Information("Reconciled {Table} with the schema baseline: added {Column}.", table, column);
                        }
                        catch (SqliteException ex)
                        {
                            // A concurrent instance may hold the file. Skipping leaves the table as it was, which
                            // is no worse than not running at all, and the next start retries.
                            Log.Warning(ex, "Could not add {Column} to {Table}; continuing with the existing schema.", column, table);
                        }
                        catch (IOException ex)
                        {
                            Log.Warning(ex, "Skipped adding {Column} to {Table}: the database file is in use.", column, table);
                        }
                    }
                }

    /// <summary>
    /// Writes <see cref="SeedData"/>'s default model mappings and placeholder credential into a
    /// brand-new database so a clean build does not open onto an empty Models list. Does nothing once
    /// <c>model_mappings</c> holds any rows, and never replaces an existing credential, so a
    /// configuration that has actually been edited is left alone.
    /// </summary>
    /// <remarks>
    /// The placeholder secret is stored as plaintext deliberately: this runs while the database is
    /// being opened, before the caller has resolved the passphrase that
    /// <see cref="Core.Security.SecretProtector"/> needs to encrypt it. The load path only decrypts
    /// values carrying an encryption envelope, so the plaintext placeholder passes through untouched
    /// and becomes a genuinely encrypted secret the first time the user saves it from the Credentials
    /// tab.
    /// <para>
    /// Failures are logged and skipped rather than thrown. A concurrent instance
    /// (<c>AllowMultipleInstances</c>) may hold the file, and optional seed data must never be able to
    /// prevent the application from starting.
    /// </para>
    /// </remarks>
    private void SeedDefaultsIfEmpty()
    {
        try
        {
            bool hasMappings;
            int maxId;

            // EXISTS short-circuits on the first row, where COUNT(*) had to scan the whole table to
            // answer "is it empty?". This runs on every startup, so the difference is paid each launch.
            // MAX(id) rides along in the same round trip so the shared ID counter can be advanced
            // past anything persisted before seed IDs are assigned (multi-instance startup race).
            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT
                        (SELECT EXISTS (SELECT 1 FROM model_mappings LIMIT 1)),
                        IFNULL((SELECT MAX(id) FROM model_mappings), 0);
                    """;
                using SqliteDataReader reader = command.ExecuteReader();
                reader.Read();
                hasMappings = reader.GetInt64(0) != 0;
                maxId = (int)reader.GetInt64(1);
            }

            if (hasMappings)
                return;

            ModelMapping.TrackMaxId(maxId);

            List<ModelMapping> mappings = SeedData.CreateModelMappings();
            SaveModelMappings(mappings);

            // SaveCredentials is a full-table replace, so carry the existing credentials forward:
            // a user's real secret is never overwritten by the placeholder, even when the mappings
            // table was emptied out from under the seeder.
            List<StoredCredential> credentials = [.. LoadCredentials()];
            foreach (StoredCredential seed in SeedData.CreateCredentials())
            {
                bool exists = credentials.Any(c =>
                    string.Equals(c.Name, seed.Name, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                    credentials.Add(seed);
            }

            SaveCredentials(credentials);

            Log.Information(
                "Seeded {MappingCount} default model mappings into the new database at {Path}",
                mappings.Count, _configuredDbPath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Log.Warning(ex, "Skipped seeding default model mappings for {Path}", _configuredDbPath);
        }
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
            // database rather than crashing; the connection opened later will fail with
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
        // Null rather than empty so "no redirect happened" is distinguishable from a redirect,
        // and so the column stays index-friendly. DbValue maps an empty OriginalModel to DBNull.
        command.Parameters.AddWithValue("$originalModel", DbValue(entry.OriginalModel));
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
        command.Parameters.AddWithValue("$stopReason", DbValue(entry.StopReason));
        command.Parameters.AddWithValue("$clientAddress", DbValue(entry.ClientAddress));
        command.Parameters.AddWithValue("$userAgent", DbValue(entry.UserAgent));
        command.Parameters.AddWithValue("$requestHeaders", DbValue(entry.RequestHeaders));
        command.Parameters.AddWithValue("$responseHeaders", DbValue(entry.ResponseHeaders));
        command.Parameters.AddWithValue("$userId", entry.UserId.HasValue ? entry.UserId.Value : DBNull.Value);
    }

    private static void AddModelMappingParameters(SqliteCommand command, ModelMapping mapping)
    {
        command.Parameters.AddWithValue("$id", mapping.Id);
        command.Parameters.AddWithValue("$proxyName", mapping.ProxyName);
        command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(mapping.IsEnabled));
        command.Parameters.AddWithValue("$hidden", ToSqliteBoolean(mapping.Hidden));
        command.Parameters.AddWithValue("$modelName", mapping.ModelName);
        command.Parameters.AddWithValue("$enableThinkingCompatibility", ToSqliteBoolean(mapping.EnableThinkingCompatibility));
        command.Parameters.AddWithValue("$capabilities", mapping.Capabilities.Count > 0
            ? DbValue(string.Join(",", mapping.Capabilities))
            : DBNull.Value);
        command.Parameters.AddWithValue("$enableSseKeepAlive", ToSqliteBoolean(mapping.EnableSseKeepAlive));
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
        command.Parameters.AddWithValue("$contextSummarizeModelId", mapping.ContextSummarizeModelId.HasValue ? (object)mapping.ContextSummarizeModelId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$contextSummarizeModelName", DbValue(mapping.ContextSummarizeModelName));
        command.Parameters.AddWithValue("$autoCompactPaths", (int)mapping.AutoCompactPaths);
        command.Parameters.AddWithValue("$redirectManualCompaction", ToSqliteBoolean(mapping.RedirectManualCompaction));
        command.Parameters.AddWithValue("$enableHeartbeats", ToSqliteBoolean(mapping.EnableHeartbeats));
        command.Parameters.AddWithValue("$enableCopilotCompatibility", ToSqliteBoolean(mapping.EnableCopilotCompatibility));
        command.Parameters.AddWithValue("$forwardClientAuthorization", ToSqliteBoolean(mapping.ForwardClientAuthorization));
        command.Parameters.AddWithValue("$enableApiTranslation", ToSqliteBoolean(mapping.EnableApiTranslation));
        command.Parameters.AddWithValue("$anthropicMaxTokens", mapping.AnthropicMaxTokens);
        command.Parameters.AddWithValue("$anthropicApiVersion", DbValue(mapping.AnthropicApiVersion));
    }

    /// <summary>
    /// Materialises one <c>model_mappings</c> row. Written as a sequence of single-property
    /// assignments rather than a single object initializer so each column read is its own
    /// operation: a debugger can step and breakpoint on any field (notably the compaction
    /// columns) instead of landing inside one opaque initializer expression.
    /// </summary>
    private static ModelMapping ReadModelMapping(SqliteDataReader reader)
    {
        ModelMapping mapping = new()
        {
            Id = reader.GetInt32(0),
            IsEnabled = ReadBoolean(reader, 1),
            Hidden = ReadBoolean(reader, 2),
            ProxyName = reader.GetString(3),
            ModelName = reader.GetString(4),
            EnableThinkingCompatibility = ReadBoolean(reader, 5),
            Capabilities = reader.IsDBNull(6)
                ? []
                : [.. reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            EnableSseKeepAlive = ReadBoolean(reader, 7)
        };

        int upstreamType = reader.GetInt32(8);
        mapping.UpstreamType = Enum.IsDefined(typeof(UpstreamType), upstreamType)
            ? (UpstreamType)upstreamType
            : UpstreamType.OpenAI;

        mapping.UpstreamUrl = reader.GetString(9);
        mapping.UpstreamTimeoutSeconds = reader.GetInt32(10);
        mapping.RepeatPenalty = reader.GetDouble(11);
        mapping.Temperature = reader.GetDouble(12);
        mapping.InstructionSetName = reader.IsDBNull(13) ? null : reader.GetString(13);
        mapping.RedactRequestBodies = ReadBoolean(reader, 14);
        mapping.RedactResponseBodies = ReadBoolean(reader, 15);
        mapping.RedactSensitiveJsonFields = ReadBoolean(reader, 16);
        mapping.CredentialName = reader.IsDBNull(17) ? null : reader.GetString(17);

        int thinkingMode = reader.GetInt32(18);
        mapping.ThinkingMode = Enum.IsDefined(typeof(ThinkingMode), thinkingMode)
            ? (ThinkingMode)thinkingMode
            : ThinkingMode.Off;

        mapping.ContextWindowTokens = reader.GetInt32(19);

        int temperaturePriority = reader.GetInt32(20);
        mapping.TemperaturePriority = Enum.IsDefined(typeof(SamplingPriority), temperaturePriority)
            ? (SamplingPriority)temperaturePriority
            : SamplingPriority.ClientApp;

        int repeatPenaltyPriority = reader.GetInt32(21);
        mapping.RepeatPenaltyPriority = Enum.IsDefined(typeof(SamplingPriority), repeatPenaltyPriority)
            ? (SamplingPriority)repeatPenaltyPriority
            : SamplingPriority.ClientApp;

        int reasoningEffortPriority = reader.GetInt32(22);
        mapping.ReasoningEffortPriority = Enum.IsDefined(typeof(SamplingPriority), reasoningEffortPriority)
            ? (SamplingPriority)reasoningEffortPriority
            : SamplingPriority.ClientApp;

        mapping.ReasoningEffort = reader.IsDBNull(23) ? null : reader.GetString(23);
        mapping.ReasoningEffortValues = reader.IsDBNull(24)
            ? []
            : [.. reader.GetString(24).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        mapping.ReasoningEffortFormat = ToReasoningEffortFormat(reader.GetInt32(25));
        mapping.ProactiveOverflowPercent = reader.GetInt32(26);
        mapping.ProactiveOverflowTokens = reader.GetInt32(27);

        // Compaction configuration. Read into named locals so the stored name and id can be
        // inspected side by side while debugging a manual-compaction redirect decision.
        int? contextSummarizeModelId = reader.IsDBNull(28) ? null : reader.GetInt32(28);
        string? contextSummarizeModelName = reader.IsDBNull(29) ? null : reader.GetString(29);
        mapping.ContextSummarizeModelId = contextSummarizeModelId;
        mapping.ContextSummarizeModelName = contextSummarizeModelName;

        int autoCompactPaths = reader.GetInt32(30);
        mapping.AutoCompactPaths = Enum.IsDefined(typeof(AutoCompactPaths), autoCompactPaths)
            ? (AutoCompactPaths)autoCompactPaths
            : AutoCompactPaths.None;

        mapping.RedirectManualCompaction = ReadBoolean(reader, 31);
        mapping.EnableHeartbeats = ReadBoolean(reader, 32);
        mapping.EnableCopilotCompatibility = ReadBoolean(reader, 33);
        mapping.ForwardClientAuthorization = ReadBoolean(reader, 34);
        mapping.EnableApiTranslation = ReadBoolean(reader, 35);
        mapping.AnthropicMaxTokens = reader.GetInt32(36);
        mapping.AnthropicApiVersion = reader.IsDBNull(37) ? "2023-06-01" : reader.GetString(37);
        return mapping;
    }

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
        StopReason = reader.IsDBNull(26) ? null : reader.GetString(26),
        OriginalModel = reader.IsDBNull(27) ? string.Empty : reader.GetString(27),
        ClientAddress = reader.IsDBNull(28) ? null : reader.GetString(28),
        UserAgent = reader.IsDBNull(29) ? null : reader.GetString(29),
        RequestHeaders = reader.IsDBNull(30) ? null : reader.GetString(30),
        ResponseHeaders = reader.IsDBNull(31) ? null : reader.GetString(31),
        UserId = reader.IsDBNull(32) ? null : reader.GetInt32(32),
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
        OriginalModel = reader.IsDBNull(21) ? string.Empty : reader.GetString(21),
        UserId = reader.IsDBNull(22) ? null : reader.GetInt32(22),
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

        /// <summary>
        /// Escapes the LIKE wildcards (<c>%</c> and <c>_</c>) plus the escape character itself, so a
        /// user typing a literal percent or underscore searches for that character instead of matching
        /// everything. Pair with <c>ESCAPE '\'</c> in the query.
        /// </summary>
        private static string EscapeLike(string value) => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

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
                    last_error,
                    signature_state,
                    signer_key_id
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
                    SignatureState = reader.IsDBNull(8) ? ModuleSignatureStates.Unsigned : reader.GetString(8),
                    SignerKeyId = reader.IsDBNull(9) ? null : reader.GetString(9),
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
                    last_error,
                    signature_state,
                    signer_key_id
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
                SignatureState = reader.IsDBNull(8) ? ModuleSignatureStates.Unsigned : reader.GetString(8),
                SignerKeyId = reader.IsDBNull(9) ? null : reader.GetString(9),
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
                    last_error,
                    signature_state,
                    signer_key_id
                )
                VALUES (
                    $assemblyPath,
                    $moduleId,
                    $name,
                    $version,
                    $isEnabled,
                    $registeredUtc,
                    $lastError,
                    $signatureState,
                    $signerKeyId
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
            command.Parameters.AddWithValue("$signatureState", entry.SignatureState);
            command.Parameters.AddWithValue("$signerKeyId", DbValue(entry.SignerKeyId));

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
                    last_error = $lastError,
                    signature_state = $signatureState,
                    signer_key_id = $signerKeyId
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$moduleId", DbValue(entry.ModuleId));
            command.Parameters.AddWithValue("$name", DbValue(entry.Name));
            command.Parameters.AddWithValue("$version", DbValue(entry.Version));
            command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(entry.IsEnabled));
            command.Parameters.AddWithValue("$lastError", DbValue(entry.LastError));
            command.Parameters.AddWithValue("$signatureState", entry.SignatureState);
            command.Parameters.AddWithValue("$signerKeyId", DbValue(entry.SignerKeyId));
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

    // ── Proxy access security: users + API keys ─────────────────────────────

    /// <summary>Loads all proxy users ordered by name.</summary>
    public IReadOnlyList<ProxyUser> LoadProxyUsers()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, email, created_utc, is_enabled,
                       dashboard_login_enabled, dashboard_username, dashboard_password_hash, dashboard_role
                FROM proxy_users
                ORDER BY name COLLATE NOCASE, id;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<ProxyUser> users = [];
            while (reader.Read())
            {
                users.Add(new ProxyUser
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Email = reader.GetString(2),
                    CreatedUtc = ReadUtc(reader, 3),
                    IsEnabled = ReadBoolean(reader, 4),
                    DashboardLoginEnabled = ReadBoolean(reader, 5),
                    DashboardUsername = reader.GetString(6),
                    DashboardPasswordHash = reader.GetString(7),
                    DashboardRole = reader.GetString(8),
                });
            }

            return users;
        }
    }

    public bool HasDashboardLogins()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM proxy_users WHERE dashboard_login_enabled = 1);";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    /// <summary>Creates a proxy user. The generated row id is written back to <paramref name="user"/>.</summary>
    public void InsertProxyUser(ProxyUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO proxy_users
                    (name, email, created_utc, is_enabled, dashboard_login_enabled, dashboard_username, dashboard_password_hash, dashboard_role)
                VALUES
                    ($name, $email, $createdUtc, $isEnabled, $dashboardLoginEnabled, $dashboardUsername, $dashboardPasswordHash, $dashboardRole);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$name", user.Name);
            command.Parameters.AddWithValue("$email", user.Email);
            command.Parameters.AddWithValue("$createdUtc", ToUtcText(user.CreatedUtc));
            command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(user.IsEnabled));
            command.Parameters.AddWithValue("$dashboardLoginEnabled", ToSqliteBoolean(user.DashboardLoginEnabled));
            command.Parameters.AddWithValue("$dashboardUsername", user.DashboardUsername);
            command.Parameters.AddWithValue("$dashboardPasswordHash", user.DashboardPasswordHash);
            command.Parameters.AddWithValue("$dashboardRole", user.DashboardRole);

            object? scalar = command.ExecuteScalar();
            user.Id = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Updates a proxy user's contact info and enabled state.</summary>
    public void UpdateProxyUser(ProxyUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE proxy_users
                SET name = $name, email = $email, is_enabled = $isEnabled,
                    dashboard_login_enabled = $dashboardLoginEnabled,
                    dashboard_username = $dashboardUsername,
                    dashboard_password_hash = $dashboardPasswordHash,
                    dashboard_role = $dashboardRole
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", user.Id);
            command.Parameters.AddWithValue("$name", user.Name);
            command.Parameters.AddWithValue("$email", user.Email);
            command.Parameters.AddWithValue("$isEnabled", ToSqliteBoolean(user.IsEnabled));
            command.Parameters.AddWithValue("$dashboardLoginEnabled", ToSqliteBoolean(user.DashboardLoginEnabled));
            command.Parameters.AddWithValue("$dashboardUsername", user.DashboardUsername);
            command.Parameters.AddWithValue("$dashboardPasswordHash", user.DashboardPasswordHash);
            command.Parameters.AddWithValue("$dashboardRole", user.DashboardRole);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes a proxy user and every API key belonging to it.</summary>
    public void DeleteProxyUser(int id)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM proxy_api_keys WHERE user_id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM proxy_users WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Loads all proxy API keys (hashes included — never expose these through an API).</summary>
    public IReadOnlyList<ProxyApiKey> LoadProxyApiKeys()
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, user_id, label, key_hash, key_prefix, created_utc, last_used_utc, allowed_models
                FROM proxy_api_keys
                ORDER BY id;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<ProxyApiKey> keys = [];
            while (reader.Read())
                keys.Add(ReadProxyApiKey(reader));

            return keys;
        }
    }

    /// <summary>Finds an API key by its SHA-256 hash, or null when no key matches.</summary>
    public ProxyApiKey? FindProxyApiKeyByHash(string keyHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, user_id, label, key_hash, key_prefix, created_utc, last_used_utc, allowed_models
                FROM proxy_api_keys
                WHERE key_hash = $keyHash;
                """;
            command.Parameters.AddWithValue("$keyHash", keyHash);

            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadProxyApiKey(reader) : null;
        }
    }

    /// <summary>Finds a proxy user by row id, or null when it does not exist.</summary>
    public ProxyUser? FindProxyUser(int id)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, email, created_utc, is_enabled,
                       dashboard_login_enabled, dashboard_username, dashboard_password_hash, dashboard_role
                FROM proxy_users
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new ProxyUser
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2),
                CreatedUtc = ReadUtc(reader, 3),
                IsEnabled = ReadBoolean(reader, 4),
                DashboardLoginEnabled = ReadBoolean(reader, 5),
                DashboardUsername = reader.GetString(6),
                DashboardPasswordHash = reader.GetString(7),
                DashboardRole = reader.GetString(8),
            };
        }
    }

    /// <summary>
    /// Finds a proxy user by dashboard username without filtering on the enabled flags, so the
    /// login flow can distinguish a wrong password (401) from an account that is disabled or has
    /// dashboard sign-in turned off (403 with a specific message).
    /// </summary>
    public ProxyUser? FindProxyUserByDashboardUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, name, email, created_utc, is_enabled, dashboard_login_enabled, dashboard_username, dashboard_password_hash, dashboard_role FROM proxy_users WHERE dashboard_username = $username COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$username", username.Trim());

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new ProxyUser
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Email = reader.GetString(2),
                CreatedUtc = ReadUtc(reader, 3),
                IsEnabled = ReadBoolean(reader, 4),
                DashboardLoginEnabled = ReadBoolean(reader, 5),
                DashboardUsername = reader.GetString(6),
                DashboardPasswordHash = reader.GetString(7),
                DashboardRole = reader.GetString(8),
            };
        }
    }

    /// <summary>Creates an API key. The generated row id is written back to <paramref name="key"/>.</summary>
    public void InsertProxyApiKey(ProxyApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO proxy_api_keys (user_id, label, key_hash, key_prefix, created_utc, last_used_utc, allowed_models)
                VALUES ($userId, $label, $keyHash, $keyPrefix, $createdUtc, $lastUsedUtc, $allowedModels);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$userId", key.UserId);
            command.Parameters.AddWithValue("$label", key.Label);
            command.Parameters.AddWithValue("$keyHash", key.KeyHash);
            command.Parameters.AddWithValue("$keyPrefix", key.KeyPrefix);
            command.Parameters.AddWithValue("$createdUtc", ToUtcText(key.CreatedUtc));
            command.Parameters.AddWithValue("$lastUsedUtc", key.LastUsedUtc is { } used ? ToUtcText(used) : DBNull.Value);
            command.Parameters.AddWithValue("$allowedModels", JsonSerializer.Serialize(key.AllowedModels, _jsonOptions));

            object? scalar = command.ExecuteScalar();
            key.Id = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Updates an API key's label and allowed-model list.</summary>
    public void UpdateProxyApiKeyAccess(int id, string label, IReadOnlyList<string> allowedModels)
    {
        ArgumentNullException.ThrowIfNull(allowedModels);

        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE proxy_api_keys
                SET label = $label, allowed_models = $allowedModels
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$allowedModels", JsonSerializer.Serialize(allowedModels, _jsonOptions));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Records the last time an API key authenticated a request.</summary>
    public void UpdateProxyApiKeyLastUsed(int id, DateTime lastUsedUtc)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE proxy_api_keys SET last_used_utc = $lastUsedUtc WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$lastUsedUtc", ToUtcText(lastUsedUtc));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes an API key.</summary>
    public void DeleteProxyApiKey(int id)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM proxy_api_keys WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private static ProxyApiKey ReadProxyApiKey(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        UserId = reader.GetInt32(1),
        Label = reader.GetString(2),
        KeyHash = reader.GetString(3),
        KeyPrefix = reader.GetString(4),
        CreatedUtc = ReadUtc(reader, 5),
        LastUsedUtc = reader.IsDBNull(6) ? null : ReadUtc(reader, 6),
        AllowedModels = DeserializeAllowedModels(reader.IsDBNull(7) ? null : reader.GetString(7)),
    };

    private static List<string> DeserializeAllowedModels(string? json)
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

    // Module operations run on dedicated pooled connections OUTSIDE the host lock: WAL plus the
    // busy timeout serializes writers at the SQLite level, so a slow module callback or query can
    // no longer stall host request logging (and vice versa).

    /// <summary>
    /// Executes a module-provided baseline schema script (DDL) against the application
    /// database. Scripts must be idempotent (CREATE TABLE IF NOT EXISTS / CREATE INDEX IF
    /// NOT EXISTS). Called through <c>ModuleDatabaseGateway</c>.
    /// </summary>
    internal void ExecuteModuleSchemaScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    /// <summary>Executes a module-provided non-query command. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal int ExecuteModuleNonQuery(string commandText, Action<DbCommand>? configureCommand)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        configureCommand?.Invoke(command);
        return command.ExecuteNonQuery();
    }

    /// <summary>Executes a module-provided scalar command. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal object? ExecuteModuleScalar(string commandText, Action<DbCommand>? configureCommand)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        configureCommand?.Invoke(command);
        return command.ExecuteScalar();
    }

    /// <summary>Executes a module-provided query and maps every row. Called through <c>ModuleDatabaseGateway</c>.</summary>
    internal IReadOnlyList<T> ExecuteModuleQuery<T>(
        string commandText,
        Func<DbDataReader, T> map,
        Action<DbCommand>? configureCommand)
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

    /// <summary>
    /// Runs module work inside a single transaction on a dedicated connection. The transaction
    /// rolls back if <paramref name="work"/> throws. Called through <c>ModuleDatabaseGateway</c>.
    /// </summary>
    internal void ExecuteModuleTransaction(Action<DbConnection, DbTransaction> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        work(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Returns the most recent system log entries (newest first).
    /// </summary>
    /// <param name="levelFilter">Optional level name to filter by (e.g. "Error"). Null returns all.</param>
    /// <param name="limit">Maximum number of entries to return. Default 500.</param>
    /// <param name="excludeHeartbeats">
    /// When true, periodic heartbeat-failure entries are omitted. Excluding in SQL rather than in the
    /// caller keeps <paramref name="limit"/> applying to the entries the caller actually wants, which
    /// matters because a failed probe repeats for every enabled model and can otherwise fill the whole
    /// window.
    /// </param>
    public IReadOnlyList<SystemLogEntry> GetSystemLogs(
            string? levelFilter = null, int limit = 500,
            IReadOnlyCollection<string>? levelFilters = null, string? searchText = null,
            bool excludeHeartbeats = false)
        {
            lock (_lock)
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();

                List<string> clauses = [];

                // Multi-select levels take precedence over the legacy single-level argument, so existing
                // callers keep working while the UI moves to a checked list.
                List<string> levels = levelFilters is { Count: > 0 }
                    ? [.. levelFilters]
                    : (string.IsNullOrEmpty(levelFilter) ? [] : [levelFilter]);

                if (levels.Count > 0)
                {
                    List<string> levelParams = [];
                    for (int i = 0; i < levels.Count; i++)
                    {
                        string name = $"$level{i}";
                        levelParams.Add(name);
                        command.Parameters.AddWithValue(name, levels[i]);
                    }

                    clauses.Add($"level IN ({string.Join(", ", levelParams)})");
                }

                // Free-text search spans the fields a user can actually see, so a term matches whether it
                // appears in the message, the exception detail, or the source context. LIKE is
                // case-insensitive for ASCII in SQLite by default; the ESCAPE clause keeps a literal
                // percent or underscore in the term from acting as a wildcard.
                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    clauses.Add(
                        "(message LIKE $search ESCAPE '\\' " +
                        "OR exception LIKE $search ESCAPE '\\' " +
                        "OR source_context LIKE $search ESCAPE '\\')");
                    command.Parameters.AddWithValue("$search", "%" + EscapeLike(searchText.Trim()) + "%");
                }

                // The heartbeat marker is stamped into the message by the logger, so the split keys on
                // stored text rather than an event property the database sink cannot persist.
                if (excludeHeartbeats)
                {
                    clauses.Add("message NOT LIKE $heartbeat ESCAPE '\\'");
                    command.Parameters.AddWithValue(
                        "$heartbeat", "%" + EscapeLike(AppLogger.HeartbeatMessageMarker) + "%");
                }

                string where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : string.Empty;
                command.CommandText =
                    "SELECT timestamp_utc, level, message, exception, source_context " +
                    $"FROM system_logs{where} ORDER BY id DESC LIMIT $limit";
                command.Parameters.AddWithValue("$limit", limit);

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

                    results.Add(new SystemLogEntry(timestamp, level, message, exception, sourceContext,
                        message.Contains(AppLogger.HeartbeatMessageMarker, StringComparison.Ordinal)));
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

    /// <summary>
    /// Prunes system log entries older than <paramref name="maxAge"/> and caps the table at
    /// <paramref name="maxRows"/> rows, deleting oldest first. Returns the number of rows deleted.
    /// </summary>
    public int PruneSystemLogs(TimeSpan maxAge, int maxRows)
    {
        lock (_lock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            int deleted = 0;

            if (maxAge > TimeSpan.Zero)
            {
                using SqliteCommand deleteOld = connection.CreateCommand();
                deleteOld.Transaction = transaction;
                deleteOld.CommandText = "DELETE FROM system_logs WHERE timestamp_utc < $cutoffUtc;";
                deleteOld.Parameters.AddWithValue("$cutoffUtc", ToUtcText(DateTime.UtcNow - maxAge));
                deleted += deleteOld.ExecuteNonQuery();
            }

            if (maxRows >= 0)
            {
                // AUTOINCREMENT ids are monotonic, so deleting everything at or below
                // (max id - cap) leaves at most maxRows rows without a COUNT/OFFSET scan.
                using SqliteCommand capRows = connection.CreateCommand();
                capRows.Transaction = transaction;
                capRows.CommandText =
                    "DELETE FROM system_logs WHERE id <= (SELECT MAX(id) - $maxRows FROM system_logs);";
                capRows.Parameters.AddWithValue("$maxRows", maxRows);
                deleted += capRows.ExecuteNonQuery();
            }

            transaction.Commit();

            if (deleted > 0)
                Log.Debug("AppDatabase pruned {Count} system log entries", deleted);

            return deleted;
        }
    }

    public void Dispose()
    {
        // Connections are opened per operation and disposed immediately; only the degraded-mode
        // anchor connection lives for the instance lifetime.
        _degradedAnchorConnection?.Dispose();
        _degradedAnchorConnection = null;
    }
}

