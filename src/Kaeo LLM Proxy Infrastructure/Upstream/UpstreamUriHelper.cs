namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Builds absolute request URIs for OpenAI-compatible upstream endpoints.
/// </summary>
/// <remarks>
/// Some OpenAI-compatible providers (e.g. Alibaba DashScope/Qwen) publish a base URL that
/// already ends in "/v1" (e.g. ".../compatible-mode/v1"). Our relative paths always start
/// with "v1/..." for OpenAI-style routes. Combining the two naively - either by string
/// concatenation or by using <see cref="System.Net.Http.HttpClient.BaseAddress"/> together
/// with a root-relative request URI (which replaces the base path entirely per RFC 3986
/// URI-combining rules) - can duplicate the "v1" segment or silently drop a configured
/// base path, producing a 404 from the upstream. This helper centralizes the correct
/// combination logic so it stays consistent across the proxy handler, the test console,
/// and the model-fetch dialog.
/// </remarks>
public static class UpstreamUriHelper
{
    /// <summary>
    /// Combines an upstream base URL with a relative OpenAI-style path (e.g. "v1/chat/completions"),
    /// avoiding a duplicated "v1" segment when the base URL's path already ends in "/v1". Any
    /// query string on the base URL is preserved and merged with a query on the relative path.
    /// </summary>
    /// <param name="baseUrl">The configured upstream base URL. May or may not end with "/v1".</param>
    /// <param name="relativePath">The relative path, e.g. "v1/chat/completions" or "v1/models".</param>
    public static Uri BuildRequestUri(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        Uri baseUri = new(baseUrl.Trim());
        string basePath = baseUri.AbsolutePath.TrimEnd('/');

        // Split any query off the relative path so it can be merged with the base query
        // instead of being parsed as part of the path.
        string relPath = relativePath.TrimStart('/');
        string relQuery = string.Empty;
        int queryIndex = relPath.IndexOf('?');
        if (queryIndex >= 0)
        {
            relQuery = relPath[(queryIndex + 1)..];
            relPath = relPath[..queryIndex];
        }

        // Dedup "v1" on the PATH only; a base query ending in "/v1" must not trigger it.
        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            && relPath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            relPath = relPath["v1/".Length..];
        }

        string baseQuery = baseUri.Query.TrimStart('?');
        string query = baseQuery.Length > 0
            ? (relQuery.Length > 0 ? baseQuery + "&" + relQuery : baseQuery)
            : relQuery;

        // UriBuilder appends to the base path rather than replacing it (RFC 3986 root-relative
        // combining would drop a configured base path).
        UriBuilder builder = new(baseUri)
        {
            Path = basePath + "/" + relPath,
            Query = query,
        };
        return builder.Uri;
    }
}
