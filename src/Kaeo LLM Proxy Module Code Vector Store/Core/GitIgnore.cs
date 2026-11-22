using System.Text;
using System.Text.RegularExpressions;

namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// Built-in .gitignore support for mirror indexing (M9). Rules are collected from the
/// .gitignore files found in the indexed tree (committed blobs for git mirrors, on-disk files
/// for local-directory mirrors) and applied with standard gitignore semantics: comments,
/// negation (!), directory-only patterns (trailing /), anchoring (leading or embedded /),
/// *, ?, ** and character classes. The last matching rule wins; deeper .gitignore files take
/// precedence over shallower ones. There is no user configuration — the defaults are built in.
/// </summary>
internal sealed class GitIgnoreMatcher
{
    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);

    private readonly List<(string Scope, List<Rule> Rules)> _scopes = [];

    /// <summary>
    /// Adds the rules from one .gitignore file. <paramref name="scope"/> is the repository-relative
    /// directory containing the file ("" for the root); its patterns apply at and below that directory.
    /// </summary>
    public void AddScope(string scope, string gitignoreContent)
    {
        // A pathological .gitignore must not stall the walk.
        if (gitignoreContent.Length > 1_000_000)
            return;

        var rules = new List<Rule>();
        foreach (var rawLine in gitignoreContent.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var negated = line[0] == '!';
            if (negated)
                line = line[1..];
            if (line.Length == 0)
                continue;

            // A leading backslash escapes a literal '#' or '!'.
            if (line[0] == '\\' && line.Length > 1 && (line[1] == '#' || line[1] == '!'))
                line = line[1..];

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
                line = line[..^1];
            if (line.Length == 0)
                continue;

            // Per gitignore: a separator at the start or middle anchors the pattern to this
            // directory; otherwise it matches at any depth below it.
            var anchored = line.StartsWith('/') || line.Contains('/');
            if (line.StartsWith('/'))
                line = line[1..];
            if (line.Length == 0)
                continue;

            try
            {
                var body = Translate(line);
                var regex = anchored ? "^" + body + "$" : "(^|/)" + body + "$";
                rules.Add(new Rule(
                    new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),
                    negated,
                    directoryOnly));
            }
            catch (ArgumentException)
            {
                // Malformed pattern (e.g. an unterminated character class): ignore the line.
            }
        }

        if (rules.Count > 0)
            _scopes.Add((scope, rules));
    }

    /// <summary>
    /// True when <paramref name="path"/> (repository-relative, forward slashes) is ignored.
    /// Directory checks prune whole subtrees during the walk, which also enforces git's rule
    /// that a file cannot be re-included when its parent directory is excluded.
    /// </summary>
    public bool IsIgnored(string path, bool isDirectory)
    {
        var ignored = false;
        foreach (var (scope, rules) in _scopes)
        {
            if (scope.Length > 0 && !path.StartsWith(scope + "/", StringComparison.Ordinal))
                continue;

            var rel = scope.Length == 0 ? path : path[(scope.Length + 1)..];
            foreach (var rule in rules)
            {
                if (rule.DirectoryOnly && !isDirectory)
                    continue;
                if (Matches(rule.Pattern, rel))
                    ignored = !rule.Negated;
            }
        }
        return ignored;
    }

    private static bool Matches(Regex pattern, string path)
    {
        try
        {
            return pattern.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Translates a gitignore glob into a regex body (path separators are '/').</summary>
    private static string Translate(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        if (i == 0 && i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");   // leading "**/" = any depth
                            i += 2;
                        }
                        else if (i + 2 == pattern.Length && i > 0 && pattern[i - 1] == '/')
                        {
                            sb.Append(".*");         // trailing "/**" = everything inside
                            i += 1;
                        }
                        else if (i > 0 && pattern[i - 1] == '/' && i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");   // "/**/" = zero or more directories
                            i += 2;
                        }
                        else
                        {
                            sb.Append("[^/]*");      // degenerate "**" behaves like "*"
                            i += 1;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    var j = i + 1;
                    if (j < pattern.Length && (pattern[j] == '!' || pattern[j] == '^'))
                        j++;
                    if (j < pattern.Length && pattern[j] == ']')
                        j++;
                    while (j < pattern.Length && pattern[j] != ']')
                        j++;
                    if (j >= pattern.Length)
                    {
                        sb.Append("\\[");           // unterminated class: literal '['
                    }
                    else
                    {
                        var cls = pattern[(i + 1)..j];
                        if (cls.StartsWith('!'))
                            cls = "^" + cls[1..];
                        sb.Append('[').Append(cls).Append(']');
                        i = j;
                    }
                    break;
                case '\\':
                    // Backslash escapes the next character.
                    if (i + 1 < pattern.Length)
                    {
                        i++;
                        sb.Append(Regex.Escape(pattern[i].ToString()));
                    }
                    else
                    {
                        sb.Append("\\\\");
                    }
                    break;
                default:
                    if ("^$.|+(){}".Contains(c))
                        sb.Append('\\').Append(c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}