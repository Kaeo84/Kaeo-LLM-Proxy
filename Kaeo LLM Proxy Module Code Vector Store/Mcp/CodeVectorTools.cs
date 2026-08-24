using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorTools
{
    private readonly CodeVectorModule _module;
    private readonly McpSessionInfo _session;

    public CodeVectorTools(CodeVectorModule module, McpSessionInfo session)
    {
        _module = module;
        _session = session;
    }

    [McpServerTool, Description("Search code semantically using vector embeddings")]
    public async Task<string> CodeSearch(
        [Description("The search query")] string query,
        [Description("Collection name (optional)")] string? collection = null,
        [Description("Number of results to return (default 5)")] int topK = 5,
        [Description("Filter by file path prefix (optional)")] string? pathFilter = null)
    {
        try
        {
            var results = await _module.SearchEngine.SearchAsync(query, _module.EmbeddingBackend, collection, topK, pathFilter, CancellationToken.None);
            if (results.Count == 0) return "No results found.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} result(s):\n");
            foreach (var result in results)
            {
                sb.AppendLine($"ðŸ“„ {result.FilePath} (lines {result.StartLine}-{result.EndLine})");
                sb.AppendLine($"   Similarity: {result.Similarity:P1}");
                sb.AppendLine($"   {result.Text.Replace("\n", "\n   ")}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Search failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Index a file's content into the vector store")]
    public string CodeIndex(
        [Description("Collection name")] string collection,
        [Description("File path")] string path,
        [Description("File content")] string content)
    {
        try
        {
            if (content.Length > _module.Settings.MaxFileSizeKb * 1024) return $"File too large (max {_module.Settings.MaxFileSizeKb} KB)";
            _module.Indexer.EnqueueIndexFile(collection, path, content, "agent");
            return $"Queued {path} for indexing in collection '{collection}'";
        }
        catch (Exception ex) { return $"Index failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Register and sync a git repository mirror")]
    public async Task<string> CodeSyncRepo(
        [Description("Collection name")] string collection,
        [Description("Git remote URL")] string remoteUrl,
        [Description("Branch name (default: main)")] string branch = "main",
        [Description("Credential name for authentication (optional)")] string? credentialName = null,
        [Description("Path prefix to filter indexing, e.g. 'dotnet' for only that subfolder (optional)")] string? pathPrefix = null)
    {
        try
        {
            var mirror = await _module.MirrorManager.RegisterMirrorAsync(collection, remoteUrl, branch, credentialName, CancellationToken.None, pathPrefix: pathPrefix);
            return $"Mirror '{collection}' registered and synced successfully";
        }
        catch (Exception ex) { return $"Mirror sync failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Get status of collections and mirrors")]
    public string CodeStatus()
    {
        try
        {
            var collections = _module.VectorDb.ListCollections();
            var mirrors = _module.Repository.LoadMirrors();
            var sb = new StringBuilder();
            sb.AppendLine("=== Code Vector Store Status ===\n");
            sb.AppendLine($"Backend: {_module.Settings.BackendType}");
            sb.AppendLine($"Model: {_module.EmbeddingBackend.ModelName}");
            sb.AppendLine($"Dimension: {_module.EmbeddingBackend.Dimension}");
            sb.AppendLine();
            if (collections.Count > 0)
            {
                sb.AppendLine("Collections:");
                foreach (var col in collections) sb.AppendLine($"  â€¢ {col.Name}: {col.FileCount} files, {col.ChunkCount} chunks");
                sb.AppendLine();
            }
            if (mirrors.Count > 0)
            {
                sb.AppendLine("Git Mirrors:");
                foreach (var mirror in mirrors)
                {
                    var lastSync = mirror.LastSyncUtc ?? "never";
                    sb.AppendLine($"  â€¢ {mirror.CollectionName}: {mirror.RemoteUrl} [{mirror.Branch}]");
                    sb.AppendLine($"    Last sync: {lastSync}");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Status check failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Delete a collection or path prefix from the vector store")]
    public string CodeRemove(
        [Description("Collection name")] string collection,
        [Description("Path prefix to delete (optional, deletes entire collection if omitted)")] string? pathPrefix = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
            {
                _module.VectorDb.DeleteCollection(collection);
                return $"Deleted collection '{collection}'";
            }
            else
            {
                _module.VectorDb.DeleteFilesByPathPrefix(collection, pathPrefix);
                return $"Deleted files matching '{pathPrefix}' from collection '{collection}'";
            }
        }
        catch (Exception ex) { return $"Remove failed: {ex.Message}"; }
    }

    [McpServerTool, Description("Reindex all files in a collection")]
    public string CodeReindex([Description("Collection name")] string collection)
    {
        try
        {
            _module.Indexer.EnqueueReindex(collection);
            return $"Queued reindex for collection '{collection}'";
        }
        catch (Exception ex) { return $"Reindex failed: {ex.Message}"; }
    }
}
