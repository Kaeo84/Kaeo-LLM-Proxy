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

/// <summary>A single domain allow/deny pattern (mcp_domain_rules table).</summary>
internal sealed class DomainRule
{
    public int Id { get; set; }

    public DomainRuleType RuleType { get; set; }

    /// <summary>Domain pattern; supports leading wildcard subdomains (e.g. "*.example.com").</summary>
    public string Pattern { get; set; } = string.Empty;
}
