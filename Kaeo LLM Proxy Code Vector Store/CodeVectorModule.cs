using Kaeo.LlmProxy.Modules;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ModelContextProtocol.Server;
using Serilog;
using System.ComponentModel;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kaeo.LlmProxy.CodeVector;

public sealed class CodeVectorModule : IKaeoModule, IMcpToolModule, IRunnableModule, IHelpModule
{
	public const string Version = "1.0.0";

	private ModuleContext? _context;
	private CodeVectorRepository? _repository;
	private CodeVectorDatabase? _vectorDb;
	private CodeVectorSettings _settings = new();
	private IEmbeddingBackend? _embeddingBackend;
	private IndexingEngine? _indexingEngine;
	private GitMirrorManager? _mirrorManager;
	private VectorSearchEngine? _searchEngine;
	private CodeVectorActivityLogger? _activity;

	public string Id => "kaeo.codevector";
	public string Name => "Code Vector Store";
	string IKaeoModule.Version => Version;
	public string Description => "Embeddings + vector store for code search via MCP tools. "
		+ "Supports remote HTTP and local ONNX CPU embedding backends, "
		+ "agent-push indexing, and server-side git mirrors with LibGit2Sharp.";

	internal CodeVectorRepository Repository =>
		_repository ?? throw new InvalidOperationException("Module not initialized.");
	internal CodeVectorDatabase VectorDb =>
		_vectorDb ?? throw new InvalidOperationException("Module not initialized.");
	internal IEmbeddingBackend EmbeddingBackend =>
		_embeddingBackend ?? throw new InvalidOperationException("Embedding backend not initialized.");
	internal IndexingEngine Indexer =>
		_indexingEngine ?? throw new InvalidOperationException("Module not initialized.");
	internal GitMirrorManager MirrorManager =>
		_mirrorManager ?? throw new InvalidOperationException("Module not initialized.");
	internal VectorSearchEngine SearchEngine =>
		_searchEngine ?? throw new InvalidOperationException("Module not initialized.");
	internal ISecretProvider Secrets =>
		_context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");
	internal HostInfo Host =>
		_context?.Host ?? throw new InvalidOperationException("Module not initialized.");
	internal CodeVectorSettings Settings => _settings;

	public void Initialize(ModuleContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		_context = context;
		ApplySharedSchema(context.Database);
		_repository = new CodeVectorRepository(context.Database);
		_settings = _repository.LoadSettings();

		string moduleDataDir = Path.Combine(context.DataDirectory, "codevector");
		Directory.CreateDirectory(moduleDataDir);

		string vectorDbPath = Path.Combine(moduleDataDir, "codevector.db");
		_vectorDb = new CodeVectorDatabase(vectorDbPath);
		_activity = new CodeVectorActivityLogger(context.ActivityLog, () => _settings.McpLogLevel);
		_embeddingBackend = CreateEmbeddingBackend(_settings, context.Secrets, context.Host);
		_indexingEngine = new IndexingEngine(_vectorDb, _embeddingBackend, _settings, _activity);
		_mirrorManager = new GitMirrorManager(moduleDataDir, _repository, _indexingEngine, _settings, _activity, context.Secrets);
		_searchEngine = new VectorSearchEngine(_vectorDb);
	}

