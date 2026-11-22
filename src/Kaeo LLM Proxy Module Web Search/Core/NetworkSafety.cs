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
/// SSRF guard for outbound web requests: enforces http/https only and blocks private/loopback
/// destinations unless the user explicitly opts in via the "allow local networks" setting.
/// DNS names are resolved and every returned address is checked, so a name resolving to both
/// public and private addresses is treated as private (conservative).
/// </summary>
internal static class NetworkSafety
{
    /// <summary>
    /// Mirrors the module's "allow local networks" setting for the shared HttpClient's
    /// connect-time SSRF check. The service refreshes this from the loaded settings before each
    /// operation; the connect callback reads it so the IP actually connected to is validated
    /// (closing the DNS-rebinding window between a pre-flight lookup and the real socket).
    /// </summary>
    public static volatile bool AllowLocalNetworks;

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
            return IsPrivateIPv4(address.GetAddressBytes());

        // IPv6
        byte[] bytes = address.GetAddressBytes();

        // :: unspecified
        if (address.Equals(IPAddress.IPv6Any))
            return true;
        // ff00::/8 multicast
        if (bytes[0] == 0xFF)
            return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            return true;

        // NAT64 64:ff9b::/96 — the low 32 bits embed the IPv4 target (e.g. 64:ff9b::a9fe:a9fe
        // reaches 169.254.169.254). Re-check the embedded address so the guard holds on NAT64 nets.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9b
            && AllZero(bytes, 4, 8))
        {
            return IsPrivateIPv4([bytes[12], bytes[13], bytes[14], bytes[15]]);
        }

        // 6to4 2002::/16 — bytes 2..5 embed the IPv4 target.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPrivateIPv4([bytes[2], bytes[3], bytes[4], bytes[5]]);

        // Teredo 2001::/32 — bytes 4..7 carry the server IPv4; bytes 12..15 carry the client IPv4
        // XOR 0xFF. Check both so neither embedded private address slips through.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            if (IsPrivateIPv4([bytes[4], bytes[5], bytes[6], bytes[7]]))
                return true;

            return IsPrivateIPv4([
                (byte)(bytes[12] ^ 0xFF), (byte)(bytes[13] ^ 0xFF),
                (byte)(bytes[14] ^ 0xFF), (byte)(bytes[15] ^ 0xFF)]);
        }

        return false;
    }

    private static bool IsPrivateIPv4(byte[] bytes)
    {
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
        // 192.0.0.0/24 IETF protocol assignments
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            return true;
        // 198.18.0.0/15 benchmarking
        if (bytes[0] == 198 && bytes[1] is 18 or 19)
            return true;
        // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, and 255.255.255.255 broadcast
        if (bytes[0] >= 224)
            return true;

        return false;
    }

    private static bool AllZero(byte[] bytes, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        return true;
    }
}
