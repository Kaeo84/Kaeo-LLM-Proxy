

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CollectionInfo
{
    public string Name { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
}