	public System.Windows.Forms.TabPage CreateConfigPage() => new CodeVectorConfigPage(this);
	public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) => [new CodeVectorTools(this, session)];
	public bool IsRunning => _indexingEngine?.IsRunning == true;
	public event EventHandler<string>? StatusChanged;

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		_indexingEngine?.Start();
		_mirrorManager?.StartTimer();
		StatusChanged?.Invoke(this, "Running");
		return Task.CompletedTask;
	}

	public async Task StopAsync()
	{
		_mirrorManager?.StopTimer();
		if (_indexingEngine is { IsRunning: true })
			await _indexingEngine.StopAsync();
		_embeddingBackend?.Dispose();
		StatusChanged?.Invoke(this, "Stopped");
	}

	public System.Windows.Forms.TabPage CreateHelpPage()
	{
		var page = new System.Windows.Forms.TabPage { Text = "Code Vector Store", Padding = new System.Windows.Forms.Padding(8) };
		var body = new System.Windows.Forms.TextBox
		{
			Multiline = true, ReadOnly = true, WordWrap = true,
			ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
			Dock = System.Windows.Forms.DockStyle.Fill,
			BorderStyle = System.Windows.Forms.BorderStyle.None,
			BackColor = System.Drawing.SystemColors.Window,
			Text = HelpText,
		};
		page.Controls.Add(body);
		return page;
	}

	private static IEmbeddingBackend CreateEmbeddingBackend(CodeVectorSettings s, ISecretProvider secrets, HostInfo host)
	{
		return s.BackendType switch
		{
			BackendType.Onnx => new OnnxEmbeddingBackend(s.OnnxModelFolder, s.OnnxMaxSequenceLength, s.OnnxMaxThreads),
			BackendType.Remote => new RemoteEmbeddingBackend(s, secrets, host),
			_ => throw new InvalidOperationException($"Unsupported backend: {s.BackendType}"),
		};
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
			last_sync_utc TEXT NULL,
			last_sync_status TEXT NULL);
		""";

	private static void ApplySharedSchema(IModuleDatabase db) => db.ExecuteSchemaScript(SharedSchema);

	private const string HelpText = """
		CODE VECTOR STORE MODULE
		Provides embeddings and a vector store for code search via MCP tools.
		TOOLS: code_search, code_index, code_sync_repo, code_status, code_remove, code_reindex
		BACKENDS: Remote (HTTP /v1/embeddings), Local ONNX (CPU, model.onnx + vocab.txt)
		SYNC: Agent push via code_index, Git mirror via LibGit2Sharp with periodic pull.
		""";
}

internal enum BackendType { Remote, Onnx }
internal enum CodeVectorMcpLogLevel { None, Connectivity, Full }

internal sealed class CodeVectorSettings
{
	public BackendType BackendType { get; set; } = BackendType.Remote;
	public string RemoteUrl { get; set; } = string.Empty;
	public string RemoteModel { get; set; } = string.Empty;
	public string RemoteCredentialName { get; set; } = string.Empty;
	public int RemoteTimeoutSeconds { get; set; } = 60;
	public string OnnxModelFolder { get; set; } = string.Empty;
	public int OnnxMaxSequenceLength { get; set; } = 512;
	public int OnnxMaxThreads { get; set; } = 4;
	public int ChunkLines { get; set; } = 60;
	public int ChunkOverlapLines { get; set; } = 10;
	public int MaxFileSizeKb { get; set; } = 256;
	public int DefaultTopK { get; set; } = 8;
	public string DefaultCollection { get; set; } = "default";
	public bool SearchEnabled { get; set; } = true;
	public bool IndexEnabled { get; set; } = true;
	public bool SyncRepoEnabled { get; set; } = true;
	public bool StatusEnabled { get; set; } = true;
	public bool RemoveEnabled { get; set; } = true;
	public bool ReindexEnabled { get; set; } = true;
	public int GitSyncIntervalMinutes { get; set; } = 15;
	public CodeVectorMcpLogLevel McpLogLevel { get; set; } = CodeVectorMcpLogLevel.Connectivity;
}
// ── Repository ─────────────────────────────────────────────────────────────

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
            "SELECT id, collection_name, remote_url, branch, credential_name, last_sync_utc, last_sync_status FROM mcp_codevector_repos",
            r => new MirrorRegistration
            {
                Id = r.GetInt32(0),
                CollectionName = r.GetString(1),
                RemoteUrl = r.GetString(2),
                Branch = r.GetString(3),
                CredentialName = r.IsDBNull(4) ? null : r.GetString(4),
                LastSyncUtc = r.IsDBNull(5) ? null : r.GetString(5),
                LastSyncStatus = r.IsDBNull(6) ? null : r.GetString(6),
            });
    }

    public MirrorRegistration UpsertMirror(string collectionName, string remoteUrl, string branch, string? credentialName)
    {
        _db.Execute(
            "INSERT INTO mcp_codevector_repos (collection_name, remote_url, branch, credential_name) VALUES ($col, $url, $branch, $cred) " +
            "ON CONFLICT(collection_name) DO UPDATE SET remote_url = excluded.remote_url, branch = excluded.branch, credential_name = excluded.credential_name",
            cmd => { AddParam(cmd, "$col", collectionName); AddParam(cmd, "$url", remoteUrl); AddParam(cmd, "$branch", branch); AddParam(cmd, "$cred", (object?)credentialName ?? DBNull.Value); });
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
}

internal sealed class MirrorRegistration
{
    public int Id { get; set; }
    public string CollectionName { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? CredentialName { get; set; }
    public string? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
}
// ── Module-owned Vector Database ───────────────────────────────────────────

internal sealed class CodeVectorDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    private const string VectorSchema = """
        CREATE TABLE IF NOT EXISTS codevector_collections (
            name TEXT PRIMARY KEY,
            embedding_model TEXT NOT NULL,
            dimension INTEGER NOT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS codevector_files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            collection_name TEXT NOT NULL,
            path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'agent',
            chunk_count INTEGER NOT NULL DEFAULT 0,
            indexed_utc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_files_collection_path ON codevector_files (collection_name, path);
        CREATE TABLE IF NOT EXISTS codevector_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            file_id INTEGER NOT NULL,
            chunk_index INTEGER NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            text TEXT NOT NULL,
            embedding BLOB NOT NULL,
            FOREIGN KEY (file_id) REFERENCES codevector_files(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_chunks_file_id ON codevector_chunks (file_id);
        """;

    public CodeVectorDatabase(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        using var schema = _connection.CreateCommand();
        schema.CommandText = VectorSchema;
        schema.ExecuteNonQuery();
    }

    public long GetOrCreateCollection(string name, string model, int dimension)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM codevector_collections WHERE name = $name";
            AddParam(cmd, "$name", name);
            var result = cmd.ExecuteScalar();
            if (result is not null) return Convert.ToInt64(result);

            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO codevector_collections (name, embedding_model, dimension, created_utc) VALUES ($name, $model, $dim, $created)";
            AddParam(insert, "$name", name);
            AddParam(insert, "$model", model);
            AddParam(insert, "$dim", dimension);
            AddParam(insert, "$created", DateTime.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
            using var lastId = _connection.CreateCommand();
            lastId.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(lastId.ExecuteScalar());
        }
    }

    public string? GetFileHash(string collectionName, string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM codevector_files WHERE collection_name = $col AND path = $path";
            AddParam(cmd, "$col", collectionName);
            AddParam(cmd, "$path", path);
            return cmd.ExecuteScalar() as string;
        }
    }

    public long UpsertFile(string collectionName, string path, string sha256, string source, int chunkCount)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO codevector_files (collection_name, path, sha256, source, chunk_count, indexed_utc) " +
                "VALUES ($col, $path, $hash, $src, $cnt, $ts) " +
                "ON CONFLICT(collection_name, path) DO UPDATE SET sha256 = excluded.sha256, source = excluded.source, chunk_count = excluded.chunk_count, indexed_utc = excluded.indexed_utc " +
                "RETURNING id";
            AddParam(cmd, "$col", collectionName);
            AddParam(cmd, "$path", path);
            AddParam(cmd, "$hash", sha256);
            AddParam(cmd, "$src", source);
            AddParam(cmd, "$cnt", chunkCount);
            AddParam(cmd, "$ts", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void DeleteFileChunks(long fileId)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM codevector_chunks WHERE file_id = $fid";
            AddParam(cmd, "$fid", fileId);
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertChunk(long fileId, int chunkIndex, int startLine, int endLine, string text, float[] embedding)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO codevector_chunks (file_id, chunk_index, start_line, end_line, text, embedding) VALUES ($fid, $idx, $sl, $el, $txt, $emb)";
            AddParam(cmd, "$fid", fileId);
            AddParam(cmd, "$idx", chunkIndex);
            AddParam(cmd, "$sl", startLine);
            AddParam(cmd, "$el", endLine);
            AddParam(cmd, "$txt", text);
            AddParam(cmd, "$emb", EmbeddingToBlob(embedding));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SearchResult> Search(string collectionName, float[] queryEmbedding, int topK, string? pathFilter = null)
    {
        lock (_lock)
        {
            var results = new List<SearchResult>();
            using var cmd = _connection.CreateCommand();
            string sql = "SELECT c.id, c.text, c.start_line, c.end_line, c.embedding, f.path FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col";
            if (!string.IsNullOrEmpty(pathFilter)) sql += " AND f.path LIKE $pf";
            cmd.CommandText = sql;
            AddParam(cmd, "$col", collectionName);
            if (!string.IsNullOrEmpty(pathFilter)) AddParam(cmd, "$pf", "%" + pathFilter + "%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var emb = BlobToEmbedding((byte[])reader.GetValue(4));
                var sim = CosineSimilarity(queryEmbedding, emb);
                results.Add(new SearchResult { ChunkId = reader.GetInt64(0), Text = reader.GetString(1), StartLine = reader.GetInt32(2), EndLine = reader.GetInt32(3), FilePath = reader.GetString(5), Similarity = sim });
            }
            return results.OrderByDescending(r => r.Similarity).Take(topK).ToList();
        }
    }

    public IReadOnlyList<CollectionInfo> ListCollections()
    {
        lock (_lock)
        {
            var result = new List<CollectionInfo>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT c.name, c.embedding_model, c.dimension, c.created_utc, COUNT(DISTINCT f.id) as file_count, COALESCE(SUM(f.chunk_count), 0) as chunk_count FROM codevector_collections c LEFT JOIN codevector_files f ON f.collection_name = c.name GROUP BY c.name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new CollectionInfo { Name = reader.GetString(0), EmbeddingModel = reader.GetString(1), Dimension = reader.GetInt32(2), CreatedUtc = reader.GetString(3), FileCount = reader.GetInt32(4), ChunkCount = reader.GetInt32(5) });
            return result;
        }
    }

    public void DeleteCollection(string name)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM codevector_collections WHERE name = $name";
            AddParam(cmd, "$name", name);
            cmd.ExecuteNonQuery();
            using var cmd2 = _connection.CreateCommand();
            cmd2.CommandText = "DELETE FROM codevector_chunks WHERE file_id IN (SELECT id FROM codevector_files WHERE collection_name = $name)";
            AddParam(cmd2, "$name", name);
            cmd2.ExecuteNonQuery();
            using var cmd3 = _connection.CreateCommand();
            cmd3.CommandText = "DELETE FROM codevector_files WHERE collection_name = $name";
            AddParam(cmd3, "$name", name);
            cmd3.ExecuteNonQuery();
        }
    }

    public void DeleteFilesByPathPrefix(string collectionName, string pathPrefix)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM codevector_chunks WHERE file_id IN (SELECT id FROM codevector_files WHERE collection_name = $col AND path LIKE $pf)";
            AddParam(cmd, "$col", collectionName);
            AddParam(cmd, "$pf", pathPrefix + "%");
            cmd.ExecuteNonQuery();
            using var cmd2 = _connection.CreateCommand();
            cmd2.CommandText = "DELETE FROM codevector_files WHERE collection_name = $col AND path LIKE $pf";
            AddParam(cmd2, "$col", collectionName);
            AddParam(cmd2, "$pf", pathPrefix + "%");
            cmd2.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<string> ListFilePaths(string collectionName, string source)
    {
        lock (_lock)
        {
            var result = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path FROM codevector_files WHERE collection_name = $col AND source = $src";
            AddParam(cmd, "$col", collectionName);
            AddParam(cmd, "$src", source);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    private static byte[] EmbeddingToBlob(float[] embedding) { var bytes = new byte[embedding.Length * sizeof(float)]; Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length); return bytes; }
    private static float[] BlobToEmbedding(byte[] blob) { var floats = new float[blob.Length / sizeof(float)]; Buffer.BlockCopy(blob, 0, floats, 0, blob.Length); return floats; }
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; normA += a[i] * a[i]; normB += b[i] * b[i]; }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }
    private static void AddParam(DbCommand cmd, string name, object? value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p); }

    public void Dispose() => _connection.Dispose();
}

internal sealed class CollectionInfo
{
    public string Name { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
}

internal sealed class SearchResult
{
    public long ChunkId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Similarity { get; set; }
}

// ── Chunker ────────────────────────────────────────────────────────────────

internal sealed record CodeChunk(int Index, int StartLine, int EndLine, string Text);

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
// ── Embedding Backends ─────────────────────────────────────────────────────

internal interface IEmbeddingBackend : IDisposable
{
    string ModelName { get; }
    int Dimension { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

internal sealed class RemoteEmbeddingBackend : IEmbeddingBackend
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly string _model;
    private int _dimension;
    private string _modelName;

    public RemoteEmbeddingBackend(CodeVectorSettings settings, ISecretProvider secrets, HostInfo host)
    {
        _url = string.IsNullOrWhiteSpace(settings.RemoteUrl)
            ? $"http://{host.DisplayHost}:{host.ListenPort}/v1/embeddings"
            : settings.RemoteUrl;
        _model = string.IsNullOrWhiteSpace(settings.RemoteModel) ? "default" : settings.RemoteModel;
        _modelName = _model;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.RemoteTimeoutSeconds) };

        if (!string.IsNullOrWhiteSpace(settings.RemoteCredentialName))
        {
            var secret = secrets.ResolveSecret(settings.RemoteCredentialName);
            if (!string.IsNullOrWhiteSpace(secret))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    public string ModelName => _modelName;
    public int Dimension => _dimension;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results.Length > 0 ? results[0] : [];
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var requestBody = new { model = _model, input = texts.ToArray() };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_url, content, ct);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        var data = doc.RootElement.GetProperty("data");
        var results = new float[data.GetArrayLength()][];
        int idx = 0;
        foreach (var item in data.EnumerateArray())
        {
            var embedding = item.GetProperty("embedding");
            var vec = new float[embedding.GetArrayLength()];
            int i = 0;
            foreach (var val in embedding.EnumerateArray()) vec[i++] = val.GetSingle();
            results[idx++] = vec;
        }
        if (_dimension == 0 && results.Length > 0) _dimension = results[0].Length;
        return results;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed class OnnxEmbeddingBackend : IEmbeddingBackend
{
    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly int _maxSeqLen;
    private int _dimension;
    private string _modelName = "onnx";

    public OnnxEmbeddingBackend(string modelFolder, int maxSeqLen, int maxThreads)
    {
        _maxSeqLen = Math.Max(32, maxSeqLen);
        if (string.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder)) return;
        var modelPath = Path.Combine(modelFolder, "model.onnx");
        var vocabPath = Path.Combine(modelFolder, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath)) return;
        _modelName = Path.GetFileName(modelFolder);
        var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, maxThreads), GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _tokenizer = WordPieceTokenizer.LoadFromFile(vocabPath);
    }

    public string ModelName => _modelName;
    public int Dimension => _dimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = EmbedBatchAsync([text], ct);
        return results.ContinueWith(t => t.Result.Length > 0 ? t.Result[0] : [], ct);
    }

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (_session is null || _tokenizer is null) throw new InvalidOperationException("ONNX backend not initialized.");
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) { ct.ThrowIfCancellationRequested(); results[i] = EmbedSingle(texts[i]); }
        return Task.FromResult(results);
    }

    private float[] EmbedSingle(string text)
    {
        var tokenIds = _tokenizer!.Tokenize(text).Select(t => (long)t.Id).ToArray();
        if (tokenIds.Length > _maxSeqLen - 2) tokenIds = tokenIds.Take(_maxSeqLen - 2).ToArray();
        int seqLen = tokenIds.Length + 2;
        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];
        var tokenTypeIds = new long[seqLen];
        inputIds[0] = _tokenizer!.GetSpecialTokenId("[CLS]") ?? 101;
        for (int i = 0; i < tokenIds.Length; i++) { inputIds[i + 1] = tokenIds[i]; attentionMask[i + 1] = 1; }
        inputIds[seqLen - 1] = _tokenizer.GetSpecialTokenId("[SEP]") ?? 102;
        attentionMask[0] = 1;
        attentionMask[seqLen - 1] = 1;

        var inputMeta = _session!.InputMetadata;
        var inputs = new List<NamedOnnxValue>();
        foreach (var kv in inputMeta)
        {
            string name = kv.Key;
            long[] data = name switch { "input_ids" => inputIds, "attention_mask" => attentionMask, "token_type_ids" => tokenTypeIds, _ => attentionMask };
            if (kv.Value.ElementType == typeof(int))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data.Select(d => (int)d).ToArray(), [1, seqLen])));
            else
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, [1, seqLen])));
        }
        using var outputs = _session.Run(inputs);
        var outputTensor = outputs.First().AsTensor<float>();
        var shape = outputTensor.Dimensions;
        float[] embedding;
        if (shape.Length == 3)
        {
            int hidden = shape[2];
            embedding = new float[hidden];
            int count = 0;
            for (int s = 0; s < seqLen; s++)
            {
                if (attentionMask[s] == 0) continue;
                for (int h = 0; h < hidden; h++) embedding[h] += outputTensor[0, s, h];
                count++;
            }
            if (count > 0) for (int h = 0; h < hidden; h++) embedding[h] /= count;
        }
        else if (shape.Length == 2)
        {
            int hidden = shape[1];
            embedding = new float[hidden];
            for (int h = 0; h < hidden; h++) embedding[h] = outputTensor[0, h];
        }
        else { embedding = []; }

        float norm = 0f;
        for (int i = 0; i < embedding.Length; i++) norm += embedding[i] * embedding[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < embedding.Length; i++) embedding[i] /= norm;
        if (_dimension == 0) _dimension = embedding.Length;
        return embedding;
    }

    public void Dispose() => _session?.Dispose();
}

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
// ── Indexing Engine ────────────────────────────────────────────────────────

internal sealed class IndexingEngine
{
    private readonly CodeVectorDatabase _db;
    private readonly IEmbeddingBackend _backend;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly Channel<IndexJob> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    public IndexingEngine(CodeVectorDatabase db, IEmbeddingBackend backend, CodeVectorSettings settings, CodeVectorActivityLogger? activity)
    {
        _db = db;
        _backend = backend;
        _settings = settings;
        _activity = activity;
        _queue = Channel.CreateUnbounded<IndexJob>(new UnboundedChannelOptions { SingleReader = true });
    }

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start()
    {
        if (_worker is not null) return;
        _worker = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
    }

    public void EnqueueIndexFile(string collection, string path, string content, string source = "agent")
    {
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.IndexFile, Collection = collection, Path = path, Content = content, Source = source });
    }

    public void EnqueueDeletePath(string collection, string pathPrefix)
    {
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeletePath, Collection = collection, Path = pathPrefix });
    }

    public void EnqueueDeleteCollection(string collection)
    {
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeleteCollection, Collection = collection });
    }

    public void EnqueueReindex(string collection)
    {
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.Reindex, Collection = collection });
    }

    public int QueueDepth => _queue.Reader.Count;

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                switch (job.Type)
                {
                    case JobType.IndexFile: await IndexFileAsync(job.Collection!, job.Path!, job.Content!, job.Source!, ct); break;
                    case JobType.DeletePath: _db.DeleteFilesByPathPrefix(job.Collection!, job.Path!); break;
                    case JobType.DeleteCollection: _db.DeleteCollection(job.Collection!); break;
                    case JobType.Reindex: _activity?.Log("reindex", job.Collection!, "Reindex requested"); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _activity?.Log("error", job.Collection ?? "", $"Indexing failed: {ex.Message}");
            }
        }
    }

    private async Task IndexFileAsync(string collection, string path, string content, string source, CancellationToken ct)
    {
        var chunker = new CodeChunker(_settings.ChunkLines, _settings.ChunkOverlapLines, _settings.MaxFileSizeKb * 1024);
        if (chunker.IsTooLarge(content)) { _activity?.Log("skip", $"{collection}:{path}", "File too large"); return; }

        var hash = ComputeSha256(content);
        var existingHash = _db.GetFileHash(collection, path);
        if (existingHash == hash) { _activity?.Log("skip", $"{collection}:{path}", "Unchanged"); return; }

        var chunks = chunker.Chunk(content);
        if (chunks.Count == 0) return;

        _db.GetOrCreateCollection(collection, _backend.ModelName, _backend.Dimension);
        var fileId = _db.UpsertFile(collection, path, hash, source, chunks.Count);
        _db.DeleteFileChunks(fileId);

        const int batchSize = 16;
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            float[][] embeddings;
            try { embeddings = await _backend.EmbedBatchAsync(texts, ct); }
            catch (Exception ex) { _activity?.Log("error", $"{collection}:{path}", $"Embedding failed: {ex.Message}"); return; }
            for (int j = 0; j < batch.Count; j++)
            {
                var chunk = batch[j];
                var embedding = j < embeddings.Length ? embeddings[j] : [];
                _db.InsertChunk(fileId, chunk.Index, chunk.StartLine, chunk.EndLine, chunk.Text, embedding);
            }
        }
        _activity?.Log("index", $"{collection}:{path}", $"Indexed {chunks.Count} chunks");
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private enum JobType { IndexFile, DeletePath, DeleteCollection, Reindex }
    private sealed class IndexJob { public JobType Type { get; init; } public string? Collection { get; init; } public string? Path { get; init; } public string? Content { get; init; } public string? Source { get; init; } }
}

// ── Git Mirror Manager ─────────────────────────────────────────────────────

internal sealed class GitMirrorManager
{
    private readonly string _mirrorRoot;
    private readonly CodeVectorRepository _repository;
    private readonly IndexingEngine _indexer;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly ISecretProvider _secrets;
    private System.Threading.Timer? _timer;

    public GitMirrorManager(string moduleDataDir, CodeVectorRepository repository, IndexingEngine indexer, CodeVectorSettings settings, CodeVectorActivityLogger? activity, ISecretProvider secrets)
    {
        _mirrorRoot = Path.Combine(moduleDataDir, "mirrors");
        _repository = repository;
        _indexer = indexer;
        _settings = settings;
        _activity = activity;
        _secrets = secrets;
        Directory.CreateDirectory(_mirrorRoot);
    }

    public void StartTimer()
    {
        if (_timer is not null || _settings.GitSyncIntervalMinutes <= 0) return;
        var interval = TimeSpan.FromMinutes(_settings.GitSyncIntervalMinutes);
        _timer = new System.Threading.Timer(_ => _ = SyncAllMirrorsAsync(), null, interval, interval);
    }

    public void StopTimer() { _timer?.Dispose(); _timer = null; }

    public async Task<MirrorRegistration> RegisterMirrorAsync(string collectionName, string remoteUrl, string branch, string? credentialName, CancellationToken ct)
    {
        var mirror = _repository.UpsertMirror(collectionName, remoteUrl, branch, credentialName);
        await SyncMirrorAsync(mirror, ct);
        return mirror;
    }

    public async Task SyncMirrorAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        try
        {
            var mirrorPath = Path.Combine(_mirrorRoot, mirror.CollectionName);
            if (!Repository.IsValid(mirrorPath))
            {
                var cloneOptions = new CloneOptions { BranchName = mirror.Branch, FetchOptions = { TagFetchMode = TagFetchMode.None } };
                if (!string.IsNullOrWhiteSpace(mirror.CredentialName))
                {
                    var cred = _secrets.ResolveCredential(mirror.CredentialName);
                    if (cred is not null) cloneOptions.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = cred.Username ?? "git", Password = cred.Secret };
                }
                Repository.Clone(mirror.RemoteUrl, mirrorPath, cloneOptions);
                _activity?.Log("clone", mirror.CollectionName, $"Cloned {mirror.RemoteUrl}");
            }
            else
            {
                using var repo = new Repository(mirrorPath);
                var fetchOptions = new FetchOptions { TagFetchMode = TagFetchMode.None };
                if (!string.IsNullOrWhiteSpace(mirror.CredentialName))
                {
                    var cred = _secrets.ResolveCredential(mirror.CredentialName);
                    if (cred is not null) fetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = cred.Username ?? "git", Password = cred.Secret };
                }
                Commands.Fetch(repo, "origin", [mirror.Branch], fetchOptions, "fetch");
                var branchRef = repo.Branches[$"origin/{mirror.Branch}"];
                if (branchRef is not null)
                {
                    var localBranch = repo.Branches[mirror.Branch];
                    if (localBranch is not null) repo.Refs.UpdateTarget(localBranch.Reference, branchRef.Tip.Id);
                }
                _activity?.Log("fetch", mirror.CollectionName, "Fetched latest");
            }
            await IndexMirrorFilesAsync(mirror, mirrorPath, ct);
            _repository.UpdateMirrorSync(mirror.Id, DateTime.UtcNow.ToString("o"), "success");
        }
        catch (Exception ex) when (ex is IOException or LibGit2SharpException)
        {
            _activity?.Log("error", mirror.CollectionName, $"Mirror sync failed: {ex.Message}");
            _repository.UpdateMirrorSync(mirror.Id, null, $"failed: {ex.Message}");
        }
    }

    private async Task IndexMirrorFilesAsync(MirrorRegistration mirror, string mirrorPath, CancellationToken ct)
    {
        using var repo = new Repository(mirrorPath);
        var workDir = repo.Info.WorkingDirectory;
        var trackedFiles = repo.RetrieveStatus(new StatusOptions()).Where(s => s.State != FileStatus.Ignored).Select(s => s.FilePath).ToList();
        int indexed = 0;
        foreach (var relPath in trackedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(workDir, relPath);
            if (!File.Exists(fullPath)) continue;
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > _settings.MaxFileSizeKb * 1024) continue;
            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                _indexer.EnqueueIndexFile(mirror.CollectionName, relPath, content, "mirror");
                indexed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"Read failed: {ex.Message}");
            }
        }
        _activity?.Log("mirror", mirror.CollectionName, $"Queued {indexed} files for indexing");
    }

    private async Task SyncAllMirrorsAsync()
    {
        try
        {
            var mirrors = _repository.LoadMirrors();
            foreach (var mirror in mirrors) await SyncMirrorAsync(mirror, CancellationToken.None);
        }
        catch (Exception ex) { _activity?.Log("error", "", $"Mirror sync cycle failed: {ex.Message}"); }
    }
}

// ── Vector Search Engine ───────────────────────────────────────────────────

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
// ── Activity Logger ────────────────────────────────────────────────────────

internal sealed class CodeVectorActivityLogger
{
    private readonly IMcpActivityLog _activityLog;
    private readonly Func<CodeVectorMcpLogLevel> _getLogLevel;

    public CodeVectorActivityLogger(IMcpActivityLog activityLog, Func<CodeVectorMcpLogLevel> getLogLevel)
    {
        _activityLog = activityLog;
        _getLogLevel = getLogLevel;
    }

    public void Log(string operation, string target, string? detail = null)
    {
        var level = _getLogLevel();
        if (level == CodeVectorMcpLogLevel.None) return;
        _activityLog.Write(new McpActivityEntry("CodeVector", operation)
        {
            Target = target,
            RequestDetail = detail,
        });
    }
}

// ── MCP Tools ──────────────────────────────────────────────────────────────

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
                sb.AppendLine($"📄 {result.FilePath} (lines {result.StartLine}-{result.EndLine})");
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
        [Description("Credential name for authentication (optional)")] string? credentialName = null)
    {
        try
        {
            var mirror = await _module.MirrorManager.RegisterMirrorAsync(collection, remoteUrl, branch, credentialName, CancellationToken.None);
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
                foreach (var col in collections) sb.AppendLine($"  • {col.Name}: {col.FileCount} files, {col.ChunkCount} chunks");
                sb.AppendLine();
            }
            if (mirrors.Count > 0)
            {
                sb.AppendLine("Git Mirrors:");
                foreach (var mirror in mirrors)
                {
                    var lastSync = mirror.LastSyncUtc ?? "never";
                    sb.AppendLine($"  • {mirror.CollectionName}: {mirror.RemoteUrl} [{mirror.Branch}]");
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
// ── Config Page ────────────────────────────────────────────────────────────

internal sealed class CodeVectorConfigPage : TabPage
{
    private readonly CodeVectorModule _module;
    private ComboBox _backendCombo = null!;
    private Label _remoteUrlLabel = null!;
    private Panel _remoteUrlPanel = null!;
    private TextBox _remoteUrlBox = null!;
    private Button _fetchModelsButton = null!;
    private Label _remoteModelLabel = null!;
    private Panel _remoteModelPanel = null!;
    private ComboBox _remoteModelCombo = null!;
    private Button _showModelButton = null!;
    private Label _fetchStatusLabel = null!;
    private Label _onnxLabel = null!;
    private Panel _onnxPanel = null!;
    private TextBox _onnxBox = null!;
    private Button _onnxBrowseButton = null!;
    private NumericUpDown _chunkLinesBox = null!;
    private NumericUpDown _overlapBox = null!;
    private NumericUpDown _maxSizeBox = null!;
    private NumericUpDown _topKBox = null!;
    private NumericUpDown _syncBox = null!;

    public CodeVectorConfigPage(CodeVectorModule module) : base("Code Vector Store")
    {
        _module = module;
        InitializeComponent();
        UpdateBackendVisibility();
    }

    private void InitializeComponent()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 12,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));

        int row = 0;

        // Backend Type
        layout.Controls.Add(new Label { Text = "Backend Type:", Anchor = AnchorStyles.Left }, 0, row);
        _backendCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _backendCombo.Items.AddRange(["Remote", "Onnx"]);
        _backendCombo.SelectedItem = _module.Settings.BackendType.ToString();
        _backendCombo.SelectedIndexChanged += BackendCombo_SelectedIndexChanged;
        layout.Controls.Add(_backendCombo, 1, row++);

        // Remote URL + Fetch Models button
        _remoteUrlLabel = new Label { Text = "Remote URL:", Anchor = AnchorStyles.Left };
        layout.Controls.Add(_remoteUrlLabel, 0, row);
        _remoteUrlBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteUrl };
        _fetchModelsButton = new Button { Text = "Fetch Models", AutoSize = true, Margin = new Padding(3, 0, 0, 0) };
        _fetchModelsButton.Click += FetchModelsButton_Click;
        _remoteUrlPanel = new Panel { Dock = DockStyle.Fill };
        var urlInnerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        urlInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        urlInnerLayout.Controls.Add(_remoteUrlBox, 0, 0);
        urlInnerLayout.Controls.Add(_fetchModelsButton, 1, 0);
        _remoteUrlPanel.Controls.Add(urlInnerLayout);
        layout.Controls.Add(_remoteUrlPanel, 1, row++);

        // Remote Model (editable ComboBox) + Show Model Info button
        _remoteModelLabel = new Label { Text = "Remote Model:", Anchor = AnchorStyles.Left };
        layout.Controls.Add(_remoteModelLabel, 0, row);
        _remoteModelCombo = new ComboBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteModel };
        _showModelButton = new Button { Text = "Show Info", AutoSize = true, Margin = new Padding(3, 0, 0, 0) };
        _showModelButton.Click += ShowModelButton_Click;
        _remoteModelPanel = new Panel { Dock = DockStyle.Fill };
        var modelInnerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        modelInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modelInnerLayout.Controls.Add(_remoteModelCombo, 0, 0);
        modelInnerLayout.Controls.Add(_showModelButton, 1, 0);
        _remoteModelPanel.Controls.Add(modelInnerLayout);
        layout.Controls.Add(_remoteModelPanel, 1, row++);

        // Fetch status label
        _fetchStatusLabel = new Label { Text = "", Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = SystemColors.GrayText };
        layout.Controls.Add(_fetchStatusLabel, 1, row++);

        // ONNX Model Folder + Browse button
        _onnxLabel = new Label { Text = "ONNX Model Folder:", Anchor = AnchorStyles.Left };
        layout.Controls.Add(_onnxLabel, 0, row);
        _onnxBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.OnnxModelFolder };
        _onnxBrowseButton = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(3, 0, 0, 0) };
        _onnxBrowseButton.Click += OnnxBrowseButton_Click;
        _onnxPanel = new Panel { Dock = DockStyle.Fill };
        var onnxInnerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        onnxInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        onnxInnerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        onnxInnerLayout.Controls.Add(_onnxBox, 0, 0);
        onnxInnerLayout.Controls.Add(_onnxBrowseButton, 1, 0);
        _onnxPanel.Controls.Add(onnxInnerLayout);
        layout.Controls.Add(_onnxPanel, 1, row++);

        // Chunk Lines
        layout.Controls.Add(new Label { Text = "Chunk Lines:", Anchor = AnchorStyles.Left }, 0, row);
        _chunkLinesBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 10, Maximum = 1000, Value = _module.Settings.ChunkLines };
        layout.Controls.Add(_chunkLinesBox, 1, row++);

        // Chunk Overlap Lines
        layout.Controls.Add(new Label { Text = "Chunk Overlap Lines:", Anchor = AnchorStyles.Left }, 0, row);
        _overlapBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = _module.Settings.ChunkOverlapLines };
        layout.Controls.Add(_overlapBox, 1, row++);

        // Max File Size (KB)
        layout.Controls.Add(new Label { Text = "Max File Size (KB):", Anchor = AnchorStyles.Left }, 0, row);
        _maxSizeBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 10240, Value = _module.Settings.MaxFileSizeKb };
        layout.Controls.Add(_maxSizeBox, 1, row++);

        // Default Top K
        layout.Controls.Add(new Label { Text = "Default Top K:", Anchor = AnchorStyles.Left }, 0, row);
        _topKBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 100, Value = _module.Settings.DefaultTopK };
        layout.Controls.Add(_topKBox, 1, row++);

        // Git Sync Interval (min)
        layout.Controls.Add(new Label { Text = "Git Sync Interval (min):", Anchor = AnchorStyles.Left }, 0, row);
        _syncBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1440, Value = _module.Settings.GitSyncIntervalMinutes };
        layout.Controls.Add(_syncBox, 1, row++);

        // Save button
        var saveButton = new Button { Text = "Save Settings", Dock = DockStyle.Fill };
        saveButton.Click += SaveButton_Click;
        layout.Controls.Add(saveButton, 0, row);
        layout.SetColumnSpan(saveButton, 2);

        Controls.Add(layout);
    }

    private void BackendCombo_SelectedIndexChanged(object? sender, EventArgs e)
        => UpdateBackendVisibility();

    private void UpdateBackendVisibility()
    {
        bool isRemote = _backendCombo.SelectedItem?.ToString() == "Remote";
        bool isOnnx = !isRemote;

        _remoteUrlLabel.Visible = isRemote;
        _remoteUrlPanel.Visible = isRemote;
        _remoteModelLabel.Visible = isRemote;
        _remoteModelPanel.Visible = isRemote;
        _fetchStatusLabel.Visible = isRemote;

        _onnxLabel.Visible = isOnnx;
        _onnxPanel.Visible = isOnnx;
    }

    private string DeriveBaseUrl()
    {
        string baseUrl = _remoteUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"http://{_module.Host.DisplayHost}:{_module.Host.ListenPort}/v1/embeddings";

        if (baseUrl.EndsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
            return baseUrl[..^"/v1/embeddings".Length];

        return baseUrl.TrimEnd('/');
    }

    private HttpClient CreateAuthedClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string? credentialName = _module.Settings.RemoteCredentialName;
        if (!string.IsNullOrWhiteSpace(credentialName))
        {
            string? secret = _module.Secrets.ResolveSecret(credentialName);
            if (!string.IsNullOrWhiteSpace(secret))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        return client;
    }

    private async void FetchModelsButton_Click(object? sender, EventArgs e)
    {
        string modelsUrl = DeriveBaseUrl() + "/v1/models";

        _fetchModelsButton.Enabled = false;
        _fetchStatusLabel.ForeColor = SystemColors.GrayText;
        _fetchStatusLabel.Text = "Fetching models…";

        try
        {
            using var client = CreateAuthedClient();
            using var response = await client.GetAsync(modelsUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    string? currentSelection = _remoteModelCombo.Text;
                    _remoteModelCombo.Items.Clear();
                    var modelIds = new List<string>();

                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is string id)
                            modelIds.Add(id);
                    }

                    _remoteModelCombo.Items.AddRange(modelIds.ToArray());

                    if (modelIds.Contains(currentSelection))
                        _remoteModelCombo.Text = currentSelection;
                    else if (modelIds.Count > 0)
                        _remoteModelCombo.SelectedIndex = 0;

                    _fetchStatusLabel.ForeColor = Color.Green;
                    _fetchStatusLabel.Text = $"Found {modelIds.Count} model(s)";
                }
                else
                {
                    _fetchStatusLabel.ForeColor = Color.OrangeRed;
                    _fetchStatusLabel.Text = $"OK but unexpected response format";
                }
            }
            else
            {
                _fetchStatusLabel.ForeColor = Color.Red;
                _fetchStatusLabel.Text = $"Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            _fetchStatusLabel.ForeColor = Color.Red;
            _fetchStatusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _fetchModelsButton.Enabled = true;
        }
    }

    private async void ShowModelButton_Click(object? sender, EventArgs e)
    {
        string modelId = _remoteModelCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            MessageBox.Show("Enter or select a model first.", "No Model Selected",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string modelUrl = DeriveBaseUrl() + "/v1/models/" + Uri.EscapeDataString(modelId);

        _showModelButton.Enabled = false;

        try
        {
            using var client = CreateAuthedClient();
            using var response = await client.GetAsync(modelUrl);
            string body = await response.Content.ReadAsStringAsync();

            string displayText;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    displayText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    displayText = body;
                }
            }
            else
            {
                displayText = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n{body}";
            }

            using var dialog = new ModelInfoDialog(modelId, displayText);
            dialog.ShowDialog(this.FindForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error fetching model info: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _showModelButton.Enabled = true;
        }
    }

    private void OnnxBrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select ONNX Model File",
            Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*",
            FilterIndex = 1,
        };

        if (!string.IsNullOrWhiteSpace(_onnxBox.Text) && Directory.Exists(_onnxBox.Text))
            dialog.InitialDirectory = _onnxBox.Text;

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            string? folder = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(folder))
                _onnxBox.Text = folder;
        }
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        var settings = _module.Settings;
        settings.BackendType = _backendCombo.SelectedItem?.ToString() == "Onnx" ? BackendType.Onnx : BackendType.Remote;
        settings.RemoteUrl = _remoteUrlBox.Text;
        settings.RemoteModel = _remoteModelCombo.Text;
        settings.OnnxModelFolder = _onnxBox.Text;
        settings.ChunkLines = (int)_chunkLinesBox.Value;
        settings.ChunkOverlapLines = (int)_overlapBox.Value;
        settings.MaxFileSizeKb = (int)_maxSizeBox.Value;
        settings.DefaultTopK = (int)_topKBox.Value;
        settings.GitSyncIntervalMinutes = (int)_syncBox.Value;

        var repo = _module.Repository;
        repo.SaveSetting("backend_type", settings.BackendType.ToString());
        repo.SaveSetting("remote_url", settings.RemoteUrl);
        repo.SaveSetting("remote_model", settings.RemoteModel);
        repo.SaveSetting("onnx_folder", settings.OnnxModelFolder);
        repo.SaveSetting("chunk_lines", settings.ChunkLines.ToString());
        repo.SaveSetting("chunk_overlap", settings.ChunkOverlapLines.ToString());
        repo.SaveSetting("max_file_kb", settings.MaxFileSizeKb.ToString());
        repo.SaveSetting("default_top_k", settings.DefaultTopK.ToString());
        repo.SaveSetting("git_sync_interval", settings.GitSyncIntervalMinutes.ToString());

        MessageBox.Show("Settings saved. Restart required for backend changes to take effect.", "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal sealed class ModelInfoDialog : Form
{
    public ModelInfoDialog(string modelId, string content)
    {
        Text = $"Model Info — {modelId}";
        Size = new Size(700, 550);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(400, 300);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = content,
            Font = new Font("Consolas", 9.5f),
            BackColor = SystemColors.Window,
        };
        layout.Controls.Add(textBox, 0, 0);

        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        layout.Controls.Add(closeButton, 0, 1);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.Add(layout);
    }
}
