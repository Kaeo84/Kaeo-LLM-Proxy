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
/// Queries a SearXNG instance's JSON API (<c>?format=json</c>). The endpoint is user-supplied
/// because SearXNG is self-hosted; the JSON API must be enabled on the instance.
/// </summary>
internal sealed class SearXngSearchProvider : ISearchProvider
{
    public string Name => "SearXNG";

    public bool RequiresApiKey => false;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchProviderConfig config,
        string query,
        int maxResults,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        string endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? "http://localhost:8888/search"
            : config.Endpoint.Trim();

        UriBuilder builder = new(endpoint);
        string newQuery = $"q={Uri.EscapeDataString(query)}&format=json";
        builder.Query = string.IsNullOrEmpty(builder.Query) || builder.Query == "?"
            ? newQuery
            : $"{builder.Query.TrimStart('?')}&{newQuery}";

        using HttpResponseMessage response = await httpClient.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new List<SearchResult>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("results", out JsonElement resultsElement)
            && resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in resultsElement.EnumerateArray())
            {
                string url = GetString(item, "url");
                if (url.Length == 0)
                    continue;

                results.Add(new SearchResult(GetString(item, "title"), url, GetString(item, "content")));
                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }

    internal static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
