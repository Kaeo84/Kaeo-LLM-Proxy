using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Kaeo.LlmProxy.Module.CodeVector;

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
