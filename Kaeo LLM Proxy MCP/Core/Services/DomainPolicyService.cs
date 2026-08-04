using Kaeo.LlmProxy.Mcp.Core.Models;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// Enforces the allow/deny domain rules for <c>web_search</c> links and <c>web_fetch</c>.
/// Rules are read fresh from the database on every check so UI edits apply immediately.
/// Semantics: deny rules always win; when at least one allow rule exists, only hosts matching
/// an allow rule pass; with no allow rules everything not denied passes.
/// </summary>
internal sealed class DomainPolicyService(McpSettingsRepository repository)
{
    private readonly McpSettingsRepository _repository = repository;

    public bool IsAllowed(Uri uri)
    {
        string host = uri.Host.Trim().ToLowerInvariant();
        if (host.Length == 0)
            return false;

        IReadOnlyList<DomainRule> rules = _repository.LoadDomainRules();

        foreach (DomainRule rule in rules)
        {
            if (rule.RuleType == DomainRuleType.Deny && Matches(rule.Pattern, host))
                return false;
        }

        List<DomainRule> allowRules = [.. rules.Where(r => r.RuleType == DomainRuleType.Allow)];
        if (allowRules.Count == 0)
            return true;

        return allowRules.Any(rule => Matches(rule.Pattern, host));
    }

    /// <summary>
    /// Matches a domain pattern: exact ("example.com") or wildcard subdomain ("*.example.com",
    /// which also matches the apex "example.com").
    /// </summary>
    private static bool Matches(string pattern, string host)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.Length == 0)
            return false;

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string suffix = pattern[1..];          // ".example.com"
            string apex = pattern[2..];             // "example.com"
            return host == apex || host.EndsWith(suffix, StringComparison.Ordinal);
        }

        return host == pattern;
    }
}
