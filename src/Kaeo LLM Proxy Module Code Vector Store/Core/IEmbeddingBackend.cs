

namespace Kaeo.LlmProxy.Module.CodeVector;

internal interface IEmbeddingBackend : IDisposable
{
    string ModelName { get; }
    int Dimension { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
