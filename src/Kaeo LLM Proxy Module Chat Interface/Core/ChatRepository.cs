using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Loads and persists the Chat Interface module's channels and feature settings through the
/// shared application database gateway. Settings live in the <c>mcp_chat_settings</c> key/value
/// table; channels in <c>mcp_chat_channels</c>. Callers catch IOException/SqliteException so a
/// sharing violation (multiple app instances) degrades gracefully instead of crashing.
/// </summary>
internal sealed class ChatRepository(IModuleDatabase database)
{
    private const string SendEnabledKey = "send_enabled";
    private const string AskEnabledKey = "ask_enabled";
    private const string ReadEnabledKey = "read_enabled";
    private const string ChannelsEnabledKey = "channels_enabled";
    private const string DefaultAskTimeoutKey = "default_ask_timeout_seconds";
    private const string MaxMessageCharsKey = "max_message_chars";
    private const string DefaultPollIntervalKey = "default_poll_interval_seconds";
    private const string McpLogLevelKey = "mcp_log_level";

    private readonly IModuleDatabase _database = database;

    // ── Settings ────────────────────────────────────────────────────────────

    public ChatSettings LoadSettings()
    {
        Dictionary<string, string> values = LoadKeyValueTable("mcp_chat_settings");

        return new ChatSettings
        {
            SendToolEnabled = ReadBool(values, SendEnabledKey, true),
            AskToolEnabled = ReadBool(values, AskEnabledKey, true),
            ReadToolEnabled = ReadBool(values, ReadEnabledKey, true),
            ChannelsToolEnabled = ReadBool(values, ChannelsEnabledKey, true),
            DefaultAskTimeoutSeconds = Math.Clamp(ReadInt(values, DefaultAskTimeoutKey, 300), 10, 3_600),
            MaxMessageChars = Math.Clamp(ReadInt(values, MaxMessageCharsKey, 4_000), 500, 50_000),
            DefaultPollIntervalSeconds = Math.Clamp(ReadInt(values, DefaultPollIntervalKey, 15), 5, 300),
            McpLogLevel = ReadLogLevel(values, McpLogLevelKey, ChatMcpLogLevel.Connectivity),
        };
    }

    public void SaveSettings(ChatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        UpsertKeyValue("mcp_chat_settings", SendEnabledKey, settings.SendToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_chat_settings", AskEnabledKey, settings.AskToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_chat_settings", ReadEnabledKey, settings.ReadToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_chat_settings", ChannelsEnabledKey, settings.ChannelsToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_chat_settings", DefaultAskTimeoutKey, Math.Clamp(settings.DefaultAskTimeoutSeconds, 10, 3_600).ToString());
        UpsertKeyValue("mcp_chat_settings", MaxMessageCharsKey, Math.Clamp(settings.MaxMessageChars, 500, 50_000).ToString());
        UpsertKeyValue("mcp_chat_settings", DefaultPollIntervalKey, Math.Clamp(settings.DefaultPollIntervalSeconds, 5, 300).ToString());
        UpsertKeyValue("mcp_chat_settings", McpLogLevelKey, settings.McpLogLevel.ToString());
    }

    // ── Channels ────────────────────────────────────────────────────────────

    public IReadOnlyList<ChatChannelConfig> LoadChannels() =>
        _database.Query(
            """
            SELECT id, name, provider_type, target, credential_name, webhook_url, config_json,
                   enabled, poll_interval_seconds
            FROM mcp_chat_channels
            ORDER BY name;
            """,
            MapChannel);

    /// <summary>Looks up a channel by its unique name (case-insensitive).</summary>
    public ChatChannelConfig? FindChannelByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        IReadOnlyList<ChatChannelConfig> matches = _database.Query(
            """
            SELECT id, name, provider_type, target, credential_name, webhook_url, config_json,
                   enabled, poll_interval_seconds
            FROM mcp_chat_channels
            WHERE name = $name COLLATE NOCASE
            LIMIT 1;
            """,
            MapChannel,
            command => AddParameter(command, "$name", name.Trim()));

        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Inserts a new channel and returns its database identity.</summary>
    public int InsertChannel(ChatChannelConfig channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        object? id = _database.ExecuteScalar(
            """
            INSERT INTO mcp_chat_channels
                (name, provider_type, target, credential_name, webhook_url, config_json, enabled, poll_interval_seconds)
            VALUES ($name, $providerType, $target, $credentialName, $webhookUrl, $configJson, $enabled, $pollInterval);
            SELECT last_insert_rowid();
            """,
            command => ConfigureChannelParameters(command, channel));

        return Convert.ToInt32(id);
    }

