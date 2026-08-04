using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Mcp.Core.Models;
using Kaeo.LlmProxy.Modules;
using Serilog;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// Orchestrates the Web Search feature: runs a query across the enabled search providers until
/// one yields results, and fetches page content subject to the domain policy, network safety
/// guard, and configured timeout/size limits. All settings are read from the database on every
/// operation so configuration edits in the UI apply without restarting the MCP server.
/// </summary>
internal sealed class WebSearchService
{
    private readonly McpSettingsRepository _repository;
    private readonly ISecretProvider _secrets;
    private readonly DomainPolicyService _domainPolicy;

    private static readonly ISearchProvider[] Providers =
    [
        new DuckDuckGoSearchProvider(),
        new SearXngSearchProvider(),
        new BraveSearchProvider(),
        new BingSearchProvider(),
    ];

    // Shared pooled client; per-operation timeouts are applied with CancellationTokenSources.
    private static readonly HttpClient SharedClient = CreateHttpClient();

    public WebSearchService(McpSettingsRepository repository, ISecretProvider secrets)
    {
        _repository = repository;
        _secrets = secrets;
        _domainPolicy = new DomainPolicyService(repository);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; KaeoLlmProxyMcp/1.0)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    /// <summary>
    /// Runs <paramref name="query"/> against enabled providers in catalog order and returns the
    /// first non-empty result set. Throws <see cref="InvalidOperationException"/> with a summary
    /// when no provider can deliver results.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();
        int capped = Math.Clamp(Math.Min(maxResults, settings.MaxResults), 1, 20);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        var failures = new List<string>();

        foreach (SearchProviderConfig config in _repository.LoadProviders())
        {
            if (!config.IsEnabled)
                continue;

            ISearchProvider? provider = Providers.FirstOrDefault(
                p => string.Equals(p.Name, config.Name, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                continue;

            string? apiKey = null;
            if (provider.RequiresApiKey)
            {
                if (string.IsNullOrWhiteSpace(config.CredentialName))
                {
                    failures.Add($"{config.Name}: no credential configured.");
                    continue;
                }

                apiKey = _secrets.ResolveSecret(config.CredentialName);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    failures.Add($"{config.Name}: credential '{config.CredentialName}' could not be resolved.");
                    continue;
                }
            }

            try
            {
                IReadOnlyList<SearchResult> results = await provider.SearchAsync(
                    config, query, capped, apiKey, SharedClient, timeoutCts.Token);

                if (results.Count > 0)
                    return results.Take(capped).ToList();

                failures.Add($"{config.Name}: returned no results.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failures.Add($"{config.Name}: timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UriFormatException)
            {
                failures.Add($"{config.Name}: {ex.Message}");
                Log.Warning(ex, "Web search provider {Provider} failed for query {Query}", config.Name, query);
            }
        }

        throw new InvalidOperationException(failures.Count == 0
            ? "No web search providers are enabled."
            : $"No search provider returned results. {string.Join(" ", failures)}");
    }

    /// <summary>
    /// Fetches <paramref name="url"/> honoring the domain policy, network safety guard, and the
    /// configured timeout/response-size limits. HTML content is converted to plain text.
    /// </summary>
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"'{url}' is not a valid absolute URL.");

        await NetworkSafety.ValidateAsync(uri, settings.AllowLocalNetworks, cancellationToken);

        if (!_domainPolicy.IsAllowed(uri))
            throw new InvalidOperationException(
                $"Domain '{uri.Host}' is blocked by the configured allow/deny domain rules.");

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using HttpResponseMessage response = await SharedClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        string body = await ReadCappedAsync(response, settings.MaxResponseBytes, timeoutCts.Token);

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(body))
            return HtmlTextExtractor.ToText(body);

        return body;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[8192];
        var output = new MemoryStream();
        int totalRead = 0;

        while (totalRead < maxBytes)
        {
            int toRead = Math.Min(buffer.Length, maxBytes - totalRead);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
                break;

            output.Write(buffer, 0, read);
            totalRead += read;
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static bool LooksLikeHtml(string body)
    {
        string head = body.Length > 512 ? body[..512] : body;
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase);
    }
}
