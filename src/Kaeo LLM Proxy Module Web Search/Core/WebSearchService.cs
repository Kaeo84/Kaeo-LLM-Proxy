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
/// Orchestrates the Web Search feature: runs a query across the enabled search providers until
/// one yields results, and fetches page content subject to the domain policy, network safety
/// guard, and configured timeout/size limits. All settings are read from the database on every
/// operation so configuration edits in the UI apply without restarting the MCP server.
/// </summary>
internal sealed class WebSearchService
{
    private const int MaxRedirects = 5;

    private readonly WebSearchRepository _repository;
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

    public WebSearchService(WebSearchRepository repository, ISecretProvider secrets)
    {
        _repository = repository;
        _secrets = secrets;
        _domainPolicy = new DomainPolicyService(repository);
    }

    private static HttpClient CreateHttpClient()
    {
        // Redirects are followed manually in FetchAsync so every hop passes the SSRF guard and
        // domain policy; automatic redirects would let a public URL bounce into private networks.
        //
        // The SSRF check is enforced in ConnectCallback against the IP actually connected to,
        // and the socket is pinned to that same validated address. A pre-flight DNS lookup alone
        // is vulnerable to DNS rebinding (public IP at validation time, private IP at connect
        // time); validating and connecting to the identical address closes that window.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectWithSsrfGuardAsync,
            // Request and transparently decompress gzip/deflate/br bodies (L4). The size cap is
            // applied to the decompressed stream, so a compression bomb cannot exhaust memory.
            AutomaticDecompression = DecompressionMethods.All,
        };

        HttpClient client = new(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; KaeoLlmProxyWebSearch/1.0)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private static async ValueTask<Stream> ConnectWithSsrfGuardAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        DnsEndPoint endpoint = context.DnsEndPoint;

        IPAddress[] addresses = IPAddress.TryParse(endpoint.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

        if (!NetworkSafety.AllowLocalNetworks)
        {
            foreach (IPAddress address in addresses)
            {
                if (NetworkSafety.IsPrivateOrLoopback(address))
                    throw new InvalidOperationException(
                        $"Blocked connection to private/loopback address {address} for host '{endpoint.Host}'. " +
                        "Enable 'Allow local networks' in the Web Search settings to permit it.");
            }
        }

        // Connect to the very address that was validated (pinned), trying each until one works.
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"Could not connect to '{endpoint.Host}:{endpoint.Port}'.", lastError);
    }

    /// <summary>
    /// Runs <paramref name="query"/> against enabled providers in catalog order and returns the
    /// first non-empty result set. Throws <see cref="InvalidOperationException"/> with a summary
    /// when no provider can deliver results.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();
        NetworkSafety.AllowLocalNetworks = settings.AllowLocalNetworks;
        int capped = Math.Clamp(Math.Min(maxResults, settings.MaxResults), 1, 20);

        // Load the provider list and domain rules once per search rather than per provider/hop (L7).
        List<SearchProviderConfig> configs = [.. _repository.LoadProviders()];
        IReadOnlyList<DomainRule> rules = _domainPolicy.LoadRules();

        TimeSpan perProvider = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        int enabledCount = configs.Count(c => c.IsEnabled);

        // An overall budget bounds the whole loop so a sequence of slow providers cannot run
        // unbounded; each provider also gets its own slice so one slow backend cannot starve the
        // others (L3).
        using CancellationTokenSource overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(perProvider * Math.Max(1, enabledCount));

        var failures = new List<string>();

        foreach (SearchProviderConfig config in configs)
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

            using CancellationTokenSource providerCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            providerCts.CancelAfter(perProvider);

            try
            {
                IReadOnlyList<SearchResult> results = await provider.SearchAsync(
                    config, query, capped, apiKey, SharedClient, settings.MaxResponseBytes, providerCts.Token);

                // Drop results whose URL is blocked by the domain policy or is not http/https before
                // returning them, so web_search cannot surface links web_fetch would refuse (L7).
                List<SearchResult> allowed = [.. results.Where(r => IsResultAllowed(r.Url, rules))];
                if (allowed.Count > 0)
                    return allowed.Take(capped).ToList();

                failures.Add(results.Count > 0
                    ? $"{config.Name}: all results blocked by the domain rules."
                    : $"{config.Name}: returned no results.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || overallCts.IsCancellationRequested)
            {
                // Caller cancellation or exhaustion of the overall budget propagates to the tool,
                // which reports a friendly timeout; a single provider timing out is handled below.
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
    /// True when a search result URL is an absolute http/https URL permitted by the domain policy.
    /// </summary>
    private bool IsResultAllowed(string url, IReadOnlyList<DomainRule> rules)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && _domainPolicy.IsAllowed(uri, rules);
    }

    /// <summary>
    /// Fetches <paramref name="url"/> honoring the domain policy, network safety guard, and the
    /// configured timeout/response-size limits. HTML content is converted to plain text.
    /// </summary>
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        WebSearchSettings settings = _repository.LoadWebSearchSettings();
        NetworkSafety.AllowLocalNetworks = settings.AllowLocalNetworks;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"'{url}' is not a valid absolute URL.");

        // Load the domain rules once and reuse them across every redirect hop (L7).
        IReadOnlyList<DomainRule> rules = _domainPolicy.LoadRules();

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using HttpResponseMessage response = await FetchWithValidatedRedirectsAsync(uri, settings, rules, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        (string body, bool truncated) = await HttpContentReader.ReadCappedAsync(response, settings.MaxResponseBytes, timeoutCts.Token);

        string text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(body)
            ? HtmlTextExtractor.ToText(body)
            : body;

        // Tell the model the page was cut off at the size cap rather than silently dropping content (M5).
        return truncated ? text + "\n[content truncated]" : text;
    }

    /// <summary>
    /// GETs <paramref name="start"/> following up to <see cref="MaxRedirects"/> redirects,
    /// validating EVERY hop against the SSRF guard and the domain policy before requesting it,
    /// so a public URL cannot redirect the fetch into private networks or blocked domains.
    /// </summary>
    private async Task<HttpResponseMessage> FetchWithValidatedRedirectsAsync(
        Uri start, WebSearchSettings settings, IReadOnlyList<DomainRule> rules, CancellationToken cancellationToken)
    {
        Uri current = start;

        for (int hop = 0; ; hop++)
        {
            await NetworkSafety.ValidateAsync(current, settings.AllowLocalNetworks, cancellationToken);

            if (!_domainPolicy.IsAllowed(current, rules))
                throw new InvalidOperationException(
                    $"Domain '{current.Host}' is blocked by the configured allow/deny domain rules.");

            HttpResponseMessage response = await SharedClient.GetAsync(
                current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is not (>= 300 and <= 308) || response.Headers.Location is null)
                return response;

            Uri next = new(current, response.Headers.Location);
            response.Dispose();

            if (hop == MaxRedirects)
                throw new InvalidOperationException($"Too many redirects while fetching '{start}'.");

            current = next;
        }
    }

    private static bool LooksLikeHtml(string body)
    {
        string head = body.Length > 512 ? body[..512] : body;
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<!doctype", StringComparison.OrdinalIgnoreCase);
    }
}