    /// <summary>Updates an existing channel identified by <see cref="ChatChannelConfig.Id"/>.</summary>
    public void UpdateChannel(ChatChannelConfig channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _database.Execute(
            """
            UPDATE mcp_chat_channels
            SET name = $name, provider_type = $providerType, target = $target,
                credential_name = $credentialName, webhook_url = $webhookUrl, config_json = $configJson,
                enabled = $enabled, poll_interval_seconds = $pollInterval
            WHERE id = $id;
            """,
            command =>
            {
                ConfigureChannelParameters(command, channel);
                AddParameter(command, "$id", channel.Id);
            });
    }

    public void DeleteChannel(int id) =>
        _database.Execute(
            "DELETE FROM mcp_chat_channels WHERE id = $id;",
            command => AddParameter(command, "$id", id));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ChatChannelConfig MapChannel(DbDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        ProviderType = reader.GetString(2),
        Target = reader.GetString(3),
        CredentialName = reader.IsDBNull(4) ? null : reader.GetString(4),
        WebhookUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
        ConfigJson = reader.IsDBNull(6) ? null : reader.GetString(6),
        Enabled = reader.GetInt32(7) != 0,
        PollIntervalSeconds = Math.Clamp(reader.GetInt32(8), 5, 300),
    };

    private static void ConfigureChannelParameters(DbCommand command, ChatChannelConfig channel)
    {
        AddParameter(command, "$name", channel.Name.Trim());
        AddParameter(command, "$providerType", channel.ProviderType.Trim().ToLowerInvariant());
        AddParameter(command, "$target", channel.Target.Trim());
        AddParameter(command, "$credentialName", string.IsNullOrWhiteSpace(channel.CredentialName) ? null : channel.CredentialName.Trim());
        AddParameter(command, "$webhookUrl", string.IsNullOrWhiteSpace(channel.WebhookUrl) ? null : channel.WebhookUrl.Trim());
        AddParameter(command, "$configJson", string.IsNullOrWhiteSpace(channel.ConfigJson) ? null : channel.ConfigJson);
        AddParameter(command, "$enabled", channel.Enabled ? 1 : 0);
        AddParameter(command, "$pollInterval", Math.Clamp(channel.PollIntervalSeconds, 5, 300));
    }

    private Dictionary<string, string> LoadKeyValueTable(string table)
    {
        // Only allow known safe table names to prevent SQL injection if
        // callers are changed in the future to accept external input.
        if (table is not ("mcp_chat_settings" or "mcp_chat_channels"))
            throw new ArgumentException($"Unknown table name: {table}", nameof(table));

        IReadOnlyList<KeyValuePair<string, string>> rows = _database.Query(
            $"SELECT key, value FROM {table};",
            reader => new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));

        return new Dictionary<string, string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    private void UpsertKeyValue(string table, string key, string value) =>
        _database.Execute(
            $"""
             INSERT INTO {table} (key, value) VALUES ($key, $value)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value;
             """,
            command =>
            {
                AddParameter(command, "$key", key);
                AddParameter(command, "$value", value);
            });

    /// <summary>Creates and adds a parameter in a provider-agnostic way.</summary>
    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out string? raw) ? raw is "1" or "true" : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : fallback;

    private static ChatMcpLogLevel ReadLogLevel(Dictionary<string, string> values, string key, ChatMcpLogLevel fallback) =>
        values.TryGetValue(key, out string? raw) && Enum.TryParse(raw, ignoreCase: true, out ChatMcpLogLevel level)
            ? level
            : fallback;
}