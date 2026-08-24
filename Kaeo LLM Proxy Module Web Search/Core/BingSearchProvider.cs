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
/// Queries the Bing Web Search API (v7). Requires an API key stored as a host credential;
/// the key is sent via the <c>Ocp-Apim-Subscription-Key</c> header.
/// </summary>
internal sealed class BingSearchProvider : ISearchProvider
{
    public string Name => "Bing";

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
            ? "https://api.bing.microsoft.com/v7.0/search"
            : config.Endpoint.Trim();

        using HttpRequestMessage request = new(HttpMethod.Get, $"{endpoint}?q={Uri.EscapeDataString(query)}&count={maxResults}");
        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        request.Headers.Add("Accept", "application/json");

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new List<SearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("webPages", out JsonElement webPages)
            && webPages.TryGetProperty("value", out JsonElement resultsElement)
            && resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in resultsElement.EnumerateArray())
            {
                string url = SearXngSearchProvider.GetString(item, "url");
                if (url.Length == 0)
                    continue;

                results.Add(new SearchResult(
                    SearXngSearchProvider.GetString(item, "name"),
                    url,
                    SearXngSearchProvider.GetString(item, "snippet")));

                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }
}
