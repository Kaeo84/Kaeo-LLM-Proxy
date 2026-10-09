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
