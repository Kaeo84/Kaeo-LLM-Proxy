namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// The canonical set of model capability tokens advertised on the <c>capabilities</c> array of the
/// discovery endpoints (/api/tags and /v1/models). Each entry pairs the wire token (lowercase
/// snake_case, e.g. <c>function_calling</c>) with the display label shown in the Model Mapping
/// dialog (e.g. "Function Calling").
/// </summary>
internal static class ModelCapabilities
{
    /// <summary>Wire tokens paired with their dialog display labels, in canonical output order.</summary>
    public static readonly IReadOnlyList<(string Token, string Display)> All =
    [
        ("text", "Text"),
        ("chat", "Chat"),
        ("reasoning", "Reasoning"),
        ("vision", "Vision"),
        ("audio", "Audio"),
        ("function_calling", "Function Calling"),
        ("embeddings", "Embeddings"),
        ("code", "Code"),
        ("image_generation", "Image Generation"),
    ];

    /// <summary>Just the wire tokens, in canonical output order.</summary>
    public static readonly IReadOnlyList<string> Tokens = All.Select(c => c.Token).ToList();

    /// <summary>Display label for a wire token, or null when the token is not in the known set.</summary>
    public static string? DisplayFor(string token)
        => All.FirstOrDefault(c => string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase)).Display;

    /// <summary>
    /// Orders an arbitrary set of capability tokens into canonical order, dropping duplicates and
    /// any tokens outside the known set (case-insensitive). Returns a fresh list safe to persist.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? tokens)
    {
        if (tokens is null)
            return [];

        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in Tokens)
            known.Add(t);

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in tokens)
        {
            if (known.Contains(t))
                selected.Add(t);
        }

        List<string> ordered = [];
        foreach (string t in Tokens)
        {
            if (selected.Contains(t))
                ordered.Add(t);
        }

        return ordered;
    }
}
