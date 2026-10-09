using System.Data.Common;
using Kaeo.LlmProxy.Core.Models;

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
            ListenAddress = ReadString(values, ServerListenAddressKey, "localhost"),
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

        UpsertKeyValue(ServerEnabledKey, settings.Enabled ? "1" : "0");
        UpsertKeyValue(ServerListenAddressKey, settings.ListenAddress);
        UpsertKeyValue(ServerListenPortKey, ClampPort(settings.ListenPort).ToString());
        UpsertKeyValue(ServerApiExplorerKey, settings.EnableApiExplorer ? "1" : "0");
        UpsertKeyValue(ServerAuthCredentialKey, settings.AuthCredentialName ?? string.Empty);
        UpsertKeyValue(ServerCollectRequestKey, settings.CollectRequestDetails ? "1" : "0");
        UpsertKeyValue(ServerCollectResponseKey, settings.CollectResponseDetails ? "1" : "0");
    }

    private static int ClampPort(int port) => Math.Clamp(port, McpServerSettings.MinPort, McpServerSettings.MaxPort);

    private Dictionary<string, string> LoadKeyValueTable()
    {
        IReadOnlyList<KeyValuePair<string, string>> rows = _database.ExecuteModuleQuery(
            "SELECT key, value FROM mcp_server_settings;",
            reader => new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)),
            null);

        return new Dictionary<string, string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    private void UpsertKeyValue(string key, string value) =>
        _database.ExecuteModuleNonQuery(
            """
            INSERT INTO mcp_server_settings (key, value) VALUES ($key, $value)
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

    private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    private static string? ReadOptionalString(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;
}
