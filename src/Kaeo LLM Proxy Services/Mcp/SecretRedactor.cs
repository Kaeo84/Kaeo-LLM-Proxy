using System.Text.RegularExpressions;

namespace Kaeo.LlmProxy.Services.Mcp;

/// <summary>
/// Masks known-sensitive fields before request/response bodies and module activity details are
/// persisted to the request log. MCP <c>tools/call</c> arguments routinely carry credentials
/// (SSH passwords, API keys, tokens); detail collection must not accumulate them in plaintext.
/// This is a best-effort masking layer applied uniformly to JSON bodies, SSE payloads, and
/// free-form detail text. It never throws and never alters non-sensitive content.
/// </summary>
internal static class SecretRedactor
{
    private const string Mask = "***REDACTED***";

    // Matches "sensitiveKey": "value" (the value may contain escaped quotes) and masks the
    // value while preserving the key and surrounding structure. Case-insensitive so camelCase,
    // snake_case, and PascalCase argument names are all covered.
    private static readonly Regex SensitiveStringRegex = new(
        @"(""(?:password|passwd|pwd|secret|clientsecret|client_secret|token|accesstoken|access_token|refreshtoken|refresh_token|apikey|api_key|privatekey|private_key|authorization|credential|credentials|passphrase|connectionstring|connection_string|bearer)""\s*:\s*)""(?:[^""\\]|\\.)*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="text"/> with the values of known-sensitive JSON fields replaced
    /// by a fixed mask. Null/empty input is returned unchanged.
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return SensitiveStringRegex.Replace(text, $"$1\"{Mask}\"");
    }
}
