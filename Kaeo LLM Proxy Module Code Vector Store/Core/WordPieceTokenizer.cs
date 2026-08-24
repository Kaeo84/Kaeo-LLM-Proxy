

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _specialTokens = new();

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
        foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string current = word;
            bool isFirst = true;
            while (current.Length > 0)
            {
                bool found = false;
                for (int end = current.Length; end > 0; end--)
                {
                    string candidate = isFirst ? current[..end] : "##" + current[..end];
                    if (_vocab.TryGetValue(candidate, out var id)) { result.Add((id, candidate)); current = current[end..]; found = true; break; }
                }
                if (!found) { int unkId = _vocab.TryGetValue("[UNK]", out var id) ? id : 100; result.Add((unkId, "[UNK]")); break; }
                isFirst = false;
            }
        }
        return result;
    }
}
