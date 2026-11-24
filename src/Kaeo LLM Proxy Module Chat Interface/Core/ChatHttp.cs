using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kaeo.LlmProxy.Module.ChatInterface;

/// <summary>
/// Shared HTTP plumbing for every chat provider: one pooled static <see cref="HttpClient"/>,
/// per-request linked timeouts, capped response reads, and URL redaction so webhook tokens
/// never reach logs. Auth headers are set per request, never on DefaultRequestHeaders.
/// </summary>
internal static class ChatHttp
{
    private const int MaxResponseBytes = 1_000_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }

    /// <summary>POSTs a JSON body and returns the (capped) response body; throws on non-success status.</summary>
    public static Task<string> PostJsonAsync(string url, string json, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        configure?.Invoke(request);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>POSTs a form-urlencoded body and returns the (capped) response body; throws on non-success status.</summary>
    public static Task<string> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        configure?.Invoke(request);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>GETs a URL and returns the (capped) response body; throws on non-success status.</summary>
    public static Task<string> GetAsync(string url, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        configure?.Invoke(request);
        return SendAsync(request, cancellationToken);
    }

    private static async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(RequestTimeout);

            using HttpResponseMessage response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            string body = await ReadCappedAsync(response, MaxResponseBytes, timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string url = request.RequestUri?.ToString() ?? string.Empty;
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} from {RedactUrl(url)}: {Truncate(body, 300)}");
            }

            return body;
        }
    }

    /// <summary>Reads the response body as UTF-8 text, stopping after <paramref name="maxBytes"/> bytes.</summary>
    public static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();

        byte[] chunk = new byte[8192];
        int total = 0;
        while (total < maxBytes)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes - total)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
            total += read;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Strips the query string, fragment, and any userinfo from a URL so secret webhook tokens
    /// (which live in the query) never appear in logs or error messages. Discord webhook tokens
    /// live in the path (/api/webhooks/{id}/{token}), so that segment is masked too.
    /// </summary>
    public static string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return RedactWebhookToken(uri.GetLeftPart(UriPartial.Path));

        // Not a parseable absolute URL: cut at the first query/fragment marker defensively.
        int cut = url.IndexOfAny(['?', '#']);
        return RedactWebhookToken(cut >= 0 ? url[..cut] : url);
    }

    /// <summary>Masks the token segment of a Discord webhook path, keeping the webhook id.</summary>
    private static string RedactWebhookToken(string path)
    {
        const string marker = "/api/webhooks/";
        int markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return path;

        int idStart = markerIndex + marker.Length;
        int tokenStart = path.IndexOf('/', idStart);
        return tokenStart >= 0 && tokenStart + 1 < path.Length
            ? path[..(tokenStart + 1)] + "***"
            : path;
    }

    /// <summary>Sets a per-request Bearer authorization header.</summary>
    public static void SetBearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Truncates text to <paramref name="maxChars"/> characters with an ellipsis marker.</summary>
    public static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");
}