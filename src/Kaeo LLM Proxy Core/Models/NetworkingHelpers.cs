using System.Net;
using System.Net.Sockets;

namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Networking and URL helpers that are pure functions with no dependency on instance state,
/// so they can be used from any layer without pulling in Services or UI.
/// </summary>
internal static class NetworkingHelpers
{
    /// <summary>
    /// The host to put in a browser-facing URL for a configured listen address.
    /// </summary>
    /// <remarks>
    /// A wildcard bind (<c>0.0.0.0</c>, <c>::</c>, <c>*</c>, <c>+</c>, <c>any</c>, or blank) is a
    /// "listen on everything" instruction, not a routable destination, so it collapses to
    /// <c>localhost</c> — the address most likely to resolve and reach the listener from this
    /// machine. A literal IPv6 address is bracketed so the resulting URL stays valid.
    /// </remarks>
    public static string ResolveBrowsableHost(string? address)
    {
        string host = (address ?? string.Empty).Trim();
        if (host.Length == 0
            || host is "*" or "+"
            || host.Equals("any", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "localhost";
        }

        // Accept the bracketed IPv6 form ("[::1]") as well as the bare form.
        string bare = host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out IPAddress? ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                return "localhost";

            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        }

        return host;
    }

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

        // host.docker.internal is deliberately NOT loopback: it names the Docker host from
        // inside a container, which is a different machine than the container this runs in.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
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