using System.Net;
using System.Net.Sockets;

namespace Kaeo.LlmProxy.Mcp.Core.Services;

/// <summary>
/// SSRF guard for outbound web requests: enforces http/https only and blocks private/loopback
/// destinations unless the user explicitly opts in via the "allow local networks" setting.
/// DNS names are resolved and every returned address is checked, so a name resolving to both
/// public and private addresses is treated as private (conservative).
/// </summary>
internal static class NetworkSafety
{
    public static async Task ValidateAsync(Uri uri, bool allowLocalNetworks, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Only http and https URLs are supported (got '{uri.Scheme}').");

        if (allowLocalNetworks)
            return;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new InvalidOperationException($"Could not resolve host '{uri.Host}'.", ex);
            }
        }

        foreach (IPAddress address in addresses)
        {
            if (IsPrivateOrLoopback(address))
            {
                throw new InvalidOperationException(
                    $"Blocked request to private/loopback address {address} for host '{uri.Host}'. " +
                    "Enable 'Allow local networks' in the Web Search settings to permit it.");
            }
        }
    }

    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();

            // 0.0.0.0/8
            if (bytes[0] == 0)
                return true;
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            // 169.254.0.0/16 link-local
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            // 100.64.0.0/10 CGNAT
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                return true;

            return false;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }
}
