using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeChunker
{
    private readonly int _chunkLines;
    private readonly int _overlapLines;
    private readonly int _maxFileSizeBytes;

    public CodeChunker(int chunkLines, int overlapLines, int maxFileSizeBytes)
    {
        _chunkLines = Math.Max(10, chunkLines);
        _overlapLines = Math.Max(0, Math.Min(overlapLines, _chunkLines / 2));
        _maxFileSizeBytes = Math.Max(1024, maxFileSizeBytes);
    }

    public bool IsTooLarge(string content) => Encoding.UTF8.GetByteCount(content) > _maxFileSizeBytes;

    public List<CodeChunk> Chunk(string content)
    {
        var lines = content.Split('\n');
        var chunks = new List<CodeChunk>();
        int idx = 0;
        int start = 0;

        while (start < lines.Length)
        {
            int end = Math.Min(start + _chunkLines, lines.Length);
            if (end < lines.Length)
            {
                for (int probe = end; probe > Math.Max(start + _chunkLines / 2, start) && probe >= end - 5; probe--)
                {
                    if (string.IsNullOrWhiteSpace(lines[probe - 1])) { end = probe; break; }
                }
            }

            var text = string.Join('\n', lines.AsSpan(start, end - start).ToArray());
            chunks.Add(new CodeChunk(idx, start + 1, end, text));

            if (end >= lines.Length) break;
            start = end - _overlapLines;
            if (start <= chunks[^1].StartLine - 1) start = end;
            idx++;
        }
        return chunks;
    }
}
