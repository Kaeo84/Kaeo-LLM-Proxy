using System.Text.Json;
using System.Text.RegularExpressions;
using Kaeo.LlmProxy.Mcp.Core.Models;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// Parses DuckDuckGo's keyless <c>/html/</c> endpoint. The endpoint returns a full HTML page;
/// results are scraped from the <c>result__a</c> / <c>result__snippet</c> markup, which is
/// best-effort by nature but requires no API key.
/// </summary>
internal sealed partial class DuckDuckGoSearchProvider : ISearchProvider
{
    public string Name => "DuckDuckGo";

    public bool RequiresApiKey => false;

    [GeneratedRegex("<a\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex("href\\s*=\\s*\"(?<href>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("<[a-z]+\\b[^>]*class=\"[^\"]*result__snippet[^\"]*\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SnippetOpenRegex();

    [GeneratedRegex("[?&]uddg=([^&]+)")]
    private static partial Regex RedirectRegex();

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchProviderConfig config,
        string query,
        int maxResults,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        string endpoint = string.IsNullOrWhiteSpace(config.Endpoint)
            ? "https://duckduckgo.com/html/"
            : config.Endpoint.Trim();

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseResults(html, maxResults);
    }

    private static IReadOnlyList<SearchResult> ParseResults(string html, int maxResults)
    {
        var titles = new List<(string Url, string Title)>();

        foreach (Match tag in AnchorTagRegex().Matches(html))
        {
            string openTag = tag.Value;
            if (!openTag.Contains("result__a", StringComparison.OrdinalIgnoreCase))
                continue;

            Match hrefMatch = HrefRegex().Match(openTag);
            if (!hrefMatch.Success)
                continue;

            string? url = ResolveRedirectUrl(hrefMatch.Groups["href"].Value);
            if (url is null)
                continue;

            int titleEnd = html.IndexOf("</a>", tag.Index + tag.Length, StringComparison.OrdinalIgnoreCase);
            if (titleEnd < 0)
                continue;

            string title = HtmlTextExtractor.Clean(html[(tag.Index + tag.Length)..titleEnd]);
            if (title.Length == 0)
                continue;

            titles.Add((url, title));
            if (titles.Count >= maxResults)
                break;
        }

        var snippets = new List<string>();
        foreach (Match openTag in SnippetOpenRegex().Matches(html))
        {
            int end = html.IndexOf("</", openTag.Index + openTag.Length, StringComparison.Ordinal);
            if (end < 0)
                break;

            snippets.Add(HtmlTextExtractor.Clean(html[(openTag.Index + openTag.Length)..end]));
            if (snippets.Count >= titles.Count)
                break;
        }

        var results = new List<SearchResult>(titles.Count);
        for (int i = 0; i < titles.Count; i++)
            results.Add(new SearchResult(titles[i].Title, titles[i].Url, i < snippets.Count ? snippets[i] : string.Empty));

        return results;
    }

    /// <summary>
    /// DuckDuckGo wraps result links in a <c>/l/?uddg=…</c> redirect; unwrap the real target.
    /// </summary>
    private static string? ResolveRedirectUrl(string href)
    {
        href = HtmlTextExtractor.DecodeEntities(href);
        if (href.StartsWith("//", StringComparison.Ordinal))
            href = "https:" + href;

        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? uri))
            return null;

        Match redirect = RedirectRegex().Match(uri.Query);
        if (redirect.Success)
        {
            string decoded = Uri.UnescapeDataString(redirect.Groups[1].Value);
            return Uri.TryCreate(decoded, UriKind.Absolute, out Uri? target) ? target.ToString() : null;
        }

        return uri.ToString();
    }
}

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
