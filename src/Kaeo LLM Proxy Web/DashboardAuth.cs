using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kaeo.LlmProxy.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Kaeo.LlmProxy.Web;

/// <summary>
/// Dashboard authentication. The configured API key is an administrator credential; an optional
/// second key is restricted to logs. Loopback remains open only when no explicit authentication is
/// configured. Token comparisons are constant-time.
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
        => !IsLoopbackAddress(settings.ListenAddress) || settings.RequireApiKey
            || !string.IsNullOrWhiteSpace(settings.ReadOnlyApiKey);

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

    internal const string RoleItemKey = "Kaeo.DashboardRole";
    internal const string AdminRole = "admin";
    internal const string ReadOnlyRole = "readOnly";

    public static bool TryAuthorize(HttpContext context, WebDashboardSettings settings, out string? error)
    {
        error = null;
        string? token = ExtractToken(context.Request);
        if (!RequiresKey(settings))
        {
            context.Items[RoleItemKey] = AdminRole;
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "A dashboard API key is required. Enter the administrator or read-only key.";
            return false;
        }

        bool isAdminKey = Matches(token, settings.ApiKey);
        bool isReadOnlyKey = Matches(token, settings.ReadOnlyApiKey);
        if (isAdminKey && isReadOnlyKey)
        {
            error = "Administrator and read-only dashboard keys are misconfigured.";
            return false;
        }

        if (isAdminKey)
        {
            context.Items[RoleItemKey] = AdminRole;
            return true;
        }

        if (isReadOnlyKey)
        {
            context.Items[RoleItemKey] = ReadOnlyRole;
            return true;
        }

        error = "The supplied dashboard API key is not valid.";
        return false;
    }

    public static bool IsReadOnlyRequestAllowed(HttpRequest request)
        => HttpMethods.IsGet(request.Method)
            && (request.Path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string token, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    private static string? ExtractToken(HttpRequest request)
    {
        string? header = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        string? apiKeyHeader = request.Headers["X-Dashboard-Api-Key"];
        if (!string.IsNullOrEmpty(apiKeyHeader))
            return apiKeyHeader.ToString().Trim();

        return null;
    }
}