using System.Text;

namespace Kaeo.LlmProxy.Module.WebSearch;

/// <summary>
/// Shared bounded response-body reader for the Web Search feature. Both <c>web_fetch</c> and the
/// search providers read through here so a malicious or misconfigured endpoint cannot stream an
/// unbounded body into memory (M5). The declared charset is honored with a UTF-8 fallback (L4);
/// the fetch path additionally detects truncation so it can append a visible marker.
/// </summary>
internal static class HttpContentReader
{
    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> of the (decompressed) body as text, honoring the
    /// response charset. Used by search providers, which parse JSON/HTML and do not need a marker.
    /// </summary>
    public static async Task<string> ReadCappedAsStringAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        (string text, _) = await ReadCoreAsync(response, maxBytes, detectTruncation: false, cancellationToken);
        return text;
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> of the body as text and reports whether more data
    /// remained (the cap truncated the page). Used by <c>web_fetch</c>.
    /// </summary>
    public static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken) =>
        await ReadCoreAsync(response, maxBytes, detectTruncation: true, cancellationToken);

    private static async Task<(string Text, bool Truncated)> ReadCoreAsync(
        HttpResponseMessage response, int maxBytes, bool detectTruncation, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[8192];
        using var output = new MemoryStream();
        int totalRead = 0;

        while (totalRead < maxBytes)
        {
            int toRead = Math.Min(buffer.Length, maxBytes - totalRead);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
                break;

            output.Write(buffer, 0, read);
            totalRead += read;
        }

        bool truncated = false;
        if (detectTruncation && totalRead >= maxBytes)
        {
            // Probe a single byte past the cap to distinguish a complete page from a truncated one.
            int extra = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            truncated = extra > 0;
        }

        Encoding encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
        string text = encoding.GetString(output.GetBuffer(), 0, (int)output.Length);
        return (text, truncated);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Unknown or unsupported charset (e.g. a code page not registered): fall back to UTF-8.
            }
        }

        return Encoding.UTF8;
    }
}