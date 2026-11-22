using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Text.RegularExpressions;
using Serilog;

namespace Kaeo.LlmProxy.Module.CodeVector;

internal sealed class CodeVectorDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>True when the sqlite-vec native extension loaded successfully. When false,
    /// all vector operations fall back to the brute-force scan.</summary>
    private bool _vecAvailable;

    /// <summary>M3: cached vec0 table metadata per collection, invalidated on delete/dimension
    /// change, so chunk inserts/deletes do not re-query collections + sqlite_master every time.</summary>
    private readonly Dictionary<string, VecTableInfo?> _vecTableCache = new(StringComparer.Ordinal);

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
        // Each PRAGMA runs as its own command and journal_mode is read back so both settings
        // are verifiably applied instead of relying on multi-statement batch semantics.
        using (var journal = _connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            object? mode = journal.ExecuteScalar();
            if (!string.Equals(mode as string, "wal", StringComparison.OrdinalIgnoreCase))
                Log.Warning("CodeVector: SQLite journal_mode is {Mode}; WAL was not applied", mode);
        }
        using (var foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
            foreignKeys.ExecuteNonQuery();
        }
        using var schema = _connection.CreateCommand();
        schema.CommandText = VectorSchema;
        schema.ExecuteNonQuery();

        // M14 backstop: prevent duplicate (file_id, chunk_index) rows from concurrent indexers.
        // Created separately because databases that already contain duplicates cannot build it;
        // in that case the in-flight dedup in the engine still prevents new duplicates.
        try
        {
            using var unique = _connection.CreateCommand();
            unique.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_chunks_file_chunk ON codevector_chunks (file_id, chunk_index)";
            unique.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            Log.Warning("CodeVector: could not create UNIQUE (file_id, chunk_index) index ({Message}); existing duplicate chunks may be present. Reindex affected collections to clean up.", ex.Message);
        }

        // Load the sqlite-vec extension for native vector search. When the native library is
        // unavailable (e.g. not deployed next to the host) the store still works: search falls
        // back to the brute-force cosine scan and no vec0 tables are created.
        try
        {
            _connection.LoadExtension("vec0");
            _vecAvailable = true;
            Log.Debug("CodeVector: sqlite-vec extension loaded; native vector search enabled");
        }
        catch (Exception ex)
        {
            _vecAvailable = false;
            Log.Warning("CodeVector: sqlite-vec extension unavailable ({Message}); native vector search disabled, using brute-force fallback", ex.Message);
        }
    }

    public long GetOrCreateCollection(string name, string model, int dimension)
    {
        lock (_lock)
        {
            long id;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT rowid, dimension, embedding_model FROM codevector_collections WHERE name = $name";
                AddParam(cmd, "$name", name);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    id = reader.GetInt64(0);
                    int storedDim = reader.GetInt32(1);
                    string storedModel = reader.GetString(2);
                    // M12: refuse to mix incompatible vector dimensions in one collection.
                    if (storedDim > 0 && dimension > 0 && storedDim != dimension)
                        throw new InvalidOperationException(
                            $"Collection '{name}' was indexed with {storedDim}-dimensional vectors (model '{storedModel}'), but the current backend produces {dimension}-dimensional vectors. Run code_reindex on '{name}' to rebuild it with the new model.");
                    // Self-heal legacy collections created before dimension tracking (stored as 0).
                    if (storedDim <= 0 && dimension > 0)
                    {
                        using var update = _connection.CreateCommand();
                        update.CommandText = "UPDATE codevector_collections SET dimension = $dim WHERE rowid = $id";
                        AddParam(update, "$dim", dimension);
                        AddParam(update, "$id", id);
                        update.ExecuteNonQuery();
                        _vecTableCache.Remove(name);
                        Log.Information("CodeVector: repaired stored dimension {Dim} for collection {Collection}", dimension, name);
                    }
                    if (dimension > 0 && storedDim == dimension && !string.Equals(storedModel, model, StringComparison.Ordinal))
                        Log.Warning("CodeVector: collection {Collection} was indexed with model {OldModel}; now using {NewModel} (same dimension). Run code_reindex to refresh embeddings.", name, storedModel, model);
                }
                else
                {
                    using var insert = _connection.CreateCommand();
                    insert.CommandText = "INSERT INTO codevector_collections (name, embedding_model, dimension, created_utc) VALUES ($name, $model, $dim, $created)";
                    AddParam(insert, "$name", name);
                    AddParam(insert, "$model", model);
                    AddParam(insert, "$dim", dimension);
                    AddParam(insert, "$created", DateTime.UtcNow.ToString("o"));
                    insert.ExecuteNonQuery();
                    using var lastId = _connection.CreateCommand();
                    lastId.CommandText = "SELECT last_insert_rowid()";
                    id = Convert.ToInt64(lastId.ExecuteScalar());
                }
            }
            EnsureVecTable(name);
            return id;
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

    public void DeleteFileChunks(long fileId, string collectionName)
    {
        lock (_lock)
        {
            var info = FindVecTable(collectionName);
            using var tx = _connection.BeginTransaction();
            try
            {
                if (info is not null)
                {
                    using var vcmd = _connection.CreateCommand();
                    vcmd.CommandText = $"DELETE FROM {info.Table} WHERE rowid IN (SELECT id FROM codevector_chunks WHERE file_id = $fid)";
                    AddParam(vcmd, "$fid", fileId);
                    vcmd.ExecuteNonQuery();
                }
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codevector_chunks WHERE file_id = $fid";
                AddParam(cmd, "$fid", fileId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void InsertChunk(long fileId, string collectionName, int chunkIndex, int startLine, int endLine, string text, float[] embedding)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO codevector_chunks (file_id, chunk_index, start_line, end_line, text, embedding) VALUES ($fid, $idx, $sl, $el, $txt, $emb) RETURNING id";
            AddParam(cmd, "$fid", fileId);
            AddParam(cmd, "$idx", chunkIndex);
            AddParam(cmd, "$sl", startLine);
            AddParam(cmd, "$el", endLine);
            AddParam(cmd, "$txt", text);
            AddParam(cmd, "$emb", EmbeddingToBlob(embedding));
            long chunkId = Convert.ToInt64(cmd.ExecuteScalar());

            var info = EnsureVecTable(collectionName);
            if (info is not null && embedding.Length == info.Dimension)
            {
                using var vcmd = _connection.CreateCommand();
                vcmd.CommandText = $"INSERT INTO {info.Table} (rowid, embedding) VALUES ($id, $emb)";
                AddParam(vcmd, "$id", chunkId);
                AddParam(vcmd, "$emb", EmbeddingToBlob(Normalize(embedding)));
                vcmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// M3: inserts all chunks of one file in a single transaction with reused parameters,
    /// instead of one lock acquisition + vec-table lookup + last_insert_rowid() per chunk.
    /// Embeddings must already be computed; empty vectors are rejected (M4).
    /// </summary>
    public void InsertChunks(long fileId, string collectionName, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings)
    {
        if (chunks.Count != embeddings.Count)
            throw new ArgumentException($"Chunk/embedding count mismatch: {chunks.Count} chunks vs {embeddings.Count} embeddings.");
        lock (_lock)
        {
            // Resolve/create the vec table BEFORE opening the transaction: creation may run
            // its own backfill transaction, which cannot nest.
            var info = EnsureVecTable(collectionName);
            using var tx = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "INSERT INTO codevector_chunks (file_id, chunk_index, start_line, end_line, text, embedding) VALUES ($fid, $idx, $sl, $el, $txt, $emb) RETURNING id";
                var pFid = cmd.CreateParameter(); pFid.ParameterName = "$fid"; pFid.Value = fileId; cmd.Parameters.Add(pFid);
                var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
                var pSl = cmd.CreateParameter(); pSl.ParameterName = "$sl"; cmd.Parameters.Add(pSl);
                var pEl = cmd.CreateParameter(); pEl.ParameterName = "$el"; cmd.Parameters.Add(pEl);
                var pTxt = cmd.CreateParameter(); pTxt.ParameterName = "$txt"; cmd.Parameters.Add(pTxt);
                var pEmb = cmd.CreateParameter(); pEmb.ParameterName = "$emb"; cmd.Parameters.Add(pEmb);

                SqliteCommand? vcmd = null;
                SqliteParameter? vpId = null, vpEmb = null;
                if (info is not null)
                {
                    vcmd = _connection.CreateCommand();
                    vcmd.CommandText = $"INSERT INTO {info.Table} (rowid, embedding) VALUES ($id, $emb)";
                    vpId = vcmd.CreateParameter(); vpId.ParameterName = "$id"; vcmd.Parameters.Add(vpId);
                    vpEmb = vcmd.CreateParameter(); vpEmb.ParameterName = "$emb"; vcmd.Parameters.Add(vpEmb);
                }
                try
                {
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var chunk = chunks[i];
                        var embedding = embeddings[i];
                        if (embedding.Length == 0)
                            throw new InvalidOperationException($"Empty embedding for chunk {chunk.Index}; refusing to store a dead vector.");
                        pIdx.Value = chunk.Index;
                        pSl.Value = chunk.StartLine;
                        pEl.Value = chunk.EndLine;
                        pTxt.Value = chunk.Text;
                        pEmb.Value = EmbeddingToBlob(embedding);
                        long chunkId = Convert.ToInt64(cmd.ExecuteScalar());
                        if (vcmd is not null && embedding.Length == info!.Dimension)
                        {
                            vpId!.Value = chunkId;
                            vpEmb!.Value = EmbeddingToBlob(Normalize(embedding));
                            vcmd.ExecuteNonQuery();
                        }
                    }
                }
                finally
                {
                    vcmd?.Dispose();
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public IReadOnlyList<SearchResult> Search(string collectionName, float[] queryEmbedding, int topK, string? pathFilter = null)
    {
        lock (_lock)
        {
            var info = EnsureVecTable(collectionName);
            if (info is not null && queryEmbedding.Length == info.Dimension)
            {
                try
                {
                    return NativeSearch(info.Table, collectionName, queryEmbedding, topK, pathFilter);
                }
                catch (Exception ex)
                {
                    Log.Warning("CodeVector: native vector search failed for {Collection} ({Message}); falling back to brute force", collectionName, ex.Message);
                }
            }
            return BruteForceSearch(collectionName, queryEmbedding, topK, pathFilter);
        }
    }

    /// <summary>
    /// Native sqlite-vec KNN search. Stored vectors are L2-normalized, so the returned L2
    /// distance converts exactly to cosine similarity via cos = 1 - d²/2.
    /// M1: sqlite-vec cuts the global top-k BEFORE any path filter can apply, so with a
    /// filter we over-fetch and post-filter by prefix, growing k until enough matches are
    /// found or the index is exhausted. Without a filter a single k = topK query suffices.
    /// </summary>
    private IReadOnlyList<SearchResult> NativeSearch(string table, string collectionName, float[] queryEmbedding, int topK, string? pathFilter)
    {
        bool hasFilter = !string.IsNullOrEmpty(pathFilter);
        int k = hasFilter ? Math.Max(topK * 10, 100) : topK;
        const int maxK = 100_000;
        while (true)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT c.id, c.text, c.start_line, c.end_line, f.path, v.distance FROM {ValidateVecTableName(table)} v " +
                "JOIN codevector_chunks c ON c.id = v.rowid " +
                "JOIN codevector_files f ON f.id = c.file_id " +
                "WHERE v.embedding MATCH $q AND k = $k AND f.collection_name = $col";
            AddParam(cmd, "$q", EmbeddingToBlob(Normalize(queryEmbedding)));
            AddParam(cmd, "$k", k);
            AddParam(cmd, "$col", collectionName);
            var results = new List<SearchResult>();
            int rawCount = 0;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    rawCount++;
                    string path = reader.GetString(4);
                    if (hasFilter && !path.StartsWith(pathFilter!, StringComparison.OrdinalIgnoreCase)) continue;
                    double dist = reader.GetDouble(5);
                    float sim = (float)Math.Clamp(1.0 - (dist * dist) / 2.0, -1.0, 1.0);
                    results.Add(new SearchResult { ChunkId = reader.GetInt64(0), Text = reader.GetString(1), StartLine = reader.GetInt32(2), EndLine = reader.GetInt32(3), FilePath = path, Similarity = sim });
                }
            }
            if (!hasFilter || results.Count >= topK || rawCount < k || k >= maxK)
                return results.Count > topK ? results.Take(topK).ToList() : results;
            k = Math.Min(k * 4, maxK);
        }
    }

    /// <summary>
    /// M2: streaming bounded top-k — only the current k winners (with their text) are kept,
    /// instead of materializing every chunk of the collection before sorting.
    /// M1: prefix semantics with escaped LIKE metacharacters, matching the native path.
    /// </summary>
    private IReadOnlyList<SearchResult> BruteForceSearch(string collectionName, float[] queryEmbedding, int topK, string? pathFilter)
    {
        var best = new List<SearchResult>(Math.Max(topK, 1));
        if (topK <= 0) return best;
        using var cmd = _connection.CreateCommand();
        string sql = "SELECT c.id, c.text, c.start_line, c.end_line, c.embedding, f.path FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col";
        if (!string.IsNullOrEmpty(pathFilter)) sql += " AND f.path LIKE $pf ESCAPE '\\'";
        cmd.CommandText = sql;
        AddParam(cmd, "$col", collectionName);
        if (!string.IsNullOrEmpty(pathFilter)) AddParam(cmd, "$pf", EscapeLike(pathFilter) + "%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(4);
            // Skip dimension-mismatched chunks without allocating a float array for them.
            if (blob.Length != queryEmbedding.Length * sizeof(float)) continue;
            var sim = CosineSimilarity(queryEmbedding, BlobToEmbedding(blob));
            if (best.Count >= topK && sim <= best[^1].Similarity) continue;
            var item = new SearchResult { ChunkId = reader.GetInt64(0), Text = reader.GetString(1), StartLine = reader.GetInt32(2), EndLine = reader.GetInt32(3), FilePath = reader.GetString(5), Similarity = sim };
            int pos = 0;
            while (pos < best.Count && best[pos].Similarity >= sim) pos++;
            best.Insert(pos, item);
            if (best.Count > topK) best.RemoveAt(topK);
        }
        return best;
    }

    /// <summary>Escapes LIKE metacharacters so user filters match literally (used with ESCAPE '\').</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
            _vecTableCache.Remove(name);
            // L13: all-or-nothing delete so a mid-way failure cannot orphan chunks/file rows.
            using var tx = _connection.BeginTransaction();
            try
            {
                using (var idCmd = _connection.CreateCommand())
                {
                    idCmd.CommandText = "SELECT rowid FROM codevector_collections WHERE name = $name";
                    AddParam(idCmd, "$name", name);
                    var idObj = idCmd.ExecuteScalar();
                    if (idObj is not null)
                        DropVecTables(Convert.ToInt64(idObj));
                }
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
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void DeleteFilesByPathPrefix(string collectionName, string pathPrefix)
    {
        lock (_lock)
        {
            // Find (not Ensure): a delete must never create/backfill a vec table inside its transaction.
            var info = FindVecTable(collectionName);
            string like = EscapeLike(pathPrefix) + "%";
            using var tx = _connection.BeginTransaction();
            try
            {
                if (info is not null)
                {
                    using var vcmd = _connection.CreateCommand();
                    vcmd.CommandText = $"DELETE FROM {info.Table} WHERE rowid IN (SELECT c.id FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col AND f.path LIKE $pf ESCAPE '\\')";
                    AddParam(vcmd, "$col", collectionName);
                    AddParam(vcmd, "$pf", like);
                    vcmd.ExecuteNonQuery();
                }
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codevector_chunks WHERE file_id IN (SELECT id FROM codevector_files WHERE collection_name = $col AND path LIKE $pf ESCAPE '\\')";
                AddParam(cmd, "$col", collectionName);
                AddParam(cmd, "$pf", like);
                cmd.ExecuteNonQuery();
                using var cmd2 = _connection.CreateCommand();
                cmd2.CommandText = "DELETE FROM codevector_files WHERE collection_name = $col AND path LIKE $pf ESCAPE '\\'";
                AddParam(cmd2, "$col", collectionName);
                AddParam(cmd2, "$pf", like);
                cmd2.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void DeleteFile(string collectionName, string path)
    {
        lock (_lock)
        {
            var info = FindVecTable(collectionName);
            using var tx = _connection.BeginTransaction();
            try
            {
                if (info is not null)
                {
                    using var vcmd = _connection.CreateCommand();
                    vcmd.CommandText = $"DELETE FROM {info.Table} WHERE rowid IN (SELECT c.id FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col AND f.path = $path)";
                    AddParam(vcmd, "$col", collectionName);
                    AddParam(vcmd, "$path", path);
                    vcmd.ExecuteNonQuery();
                }
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codevector_chunks WHERE file_id IN (SELECT id FROM codevector_files WHERE collection_name = $col AND path = $path)";
                AddParam(cmd, "$col", collectionName);
                AddParam(cmd, "$path", path);
                cmd.ExecuteNonQuery();
                using var cmd2 = _connection.CreateCommand();
                cmd2.CommandText = "DELETE FROM codevector_files WHERE collection_name = $col AND path = $path";
                AddParam(cmd2, "$col", collectionName);
                AddParam(cmd2, "$path", path);
                cmd2.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
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

    /// <summary>
    /// Ensures the collection's vec0 virtual table (sqlite-vec) exists and is populated.
    /// Tables are keyed by collection rowid and embedding dimension so collections with
    /// different embedding models can coexist. Returns null when native search is
    /// unavailable or the collection does not exist.
    /// </summary>
    private VecTableInfo? EnsureVecTable(string collectionName)
    {
        if (!_vecAvailable) return null;
        // M3: hot path (every chunk insert/delete) hits the cache instead of re-querying
        // collection metadata + sqlite_master under the global lock.
        if (_vecTableCache.TryGetValue(collectionName, out var cached)) return cached;
        var info = EnsureVecTableCore(collectionName);
        if (info is not null) _vecTableCache[collectionName] = info;
        return info;
    }

    /// <summary>
    /// Looks up an existing vec0 table without creating or backfilling one. Used by delete
    /// paths so they can run inside a transaction (creation would nest a second transaction).
    /// </summary>
    private VecTableInfo? FindVecTable(string collectionName)
    {
        if (!_vecAvailable) return null;
        if (_vecTableCache.TryGetValue(collectionName, out var cached)) return cached;
        long colId;
        int dimension;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid, dimension FROM codevector_collections WHERE name = $name";
            AddParam(cmd, "$name", collectionName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            colId = reader.GetInt64(0);
            dimension = reader.GetInt32(1);
        }
        if (dimension <= 0) return null;
        string table = VecTableName(colId, dimension);
        using var exists = _connection.CreateCommand();
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $table";
        AddParam(exists, "$table", table);
        if (exists.ExecuteScalar() is null) return null;
        var info = new VecTableInfo(colId, dimension, table);
        _vecTableCache[collectionName] = info;
        return info;
    }

    private VecTableInfo? EnsureVecTableCore(string collectionName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, dimension FROM codevector_collections WHERE name = $name";
        AddParam(cmd, "$name", collectionName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        long colId = reader.GetInt64(0);
        int dimension = reader.GetInt32(1);
        if (dimension <= 0)
        {
            // Legacy collection with unknown dimension: infer it from an existing chunk and persist it.
            using var infer = _connection.CreateCommand();
            infer.CommandText = "SELECT length(c.embedding) / 4 FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col LIMIT 1";
            AddParam(infer, "$col", collectionName);
            var inferred = infer.ExecuteScalar();
            dimension = inferred is null ? 0 : Convert.ToInt32(inferred);
            if (dimension > 0)
            {
                using var update = _connection.CreateCommand();
                update.CommandText = "UPDATE codevector_collections SET dimension = $dim WHERE rowid = $id";
                AddParam(update, "$dim", dimension);
                AddParam(update, "$id", colId);
                update.ExecuteNonQuery();
                Log.Information("CodeVector: inferred stored dimension {Dim} for collection {Collection}", dimension, collectionName);
            }
            if (dimension <= 0) return null;
        }
        string table = VecTableName(colId, dimension);

        using var exists = _connection.CreateCommand();
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $table";
        AddParam(exists, "$table", table);
        if (exists.ExecuteScalar() is null)
        {
            using var create = _connection.CreateCommand();
            create.CommandText = $"CREATE VIRTUAL TABLE {table} USING vec0(embedding float[{dimension}])";
            create.ExecuteNonQuery();
            BackfillVecTable(table, collectionName, dimension);
            DropStaleVecTables(colId, dimension);
            Log.Information("CodeVector: created native vector table {Table} for collection {Collection}", table, collectionName);
        }
        return new VecTableInfo(colId, dimension, table);
    }

    /// <summary>
    /// Populates a freshly created vec0 table from existing chunks. Embeddings are
    /// L2-normalized so the native L2 distance is monotonic with cosine similarity.
    /// Only chunks matching the table's dimension are included.
    /// </summary>
    private void BackfillVecTable(string table, string collectionName, int dimension)
    {
        using var tx = _connection.BeginTransaction();
        try
        {
            using var read = _connection.CreateCommand();
            read.CommandText = "SELECT c.id, c.embedding FROM codevector_chunks c JOIN codevector_files f ON c.file_id = f.id WHERE f.collection_name = $col AND length(c.embedding) = $bytes";
            AddParam(read, "$col", collectionName);
            AddParam(read, "$bytes", dimension * sizeof(float));

            using var insert = _connection.CreateCommand();
            insert.CommandText = $"INSERT INTO {table} (rowid, embedding) VALUES ($id, $emb)";
            var pId = insert.CreateParameter();
            pId.ParameterName = "$id";
            insert.Parameters.Add(pId);
            var pEmb = insert.CreateParameter();
            pEmb.ParameterName = "$emb";
            insert.Parameters.Add(pEmb);

            int count = 0;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                pId.Value = reader.GetInt64(0);
                pEmb.Value = EmbeddingToBlob(Normalize(BlobToEmbedding((byte[])reader.GetValue(1))));
                insert.ExecuteNonQuery();
                count++;
            }
            tx.Commit();
            Log.Debug("CodeVector: backfilled {Count} vectors into {Table}", count, table);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Drops all vec0 tables created for a collection (across all dimensions).</summary>
    private void DropVecTables(long colId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name GLOB $glob";
        AddParam(cmd, "$glob", $"cv_vec_{colId}_*");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = $"DROP TABLE {ValidateVecTableName(reader.GetString(0))}";
            drop.ExecuteNonQuery();
        }
    }

    /// <summary>Drops vec0 tables for a collection created under a previous dimension.</summary>
    private void DropStaleVecTables(long colId, int currentDimension)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name GLOB $glob AND name != $current";
        AddParam(cmd, "$glob", $"cv_vec_{colId}_*");
        AddParam(cmd, "$current", VecTableName(colId, currentDimension));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = $"DROP TABLE {ValidateVecTableName(reader.GetString(0))}";
            drop.ExecuteNonQuery();
        }
    }

    /// <summary>Strict shape of the generated vec0 table names (<see cref="VecTableName"/>).</summary>
    private static readonly Regex VecTableNamePattern = new(@"^cv_vec_\d+_\d+$", RegexOptions.Compiled);

    /// <summary>
    /// SQL cannot parameterize identifiers, so every vec0 table name is re-validated against the
    /// strict generated shape before it is interpolated into a statement. Names originate either
    /// from <see cref="VecTableName"/> (numeric inputs, safe by construction) or are read back
    /// from <c>sqlite_master</c>; refusing anything else keeps the interpolation sites safe
    /// against tampered metadata and future callers.
    /// </summary>
    private static string ValidateVecTableName(string name) =>
        VecTableNamePattern.IsMatch(name)
            ? name
            : throw new InvalidOperationException(
                $"Unexpected vector table name '{name}' does not match the cv_vec_<colId>_<dimension> shape.");

    private static string VecTableName(long colId, int dimension) => ValidateVecTableName($"cv_vec_{colId}_{dimension}");

    /// <summary>L2-normalizes an embedding so L2 distance becomes monotonic with cosine similarity.</summary>
    private static float[] Normalize(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += v[i] * v[i];
        norm = Math.Sqrt(norm);
        if (norm == 0) return (float[])v.Clone();
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    private sealed record VecTableInfo(long CollectionId, int Dimension, string Table);

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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
