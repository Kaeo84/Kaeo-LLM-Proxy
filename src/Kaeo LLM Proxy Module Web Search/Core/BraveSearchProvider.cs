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
/// Queries the Brave Search API. Requires an API key stored as a host credential;
/// the key is sent via the <c>X-Subscription-Token</c> header.
/// </summary>
internal sealed class BraveSearchProvider : ISearchProvider
{
    public string Name => "Brave";

    public bool RequiresApiKey => true;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchProviderConfig config,
        string query,
        int maxResults,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        string endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? "https://api.search.brave.com/res/v1/web/search"
            : config.Endpoint.Trim();

        using HttpRequestMessage request = new(HttpMethod.Get, $"{endpoint}?q={Uri.EscapeDataString(query)}&count={maxResults}");
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.Add("Accept", "application/json");

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new List<SearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("web", out JsonElement web)
            && web.TryGetProperty("results", out JsonElement resultsElement)
            && resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in resultsElement.EnumerateArray())
            {
                string url = SearXngSearchProvider.GetString(item, "url");
                if (url.Length == 0)
                    continue;

                results.Add(new SearchResult(
                    SearXngSearchProvider.GetString(item, "title"),
                    url,
                    SearXngSearchProvider.GetString(item, "description")));

                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }
}
