using Kaeo.LlmProxy.Core.Modules;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

/// <summary>
/// The MCP tools exposed by the Code Vector Store module: semantic search, single-file
/// indexing, mirror registration/sync, status inspection, and maintenance. Engine state is
/// checked per invocation, and failures are reported as text results so the calling agent
/// always sees what went wrong.
/// </summary>
[McpServerToolType]
internal sealed class CodeVectorTools
{
    private readonly CodeVectorModule _module;
    private readonly McpSessionInfo _session;

    public CodeVectorTools(CodeVectorModule module, McpSessionInfo session)
    {
        _module = module;
        _session = session;
    }

    [McpServerTool(Name = "code_search"), Description(
        "Semantic search across the code vector store: embeds the query and returns the most " +
        "similar indexed code chunks, each with file path, line range, and similarity score. " +
        "Use this instead of text search to find functions, classes, or patterns by meaning. " +
        "Call code_list_collections first when you do not know which collection to search.")]
    public async Task<string> CodeSearch(
        [Description("Natural language description or code snippet to search for.")] string query,
        [Description("Collection to search in. Optional; searches every collection when omitted. Call code_list_collections to discover names.")] string? collection = null,
        [Description("Maximum number of results to return. Optional; defaults to the store's Default Top K setting when omitted.")] int? topK = null,
        [Description("Only include files whose path starts with this prefix, e.g. 'src/Services'. Optional.")] string? pathFilter = null)
    {
        try
        {
            int effectiveTopK = topK is > 0 ? topK.Value : _module.Settings.DefaultTopK;
            var results = await _module.SearchEngine.SearchAsync(query, _module.EmbeddingBackend, collection, effectiveTopK, pathFilter, CancellationToken.None);
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
        catch (Exception ex)
        {
            _module.Activity.Log("error", collection ?? "", $"Search failed: {ex}");
            return $"Search failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "code_index"), Description(
        "Indexes a single file into the vector store: chunks the content, generates " +
        "embeddings, and upserts the chunks. Call this whenever a file is created or modified " +
        "outside a registered mirror so search results stay current; files inside a registered " +
        "git mirror or watched directory are synced automatically and do not need this. The " +
        "file is queued for background indexing, which requires the indexing engine to be " +
        "running.")]
    public string CodeIndex(
        [Description("Name of the collection the file belongs to. Call code_list_collections to discover names.")] string collection,
        [Description("Relative path of the file within the collection, e.g. 'src/Services/OrderService.cs'. Keys the chunks and deduplicates re-indexed files.")] string path,
        [Description("Full text content of the file. Limited by the module's max file size setting.")] string content)
    {
        try
        {
            if (content.Length > _module.Settings.MaxFileSizeKb * 1024) return $"File too large (max {_module.Settings.MaxFileSizeKb} KB)";
            bool queued = _module.Indexer.EnqueueIndexFile(collection, path, content, "agent");
            return queued
                ? $"Queued {path} for indexing in collection '{collection}'"
                : $"Not queued: the indexing engine is not running. Start it from the Code Vector Store config tab and re-submit.";
        }
        catch (Exception ex)
        {
            _module.Activity.Log("error", collection, $"Index failed: {ex}");
            return $"Index failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "code_sync_repo"), Description(
        "Registers a code source for a collection and syncs it into the vector store: either " +
        "a git repository (cloned/pulled on the configured branch) or a local directory/file " +
        "share that is watched for changes. Re-syncs an already registered source on every " +
        "call. Use code_status afterwards to check the last sync result.")]
    public async Task<string> CodeSyncRepo(
        [Description("Name of the collection to register the source for. Created when it does not exist yet.")] string collection,
        [Description("Git remote URL to clone/pull from, e.g. 'https://github.com/org/repo.git'. Ignored when localDirectory is set.")] string? remoteUrl = null,
        [Description("Git branch to track. Optional; defaults to 'main'. Ignored when localDirectory is set.")] string branch = "main",
        [Description("Name of a credential in the host's central credential store used for repository authentication. Optional.")] string? credentialName = null,
        [Description("Only index files whose path starts with this prefix, e.g. 'dotnet' for a single subfolder. Optional; indexes everything when omitted.")] string? pathPrefix = null,
        [Description("Local directory or file-share path to watch instead of a git repository. When set, remoteUrl and branch are ignored and file changes are picked up locally without a push.")] string? localDirectory = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(localDirectory))
            {
                _ = await _module.MirrorManager.RegisterMirrorAsync(collection, localDirectory, branch, credentialName, CancellationToken.None, pathPrefix: pathPrefix, sourceKind: MirrorRegistration.SourceKindDir, sourcePath: localDirectory);
                return $"Local directory mirror '{collection}' ({localDirectory}) registered and synced successfully";
            }
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return "Provide either a git remoteUrl or a localDirectory to sync.";
            _ = await _module.MirrorManager.RegisterMirrorAsync(collection, remoteUrl, branch, credentialName, CancellationToken.None, pathPrefix: pathPrefix);
            return $"Mirror '{collection}' registered and synced successfully";
        }
        catch (Exception ex)
        {
            _module.Activity.Log("error", collection, $"Mirror sync failed: {ex.Message}");
            return $"Mirror sync failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "code_list_collections"), Description(
        "Lists every collection in the vector store with its file count, chunk count, " +
        "embedding model, and vector dimension. Use this to discover what code is indexed and " +
        "which collection name to pass to code_search, code_index, and the other " +
        "collection-scoped tools.")]
    public string CodeListCollections()
    {
        try
        {
            var collections = _module.VectorDb.ListCollections();
            if (collections.Count == 0) return "No collections are indexed yet. Register a mirror (git repo or local directory) to index code.";
            var sb = new StringBuilder();
            sb.AppendLine($"Found {collections.Count} collection(s):");
            foreach (var col in collections)
                sb.AppendLine($"  - {col.Name}: {col.FileCount} files, {col.ChunkCount} chunks (model: {col.EmbeddingModel}, dim: {col.Dimension})");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _module.Activity.Log("error", "", $"List collections failed: {ex.Message}");
            return $"List collections failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "code_status"), Description(
        "Reports the overall state of the Code Vector Store: the active embedding backend and " +
        "model, the vector dimension, every collection with file and chunk counts, and every " +
        "registered mirror (git repository or watched directory) with its source and last " +
        "sync time/status. Use this to confirm the store is healthy before searching or after " +
        "syncing.")]
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
                sb.AppendLine("Mirrors:");
                foreach (var mirror in mirrors)
                {
                    var lastSync = mirror.LastSyncUtc ?? "never";
                    var status = mirror.LastSyncStatus ?? "pending";
                    sb.AppendLine($"  - {mirror.CollectionName}: {mirror.DescribeSource}");
                    sb.AppendLine($"    Last sync: {lastSync} | Status: {status}");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Status check failed: {ex.Message}"; }
    }

    [McpServerTool(Name = "code_remove"), Description(
        "Deletes indexed data from the vector store. With a path prefix, removes only the " +
        "files whose path starts with that prefix; without one, deletes the ENTIRE collection. " +
        "This is destructive and cannot be undone - call it only to purge stale or unwanted " +
        "code context, and re-index or re-sync afterwards if the data is still needed.")]
    public string CodeRemove(
        [Description("Name of the collection to delete from.")] string collection,
        [Description("Path prefix selecting the files to delete, e.g. 'src/Legacy'. Optional but strongly recommended: when omitted, the entire collection is deleted.")] string? pathPrefix = null)
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

    [McpServerTool(Name = "code_reindex"), Description(
        "Queues a full re-embedding of every file in a collection: all chunks are regenerated " +
        "and upserted. Use this after the embedding model or chunking settings changed, or " +
        "when search results look stale. Requires the indexing engine to be running; check " +
        "progress with code_status.")]
    public string CodeReindex(
        [Description("Name of the collection to reindex. Call code_list_collections to discover names.")] string collection)
    {
        try
        {
            _module.Indexer.EnqueueReindex(collection);
            return $"Queued reindex for collection '{collection}'";
        }
        catch (Exception ex) { return $"Reindex failed: {ex.Message}"; }
    }
}
