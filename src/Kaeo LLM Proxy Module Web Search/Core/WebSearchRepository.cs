using Kaeo.LlmProxy.Core.Modules;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Data.Common;
using Serilog;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Module.WebSearch;

/// <summary>
/// Loads and persists the Web Search feature settings through the shared application database
/// gateway. Keys are stored in small key/value tables; the provider catalog and domain rules in
/// row tables.
/// </summary>
internal sealed class WebSearchRepository(IModuleDatabase database)
{
    private const string WebSearchEnabledKey = "web_search_enabled";
    private const string WebFetchEnabledKey = "web_fetch_enabled";
    private const string MaxResultsKey = "max_results";
    private const string TimeoutSecondsKey = "timeout_seconds";
    private const string MaxResponseBytesKey = "max_response_bytes";
    private const string AllowLocalNetworksKey = "allow_local_networks";

    private readonly IModuleDatabase _database = database;

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

        (string Key, string Value)[] values =
        [
            (WebSearchEnabledKey, settings.WebSearchToolEnabled ? "1" : "0"),
            (WebFetchEnabledKey, settings.WebFetchToolEnabled ? "1" : "0"),
            (MaxResultsKey, settings.MaxResults.ToString()),
            (TimeoutSecondsKey, settings.TimeoutSeconds.ToString()),
            (MaxResponseBytesKey, settings.MaxResponseBytes.ToString()),
            (AllowLocalNetworksKey, settings.AllowLocalNetworks ? "1" : "0"),
        ];

        // M11: write every key in one transaction so a failure or a concurrent writer cannot leave
        // a partially updated configuration (e.g. a new timeout paired with an old size cap).
        _database.ExecuteInTransaction((connection, transaction) =>
        {
            foreach ((string key, string value) in values)
            {
                using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO mcp_web_search_settings (key, value) VALUES ($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                AddParameter(command, "$key", key);
                AddParameter(command, "$value", value);
                command.ExecuteNonQuery();
            }
        });
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

    /// <summary>
    /// Inserts the default provider catalog. Each row uses INSERT OR IGNORE against the UNIQUE
    /// name column, so seeding is idempotent and safe under concurrent first-run initialization
    /// without a COUNT-then-insert race (L8).
    /// </summary>
    public void SeedDefaultProviders()
    {
        InsertProviderIfAbsent("DuckDuckGo", true, "https://duckduckgo.com/html/");
        InsertProviderIfAbsent("SearXNG", false, "http://localhost:8888/search");
        InsertProviderIfAbsent("Brave", false, "https://api.search.brave.com/res/v1/web/search");
        InsertProviderIfAbsent("Bing", false, "https://api.bing.microsoft.com/v7.0/search");
    }

    private void InsertProviderIfAbsent(string name, bool isEnabled, string endpoint) =>
        _database.Execute(
            """
            INSERT OR IGNORE INTO mcp_web_search_providers (name, is_enabled, endpoint, credential_name)
            VALUES ($name, $isEnabled, $endpoint, NULL);
            """,
            command =>
            {
                AddParameter(command, "$name", name);
                AddParameter(command, "$isEnabled", isEnabled ? 1 : 0);
                AddParameter(command, "$endpoint", endpoint);
            });

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

    private Dictionary<string, string> LoadKeyValueTable(string table)
    {
        // Only allow known safe table names to prevent SQL injection if
        // callers are changed in the future to accept external input.
        if (table is not "mcp_web_search_settings")
            throw new ArgumentException($"Unknown table name: {table}", nameof(table));

        IReadOnlyList<KeyValuePair<string, string>> rows = _database.Query(
            $"SELECT key, value FROM {table};",
            reader => new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));

        return new Dictionary<string, string>(rows, StringComparer.OrdinalIgnoreCase);
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
        values.TryGetValue(key, out string? raw) ? raw is "1" or "true" : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : fallback;
    }
