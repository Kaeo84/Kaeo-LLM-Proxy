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
/// Enforces the allow/deny domain rules for <c>web_search</c> links and <c>web_fetch</c>.
/// Rules are read fresh from the database on every check so UI edits apply immediately.
/// Semantics: deny rules always win; when at least one allow rule exists, only hosts matching
/// an allow rule pass; with no allow rules everything not denied passes.
/// </summary>
internal sealed class DomainPolicyService(WebSearchRepository repository)
{
    private static readonly System.Globalization.IdnMapping Idn = new();

    private readonly WebSearchRepository _repository = repository;

    /// <summary>Loads the current rule set once so a multi-hop fetch can reuse it (L7).</summary>
    public IReadOnlyList<DomainRule> LoadRules() => _repository.LoadDomainRules();

    public bool IsAllowed(Uri uri) => IsAllowed(uri, LoadRules());

    /// <summary>
    /// Checks <paramref name="uri"/> against a preloaded rule set, avoiding a database read per
    /// redirect hop. Host and patterns are normalized identically (trailing dot removed, IDN
    /// converted to ASCII) so a deny rule cannot be bypassed with "example.com." or a Unicode form.
    /// </summary>
    public bool IsAllowed(Uri uri, IReadOnlyList<DomainRule> rules)
    {
        string host = NormalizeHost(uri.Host);
        if (host.Length == 0)
            return false;

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
    /// which also matches the apex "example.com"). <paramref name="host"/> is already normalized.
    /// </summary>
    private static bool Matches(string pattern, string host)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.Length == 0)
            return false;

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string apex = NormalizeHost(pattern[2..]);   // "example.com"
            return host == apex || host.EndsWith("." + apex, StringComparison.Ordinal);
        }

        return host == NormalizeHost(pattern);
    }

    /// <summary>
    /// Lower-cases, strips a trailing FQDN dot, and converts IDN labels to their ASCII (punycode)
    /// form so rules and hosts compare on the same representation. <see cref="Uri.Host"/> already
    /// yields punycode for IDN hosts; normalizing rules the same way makes Unicode rules match.
    /// </summary>
    private static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
            return host;

        try
        {
            return Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }
}
