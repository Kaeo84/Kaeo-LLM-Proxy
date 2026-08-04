using Kaeo.LlmProxy.Mcp.Core.Models;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>One result item from a web search provider.</summary>
internal sealed record SearchResult(string Title, string Url, string Snippet);

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
