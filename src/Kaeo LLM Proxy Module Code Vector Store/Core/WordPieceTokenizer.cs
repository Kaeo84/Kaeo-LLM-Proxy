using System.Globalization;
using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _specialTokens = [];

    private WordPieceTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        foreach (var key in vocab.Keys)
            if (key.StartsWith("[") && key.EndsWith("]")) _specialTokens[key] = vocab[key];
    }

    public static WordPieceTokenizer LoadFromFile(string vocabPath)
    {
        var vocab = new Dictionary<string, int>();
        int idx = 0;
        foreach (var line in File.ReadAllLines(vocabPath))
        {
            var token = line.Trim();
            if (token.Length > 0) vocab[token] = idx;
            idx++;
        }
        return new WordPieceTokenizer(vocab);
    }

    public long? GetSpecialTokenId(string token) => _specialTokens.TryGetValue(token, out var id) ? id : null;

    public IReadOnlyList<(int Id, string Token)> Tokenize(string text)
    {
        var result = new List<(int, string)>();
        int unkId = _vocab.TryGetValue("[UNK]", out var uid) ? uid : 100;
        foreach (var rawWord in BasicTokenize(text))
        {
            // Lowercase + strip accents (BERT basic tokenization for uncased models).
            var normalized = rawWord.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            string word = sb.ToString();
            if (word.Length == 0) continue;

            // Cap pathological words so the greedy match stays O(MaxWordLength^2) per word
            // instead of O(L^2) over an entire minified line (H6).
            if (word.Length > MaxWordLength)
            {
                result.Add((unkId, "[UNK]"));
                continue;
            }

            var subTokens = new List<(int, string)>();
            int start = 0;
            bool bad = false;
            while (start < word.Length)
            {
                int end = word.Length;
                int foundId = -1;
                string? foundToken = null;
                while (end > start)
                {
                    string candidate = start == 0 ? word[start..end] : "##" + word[start..end];
                    if (_vocab.TryGetValue(candidate, out var id)) { foundId = id; foundToken = candidate; break; }
                    end--;
                }
                if (foundToken is null) { bad = true; break; }
                subTokens.Add((foundId, foundToken));
                start = end;
            }
            // BERT semantics: an unmatchable word becomes a single [UNK]; other words survive.
            if (bad) result.Add((unkId, "[UNK]"));
            else result.AddRange(subTokens);
        }
        return result;
    }

    private const int MaxWordLength = 200;

    /// <summary>BERT basic tokenization: drop control chars, split on whitespace and punctuation.</summary>
    private static IEnumerable<string> BasicTokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\0' || ch == '\uFFFD') continue;
            if (IsWhitespace(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            if (IsControl(ch)) continue;
            if (IsPunctuation(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return ch.ToString();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool IsWhitespace(char ch)
        => ch is ' ' or '\t' or '\n' or '\r'
        || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;

    private static bool IsControl(char ch)
        => CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Control;

    private static bool IsPunctuation(char ch)
    {
        // ASCII punctuation always splits, matching BERT's BasicTokenizer.
        if (ch is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~'))
            return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }
}
