using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Cheap, in-process C# syntax validation (parse only — no compilation, no references).
/// Used by the optional post-edit syntax feedback so the model sees parse errors immediately
/// after an edit instead of claiming success. Semantic diagnostics stay with vs_build /
/// vs_get_diagnostics.
/// </summary>
public static class SyntaxCheck
{
    /// <summary>
    /// Returns up to <paramref name="maxErrors"/> syntax errors formatted as "(line N): message".
    /// An empty list means the text parses cleanly (it does NOT mean the code compiles).
    /// </summary>
    public static IReadOnlyList<string> CheckCSharp(string text, int maxErrors = 10)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse));
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(maxErrors)
            .Select(d => $"(line {d.Location.GetLineSpan().StartLinePosition.Line + 1}): {d.GetMessage()}")
            .ToList();
    }
}