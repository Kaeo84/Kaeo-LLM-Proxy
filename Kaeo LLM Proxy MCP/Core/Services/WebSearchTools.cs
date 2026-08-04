using System.ComponentModel;
using System.Text;
using Kaeo.LlmProxy.Mcp.Core.Models;
using ModelContextProtocol.Server;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// The MCP tools exposed by the Web Search feature. Tool enablement and limits are read from
/// the database on every invocation so configuration changes apply without restarting the
/// server. Disabled tools report themselves rather than vanishing mid-session.
/// </summary>
[McpServerToolType]
internal sealed class WebSearchTools(WebSearchService service, McpSettingsRepository repository)
{
    private readonly WebSearchService _service = service;
    private readonly McpSettingsRepository _repository = repository;

    [McpServerTool(Name = "web_search"), Description(
        "Searches the web using the configured search providers and returns matching pages with " +
        "title, URL, and a short snippet. Use this to find up-to-date information or relevant " +
        "pages for a query. Follow up with web_fetch to read a specific result.")]
    public async Task<string> SearchAsync(
        [Description("The search query text.")] string query,
        [Description("Maximum number of results to return. Optional; capped by the module's configured limit.")] int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();
        if (!settings.WebSearchToolEnabled)
            return "The web_search tool is disabled in the Kaeo LLM Proxy MCP module settings.";

        if (string.IsNullOrWhiteSpace(query))
            return "The query must not be empty.";

        try
        {
            IReadOnlyList<SearchResult> results = await _service.SearchAsync(
                query.Trim(), maxResults ?? settings.MaxResults, cancellationToken);

            var output = new StringBuilder();
            output.AppendLine($"Web search results for: {query.Trim()}");
            output.AppendLine();

            for (int i = 0; i < results.Count; i++)
            {
                SearchResult result = results[i];
                output.AppendLine($"{i + 1}. {result.Title}");
                output.AppendLine($"   URL: {result.Url}");

                if (!string.IsNullOrWhiteSpace(result.Snippet))
                    output.AppendLine($"   Snippet: {result.Snippet}");

                output.AppendLine();
            }

            return output.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The web search timed out. Try again with a simpler query or increase the timeout in the module settings.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Web search failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "web_fetch"), Description(
        "Fetches a web page by absolute URL and returns its content as plain text (HTML markup is " +
        "stripped). Use this after web_search to read a specific result page. Respects the " +
        "configured domain allow/deny rules and network safety settings.")]
    public async Task<string> FetchAsync(
        [Description("The absolute http or https URL of the page to fetch.")] string url,
        CancellationToken cancellationToken = default)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();
        if (!settings.WebFetchToolEnabled)
            return "The web_fetch tool is disabled in the Kaeo LLM Proxy MCP module settings.";

        if (string.IsNullOrWhiteSpace(url))
            return "The URL must not be empty.";

        try
        {
            string content = await _service.FetchAsync(url.Trim(), cancellationToken);
            return content.Length == 0
                ? "The page was fetched but contained no readable text."
                : content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The page fetch timed out. Try again or increase the timeout in the module settings.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Fetch failed: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Fetch failed: {ex.Message}";
        }
    }
}
