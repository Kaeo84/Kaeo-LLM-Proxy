namespace Kaeo.LlmProxy.Mcp.Core.Models;

/// <summary>Allow/deny rule kinds for the Web Search domain policy.</summary>
internal enum DomainRuleType
{
    Allow = 0,
    Deny = 1,
}

/// <summary>A single domain allow/deny pattern (mcp_domain_rules table).</summary>
internal sealed class DomainRule
{
    public int Id { get; set; }

    public DomainRuleType RuleType { get; set; }

    /// <summary>Domain pattern; supports leading wildcard subdomains (e.g. "*.example.com").</summary>
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>A web search provider row (mcp_web_search_providers table).</summary>
internal sealed class SearchProviderConfig
{
    public int Id { get; set; }

    /// <summary>Provider key: DuckDuckGo, SearXNG, Brave, or Bing.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this provider participates in searches.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Provider endpoint (query URL; may contain a {query} placeholder where supported).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Host credential name supplying the provider API key (providers that need one).</summary>
    public string? CredentialName { get; set; }
}

/// <summary>
/// Persisted Web Search feature settings (mcp_web_search_settings table). Ranges are clamped
/// when loaded.
/// </summary>
internal sealed class WebSearchSettings
{
    public bool WebSearchToolEnabled { get; set; } = true;

    public bool WebFetchToolEnabled { get; set; } = true;

    /// <summary>Maximum number of results returned per search. Clamped to 1..20.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Per-request timeout in seconds. Clamped to 5..120.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Maximum bytes read from a fetched page. Clamped to 10 KB..2 MB.</summary>
    public int MaxResponseBytes { get; set; } = 200_000;

    /// <summary>
    /// Opt-in allowing fetch/search requests to private and loopback addresses. Off by default
    /// to prevent SSRF against services on the local network.
    /// </summary>
    public bool AllowLocalNetworks { get; set; }
}
