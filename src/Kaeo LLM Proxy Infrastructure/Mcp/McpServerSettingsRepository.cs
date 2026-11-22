using System.Data.Common;
using System.Net;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure.Mcp;

/// <summary>
/// Loads and persists the MCP server endpoint settings in the host-owned
/// <c>mcp_server_settings</c> key/value table.
/// </summary>
internal sealed class McpServerSettingsRepository(AppDatabase database)
{
    private const string ServerEnabledKey = "enabled";
    private const string ServerListenAddressKey = "listen_address";
    private const string ServerListenPortKey = "listen_port";
    private const string ServerApiExplorerKey = "enable_api_explorer";
    private const string ServerAuthCredentialKey = "auth_credential_name";
    private const string ServerCollectRequestKey = "collect_request_details";
    private const string ServerCollectResponseKey = "collect_response_details";
#if DEBUG
    private const bool defaultRequestDefault = true;
    private const bool defaultResponseDefault = true;
#else
    private const bool defaultRequestDefault = false;
    private const bool defaultResponseDefault = false;
#endif

    private readonly AppDatabase _database = database;

    public McpServerSettings LoadServerSettings()
    {
        Dictionary<string, string> values = LoadKeyValueTable();

        return new McpServerSettings
        {
            Enabled = ReadBool(values, ServerEnabledKey, false),
            ListenAddress = SanitizeListenAddress(ReadString(values, ServerListenAddressKey, "localhost")),
            ListenPort = ClampPort(ReadInt(values, ServerListenPortKey, McpServerSettings.DefaultPort)),
            EnableApiExplorer = ReadBool(values, ServerApiExplorerKey, false),
            AuthCredentialName = ReadOptionalString(values, ServerAuthCredentialKey),
            CollectRequestDetails = ReadBool(values, ServerCollectRequestKey, defaultRequestDefault),
            CollectResponseDetails = ReadBool(values, ServerCollectResponseKey, defaultResponseDefault),
        };
    }

    public void SaveServerSettings(McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        (string Key, string Value)[] pairs =
        [
            (ServerEnabledKey, settings.Enabled ? "1" : "0"),
            // L25: null/invalid addresses must not hit the NOT NULL constraint as a raw
            // SqliteException; coalesce and sanitize on save just as the load side does.
            (ServerListenAddressKey, SanitizeListenAddress(settings.ListenAddress ?? "localhost")),
            (ServerListenPortKey, ClampPort(settings.ListenPort).ToString()),
            (ServerApiExplorerKey, settings.EnableApiExplorer ? "1" : "0"),
            (ServerAuthCredentialKey, settings.AuthCredentialName ?? string.Empty),
            (ServerCollectRequestKey, settings.CollectRequestDetails ? "1" : "0"),
            (ServerCollectResponseKey, settings.CollectResponseDetails ? "1" : "0"),
        ];

        // One connection + one transaction for all keys so a concurrent reader never sees a
        // half-saved settings set.
        try
        {
            _database.ExecuteModuleTransaction((connection, transaction) =>
            {
                foreach ((string key, string value) in pairs)
                {
                    using DbCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO mcp_server_settings (key, value) VALUES ($key, $value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    AddParameter(command, "$key", key);
                    AddParameter(command, "$value", value);
                    command.ExecuteNonQuery();
                }
            });
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // A sharing violation from a concurrent instance must not crash a settings apply;
            // the change persists on the next successful write.
            Log.Warning(ex, "Could not persist MCP server settings");
        }
    }

    private static int ClampPort(int port) => Math.Clamp(port, McpServerSettings.MinPort, McpServerSettings.MaxPort);

    /// <summary>
    /// The stored listen address comes from a user-editable key/value table, so restrict it to
    /// values that can form a valid HttpListener prefix host: a wildcard form understood by the
    /// server, an IP literal, or a plain hostname. Scheme-qualified URLs, embedded ports/paths,
    /// and values with prefix-breaking characters fall back to localhost so a corrupted row
    /// cannot make the MCP server unstartable.
    /// </summary>
    private static string SanitizeListenAddress(string raw)
    {
        string host = raw.Trim();
        if (host.Length == 0)
            return "localhost";

        if (host is "*" or "+" or "0.0.0.0" or "::" or "[::]")
            return host;

        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
            return host;

        foreach (char c in host)
        {
            if (char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '#' or '?' or '@')
                return "localhost";
        }

        return host;
    }

    private Dictionary<string, string> LoadKeyValueTable()
    {
        try
        {
            IReadOnlyList<KeyValuePair<string, string>> rows = _database.ExecuteModuleQuery(
                "SELECT key, value FROM mcp_server_settings;",
                reader => new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)),
                null);

            return new Dictionary<string, string>(rows, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // A sharing violation from a concurrent instance must not crash settings reads;
            // fall back to defaults so the server stays startable with sane values.
            Log.Warning(ex, "Could not read MCP server settings; using defaults");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Creates and adds a parameter in a provider-agnostic way.</summary>
    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out string? raw)
            ? raw.Trim() is "1" || (bool.TryParse(raw.Trim(), out bool parsed) && parsed)
            : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : fallback;

    private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    private static string? ReadOptionalString(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;
}
