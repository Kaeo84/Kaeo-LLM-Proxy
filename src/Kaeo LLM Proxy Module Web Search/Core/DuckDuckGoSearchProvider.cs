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

        // DuckDuckGo's /html/ endpoint returns a 302 domain redirect (duckduckgo.com →
        // html.duckduckgo.com) before the results page. Follow redirects keeping the POST
        // body since the Location header does not carry the query. A browser-like User-Agent
        // reduces the chance of a bot-detection challenge redirect.
        string html;
        Uri currentUrl = new(endpoint);

        for (int hop = 0; ; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, currentUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and <= 308 && response.Headers.Location is not null)
            {
                if (hop >= 5)
                    throw new HttpRequestException($"Too many redirects while searching DuckDuckGo from '{endpoint}'.");
                currentUrl = new Uri(currentUrl, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(cancellationToken);
            break;
        }

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
