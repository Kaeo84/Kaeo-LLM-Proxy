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
        => new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), endpointPath.TrimStart('/'));

    /// <summary>
    /// Applies a Bearer authorization header to an <see cref="HttpClient"/> when a key is
    /// present. No-op for null/blank keys.
    /// </summary>
    public static void SetBearerAuth(HttpClient http, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }
}
