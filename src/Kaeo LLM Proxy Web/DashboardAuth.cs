using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Dashboard authentication. Loopback bindings are open by default; non-loopback bindings (or
/// <see cref="WebDashboardSettings.RequireApiKey"/>) require the configured API key. The token
/// compare is constant-time, mirroring <c>McpServerHost.IsAuthorized</c>.
/// </summary>
internal static class DashboardAuth
{
    public static bool IsLoopbackAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return true;
        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(address, out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }

    public static bool RequiresKey(WebDashboardSettings settings)
        => !IsLoopbackAddress(settings.ListenAddress) || settings.RequireApiKey;

    public static string AuthMode(WebDashboardSettings settings)
        => RequiresKey(settings) ? "apiKey" : "open";

    public static bool IsAuthorized(HttpContext context, WebDashboardSettings settings, out string? error)
    {
        error = null;
        if (!RequiresKey(settings))
            return true;

        string? expected = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Keep the misconfiguration server-side; the caller gets a generic message.
            error = "Dashboard authentication is misconfigured on the server.";
            return false;
        }

        string? token = ExtractToken(context.Request);
        if (string.IsNullOrEmpty(token))
        {
            error = "A dashboard API key is required. Send 'Authorization: Bearer <key>' or 'X-Dashboard-Api-Key: <key>'.";
            return false;
        }

        bool match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
        if (!match)
            error = "The supplied dashboard API key is not valid.";
        return match;
    }

    private static string? ExtractToken(HttpRequest request)
    {
        string? header = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        string? apiKeyHeader = request.Headers["X-Dashboard-Api-Key"];
        if (!string.IsNullOrEmpty(apiKeyHeader))
            return apiKeyHeader.ToString().Trim();

        string? query = request.Query["api_key"];
        if (!string.IsNullOrEmpty(query))
            return query.ToString().Trim();

        return null;
    }
}