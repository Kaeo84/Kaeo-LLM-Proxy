using Kaeo.LlmProxy.Core.Modules;
using System.Data.Common;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorRepository
{
    private readonly IModuleDatabase _db;
    public CodeVectorRepository(IModuleDatabase db) { _db = db ?? throw new ArgumentNullException(nameof(db)); }

    public CodeVectorSettings LoadSettings()
    {
        var s = new CodeVectorSettings();
        var rows = _db.Query("SELECT key, value FROM mcp_codevector_settings",
            r => (Key: r.GetString(0), Value: r.GetString(1)));
        foreach (var (k, v) in rows)
        {
            switch (k)
            {
                case "backend_type": if (Enum.TryParse<BackendType>(v, true, out var bt)) s.BackendType = bt; break;
                case "remote_url": s.RemoteUrl = v; break;
                case "remote_model": s.RemoteModel = v; break;
                case "remote_credential": s.RemoteCredentialName = v; break;
                case "remote_timeout": if (int.TryParse(v, out var rt)) s.RemoteTimeoutSeconds = rt; break;
                case "onnx_folder": s.OnnxModelFolder = v; break;
                case "onnx_max_seq": if (int.TryParse(v, out var ms)) s.OnnxMaxSequenceLength = ms; break;
                case "onnx_threads": if (int.TryParse(v, out var mt)) s.OnnxMaxThreads = mt; break;
                case "chunk_lines": if (int.TryParse(v, out var cl)) s.ChunkLines = cl; break;
                case "chunk_overlap": if (int.TryParse(v, out var co)) s.ChunkOverlapLines = co; break;
                case "max_file_kb": if (int.TryParse(v, out var mf)) s.MaxFileSizeKb = mf; break;
                case "default_top_k": if (int.TryParse(v, out var tk)) s.DefaultTopK = tk; break;
                case "default_collection": s.DefaultCollection = v; break;
                case "search_enabled": s.SearchEnabled = v == "1"; break;
                case "index_enabled": s.IndexEnabled = v == "1"; break;
                case "sync_enabled": s.SyncRepoEnabled = v == "1"; break;
                case "status_enabled": s.StatusEnabled = v == "1"; break;
                case "remove_enabled": s.RemoveEnabled = v == "1"; break;
                case "reindex_enabled": s.ReindexEnabled = v == "1"; break;
                case "sync_interval": if (int.TryParse(v, out var si)) s.GitSyncIntervalMinutes = si; break;
                case "log_level": if (Enum.TryParse<CodeVectorMcpLogLevel>(v, true, out var ll)) s.McpLogLevel = ll; break;
                case "remote_parallelism": if (int.TryParse(v, out var rp)) s.RemoteParallelism = rp; break;
                case "vector_database_path": s.VectorDatabasePath = v; break;
            }
        }
        return s;
    }

    public void SaveSetting(string key, string value)
    {
        _db.Execute("INSERT INTO mcp_codevector_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            cmd => { AddParam(cmd, "$key", key); AddParam(cmd, "$value", value); });
    }

    public IReadOnlyList<MirrorRegistration> LoadMirrors()
    {
        return _db.Query(
            "SELECT id, collection_name, remote_url, branch, credential_name, mirror_path, path_prefix, last_sync_utc, last_sync_status FROM mcp_codevector_repos",
            r => new MirrorRegistration
            {
                Id = r.GetInt32(0),
                CollectionName = r.GetString(1),
                RemoteUrl = r.GetString(2),
                Branch = r.GetString(3),
                CredentialName = r.IsDBNull(4) ? null : r.GetString(4),
                MirrorPath = r.IsDBNull(5) ? null : r.GetString(5),
                PathPrefix = r.IsDBNull(6) ? null : r.GetString(6),
                LastSyncUtc = r.IsDBNull(7) ? null : r.GetString(7),
                LastSyncStatus = r.IsDBNull(8) ? null : r.GetString(8),
            });
    }

    public MirrorRegistration UpsertMirror(string collectionName, string remoteUrl, string branch, string? credentialName, string? mirrorPath = null, string? pathPrefix = null)
    {
        _db.Execute(
            "INSERT INTO mcp_codevector_repos (collection_name, remote_url, branch, credential_name, mirror_path, path_prefix) VALUES ($col, $url, $branch, $cred, $path, $prefix) " +
            "ON CONFLICT(collection_name) DO UPDATE SET remote_url = excluded.remote_url, branch = excluded.branch, credential_name = excluded.credential_name, mirror_path = excluded.mirror_path, path_prefix = excluded.path_prefix",
            cmd => { AddParam(cmd, "$col", collectionName); AddParam(cmd, "$url", remoteUrl); AddParam(cmd, "$branch", branch); AddParam(cmd, "$cred", (object?)credentialName ?? DBNull.Value); AddParam(cmd, "$path", (object?)mirrorPath ?? DBNull.Value); AddParam(cmd, "$prefix", (object?)pathPrefix ?? DBNull.Value); });
        return LoadMirrors().First(m => m.CollectionName == collectionName);
    }

    public void UpdateMirrorSync(int id, string? lastSyncUtc, string? lastSyncStatus)
    {
        _db.Execute("UPDATE mcp_codevector_repos SET last_sync_utc = $ts, last_sync_status = $st WHERE id = $id",
            cmd => { AddParam(cmd, "$ts", (object?)lastSyncUtc ?? DBNull.Value); AddParam(cmd, "$st", (object?)lastSyncStatus ?? DBNull.Value); AddParam(cmd, "$id", id); });
    }

    public void DeleteMirror(string collectionName)
    {
        _db.Execute("DELETE FROM mcp_codevector_repos WHERE collection_name = $col",
            cmd => AddParam(cmd, "$col", collectionName));
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Creates the module's shared tables in the host database and applies additive
    /// column migrations for older databases.
    /// </summary>
    internal static void ApplySharedSchema(IModuleDatabase db)
    {
        db.ExecuteSchemaScript(SharedSchema);
        var columns = db.Query("PRAGMA table_info(mcp_codevector_repos)", r => r.GetString(1));
        if (!columns.Contains("mirror_path", StringComparer.OrdinalIgnoreCase))
            db.Execute("ALTER TABLE mcp_codevector_repos ADD COLUMN mirror_path TEXT NULL", _ => { });
        if (!columns.Contains("path_prefix", StringComparer.OrdinalIgnoreCase))
            db.Execute("ALTER TABLE mcp_codevector_repos ADD COLUMN path_prefix TEXT NULL", _ => { });
    }

    private const string SharedSchema = """
        CREATE TABLE IF NOT EXISTS mcp_codevector_settings (
        key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS mcp_codevector_repos (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        collection_name TEXT NOT NULL UNIQUE,
        remote_url TEXT NOT NULL,
        branch TEXT NOT NULL DEFAULT 'main',
        credential_name TEXT NULL,
        mirror_path TEXT NULL,
        path_prefix TEXT NULL,
        last_sync_utc TEXT NULL,
        last_sync_status TEXT NULL);
        """;
}
