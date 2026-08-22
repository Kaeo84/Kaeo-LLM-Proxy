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
    /// avoiding a duplicated "v1" segment when the base URL already ends in "/v1".
    /// </summary>
    /// <param name="baseUrl">The configured upstream base URL. May or may not end with "/v1".</param>
    /// <param name="relativePath">The relative path, e.g. "v1/chat/completions" or "v1/models".</param>
    public static Uri BuildRequestUri(string baseUrl, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string trimmedBaseUrl = baseUrl.TrimEnd('/');
        string trimmedRelativePath = relativePath.TrimStart('/');

        if (trimmedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            && trimmedRelativePath.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            trimmedRelativePath = trimmedRelativePath["v1/".Length..];
        }

        return new Uri(new Uri(trimmedBaseUrl + "/"), trimmedRelativePath);
    }
}
