using System.Data.Common;
using Kaeo.LlmProxy.Mcp.Core.Models;
using Kaeo.LlmProxy.Modules;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// Loads and persists all MCP module settings through the shared application database gateway.
/// Keys are stored in small key/value tables; the provider catalog and domain rules in row tables.
/// </summary>
internal sealed class McpSettingsRepository(IModuleDatabase database)
{
    private const string ServerEnabledKey = "enabled";
    private const string ServerListenAddressKey = "listen_address";
    private const string ServerListenPortKey = "listen_port";
    private const string ServerAuthCredentialKey = "auth_credential_name";

    private const string WebSearchEnabledKey = "web_search_enabled";
    private const string WebFetchEnabledKey = "web_fetch_enabled";
    private const string MaxResultsKey = "max_results";
    private const string TimeoutSecondsKey = "timeout_seconds";
    private const string MaxResponseBytesKey = "max_response_bytes";
    private const string AllowLocalNetworksKey = "allow_local_networks";

    private readonly IModuleDatabase _database = database;

    // ── Server settings ─────────────────────────────────────────────────────

    public McpServerSettings LoadServerSettings()
    {
        Dictionary<string, string> values = LoadKeyValueTable("mcp_server_settings");

        return new McpServerSettings
        {
            Enabled = ReadBool(values, ServerEnabledKey, false),
            ListenAddress = ReadString(values, ServerListenAddressKey, "localhost"),
            ListenPort = ClampPort(ReadInt(values, ServerListenPortKey, McpServerSettings.DefaultPort)),
            AuthCredentialName = ReadOptionalString(values, ServerAuthCredentialKey),
        };
    }

    public void SaveServerSettings(McpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        UpsertKeyValue("mcp_server_settings", ServerEnabledKey, settings.Enabled ? "1" : "0");
        UpsertKeyValue("mcp_server_settings", ServerListenAddressKey, settings.ListenAddress);
        UpsertKeyValue("mcp_server_settings", ServerListenPortKey, ClampPort(settings.ListenPort).ToString());
        UpsertKeyValue("mcp_server_settings", ServerAuthCredentialKey, settings.AuthCredentialName ?? string.Empty);
    }

    // ── Web Search settings ─────────────────────────────────────────────────

    public WebSearchSettings LoadWebSearchSettings()
    {
        Dictionary<string, string> values = LoadKeyValueTable("mcp_web_search_settings");

        return new WebSearchSettings
        {
            WebSearchToolEnabled = ReadBool(values, WebSearchEnabledKey, true),
            WebFetchToolEnabled = ReadBool(values, WebFetchEnabledKey, true),
            MaxResults = Math.Clamp(ReadInt(values, MaxResultsKey, 5), 1, 20),
            TimeoutSeconds = Math.Clamp(ReadInt(values, TimeoutSecondsKey, 20), 5, 120),
            MaxResponseBytes = Math.Clamp(ReadInt(values, MaxResponseBytesKey, 200_000), 10_000, 2_000_000),
            AllowLocalNetworks = ReadBool(values, AllowLocalNetworksKey, false),
        };
    }

    public void SaveWebSearchSettings(WebSearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        UpsertKeyValue("mcp_web_search_settings", WebSearchEnabledKey, settings.WebSearchToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_web_search_settings", WebFetchEnabledKey, settings.WebFetchToolEnabled ? "1" : "0");
        UpsertKeyValue("mcp_web_search_settings", MaxResultsKey, settings.MaxResults.ToString());
        UpsertKeyValue("mcp_web_search_settings", TimeoutSecondsKey, settings.TimeoutSeconds.ToString());
        UpsertKeyValue("mcp_web_search_settings", MaxResponseBytesKey, settings.MaxResponseBytes.ToString());
        UpsertKeyValue("mcp_web_search_settings", AllowLocalNetworksKey, settings.AllowLocalNetworks ? "1" : "0");
    }

    // ── Search providers ────────────────────────────────────────────────────

    /// <summary>Known provider names in display order.</summary>
    public static readonly string[] KnownProviderNames = ["DuckDuckGo", "SearXNG", "Brave", "Bing"];

    public IReadOnlyList<SearchProviderConfig> LoadProviders() =>
        _database.Query(
            "SELECT id, name, is_enabled, endpoint, credential_name FROM mcp_web_search_providers ORDER BY id;",
            reader => new SearchProviderConfig
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                IsEnabled = reader.GetInt64(2) != 0,
                Endpoint = reader.GetString(3),
                CredentialName = reader.IsDBNull(4) ? null : reader.GetString(4),
            });

    public void UpsertProvider(SearchProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _database.Execute(
            """
            INSERT INTO mcp_web_search_providers (name, is_enabled, endpoint, credential_name)
            VALUES ($name, $isEnabled, $endpoint, $credentialName)
            ON CONFLICT(name) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                endpoint = excluded.endpoint,
                credential_name = excluded.credential_name;
            """,
            command =>
            {
                AddParameter(command, "$name", provider.Name);
                AddParameter(command, "$isEnabled", provider.IsEnabled ? 1 : 0);
                AddParameter(command, "$endpoint", provider.Endpoint);
                AddParameter(command, "$credentialName", provider.CredentialName);
            });
    }

    /// <summary>Inserts the default provider catalog on first run (does nothing when rows exist).</summary>
    public void SeedDefaultProviders()
    {
        object? count = _database.ExecuteScalar("SELECT COUNT(*) FROM mcp_web_search_providers;");
        if (Convert.ToInt64(count) > 0)
            return;

        UpsertProvider(new SearchProviderConfig
        {
            Name = "DuckDuckGo",
            IsEnabled = true,
            Endpoint = "https://duckduckgo.com/html/",
        });
        UpsertProvider(new SearchProviderConfig
        {
            Name = "SearXNG",
            IsEnabled = false,
            Endpoint = "http://localhost:8888/search",
        });
        UpsertProvider(new SearchProviderConfig
        {
            Name = "Brave",
            IsEnabled = false,
            Endpoint = "https://api.search.brave.com/res/v1/web/search",
        });
        UpsertProvider(new SearchProviderConfig
        {
            Name = "Bing",
            IsEnabled = false,
            Endpoint = "https://api.bing.microsoft.com/v7.0/search",
        });
    }

    // ── Domain rules ────────────────────────────────────────────────────────

    public IReadOnlyList<DomainRule> LoadDomainRules() =>
        _database.Query(
            "SELECT id, rule_type, pattern FROM mcp_domain_rules ORDER BY rule_type, pattern;",
            reader => new DomainRule
            {
                Id = reader.GetInt32(0),
                RuleType = reader.GetInt32(1) == (int)DomainRuleType.Allow ? DomainRuleType.Allow : DomainRuleType.Deny,
                Pattern = reader.GetString(2),
            });

    /// <summary>Adds a rule; duplicate (type, pattern) pairs are ignored.</summary>
    public void AddDomainRule(DomainRuleType ruleType, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        _database.Execute(
            """
            INSERT OR IGNORE INTO mcp_domain_rules (rule_type, pattern)
            VALUES ($ruleType, $pattern);
            """,
            command =>
            {
                AddParameter(command, "$ruleType", (int)ruleType);
                AddParameter(command, "$pattern", pattern.Trim());
            });
    }

    public void RemoveDomainRule(int id) =>
        _database.Execute(
            "DELETE FROM mcp_domain_rules WHERE id = $id;",
            command => AddParameter(command, "$id", id));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int ClampPort(int port) => Math.Clamp(port, McpServerSettings.MinPort, McpServerSettings.MaxPort);

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

    private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    private static string? ReadOptionalString(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;
}
