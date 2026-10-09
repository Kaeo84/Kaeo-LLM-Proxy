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
