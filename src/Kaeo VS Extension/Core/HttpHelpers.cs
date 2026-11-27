using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Shared HTTP helpers for the extension's upstream clients. Eliminates the duplicated
/// endpoint-URI builder and Bearer-auth setup that previously lived verbatim in
/// <see cref="OllamaChatClient"/>, <see cref="OllamaApiClient"/>, and <see cref="McpServerManager"/>.
/// </summary>
internal static class HttpHelpers
{
    /// <summary>
    /// Builds an endpoint URI without discarding a path in the base URL: a naive
    /// <c>new Uri(base, "/path")</c> treats the absolute path as a replacement, so a
    /// base like <c>https://host/kaeo</c> silently became <c>https://host/path</c>.
    /// </summary>
    public static Uri BuildEndpointUri(string baseUrl, string endpointPath)
        => new(new Uri(baseUrl.TrimEnd('/') + "/"), endpointPath.TrimStart('/'));

    /// <summary>
    /// Applies a Bearer authorization header to an <see cref="HttpClient"/> when a key is
    /// present. No-op for null/blank keys.
    /// </summary>
    public static void SetBearerAuth(HttpClient http, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
    /// <summary>
    /// The User-Agent every extension request carries: <c>Kaeo-VSExt/&lt;version&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The proxy stores each request's User-Agent and derives a client label from it, so this
    /// token is what lets the Requests views separate extension traffic from Copilot's — both
    /// reach the proxy through the same Ollama-compatible endpoints and send no other
    /// distinguishing header.
    /// </remarks>
    public static readonly string ClientUserAgent =
        "Kaeo-VSExt/" + (typeof(HttpHelpers).Assembly.GetName().Version?.ToString() ?? "1.0.0.0");

    /// <summary>
    /// Stamps the extension's identity headers on an outgoing proxy request. Applied per request
    /// rather than on <c>DefaultRequestHeaders</c>, so a shared <see cref="HttpClient"/> can serve
    /// connections configured with different keys without cross-talk.
    /// </summary>
    public static void StampClientIdentity(HttpRequestMessage request)
    {
        if (request is null)
            return;
        request.Headers.TryAddWithoutValidation("User-Agent", ClientUserAgent);
        request.Headers.TryAddWithoutValidation("X-Kaeo-Client", "vsext");
    }
}
