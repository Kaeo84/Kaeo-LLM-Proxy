using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kaeo.LlmProxy.VSExtension.Core;

/// <summary>
/// Outcome of an AST edit. On failure the source must be left untouched; <see cref="Error"/>
/// explains exactly why so the caller can report honestly instead of writing broken code.
/// </summary>
public sealed class AstEditResult
{
    public bool Success { get; private set; }
    public string? NewText { get; private set; }
    public string? Error { get; private set; }

    public static AstEditResult Ok(string newText) => new() { Success = true, NewText = newText };
    public static AstEditResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Roslyn-backed, syntax-aware C# member editing: replace a method/property/field/event by
/// name, or add a using directive. Pure string-in/string-out (no I/O) so it is unit-testable
/// and cross-platform. Every edit is re-parsed before it is returned; a replacement that does
/// not parse cleanly is refused, never written. The file's dominant line ending is preserved
/// and the existing member's surrounding trivia (blank lines, comments, indentation) is kept.
/// </summary>
public static class AstEditor
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.Parse);

    /// <summary>
    /// Replaces a method declaration. When the name has overloads, <paramref name="parameterTypes"/>
    /// (the exact parameter type texts, e.g. ["string", "int"]) must disambiguate; otherwise the
    /// edit is refused with the available signatures.
    /// </summary>
    public static AstEditResult ReplaceMethod(string source, string typeName, string methodName, string newCode, IReadOnlyList<string>? parameterTypes = null)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return AstEditResult.Fail("type is required.");
        if (string.IsNullOrWhiteSpace(methodName)) return AstEditResult.Fail("method is required.");
        if (string.IsNullOrWhiteSpace(newCode)) return AstEditResult.Fail("newCode is required.");
        if (!TryParseSource(source, out var root, out var sourceError)) return AstEditResult.Fail(sourceError!);

        var (type, typeError) = FindType(root!, typeName);
        if (type is null) return AstEditResult.Fail(typeError!);

        var all = type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList();
        if (all.Count == 0)
            return AstEditResult.Fail($"Method '{methodName}' not found in type '{typeName}'.");

        var candidates = all;
        if (parameterTypes is { Count: > 0 })
        {
            candidates = all.Where(m => MatchesParameters(m, parameterTypes)).ToList();
            if (candidates.Count == 0)
                return AstEditResult.Fail($"No overload of '{methodName}' matches parameter types ({string.Join(", ", parameterTypes)}). Available: {Signatures(all)}");
        }
        if (candidates.Count > 1)
            return AstEditResult.Fail($"Method '{methodName}' has {candidates.Count} overloads; pass parameterTypes to pick one. Available: {Signatures(candidates)}");

        if (!TryParseMember(newCode, source, out var parsed, out var memberError)) return AstEditResult.Fail(memberError!);
        if (parsed is not MethodDeclarationSyntax)
            return AstEditResult.Fail($"newCode must be a method declaration, but parsed as {parsed!.Kind()}. Use vs_ast_replace_member for properties/fields.");

        return ReplaceNode(source, root!, candidates[0], parsed);
    }

    /// <summary>
    /// Replaces a property, field, or event declaration by name. A field declaration that
    /// declares several variables ("int a, b;") is refused: replacing it would silently
    /// change its siblings.
    /// </summary>
    public static AstEditResult ReplaceMember(string source, string typeName, string memberName, string newCode)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return AstEditResult.Fail("type is required.");
        if (string.IsNullOrWhiteSpace(memberName)) return AstEditResult.Fail("member is required.");
        if (string.IsNullOrWhiteSpace(newCode)) return AstEditResult.Fail("newCode is required.");
        if (!TryParseSource(source, out var root, out var sourceError)) return AstEditResult.Fail(sourceError!);

        var (type, typeError) = FindType(root!, typeName);
        if (type is null) return AstEditResult.Fail(typeError!);

        MemberDeclarationSyntax? old = null;
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case PropertyDeclarationSyntax p when p.Identifier.Text == memberName:
                case EventDeclarationSyntax e when e.Identifier.Text == memberName:
                    if (old is not null) return AstEditResult.Fail($"Member '{memberName}' is ambiguous in type '{typeName}'.");
                    old = member;
                    break;
                case FieldDeclarationSyntax f when f.Declaration.Variables.Any(v => v.Identifier.Text == memberName):
                case EventFieldDeclarationSyntax ef when ef.Declaration.Variables.Any(v => v.Identifier.Text == memberName):
                    var decl = member is FieldDeclarationSyntax fd ? fd.Declaration : ((EventFieldDeclarationSyntax)member).Declaration;
                    if (decl.Variables.Count > 1)
                        return AstEditResult.Fail($"'{memberName}' shares a declaration with {decl.Variables.Count - 1} other variable(s); replace the whole declaration with a line edit instead.");
                    if (old is not null) return AstEditResult.Fail($"Member '{memberName}' is ambiguous in type '{typeName}'.");
                    old = member;
                    break;
            }
        }
        if (old is null)
            return AstEditResult.Fail($"Member '{memberName}' (property/field/event) not found in type '{typeName}'.");

        if (!TryParseMember(newCode, source, out var parsed, out var memberError)) return AstEditResult.Fail(memberError!);
        if (parsed is not (PropertyDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax))
            return AstEditResult.Fail($"newCode must be a property, field, or event declaration, but parsed as {parsed!.Kind()}. Use vs_ast_replace_method for methods.");

        return ReplaceNode(source, root!, old, parsed);
    }

    /// <summary>
    /// Adds a plain using directive when it is not already present. Refuses (without changing
    /// the file) when the directive already exists or the namespace text does not parse.
    /// </summary>
    public static AstEditResult AddUsing(string source, string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName)) return AstEditResult.Fail("namespace is required.");
        var name = SyntaxFactory.ParseName(namespaceName.Trim());
        if (name.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            return AstEditResult.Fail($"'{namespaceName}' is not a valid namespace name.");
        if (!TryParseSource(source, out var root, out var sourceError)) return AstEditResult.Fail(sourceError!);

        var unit = (CompilationUnitSyntax)root!;
        var requested = name.ToString();
        if (unit.Usings.Any(u => u.Alias is null && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && u.Name?.ToString() == requested))
            return AstEditResult.Fail($"Using directive '{requested}' already exists; no change made.");

        var directive = SyntaxFactory.UsingDirective(name)
            .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(DominantNewline(source)));
        var newText = unit.AddUsings(directive).ToFullString();

        var errors = SyntaxErrors(newText);
        if (errors.Count > 0)
            return AstEditResult.Fail($"Adding the using produced invalid syntax (file unchanged): {string.Join("; ", errors)}");
        return AstEditResult.Ok(newText);
    }

    private static bool TryParseSource(string source, out CompilationUnitSyntax? root, out string? error)
    {
        root = null;
        error = null;
        if (string.IsNullOrEmpty(source))
        {
            error = "File is empty; AST edits need existing C# source.";
            return false;
        }
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)
            .Select(FormatDiagnostic).ToList();
        if (errors.Count > 0)
        {
            error = "Source does not parse cleanly; fix syntax errors first (line edit or vs_build): " + string.Join("; ", errors);
            return false;
        }
        root = (CompilationUnitSyntax)tree.GetRoot();
        return true;
    }

    private static bool TryParseMember(string newCode, string source, out MemberDeclarationSyntax? member, out string? error)
    {
        member = null;
        error = null;
        var normalized = NormalizeNewlines(newCode, DominantNewline(source));
        var parsed = SyntaxFactory.ParseMemberDeclaration(normalized, options: ParseOptions);
        if (parsed is null)
        {
            error = "newCode is not a valid member declaration.";
            return false;
        }
        var errors = parsed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)
            .Select(FormatDiagnostic).ToList();
        if (errors.Count > 0)
        {
            error = "newCode does not parse cleanly (file unchanged): " + string.Join("; ", errors);
            return false;
        }
        member = parsed;
        return true;
    }

    private static AstEditResult ReplaceNode(string source, CompilationUnitSyntax root, MemberDeclarationSyntax old, MemberDeclarationSyntax parsed)
    {
        var replacement = parsed
            .WithLeadingTrivia(MergeLeadingTrivia(old, parsed))
            .WithTrailingTrivia(old.GetTrailingTrivia());
        var newText = root.ReplaceNode(old, replacement).ToFullString();

        var errors = SyntaxErrors(newText);
        if (errors.Count > 0)
            return AstEditResult.Fail($"Replacement produced invalid syntax (file unchanged): {string.Join("; ", errors)}");
        if (string.Equals(newText, source, StringComparison.Ordinal))
            return AstEditResult.Fail("Replacement is identical to the existing member; no change made.");
        return AstEditResult.Ok(newText);
    }

    /// <summary>
    /// Keeps the old member's surrounding trivia (blank lines, comments, indentation). When the
    /// replacement supplies its own doc comments or directives they are inserted just before the
    /// member (after the old trivia, at the old indentation) so they are not silently dropped.
    /// </summary>
    private static SyntaxTriviaList MergeLeadingTrivia(MemberDeclarationSyntax old, MemberDeclarationSyntax parsed)
    {
        var oldLeading = old.GetLeadingTrivia();
        var parsedLeading = parsed.GetLeadingTrivia();
        if (!parsedLeading.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
            return oldLeading;

        var list = oldLeading.ToList();
        var indentStart = list.Count;
        while (indentStart > 0 && list[indentStart - 1].IsKind(SyntaxKind.WhitespaceTrivia)) indentStart--;
        var indent = list.GetRange(indentStart, list.Count - indentStart);
        list.RemoveRange(indentStart, list.Count - indentStart);

        var parsedList = parsedLeading.ToList();
        while (parsedList.Count > 0 && parsedList[parsedList.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia))
            parsedList.RemoveAt(parsedList.Count - 1);

        list.AddRange(parsedList);
        list.AddRange(indent);
        return SyntaxFactory.TriviaList(list);
    }

    private static (TypeDeclarationSyntax? Type, string? Error) FindType(CompilationUnitSyntax root, string typeName)
    {
        var requested = typeName.Trim();
        var generic = requested.IndexOf('<');
        if (generic >= 0) requested = requested.Substring(0, generic);

        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => TypeMatches(t, requested)).ToList();
        if (matches.Count == 1) return (matches[0], null);
        if (matches.Count == 0) return (null, $"Type '{typeName}' not found.");
        return (null, $"Type '{typeName}' is ambiguous: {string.Join(", ", matches.Select(QualifiedName))}. Use a namespace-qualified name.");
    }

    private static bool TypeMatches(TypeDeclarationSyntax type, string requested)
    {
        var qualified = QualifiedName(type);
        if (string.Equals(qualified, requested, StringComparison.Ordinal)) return true;
        if (requested.IndexOf('.') < 0) return type.Identifier.Text == requested;
        return qualified.EndsWith("." + requested, StringComparison.Ordinal);
    }

    private static string QualifiedName(TypeDeclarationSyntax type)
    {
        var parts = new List<string> { type.Identifier.Text };
        for (var parent = type.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is TypeDeclarationSyntax nested) parts.Insert(0, nested.Identifier.Text);
            else if (parent is BaseNamespaceDeclarationSyntax ns) parts.Insert(0, ns.Name.ToString());
        }
        return string.Join(".", parts);
    }

    private static bool MatchesParameters(MethodDeclarationSyntax method, IReadOnlyList<string> parameterTypes)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != parameterTypes.Count) return false;
        for (var i = 0; i < parameters.Count; i++)
        {
            var actual = parameters[i].Type?.ToString().Trim() ?? string.Empty;
            if (!string.Equals(actual, parameterTypes[i].Trim(), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string Signatures(IEnumerable<MethodDeclarationSyntax> methods) =>
        string.Join("; ", methods.Select(m => m.Identifier.Text + m.ParameterList));

    private static List<string> SyntaxErrors(string text) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)
            .Select(FormatDiagnostic).ToList();

    private static string FormatDiagnostic(Diagnostic diagnostic) =>
        $"(line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}): {diagnostic.GetMessage()}";

    private static string DominantNewline(string source) =>
        source.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

    private static string NormalizeNewlines(string text, string newline) =>
        text.Replace("\r\n", "\n").Replace("\n", newline);
}