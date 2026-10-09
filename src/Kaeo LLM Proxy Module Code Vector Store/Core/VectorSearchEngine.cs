

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class VectorSearchEngine
{
    private readonly CodeVectorDatabase _db;
    public VectorSearchEngine(CodeVectorDatabase db) { _db = db; }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, IEmbeddingBackend backend, string? collection, int topK, string? pathFilter, CancellationToken ct)
    {
        var queryEmbedding = await backend.EmbedAsync(query, ct);
        if (string.IsNullOrWhiteSpace(collection))
        {
            var collections = _db.ListCollections();
            var allResults = new List<SearchResult>();
            foreach (var col in collections) allResults.AddRange(_db.Search(col.Name, queryEmbedding, topK, pathFilter));
            return allResults.OrderByDescending(r => r.Similarity).Take(topK).ToList();
        }
        return _db.Search(collection, queryEmbedding, topK, pathFilter);
    }
}
