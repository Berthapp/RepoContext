using Microsoft.Data.Sqlite;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Storage;

/// <summary>Well-known keys in the <c>meta</c> table.</summary>
public static class MetaKeys
{
    public const string SchemaVersion = "schema_version";
    public const string ToolVersion = "tool_version";
    public const string ConfigHash = "config_hash";
    public const string IndexedAtUtc = "indexed_at_utc";
    public const string FileCount = "file_count";
    public const string ChunkCount = "chunk_count";
}

/// <summary>Existing file identity used for incremental diffing.</summary>
public readonly record struct FileRecord(long Id, string ContentHash);

/// <summary>
/// The SQLite-backed index store. Owns the connection and all reads/writes.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _connection;

    private IndexStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (creating if needed) the index database and ensures the schema.</summary>
    public static IndexStore Open(string databasePath)
    {
        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        var store = new IndexStore(connection);
        store.Execute(IndexSchema.Ddl);
        return store;
    }

    public string? GetMeta(string key)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v) " +
                          "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns all indexed files keyed by their repo-relative path.</summary>
    public Dictionary<string, FileRecord> GetExistingFiles()
    {
        var map = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, path, content_hash FROM files";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(1)] = new FileRecord(reader.GetInt64(0), reader.GetString(2));
        }

        return map;
    }

    /// <summary>Removes all files and chunks (used by <c>--full</c> / config change).</summary>
    public void Clear()
    {
        Execute("DELETE FROM chunks_fts; DELETE FROM chunks; DELETE FROM files;");
    }

    public SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    /// <summary>Deletes a file and its chunks (including FTS rows).</summary>
    public void DeleteFile(long fileId, SqliteTransaction transaction)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE file_id = $f);" +
            "DELETE FROM files WHERE id = $f;";
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts a file and its chunks; returns the number of chunks written.</summary>
    public int InsertFile(
        string path, FileKindLabel labels, long sizeBytes, int lineCount,
        string contentHash, IReadOnlyList<Chunk> chunks, SqliteTransaction transaction)
    {
        long fileId;
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                "INSERT INTO files(path, kind, language, size_bytes, line_count, content_hash) " +
                "VALUES($p, $k, $l, $s, $lc, $h); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$k", labels.Kind);
            cmd.Parameters.AddWithValue("$l", labels.Language);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$lc", lineCount);
            cmd.Parameters.AddWithValue("$h", contentHash);
            fileId = (long)cmd.ExecuteScalar()!;
        }

        foreach (Chunk chunk in chunks)
        {
            InsertChunk(fileId, chunk, transaction);
        }

        return chunks.Count;
    }

    private void InsertChunk(long fileId, Chunk chunk, SqliteTransaction transaction)
    {
        long chunkId;
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                "INSERT INTO chunks(file_id, kind, start_line, end_line, heading, content) " +
                "VALUES($f, $k, $s, $e, $h, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.Parameters.AddWithValue("$k", chunk.Kind.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$s", chunk.StartLine);
            cmd.Parameters.AddWithValue("$e", chunk.EndLine);
            cmd.Parameters.AddWithValue("$h", (object?)chunk.Heading ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", chunk.Content);
            chunkId = (long)cmd.ExecuteScalar()!;
        }

        using SqliteCommand fts = _connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText = "INSERT INTO chunks_fts(rowid, content) VALUES($id, $c)";
        fts.Parameters.AddWithValue("$id", chunkId);
        fts.Parameters.AddWithValue("$c", chunk.Content);
        fts.ExecuteNonQuery();
    }

    /// <summary>Runs a BM25 full-text search and returns the best chunk per file.</summary>
    public IReadOnlyList<SearchHit> Search(string matchExpression, int top)
    {
        var best = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT f.path, f.kind, c.kind, c.start_line, c.end_line, c.heading, " +
                "       bm25(chunks_fts) AS score " +
                "FROM chunks_fts " +
                "JOIN chunks c ON c.id = chunks_fts.rowid " +
                "JOIN files f ON f.id = c.file_id " +
                "WHERE chunks_fts MATCH $q " +
                "ORDER BY score ASC LIMIT $cap";
            cmd.Parameters.AddWithValue("$q", matchExpression);
            cmd.Parameters.AddWithValue("$cap", Math.Max(top * 20, 200));
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                double score = -reader.GetDouble(6); // bm25: lower is better -> flip.
                if (best.TryGetValue(path, out SearchHit? existing) && existing.Score >= score)
                {
                    continue;
                }

                best[path] = new SearchHit
                {
                    Path = path,
                    Kind = reader.GetString(1),
                    ChunkKind = reader.GetString(2),
                    StartLine = reader.GetInt32(3),
                    EndLine = reader.GetInt32(4),
                    Heading = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Score = score,
                    Reasons = ["fts"],
                };
            }
        }

        return best.Values
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    /// <summary>Returns the total number of chunks in the index.</summary>
    public int CountChunks()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM chunks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

/// <summary>String labels for a file's kind and language as stored in the DB.</summary>
public readonly record struct FileKindLabel(string Kind, string Language);
