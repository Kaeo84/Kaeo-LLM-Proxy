using System.Text;
using System.Text.RegularExpressions;

namespace Kaeo.LlmProxy.WebSearch.Core.Services;

/// <summary>
/// Minimal dependency-free HTML-to-text conversion and entity decoding, used to make fetched
/// pages and parsed search fragments readable for LLM consumption.
/// </summary>
internal static partial class HtmlTextExtractor
{
    [GeneratedRegex("<(script|style|noscript|template|svg)\\b[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("</(p|div|li|tr|td|th|h[1-6]|section|article|header|footer|blockquote|pre|br)\\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    // HTML comments: a classic hiding place for injected instructions that never render for humans.
    private static readonly Regex CommentRegex = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    // Best-effort removal of elements hidden from humans (hidden attribute, display:none,
    // visibility:hidden, aria-hidden="true") — the primary covert channel for indirect
    // prompt-injection payloads. Nested same-name tags may truncate the match; acceptable for a
    // sanitization layer.
    private static readonly Regex HiddenElementRegex = new(
        "<([a-zA-Z][a-zA-Z0-9]*)((?:(?!</?\\1[\\s>]).)*?(?:\\shidden(?=[\\s=>/])|aria-hidden\\s*=\\s*[\"']?true[\"']?|style\\s*=\\s*[\"'][^\"']*(?:display\\s*:\\s*none|visibility\\s*:\\s*hidden))[^>]*)>.*?</\\1\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Zero-width / directional / soft-hyphen characters: invisible to humans, visible to models.
    private static readonly Regex InvisibleUnicodeRegex = new(
        "[\u00AD\u200B-\u200F\u202A-\u202E\u2060-\u2064\uFEFF]", RegexOptions.Compiled);

    /// <summary>Converts a full HTML document to readable plain text (block structure preserved).</summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        string text = CommentRegex.Replace(html, "\n");
        text = HiddenElementRegex.Replace(text, "\n");
        text = ScriptStyleRegex().Replace(text, "\n");
        text = BlockCloseRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = DecodeEntities(text);
        text = InvisibleUnicodeRegex.Replace(text, string.Empty);

        var output = new StringBuilder();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = WhitespaceRegex().Replace(rawLine, " ").Trim();
            if (line.Length > 0)
                output.AppendLine(line);
        }

        return output.ToString().Trim();
    }

    /// <summary>Strips tags and decodes entities in a small inline HTML fragment.</summary>
    public static string Clean(string fragment)
    {
        string text = CommentRegex.Replace(fragment, " ");
        text = HiddenElementRegex.Replace(text, " ");
        text = InvisibleUnicodeRegex.Replace(DecodeEntities(TagRegex().Replace(text, " ")), string.Empty);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    /// <summary>Decodes the common named and numeric HTML entities.</summary>
    public static string DecodeEntities(string value)
    {
        if (value.IndexOf('&') < 0)
            return value;

        var result = new StringBuilder(value.Length);
        int index = 0;

        while (index < value.Length)
        {
            int ampersand = value.IndexOf('&', index);
            if (ampersand < 0)
            {
                result.Append(value, index, value.Length - index);
                break;
            }

            result.Append(value, index, ampersand - index);

            int semicolon = value.IndexOf(';', ampersand + 1);
            if (semicolon < 0 || semicolon - ampersand > 10)
            {
                result.Append('&');
                index = ampersand + 1;
                continue;
            }

            string entity = value[(ampersand + 1)..semicolon];
            char? decoded = entity switch
            {
                "amp" => '&',
                "lt" => '<',
                "gt" => '>',
                "quot" => '"',
                "apos" => '\'',
                "nbsp" => ' ',
                "ndash" => '\u2013',
                "mdash" => '\u2014',
                "hellip" => '\u2026',
                "lsquo" => '\u2018',
                "rsquo" => '\u2019',
                "ldquo" => '\u201C',
                "rdquo" => '\u201D',
                _ => DecodeNumericEntity(entity),
            };

            if (decoded.HasValue)
            {
                result.Append(decoded.Value);
                index = semicolon + 1;
            }
            else
            {
                result.Append('&');
                index = ampersand + 1;
            }
        }

        return result.ToString();
    }

    private static char? DecodeNumericEntity(string entity)
    {
        int codePoint;

        if (entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(entity.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out codePoint))
        {
        }
        else if (entity.StartsWith('#') && int.TryParse(entity.AsSpan(1), out codePoint))
        {
        }
        else
        {
            return null;
        }

        return codePoint is >= 1 and <= 0xFFFF ? (char)codePoint : null;
    }
}
