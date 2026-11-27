namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// Classifies a request's <c>User-Agent</c> into a short client label, so traffic can be attributed
/// to the tool that produced it — the Visual Studio extension, Copilot, a browser, a script — without
/// reading the full header string.
/// </summary>
/// <remarks>
/// Derived on read rather than persisted: the label is a pure function of the stored
/// <see cref="RequestLog.UserAgent"/>, so adding a rule applies retroactively to every row already in
/// the database instead of only to traffic logged after the upgrade. The same reason
/// <see cref="RequestLog.Requestor"/> is not a column.
/// <para>
/// The Visual Studio extension sends <c>User-Agent: Kaeo-VSExt/&lt;version&gt;</c> (plus an
/// <c>X-Kaeo-Client: vsext</c> header), which is what makes extension traffic separable from Copilot
/// traffic — both talk to the same proxy and would otherwise be indistinguishable.
/// </para>
/// </remarks>
internal static class ClientTag
{
    /// <summary>The label reported for the Visual Studio extension.</summary>
    public const string VisualStudioExtension = "VS extension";

    /// <summary>The label reported for GitHub Copilot's chat clients.</summary>
    public const string Copilot = "Copilot";

    /// <summary>
    /// Returns a short label for <paramref name="userAgent"/>, or null when the header is absent or
    /// names no client this build recognises.
    /// </summary>
    public static string? Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        // The extension's own token is checked first: it is the one client we control, and its
        // string must never be captured by a broader rule if it ever mentions Copilot.
        if (userAgent.Contains("kaeo-vsext", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("kaeo vsext", StringComparison.OrdinalIgnoreCase))
        {
            return VisualStudioExtension;
        }

        if (userAgent.Contains("copilot", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("github", StringComparison.OrdinalIgnoreCase))
        {
            return Copilot;
        }

        if (userAgent.Contains("ollama", StringComparison.OrdinalIgnoreCase))
            return "Ollama";
        if (userAgent.Contains("openai", StringComparison.OrdinalIgnoreCase))
            return "OpenAI SDK";
        if (userAgent.Contains("python", StringComparison.OrdinalIgnoreCase))
            return "Python";
        if (userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase))
            return "curl";
        if (userAgent.Contains("mozilla", StringComparison.OrdinalIgnoreCase))
            return "Browser";

        return null;
    }
}