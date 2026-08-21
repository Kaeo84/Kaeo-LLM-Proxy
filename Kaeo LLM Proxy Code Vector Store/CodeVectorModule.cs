using Kaeo.LlmProxy.Modules;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ModelContextProtocol.Server;
using Serilog;
using System.Collections.Concurrent;
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
    private readonly object _vectorDatabaseLock = new();
    private string? _moduleDataDir;
    private string? _dataDirectory;
    private bool _started;

	public string Id => "kaeo.codevector";
	public string Name => "Code Vector Store";
	string IKaeoModule.Version => Version;
	public string Description => "Embeddings + vector store for code search via MCP tools. "
		+ "Supports remote HTTP and local ONNX CPU embedding backends, "
		+ "agent-push indexing, and server-side git mirrors with LibGit2Sharp.";

	internal CodeVectorRepository Repository =>
		_repository ?? throw new InvalidOperationException("Module not initialized.");
    internal CodeVectorDatabase VectorDb => EnsureVectorDatabase();
	internal IEmbeddingBackend EmbeddingBackend =>
		_embeddingBackend ?? throw new InvalidOperationException("Embedding backend not initialized.");
    internal IndexingEngine Indexer => EnsureIndexingEngine();
    internal GitMirrorManager MirrorManager => EnsureMirrorManager();
    internal VectorSearchEngine SearchEngine => EnsureSearchEngine();
	internal CodeVectorActivityLogger Activity =>
		_activity ?? throw new InvalidOperationException("Module not initialized.");
	internal ISecretProvider Secrets =>
		_context?.Secrets ?? throw new InvalidOperationException("Module not initialized.");
	internal HostInfo Host =>
		_context?.Host ?? throw new InvalidOperationException("Module not initialized.");
	internal CodeVectorSettings Settings => _settings;

	/// <summary>
	/// Disposes the cached vector database and dependent engines so the next access
	/// re-resolves the path from the updated <see cref="Settings.VectorDatabasePath"/>.
	/// </summary>
	internal void InvalidateVectorDatabase()
	{
		lock (_vectorDatabaseLock)
		{
			_searchEngine = null;
			_indexingEngine = null;
			_vectorDb?.Dispose();
			_vectorDb = null;
		}
	}

	/// <summary>
	/// Disposes the cached embedding backend and dependent engines, then recreates the
	/// backend from the current settings. Call when the backend type or ONNX model folder changes.
	/// </summary>
	internal void InvalidateEmbeddingBackend()
	{
		lock (_vectorDatabaseLock)
		{
			_mirrorManager = null;
			_searchEngine = null;
			_indexingEngine = null;
			_embeddingBackend?.Dispose();
			_embeddingBackend = CreateEmbeddingBackend(_settings, Secrets, Host);
		}
	}

	/// <summary>
	/// Stops and disposes the cached mirror manager so the next access recreates it
	/// with the updated sync interval.
	/// </summary>
	internal void InvalidateMirrorManager()
	{
		lock (_vectorDatabaseLock)
		{
			_mirrorManager?.StopTimer();
			_mirrorManager = null;
		}
	}

	public void Initialize(ModuleContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		_context = context;
		ApplySharedSchema(context.Database);
		_repository = new CodeVectorRepository(context.Database);
		_settings = _repository.LoadSettings();

        _dataDirectory = context.DataDirectory;
        _moduleDataDir = Path.Combine(context.DataDirectory, "codevector");
		_activity = new CodeVectorActivityLogger(context.ActivityLog, () => _settings.McpLogLevel);
		_embeddingBackend = CreateEmbeddingBackend(_settings, context.Secrets, context.Host);
	}

	public System.Windows.Forms.TabPage CreateConfigPage() => new CodeVectorConfigPage(this);
	public IReadOnlyList<object> CreateMcpToolTargets(McpSessionInfo session) => [new CodeVectorTools(this, session)];
	public bool IsRunning => _indexingEngine?.IsRunning == true;
	public event EventHandler<string>? StatusChanged;

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
        _started = true;
        if (_indexingEngine is not null)
            _indexingEngine.Start();
        if (_mirrorManager is not null)
            _mirrorManager.StartTimer();
		StatusChanged?.Invoke(this, "Running");
		return Task.CompletedTask;
	}

	public async Task StopAsync()
	{
        _started = false;
        _mirrorManager?.StopTimer();
		if (_indexingEngine is { IsRunning: true })
			await _indexingEngine.StopAsync();
		_embeddingBackend?.Dispose();
        _vectorDb?.Dispose();
		StatusChanged?.Invoke(this, "Stopped");
	}

    private CodeVectorDatabase EnsureVectorDatabase()
    {
        if (_vectorDb is not null)
            return _vectorDb;

        lock (_vectorDatabaseLock)
        {
            if (_vectorDb is not null)
                return _vectorDb;

            string moduleDataDir = _moduleDataDir ?? throw new InvalidOperationException("Module not initialized.");
            string vectorDbPath = string.IsNullOrWhiteSpace(_settings.VectorDatabasePath)
                ? Path.Combine(moduleDataDir, "codevectordb")
                : (Path.IsPathRooted(_settings.VectorDatabasePath)
                    ? _settings.VectorDatabasePath
                    : Path.Combine(moduleDataDir, _settings.VectorDatabasePath));
            string? vectorDbDirectory = Path.GetDirectoryName(vectorDbPath);
            if (!string.IsNullOrWhiteSpace(vectorDbDirectory))
                Directory.CreateDirectory(vectorDbDirectory);

            Log.Information("Vector database: {Path}", vectorDbPath);
            _vectorDb = new CodeVectorDatabase(vectorDbPath);
            return _vectorDb;
        }
    }

    private IndexingEngine EnsureIndexingEngine()
    {
        if (_indexingEngine is not null)
            return _indexingEngine;

        lock (_vectorDatabaseLock)
        {
            _indexingEngine ??= new IndexingEngine(EnsureVectorDatabase(), EmbeddingBackend, _settings, _activity);
            if (_started)
                _indexingEngine.Start();
            return _indexingEngine;
        }
    }

    private GitMirrorManager EnsureMirrorManager()
    {
        if (_mirrorManager is not null)
            return _mirrorManager;

        lock (_vectorDatabaseLock)
        {
            string moduleDataDir = _moduleDataDir ?? throw new InvalidOperationException("Module not initialized.");
            _mirrorManager ??= new GitMirrorManager(moduleDataDir, Repository, EnsureIndexingEngine(), _settings, _activity, Secrets);
            if (_started)
                _mirrorManager.StartTimer();
            return _mirrorManager;
        }
    }

    private VectorSearchEngine EnsureSearchEngine()
    {
        if (_searchEngine is not null)
            return _searchEngine;

        lock (_vectorDatabaseLock)
        {
            _searchEngine ??= new VectorSearchEngine(EnsureVectorDatabase());
            return _searchEngine;
        }
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
        mirror_path TEXT NULL,
        last_sync_utc TEXT NULL,
        last_sync_status TEXT NULL);
        """;
        private static void ApplySharedSchema(IModuleDatabase db)
    {
        db.ExecuteSchemaScript(SharedSchema);
        var columns = db.Query("PRAGMA table_info(mcp_codevector_repos)", r => r.GetString(1));
        if (!columns.Contains("mirror_path", StringComparer.OrdinalIgnoreCase))
            db.Execute("ALTER TABLE mcp_codevector_repos ADD COLUMN mirror_path TEXT NULL", _ => { });
    }

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

	/// <summary>
	/// Root directory where git mirrors are checked out. Defaults to <c>moduleDataDir/mirrors</c>.
	/// Set to a different path to monitor a specific repository externally or share mirrors across sessions.
	/// </summary>
    public string VectorDatabasePath { get; set; } = string.Empty;
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
            "SELECT id, collection_name, remote_url, branch, credential_name, mirror_path, last_sync_utc, last_sync_status FROM mcp_codevector_repos",
            r => new MirrorRegistration
            {
                Id = r.GetInt32(0),
                CollectionName = r.GetString(1),
                RemoteUrl = r.GetString(2),
                Branch = r.GetString(3),
                CredentialName = r.IsDBNull(4) ? null : r.GetString(4),
                MirrorPath = r.IsDBNull(5) ? null : r.GetString(5),
                LastSyncUtc = r.IsDBNull(6) ? null : r.GetString(6),
                LastSyncStatus = r.IsDBNull(7) ? null : r.GetString(7),
            });
    }

    public MirrorRegistration UpsertMirror(string collectionName, string remoteUrl, string branch, string? credentialName, string? mirrorPath = null)
    {
        _db.Execute(
            "INSERT INTO mcp_codevector_repos (collection_name, remote_url, branch, credential_name, mirror_path) VALUES ($col, $url, $branch, $cred, $path) " +
            "ON CONFLICT(collection_name) DO UPDATE SET remote_url = excluded.remote_url, branch = excluded.branch, credential_name = excluded.credential_name, mirror_path = excluded.mirror_path",
            cmd => { AddParam(cmd, "$col", collectionName); AddParam(cmd, "$url", remoteUrl); AddParam(cmd, "$branch", branch); AddParam(cmd, "$cred", (object?)credentialName ?? DBNull.Value); AddParam(cmd, "$path", (object?)mirrorPath ?? DBNull.Value); });
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
    public string? MirrorPath { get; set; }
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
        string url = string.IsNullOrWhiteSpace(settings.RemoteUrl)
            ? $"http://{host.DisplayHost}:{host.ListenPort}/v1/embeddings"
            : settings.RemoteUrl.Trim();

        // Users typically enter just the host (e.g. http://192.168.1.1:8081).
        // Append the embeddings endpoint if the path is not already specified.
        if (!url.Contains("/v1/", StringComparison.OrdinalIgnoreCase)
            && !url.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            url = url.TrimEnd('/') + "/v1/embeddings";

        _url = url;
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
        Log.Debug("Embedding request: POST {Url} model={Model} count={Count}", _url, _model, texts.Count);
        using var response = await _httpClient.PostAsync(_url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Embedding request failed: {(int)response.StatusCode} {response.ReasonPhrase}\nURL: {_url}\nModel: {_model}\nResponse: {errBody}");
        }
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
    private readonly ConcurrentQueue<QueueItemInfo> _pendingJobs = new();
    private Task? _worker;
    private volatile QueueItemInfo? _currentJob;

    public IndexingEngine(CodeVectorDatabase db, IEmbeddingBackend backend, CodeVectorSettings settings, CodeVectorActivityLogger? activity)
    {
        _db = db;
        _backend = backend;
        _settings = settings;
        _activity = activity;
        _queue = Channel.CreateUnbounded<IndexJob>(new UnboundedChannelOptions { SingleReader = true });
    }

    public bool IsRunning => _worker is { IsCompleted: false };
    public int QueueDepth => _pendingJobs.Count;
    public QueueItemInfo? CurrentJob => _currentJob;

    public IReadOnlyList<QueueItemInfo> GetQueueSnapshot() => _pendingJobs.ToArray();

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
        var info = new QueueItemInfo("Index", collection, path, source);
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.IndexFile, Collection = collection, Path = path, Content = content, Source = source, Info = info });
    }

    public void EnqueueDeletePath(string collection, string pathPrefix)
    {
        var info = new QueueItemInfo("DeletePath", collection, pathPrefix, "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeletePath, Collection = collection, Path = pathPrefix, Info = info });
    }

    public void EnqueueDeleteCollection(string collection)
    {
        var info = new QueueItemInfo("DeleteCollection", collection, "(all)", "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.DeleteCollection, Collection = collection, Info = info });
    }

    public void EnqueueReindex(string collection)
    {
        var info = new QueueItemInfo("Reindex", collection, "(all)", "-");
        _pendingJobs.Enqueue(info);
        _queue.Writer.TryWrite(new IndexJob { Type = JobType.Reindex, Collection = collection, Info = info });
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            _currentJob = job.Info;
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
            finally
            {
                _pendingJobs.TryDequeue(out _);
                _currentJob = null;
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

        _activity?.Log("file_start", $"{collection}:{path}", $"Chunked into {chunks.Count} pieces, source={source}");
        _db.GetOrCreateCollection(collection, _backend.ModelName, _backend.Dimension);
        var fileId = _db.UpsertFile(collection, path, hash, source, chunks.Count);
        _db.DeleteFileChunks(fileId);

        const int batchSize = 16;
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            _activity?.Log("embed_batch", $"{collection}:{path}", $"Embedding batch {i / batchSize + 1} ({batch.Count} chunks, offset {i})");
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
        _activity?.Log("file_complete", $"{collection}:{path}", $"Indexed {chunks.Count} chunks");
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private enum JobType { IndexFile, DeletePath, DeleteCollection, Reindex }
    private sealed class IndexJob
    {
        public JobType Type { get; init; }
        public string? Collection { get; init; }
        public string? Path { get; init; }
        public string? Content { get; init; }
        public string? Source { get; init; }
        public QueueItemInfo? Info { get; init; }
    }

    internal sealed record QueueItemInfo(string Operation, string Collection, string Path, string Source);
}

// ── Git Mirror Manager ─────────────────────────────────────────────────────

internal sealed class GitMirrorManager
{
    private readonly string _moduleDataDir;
    private readonly CodeVectorRepository _repository;
    private readonly IndexingEngine _indexer;
    private readonly CodeVectorSettings _settings;
    private readonly CodeVectorActivityLogger? _activity;
    private readonly ISecretProvider _secrets;
    private System.Threading.Timer? _timer;

    public GitMirrorManager(string moduleDataDir, CodeVectorRepository repository, IndexingEngine indexer, CodeVectorSettings settings, CodeVectorActivityLogger? activity, ISecretProvider secrets)
     {
         _moduleDataDir = moduleDataDir;
         _repository = repository;
         _indexer = indexer;
         _settings = settings;
         _activity = activity;
         _secrets = secrets;
     }

    public void StartTimer()
    {
        if (_timer is not null || _settings.GitSyncIntervalMinutes <= 0) return;
        var interval = TimeSpan.FromMinutes(_settings.GitSyncIntervalMinutes);
        _timer = new System.Threading.Timer(_ => _ = SyncAllMirrorsAsync(), null, interval, interval);
    }

    public void StopTimer() { _timer?.Dispose(); _timer = null; }

    public async Task<MirrorRegistration> RegisterMirrorAsync(string collectionName, string remoteUrl, string branch, string? credentialName, CancellationToken ct, string? mirrorPath = null)
    {
        var mirror = _repository.UpsertMirror(collectionName, remoteUrl, branch, credentialName, mirrorPath);
        await SyncMirrorAsync(mirror, ct);
        return mirror;
    }

    public async Task SyncMirrorAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        _activity?.Log("sync_start", mirror.CollectionName, $"Syncing {mirror.RemoteUrl} [{mirror.Branch}]");
        try
        {
            var mirrorPath = ResolveMirrorPath(mirror);
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
            _activity?.Log("sync_success", mirror.CollectionName, "Mirror synced successfully");
        }
        catch (Exception ex) when (ex is IOException or LibGit2SharpException)
        {
            _activity?.Log("error", mirror.CollectionName, $"Mirror sync failed: {ex.Message}");
            _repository.UpdateMirrorSync(mirror.Id, null, $"failed: {ex.Message}");
        }
    }

    public async Task IndexMirrorFilesAsync(MirrorRegistration mirror, CancellationToken ct)
    {
        var mirrorPath = ResolveMirrorPath(mirror);
        if (!Repository.IsValid(mirrorPath))
        {
            _activity?.Log("index_error", mirror.CollectionName, "Mirror not yet cloned. Run Sync first.");
            return;
        }
        await IndexMirrorFilesAsync(mirror, mirrorPath, ct);
    }

    private async Task IndexMirrorFilesAsync(MirrorRegistration mirror, string mirrorPath, CancellationToken ct)
    {
        using var repo = new Repository(mirrorPath);
        var workDir = repo.Info.WorkingDirectory;
        var trackedFiles = new List<string>();
        WalkTree(repo.Head!.Tip.Tree, string.Empty, trackedFiles);
        int queued = 0, skipped = 0;
        foreach (var relPath in trackedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(workDir, relPath);
            if (!File.Exists(fullPath)) { skipped++; continue; }
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > _settings.MaxFileSizeKb * 1024) { _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"File too large ({fileInfo.Length / 1024} KB)"); skipped++; continue; }
            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                _indexer.EnqueueIndexFile(mirror.CollectionName, relPath, content, "mirror");
                queued++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activity?.Log("skip", $"{mirror.CollectionName}:{relPath}", $"Read failed: {ex.Message}");
                skipped++;
            }
        }
        _activity?.Log("sync_complete", mirror.CollectionName, $"Discovered {trackedFiles.Count} files, queued {queued}, skipped {skipped}");
    }

    private string ResolveMirrorPath(MirrorRegistration mirror)
    {
        string basePath = string.IsNullOrWhiteSpace(mirror.MirrorPath)
            ? Path.Combine(_moduleDataDir, "mirrors")
            : mirror.MirrorPath;
        string resolvedBasePath = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(_moduleDataDir, basePath);
        string mirrorPath = Path.Combine(resolvedBasePath, mirror.CollectionName);
        Directory.CreateDirectory(resolvedBasePath);
        return mirrorPath;
    }

    private static void WalkTree(Tree tree, string prefix, List<string> files)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Blob:
                    files.Add(path);
                    break;
                case TreeEntryTargetType.Tree:
                    if (entry.Target is Tree subtree)
                        WalkTree(subtree, path, files);
                    break;
            }
        }
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
    private const int MaxBufferedEntries = 500;

    private readonly IMcpActivityLog _activityLog;
    private readonly Func<CodeVectorMcpLogLevel> _getLogLevel;
    private readonly object _bufferLock = new();
    private readonly List<LogEntry> _buffer = new();
    private long _totalLogged;
    private long _errorCount;

    public CodeVectorActivityLogger(IMcpActivityLog activityLog, Func<CodeVectorMcpLogLevel> getLogLevel)
    {
        _activityLog = activityLog;
        _getLogLevel = getLogLevel;
    }

    public long TotalLogged => Interlocked.Read(ref _totalLogged);
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public void Log(string operation, string target, string? detail = null)
    {
        var level = _getLogLevel();
        var isError = operation == "error";
        if (isError) Interlocked.Increment(ref _errorCount);
        Interlocked.Increment(ref _totalLogged);

        if (level != CodeVectorMcpLogLevel.None)
        {
            _activityLog.Write(new McpActivityEntry("CodeVector", operation)
            {
                Target = target,
                RequestDetail = detail,
                IsError = isError,
            });
        }

        var entry = new LogEntry(DateTime.Now, operation, target, detail);
        lock (_bufferLock)
        {
            _buffer.Add(entry);
            if (_buffer.Count > MaxBufferedEntries)
                _buffer.RemoveRange(0, _buffer.Count - MaxBufferedEntries);
        }
    }

    public IReadOnlyList<LogEntry> GetRecentEntries()
    {
        lock (_bufferLock)
            return _buffer.ToList();
    }

    public void ClearBuffer()
    {
        lock (_bufferLock) _buffer.Clear();
    }

    internal sealed record LogEntry(DateTime Timestamp, string Operation, string Target, string? Detail);
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
    private GroupBox _remoteGroup = null!;
    private TextBox _remoteUrlBox = null!;
    private Button _fetchModelsButton = null!;
    private ComboBox _remoteModelCombo = null!;
    private Button _showModelButton = null!;
    private ComboBox _credentialCombo = null!;
    private NumericUpDown _timeoutBox = null!;
    private Label _fetchStatusLabel = null!;
    private Button _testConnectionButton = null!;
    private GroupBox _onnxGroup = null!;
    private TextBox _onnxBox = null!;
    private Button _onnxBrowseButton = null!;
    private NumericUpDown _onnxMaxSeqBox = null!;
    private NumericUpDown _onnxThreadsBox = null!;
    private GroupBox _generalGroup = null!;
    private NumericUpDown _chunkLinesBox = null!;
    private NumericUpDown _overlapBox = null!;
    private NumericUpDown _maxSizeBox = null!;
    private NumericUpDown _topKBox = null!;
    private NumericUpDown _syncBox = null!;
    private TextBox _vectorDatabasePathBox = null!;
    private ComboBox _logLevelCombo = null!;
    private CheckBox _chkSearch = null!;
    private CheckBox _chkIndex = null!;
    private CheckBox _chkSync = null!;
    private CheckBox _chkStatus = null!;
    private CheckBox _chkRemove = null!;
    private CheckBox _chkReindex = null!;
    private GroupBox _reposGroup = null!;
    private ListView _reposListView = null!;
    private GroupBox _statusGroup = null!;
    private Label _engineStatusLabel = null!;
    private Label _queueStatusLabel = null!;
    private Label _currentStatusLabel = null!;
    private Label _logSummaryLabel = null!;
    private ListView _queueListView = null!;
    private ListView _logListView = null!;
    private System.Windows.Forms.Timer? _refreshTimer;
    private long _lastRefreshedLogged = -1;

    public CodeVectorConfigPage(CodeVectorModule module) : base("Code Vector Store")
    {
        _module = module;
        BuildUi();
        WireAutoSave();
        UpdateBackendVisibility();
        RefreshRepos();
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        Disposed += (_, _) => { _refreshTimer?.Dispose(); _refreshTimer = null; };
        RefreshStatus();
    }

    private void BuildUi()
    {
        AutoScroll = true;
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoScroll = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(14, 8, 14, 8),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < main.RowCount; row++)
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Vector database location
        var databasePanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        databasePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        databasePanel.Controls.Add(new Label { Text = "Vector Database:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, 0);
        _vectorDatabasePathBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.VectorDatabasePath, Margin = new Padding(3) };
        var browseDatabaseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(3) };
        browseDatabaseButton.Click += BrowseDatabaseButton_Click;
        databasePanel.Controls.Add(_vectorDatabasePathBox, 1, 0);
        databasePanel.Controls.Add(browseDatabaseButton, 2, 0);
        main.Controls.Add(databasePanel, 0, 0);

        // Backend selector
        var backendPanel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
        backendPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        backendPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        backendPanel.Controls.Add(new Label { Text = "Backend:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        _backendCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _backendCombo.Items.AddRange(["Remote", "Onnx"]);
        _backendCombo.SelectedItem = _module.Settings.BackendType.ToString();
        _backendCombo.SelectedIndexChanged += BackendCombo_SelectedIndexChanged;
        backendPanel.Controls.Add(_backendCombo, 1, 0);
        main.Controls.Add(backendPanel, 0, 1);

        // Remote group
        _remoteGroup = BuildRemoteGroup();
        main.Controls.Add(_remoteGroup, 0, 2);

        // ONNX group
        _onnxGroup = BuildOnnxGroup();
        main.Controls.Add(_onnxGroup, 0, 3);

        // General settings
        _generalGroup = BuildGeneralGroup();
        main.Controls.Add(_generalGroup, 0, 4);

        // Git Repos
        _reposGroup = BuildReposGroup();
        main.Controls.Add(_reposGroup, 0, 5);

        // Status
        _statusGroup = BuildStatusGroup();
        main.Controls.Add(_statusGroup, 0, 6);

        Controls.Add(main);
    }

    // (BuildUi replaces the old InitializeComponent)

    private GroupBox BuildRemoteGroup()
    {
        var group = new GroupBox { Text = "Remote Backend", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;

        layout.Controls.Add(new Label { Text = "URL:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _remoteUrlBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteUrl, Margin = new Padding(3) };
        _fetchModelsButton = new Button { Text = "Fetch", AutoSize = true, Margin = new Padding(3) };
        _fetchModelsButton.Click += FetchModelsButton_Click;
        layout.Controls.Add(_remoteUrlBox, 1, row);
        layout.Controls.Add(_fetchModelsButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Model:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _remoteModelCombo = new ComboBox { Dock = DockStyle.Fill, Text = _module.Settings.RemoteModel, Margin = new Padding(3) };
        _showModelButton = new Button { Text = "Info", AutoSize = true, Margin = new Padding(3) };
        _showModelButton.Click += ShowModelButton_Click;
        layout.Controls.Add(_remoteModelCombo, 1, row);
        layout.Controls.Add(_showModelButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Test:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _testConnectionButton = new Button { Text = "Test Connection", AutoSize = true, Margin = new Padding(3) };
        _testConnectionButton.Click += TestConnectionButton_Click;
        layout.Controls.Add(_testConnectionButton, 1, row++);

        layout.Controls.Add(new Label { Text = "Credential:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _credentialCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3) };
        try { _credentialCombo.Items.AddRange(_module.Secrets.ListCredentialNames().ToArray()); } catch { }
        _credentialCombo.Text = _module.Settings.RemoteCredentialName;
        layout.Controls.Add(_credentialCombo, 1, row++);

        layout.Controls.Add(new Label { Text = "Timeout (s):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _timeoutBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 5, Maximum = 300, Value = _module.Settings.RemoteTimeoutSeconds, Margin = new Padding(3) };
        layout.Controls.Add(_timeoutBox, 1, row++);

        _fetchStatusLabel = new Label { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3) };
        layout.Controls.Add(_fetchStatusLabel, 1, row);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildOnnxGroup()
    {
        var group = new GroupBox { Text = "ONNX Backend", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;

        layout.Controls.Add(new Label { Text = "Model Folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxBox = new TextBox { Dock = DockStyle.Fill, Text = _module.Settings.OnnxModelFolder, Margin = new Padding(3) };
        _onnxBrowseButton = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(3) };
        _onnxBrowseButton.Click += OnnxBrowseButton_Click;
        layout.Controls.Add(_onnxBox, 1, row);
        layout.Controls.Add(_onnxBrowseButton, 2, row++);

        layout.Controls.Add(new Label { Text = "Max Sequence:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxMaxSeqBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 32, Maximum = 4096, Value = _module.Settings.OnnxMaxSequenceLength, Margin = new Padding(3) };
        layout.Controls.Add(_onnxMaxSeqBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Threads:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _onnxThreadsBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 32, Value = _module.Settings.OnnxMaxThreads, Margin = new Padding(3) };
        layout.Controls.Add(_onnxThreadsBox, 1, row);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildGeneralGroup()
    {
        var group = new GroupBox { Text = "General Settings", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        var layout = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;

        layout.Controls.Add(new Label { Text = "Chunk Lines:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _chunkLinesBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 10, Maximum = 1000, Value = _module.Settings.ChunkLines, Margin = new Padding(3) };
        layout.Controls.Add(_chunkLinesBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Overlap Lines:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _overlapBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = _module.Settings.ChunkOverlapLines, Margin = new Padding(3) };
        layout.Controls.Add(_overlapBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Max File (KB):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _maxSizeBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 10240, Value = _module.Settings.MaxFileSizeKb, Margin = new Padding(3) };
        layout.Controls.Add(_maxSizeBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Default Top K:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _topKBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 100, Value = _module.Settings.DefaultTopK, Margin = new Padding(3) };
        layout.Controls.Add(_topKBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Git Sync (min):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _syncBox = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1440, Value = _module.Settings.GitSyncIntervalMinutes, Margin = new Padding(3) };
        layout.Controls.Add(_syncBox, 1, row++);

        layout.Controls.Add(new Label { Text = "Log Level:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        _logLevelCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3) };
        _logLevelCombo.Items.AddRange(["None", "Connectivity", "Full"]);
        _logLevelCombo.SelectedItem = _module.Settings.McpLogLevel.ToString();
        layout.Controls.Add(_logLevelCombo, 1, row++);

        layout.Controls.Add(new Label { Text = "Tools:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        var toolsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(3) };
        _chkSearch = new CheckBox { Text = "Search", AutoSize = true, Checked = _module.Settings.SearchEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkIndex = new CheckBox { Text = "Index", AutoSize = true, Checked = _module.Settings.IndexEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkSync = new CheckBox { Text = "Sync", AutoSize = true, Checked = _module.Settings.SyncRepoEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkStatus = new CheckBox { Text = "Status", AutoSize = true, Checked = _module.Settings.StatusEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkRemove = new CheckBox { Text = "Remove", AutoSize = true, Checked = _module.Settings.RemoveEnabled, Margin = new Padding(3, 6, 12, 3) };
        _chkReindex = new CheckBox { Text = "Reindex", AutoSize = true, Checked = _module.Settings.ReindexEnabled, Margin = new Padding(3, 6, 3, 3) };
        toolsPanel.Controls.AddRange([_chkSearch, _chkIndex, _chkSync, _chkStatus, _chkRemove, _chkReindex]);
        layout.Controls.Add(toolsPanel, 1, row);

        group.Controls.Add(layout);
        return group;
    }

               private GroupBox BuildReposGroup()
               {
                   var group = new GroupBox { Text = "Git Repos", Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
                   var layout = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
                   layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                   _reposListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _reposListView.Columns.Add("Collection", 140);
                   _reposListView.Columns.Add("Remote URL", 260);
                    _reposListView.Columns.Add("Branch", 70);
                    _reposListView.Columns.Add("Mirror Path", 220);
                   _reposListView.Columns.Add("Last Sync", 140);
                   _reposListView.Columns.Add("Status", 100);
                   layout.Controls.Add(_reposListView, 0, 0);

                   var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
                   var btnAdd = new Button { Text = "Add", AutoSize = true, Margin = new Padding(3) };
                   btnAdd.Click += AddRepoButton_Click;
                   var btnEdit = new Button { Text = "Edit", AutoSize = true, Margin = new Padding(3) };
                   btnEdit.Click += EditRepoButton_Click;
                   var btnRemove = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(3) };
                   btnRemove.Click += RemoveRepoButton_Click;
                   var btnIndex = new Button { Text = "Index", AutoSize = true, Margin = new Padding(3) };
                   btnIndex.Click += IndexRepoButton_Click;
                   var btnSync = new Button { Text = "Sync", AutoSize = true, Margin = new Padding(3) };
                   btnSync.Click += SyncRepoButton_Click;
                   var btnStatus = new Button { Text = "Status", AutoSize = true, Margin = new Padding(3) };
                   btnStatus.Click += RepoStatusButton_Click;
                   var btnReindex = new Button { Text = "Reindex", AutoSize = true, Margin = new Padding(3) };
                   btnReindex.Click += ReindexRepoButton_Click;
                   btnPanel.Controls.AddRange([btnAdd, btnEdit, btnRemove, btnIndex, btnSync, btnStatus, btnReindex]);
                   layout.Controls.Add(btnPanel, 0, 1);

                   group.Controls.Add(layout);
                   return group;
               }

               private GroupBox BuildStatusGroup()
               {
                   var group = new GroupBox { Text = "Status", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
                   var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4 };
                   layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
                   layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));

                   var statusPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
                   _engineStatusLabel = new Label { Text = "Engine: —", AutoSize = true, Margin = new Padding(3, 6, 14, 3) };
                   _queueStatusLabel = new Label { Text = "Queue: 0", AutoSize = true, Margin = new Padding(3, 6, 14, 3) };
                   _currentStatusLabel = new Label { Text = "Current: —", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
                   statusPanel.Controls.AddRange([_engineStatusLabel, _queueStatusLabel, _currentStatusLabel]);
                   layout.Controls.Add(statusPanel, 0, 0);

                   _logSummaryLabel = new Label { Text = "Logged: 0 | Errors: 0", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };
                   layout.Controls.Add(_logSummaryLabel, 0, 1);

                   _queueListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _queueListView.Columns.Add("Operation", 90);
                   _queueListView.Columns.Add("Collection", 130);
                   _queueListView.Columns.Add("Path", 250);
                   _queueListView.Columns.Add("Source", 60);
                   layout.Controls.Add(_queueListView, 0, 2);

                   var logHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                   logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                   logHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                   logHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                   var logHeaderBar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
                   logHeaderBar.Controls.Add(new Label { Text = "Activity Log:", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                   var clearButton = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(0) };
                   clearButton.Click += (_, _) => { _module.Activity.ClearBuffer(); RefreshStatus(); };
                   logHeaderBar.Controls.Add(clearButton);
                   logHeader.Controls.Add(logHeaderBar, 0, 0);
                   _logListView = new ListView
                   {
                       View = View.Details,
                       FullRowSelect = true,
                       GridLines = true,
                       MultiSelect = false,
                       Dock = DockStyle.Fill,
                       HeaderStyle = ColumnHeaderStyle.Nonclickable,
                   };
                   _logListView.Columns.Add("Time", 65);
                   _logListView.Columns.Add("Operation", 90);
                   _logListView.Columns.Add("Target", 160);
                   _logListView.Columns.Add("Detail", 230);
                   logHeader.Controls.Add(_logListView, 0, 1);
                   layout.Controls.Add(logHeader, 0, 3);

                   group.Controls.Add(layout);
                   return group;
               }

               private void RefreshStatus()
               {
                   try
                   {
                       var engine = _module.Indexer;
                       var running = engine.IsRunning;
                       _engineStatusLabel.Text = running ? "Engine: Running" : "Engine: Stopped";
                       _engineStatusLabel.ForeColor = running ? Color.Green : SystemColors.GrayText;
                       _queueStatusLabel.Text = $"Queue: {engine.QueueDepth}";
                       var current = engine.CurrentJob;
                       _currentStatusLabel.Text = current is null ? "Current: —" : $"Current: {current.Path}";

                       var activity = _module.Activity;
                       _logSummaryLabel.Text = $"Logged: {activity.TotalLogged} | Errors: {activity.ErrorCount}";

                       _queueListView.BeginUpdate();
                       _queueListView.Items.Clear();
                       foreach (var item in engine.GetQueueSnapshot())
                       {
                           var lvi = new ListViewItem(item.Operation);
                           lvi.SubItems.Add(item.Collection);
                           lvi.SubItems.Add(item.Path);
                           lvi.SubItems.Add(item.Source);
                           _queueListView.Items.Add(lvi);
                       }
                       _queueListView.EndUpdate();

                       if (activity.TotalLogged != _lastRefreshedLogged)
                       {
                           _lastRefreshedLogged = activity.TotalLogged;
                           _logListView.BeginUpdate();
                           _logListView.Items.Clear();
                           foreach (var entry in activity.GetRecentEntries())
                           {
                               var lvi = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss"));
                               lvi.SubItems.Add(entry.Operation);
                               lvi.SubItems.Add(entry.Target);
                               lvi.SubItems.Add(entry.Detail ?? "");
                               if (entry.Operation == "error") lvi.ForeColor = Color.Red;
                               _logListView.Items.Add(lvi);
                           }
                           _logListView.EndUpdate();
                       }
                   }
                   catch (Exception ex)
                   {
                       _engineStatusLabel.Text = $"Status error: {ex.Message}";
                       _engineStatusLabel.ForeColor = Color.OrangeRed;
                   }
               }

               private void RefreshRepos()
               {
                   try
                   {
                       _reposListView.BeginUpdate();
                       _reposListView.Items.Clear();
                       foreach (var m in _module.Repository.LoadMirrors())
                       {
                           var lvi = new ListViewItem(m.CollectionName);
                           lvi.SubItems.Add(m.RemoteUrl);
                           lvi.SubItems.Add(m.Branch);
                            lvi.SubItems.Add(m.MirrorPath ?? "default");
                           lvi.SubItems.Add(m.LastSyncUtc ?? "never");
                           lvi.SubItems.Add(m.LastSyncStatus ?? "pending");
                           lvi.Tag = m;
                           _reposListView.Items.Add(lvi);
                       }
                       _reposListView.EndUpdate();
                   }
                   catch { }
               }

               private MirrorRegistration? GetSelectedRepo()
                   => _reposListView.SelectedItems.Count > 0 ? _reposListView.SelectedItems[0].Tag as MirrorRegistration : null;

               private void AddRepoButton_Click(object? sender, EventArgs e)
               {
                   using var dlg = new RepoDialog(null);
                   if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                   {
                       try
                       {
                            _ = _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, dlg.RemoteUrl, dlg.Branch, dlg.CredentialName, CancellationToken.None, dlg.MirrorPath);
                           RefreshRepos();
                       }
                       catch (Exception ex) { MessageBox.Show(ex.Message, "Add Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                   }
               }

               private void EditRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   using var dlg = new RepoDialog(m);
                   if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                   {
                       try
                       {
                           _module.Repository.DeleteMirror(m.CollectionName);
                            _ = _module.MirrorManager.RegisterMirrorAsync(dlg.CollectionName, dlg.RemoteUrl, dlg.Branch, dlg.CredentialName, CancellationToken.None, dlg.MirrorPath);
                           RefreshRepos();
                       }
                       catch (Exception ex) { MessageBox.Show(ex.Message, "Edit Repo Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                   }
               }

               private void RemoveRepoButton_Click(object? sender, EventArgs e)
               {
                   var m = GetSelectedRepo();
                   if (m is null) { MessageBox.Show("Select a repo first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                   if (MessageBox.Show($"Remove '{m.CollectionName}'?", "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                   {
                       _module.Repository.DeleteMirror(m.CollectionName);
                       RefreshRepos();
                   }
               }

               private void RequireRepo(out MirrorRegistration m)
               {
                   m = GetSelectedRepo()!;
                   if (m is null) MessageBox.Show("Select a repo in the list first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
               }

               private async void IndexRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                       await _module.MirrorManager.IndexMirrorFilesAsync(m, CancellationToken.None);
                       _module.Activity.Log("ui_index", m.CollectionName, $"Re-indexed files for {m.CollectionName}");
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Index Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private async void SyncRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                       await _module.MirrorManager.SyncMirrorAsync(m, CancellationToken.None);
                       RefreshRepos();
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Sync Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private void RepoStatusButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   var collections = _module.VectorDb.ListCollections();
                   var col = collections.FirstOrDefault(c => c.Name == m.CollectionName);
                   var sb = new StringBuilder();
                   sb.AppendLine($"Collection: {m.CollectionName}");
                   sb.AppendLine($"Remote: {m.RemoteUrl} [{m.Branch}]");
                   sb.AppendLine($"Last Sync: {m.LastSyncUtc ?? "never"}");
                   sb.AppendLine($"Sync Status: {m.LastSyncStatus ?? "pending"}");
                   if (col is not null) sb.AppendLine($"Indexed: {col.FileCount} files, {col.ChunkCount} chunks");
                   sb.AppendLine();
                   sb.AppendLine($"Engine: {(_module.Indexer.IsRunning ? "Running" : "Stopped")} | Queue: {_module.Indexer.QueueDepth}");
                   MessageBox.Show(sb.ToString(), $"Status — {m.CollectionName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
               }

               private async void ReindexRepoButton_Click(object? sender, EventArgs e)
               {
                   if (GetSelectedRepo() is not { } m) { RequireRepo(out _); return; }
                   try
                   {
                       // Reindex = clear the collection, then re-walk the mirror and re-enqueue all files.
                       _module.VectorDb.DeleteCollection(m.CollectionName);
                       await _module.MirrorManager.IndexMirrorFilesAsync(m, CancellationToken.None);
                       _module.Activity.Log("ui_reindex", m.CollectionName, "Reindex: collection cleared + files re-queued");
                   }
                   catch (Exception ex) { MessageBox.Show(ex.Message, "Reindex Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
               }

               private void BackendCombo_SelectedIndexChanged(object? sender, EventArgs e)
                   => UpdateBackendVisibility();

    private void BrowseDatabaseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Select Vector Database Location",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = Path.GetFileName(string.IsNullOrWhiteSpace(_vectorDatabasePathBox.Text) ? "codevectordb" : _vectorDatabasePathBox.Text),
            OverwritePrompt = false,
        };

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _vectorDatabasePathBox.Text = dialog.FileName;
    }

    private void UpdateBackendVisibility()
    {
        bool isRemote = _backendCombo.SelectedItem?.ToString() == "Remote";
        _remoteGroup.Visible = isRemote;
        _onnxGroup.Visible = !isRemote;
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
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds((double)_timeoutBox.Value) };
        string credentialName = _credentialCombo.Text.Trim();
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

        string baseUrl = DeriveBaseUrl();
        _showModelButton.Enabled = false;

        try
        {
            using var client = CreateAuthedClient();

            // Try the per-model endpoint first (OpenAI-compatible), fall back to the
            // list endpoint (Ollama and others that don't support GET /v1/models/{id}).
            string modelUrl = baseUrl + "/v1/models/" + Uri.EscapeDataString(modelId);
            using var response = await client.GetAsync(modelUrl);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                using var listResponse = await client.GetAsync(baseUrl + "/v1/models");
                string listBody = await listResponse.Content.ReadAsStringAsync();
                if (listResponse.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(listBody);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp)
                                && string.Equals(idProp.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
                            {
                                body = JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true });
                                response.StatusCode = (System.Net.HttpStatusCode)200;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    body = listBody;
                }
            }

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

            var dialog = new ModelInfoDialog(modelId, displayText);
            dialog.Show(this.FindForm());
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

     private async void TestConnectionButton_Click(object? sender, EventArgs e)
     {
         _testConnectionButton.Enabled = false;
         _fetchStatusLabel.ForeColor = SystemColors.GrayText;
         _fetchStatusLabel.Text = "Testing connection…";

         string baseUrl = DeriveBaseUrl();
         string testUrl = baseUrl + "/v1/models";

         try
         {
             using var client = CreateAuthedClient();
             using var response = await client.GetAsync(testUrl, System.Threading.CancellationToken.None);

             if (response.IsSuccessStatusCode)
             {
                 string body = await response.Content.ReadAsStringAsync();
                 _fetchStatusLabel.ForeColor = Color.Green;
                 try
                 {
                     using var doc = JsonDocument.Parse(body);
                          _fetchStatusLabel.Text = $"OK — {doc.RootElement.GetProperty("description").GetString() ?? body}";
                 }
                 catch
                 {
                     _fetchStatusLabel.Text = $"OK — {body.Substring(0, Math.Min(100, body.Length))}";
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
             _testConnectionButton.Enabled = true;
         }
     }

     private void WireAutoSave()
     {
         _vectorDatabasePathBox.Validated += (_, _) => SaveSettings();
         _backendCombo.SelectedIndexChanged += (_, _) => SaveSettings();
         _remoteUrlBox.Validated += (_, _) => SaveSettings();
         _remoteModelCombo.TextChanged += (_, _) => SaveSettings();
         _credentialCombo.TextChanged += (_, _) => SaveSettings();
         _timeoutBox.ValueChanged += (_, _) => SaveSettings();
         _onnxBox.Validated += (_, _) => SaveSettings();
         _onnxMaxSeqBox.ValueChanged += (_, _) => SaveSettings();
         _onnxThreadsBox.ValueChanged += (_, _) => SaveSettings();
         _chunkLinesBox.ValueChanged += (_, _) => SaveSettings();
         _overlapBox.ValueChanged += (_, _) => SaveSettings();
         _maxSizeBox.ValueChanged += (_, _) => SaveSettings();
         _topKBox.ValueChanged += (_, _) => SaveSettings();
         _syncBox.ValueChanged += (_, _) => SaveSettings();
         _logLevelCombo.SelectedIndexChanged += (_, _) => SaveSettings();
         _chkSearch.CheckedChanged += (_, _) => SaveSettings();
         _chkIndex.CheckedChanged += (_, _) => SaveSettings();
         _chkSync.CheckedChanged += (_, _) => SaveSettings();
         _chkStatus.CheckedChanged += (_, _) => SaveSettings();
         _chkRemove.CheckedChanged += (_, _) => SaveSettings();
         _chkReindex.CheckedChanged += (_, _) => SaveSettings();
     }

     private void SaveSettings()
     {
         bool isOnnx = _backendCombo.SelectedItem?.ToString() == "Onnx";

         var settings = _module.Settings;
        var oldBackendType = settings.BackendType;
        string oldDbPath = settings.VectorDatabasePath;
        string oldOnnxFolder = settings.OnnxModelFolder;
        int oldSyncInterval = settings.GitSyncIntervalMinutes;

        settings.BackendType = isOnnx ? BackendType.Onnx : BackendType.Remote;
        settings.RemoteUrl = _remoteUrlBox.Text.Trim();
        settings.RemoteModel = _remoteModelCombo.Text.Trim();
        settings.RemoteCredentialName = _credentialCombo.Text.Trim();
        settings.RemoteTimeoutSeconds = (int)_timeoutBox.Value;
        settings.OnnxModelFolder = _onnxBox.Text.Trim();
        settings.OnnxMaxSequenceLength = (int)_onnxMaxSeqBox.Value;
        settings.OnnxMaxThreads = (int)_onnxThreadsBox.Value;
        settings.ChunkLines = (int)_chunkLinesBox.Value;
        settings.ChunkOverlapLines = (int)_overlapBox.Value;
        settings.MaxFileSizeKb = (int)_maxSizeBox.Value;
        settings.DefaultTopK = (int)_topKBox.Value;
        settings.GitSyncIntervalMinutes = (int)_syncBox.Value;
        if (Enum.TryParse<CodeVectorMcpLogLevel>(_logLevelCombo.SelectedItem?.ToString(), out var logLevel))
            settings.McpLogLevel = logLevel;
        settings.SearchEnabled = _chkSearch.Checked;
        settings.IndexEnabled = _chkIndex.Checked;
        settings.SyncRepoEnabled = _chkSync.Checked;
        settings.StatusEnabled = _chkStatus.Checked;
        settings.RemoveEnabled = _chkRemove.Checked;
        settings.ReindexEnabled = _chkReindex.Checked;
        settings.VectorDatabasePath = _vectorDatabasePathBox.Text.Trim();

        var repo = _module.Repository;
        repo.SaveSetting("backend_type", settings.BackendType.ToString());
        repo.SaveSetting("remote_url", settings.RemoteUrl);
        repo.SaveSetting("remote_model", settings.RemoteModel);
        repo.SaveSetting("remote_credential", settings.RemoteCredentialName);
        repo.SaveSetting("remote_timeout", settings.RemoteTimeoutSeconds.ToString());
        repo.SaveSetting("onnx_folder", settings.OnnxModelFolder);
        repo.SaveSetting("onnx_max_seq", settings.OnnxMaxSequenceLength.ToString());
        repo.SaveSetting("onnx_threads", settings.OnnxMaxThreads.ToString());
        repo.SaveSetting("chunk_lines", settings.ChunkLines.ToString());
        repo.SaveSetting("chunk_overlap", settings.ChunkOverlapLines.ToString());
        repo.SaveSetting("max_file_kb", settings.MaxFileSizeKb.ToString());
        repo.SaveSetting("default_top_k", settings.DefaultTopK.ToString());
        repo.SaveSetting("sync_interval", settings.GitSyncIntervalMinutes.ToString());
        repo.SaveSetting("log_level", settings.McpLogLevel.ToString());
        repo.SaveSetting("search_enabled", settings.SearchEnabled ? "1" : "0");
        repo.SaveSetting("index_enabled", settings.IndexEnabled ? "1" : "0");
        repo.SaveSetting("sync_enabled", settings.SyncRepoEnabled ? "1" : "0");
        repo.SaveSetting("status_enabled", settings.StatusEnabled ? "1" : "0");
        repo.SaveSetting("remove_enabled", settings.RemoveEnabled ? "1" : "0");
        repo.SaveSetting("reindex_enabled", settings.ReindexEnabled ? "1" : "0");
        repo.SaveSetting("vector_database_path", settings.VectorDatabasePath);

        if (oldDbPath != settings.VectorDatabasePath)
            _module.InvalidateVectorDatabase();

        if (oldBackendType != settings.BackendType || oldOnnxFolder != settings.OnnxModelFolder)
            _module.InvalidateEmbeddingBackend();

        if (oldSyncInterval != settings.GitSyncIntervalMinutes)
            _module.InvalidateMirrorManager();
    }
}

internal sealed class RepoDialog : Form
{
    private readonly TextBox _collectionBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly TextBox _urlBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly TextBox _branchBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3), Text = "main" };
    private readonly TextBox _mirrorPathBox = new() { Dock = DockStyle.Fill, Margin = new Padding(3) };
    private readonly ComboBox _credentialCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3) };

    public string CollectionName => _collectionBox.Text.Trim();
    public string RemoteUrl => _urlBox.Text.Trim();
    public string Branch => string.IsNullOrWhiteSpace(_branchBox.Text) ? "main" : _branchBox.Text.Trim();
    public string? MirrorPath => string.IsNullOrWhiteSpace(_mirrorPathBox.Text) ? null : _mirrorPathBox.Text.Trim();
    public string? CredentialName => string.IsNullOrWhiteSpace(_credentialCombo.Text) ? null : _credentialCombo.Text.Trim();

    public RepoDialog(MirrorRegistration? existing)
    {
        Text = existing is null ? "Add Git Repo" : "Edit Git Repo";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 260);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Collection:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 0);
        layout.Controls.Add(_collectionBox, 1, 0);
        layout.Controls.Add(new Label { Text = "Remote URL:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 1);
        layout.Controls.Add(_urlBox, 1, 1);
        layout.Controls.Add(new Label { Text = "Branch:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 2);
        layout.Controls.Add(_branchBox, 1, 2);
        layout.Controls.Add(new Label { Text = "Mirror Path:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 3);
        layout.Controls.Add(_mirrorPathBox, 1, 3);
        layout.Controls.Add(new Label { Text = "Credential:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) }, 0, 4);
        layout.Controls.Add(_credentialCombo, 1, 4);

        var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var okBtn = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(3) };
        var cancelBtn = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(3) };
        btnPanel.Controls.Add(cancelBtn);
        btnPanel.Controls.Add(okBtn);
        layout.Controls.Add(btnPanel, 0, 5);
        layout.SetColumnSpan(btnPanel, 2);

        Controls.Add(layout);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        if (existing is not null)
        {
            _collectionBox.Text = existing.CollectionName;
            _urlBox.Text = existing.RemoteUrl;
            _branchBox.Text = existing.Branch;
            _mirrorPathBox.Text = existing.MirrorPath ?? "";
            _credentialCombo.Text = existing.CredentialName ?? "";
        }
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
