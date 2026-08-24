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
/// Abstraction over a web search backend. Implementations throw
/// <see cref="HttpRequestException"/> (or fail the task) when the provider cannot deliver
/// results; the caller decides whether to fall through to the next provider.
/// </summary>
internal interface ISearchProvider
{
    /// <summary>Provider name matching <see cref="SearchProviderConfig.Name"/>.</summary>
    string Name { get; }

    /// <summary>Whether this provider needs an API key resolved from the host credential store.</summary>
    bool RequiresApiKey { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchProviderConfig config,
        string query,
        int maxResults,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken);
}
