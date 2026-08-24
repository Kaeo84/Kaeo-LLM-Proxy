using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Renci.SshNet.Common;

namespace Kaeo.LlmProxy.Module.Ssh;

/// <summary>
/// Loads and persists the SSH module's stored connections and feature settings through the
/// shared application database gateway. Settings live in the <c>mcp_ssh_settings</c> key/value
/// table; named connections in <c>mcp_ssh_connections</c>.
/// </summary>
internal sealed class SshRepository(IModuleDatabase database)
{
    private const string ConnectEnabledKey = "connect_enabled";
    private const string ExecEnabledKey = "exec_enabled";
    private const string DisconnectEnabledKey = "disconnect_enabled";
    private const string ListEnabledKey = "list_enabled";
    private const string DefaultIdleTimeoutKey = "default_idle_timeout_seconds";
    private const string CommandTimeoutKey = "command_timeout_seconds";
    private const string MaxOutputCharsKey = "max_output_chars";
    private const string McpLogLevelKey = "mcp_log_level";

    private readonly IModuleDatabase _database = database;

    // ── Settings ────────────────────────────────────────────────────────────

    public SshSettings LoadSettings()
    {
        Dictionary<string, string> values = LoadKeyValueTable("mcp_ssh_settings");

        return new SshSettings
        {
            ConnectToolEnabled = ReadBool(values, ConnectEnabledKey, true),
            ExecToolEnabled = ReadBool(values, ExecEnabledKey, true),
            DisconnectToolEnabled = ReadBool(values, DisconnectEnabledKey, true),
            ListToolEnabled = ReadBool(values, ListEnabledKey, true),
            DefaultIdleTimeoutSeconds = Math.Clamp(ReadInt(values, DefaultIdleTimeoutKey, 600), 0, 86_400),
            CommandTimeoutSeconds = Math.Clamp(ReadInt(values, CommandTimeoutKey, 60), 5, 3_600),
            MaxOutputChars = Math.Clamp(ReadInt(values, MaxOutputCharsKey, 20_000), 1_000, 200_000),
            McpLogLevel = ReadLogLevel(values, McpLogLevelKey, SshMcpLogLevel.Connectivity),
        };
    }

    public void SaveSettings(SshSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        UpsertKeyValue("mcp_ssh_settings", ConnectEnabledKey, settings.ConnectToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_ssh_settings", ExecEnabledKey, settings.ExecToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_ssh_settings", DisconnectEnabledKey, settings.DisconnectToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_ssh_settings", ListEnabledKey, settings.ListToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_ssh_settings", DefaultIdleTimeoutKey, settings.DefaultIdleTimeoutSeconds.ToString());
        UpsertKeyValue("mcp_ssh_settings", CommandTimeoutKey, settings.CommandTimeoutSeconds.ToString());
        UpsertKeyValue("mcp_ssh_settings", MaxOutputCharsKey, settings.MaxOutputChars.ToString());
        UpsertKeyValue("mcp_ssh_settings", McpLogLevelKey, settings.McpLogLevel.ToString());
    }

    // ── Stored connections ──────────────────────────────────────────────────

    public IReadOnlyList<SshStoredConnection> LoadConnections() =>
        _database.Query(
            """
            SELECT id, name, host, port, username, credential_name, idle_timeout_seconds
            FROM mcp_ssh_connections
            ORDER BY name;
            """,
            reader => new SshStoredConnection
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Host = reader.GetString(2),
                Port = reader.GetInt32(3),
                Username = reader.GetString(4),
                CredentialName = reader.IsDBNull(5) ? null : reader.GetString(5),
                IdleTimeoutSeconds = reader.GetInt32(6),
            });

    /// <summary>Looks up a stored connection by its unique name (case-insensitive).</summary>
    public SshStoredConnection? FindConnectionByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        IReadOnlyList<SshStoredConnection> matches = _database.Query(
            """
            SELECT id, name, host, port, username, credential_name, idle_timeout_seconds
            FROM mcp_ssh_connections
            WHERE name = $name COLLATE NOCASE
            LIMIT 1;
            """,
            reader => new SshStoredConnection
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Host = reader.GetString(2),
                Port = reader.GetInt32(3),
                Username = reader.GetString(4),
                CredentialName = reader.IsDBNull(5) ? null : reader.GetString(5),
                IdleTimeoutSeconds = reader.GetInt32(6),
            },
            command => AddParameter(command, "$name", name.Trim()));

        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Inserts a new stored connection and returns its database identity.</summary>
    public int InsertConnection(SshStoredConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        object? id = _database.ExecuteScalar(
            """
            INSERT INTO mcp_ssh_connections (name, host, port, username, credential_name, idle_timeout_seconds)
            VALUES ($name, $host, $port, $username, $credentialName, $idleTimeout);
            SELECT last_insert_rowid();
            """,
            command => ConfigureConnectionParameters(command, connection));

        return Convert.ToInt32(id);
    }

    /// <summary>Updates an existing stored connection identified by <see cref="SshStoredConnection.Id"/>.</summary>
    public void UpdateConnection(SshStoredConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _database.Execute(
            """
            UPDATE mcp_ssh_connections
            SET name = $name, host = $host, port = $port, username = $username,
                credential_name = $credentialName, idle_timeout_seconds = $idleTimeout
            WHERE id = $id;
            """,
            command =>
            {
                ConfigureConnectionParameters(command, connection);
                AddParameter(command, "$id", connection.Id);
            });
    }

    public void DeleteConnection(int id) =>
        _database.Execute(
            "DELETE FROM mcp_ssh_connections WHERE id = $id;",
            command => AddParameter(command, "$id", id));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void ConfigureConnectionParameters(DbCommand command, SshStoredConnection connection)
    {
        AddParameter(command, "$name", connection.Name.Trim());
        AddParameter(command, "$host", connection.Host.Trim());
        AddParameter(command, "$port", connection.Port);
        AddParameter(command, "$username", connection.Username.Trim());
        AddParameter(command, "$credentialName", connection.CredentialName);
        AddParameter(command, "$idleTimeout", Math.Max(connection.IdleTimeoutSeconds, 0));
    }

    private Dictionary<string, string> LoadKeyValueTable(string table)
    {
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

    private static SshMcpLogLevel ReadLogLevel(Dictionary<string, string> values, string key, SshMcpLogLevel fallback) =>
        values.TryGetValue(key, out string? raw) && Enum.TryParse(raw, ignoreCase: true, out SshMcpLogLevel level)
            ? level
            : fallback;
}
