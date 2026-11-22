namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// M7: strips credentials from URLs before they reach logs, activity entries, or UI text:
/// both userinfo (<c>https://user:token@host/…</c>) and query strings (<c>?api_key=…</c>).
/// Scheme, host, and path remain for diagnostics.
/// </summary>
internal static class UrlRedactor
{
    public static string Redact(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        string redacted = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            redacted = uri.GetLeftPart(UriPartial.Path);
        int queryStart = redacted.IndexOf('?');
        if (queryStart >= 0) redacted = redacted[..queryStart] + "?…";
        return redacted;
    }
}