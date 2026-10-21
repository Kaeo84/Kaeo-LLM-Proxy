using System.Net;

namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Networking and URL helpers that are pure functions with no dependency on instance state,
/// so they can be used from any layer without pulling in Services or UI.
/// </summary>
internal static class NetworkingHelpers
{
    /// <summary>
    /// Whether a URL's host names this machine rather than a remote host.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: an unparseable URL is not loopback, so an unopted
    /// mapping with a malformed base URL drops the client credential instead of
    /// assuming the local case. Only the host is inspected — a loopback host on any
    /// port is still this machine.
    /// </remarks>
    public static bool IsLoopbackHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host is not { Length: > 0 } host)
            return false;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // IPv6 loopback: Uri.Host returns the bracketed form ("[::1]"), and IPAddress.IsLoopback
        // also covers the rest of 127.0.0.0/8 so "http://127.0.0.2:8080" counts as this machine.
        return IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address)
            && IPAddress.IsLoopback(address);
    }
}

/// <summary>Well-known HTTP header names.</summary>
internal static class HttpHeaderNames
{
    public const string Authorization = "Authorization";
}

/// <summary>Well-known endpoint paths shared across the proxy and MCP host.</summary>
internal static class EndpointPaths
{
    public const string Scalar = "/scalar";
    public const string OpenApiSpec = "/openapi/v1/openapi.json";
    public const string V1ChatCompletions = "/v1/chat/completions";
    public const string V1Completions = "/v1/completions";
    public const string V1Embeddings = "/v1/embeddings";
    public const string V1Models = "/v1/models";
}