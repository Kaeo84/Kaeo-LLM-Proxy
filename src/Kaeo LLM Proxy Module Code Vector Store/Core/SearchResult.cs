

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class SearchResult
{
    public long ChunkId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Similarity { get; set; }
}

