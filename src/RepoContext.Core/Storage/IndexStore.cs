using Microsoft.Data.Sqlite;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Parsing;

namespace RepoContext.Core.Storage;

/// <summary>Well-known keys in the <c>meta</c> table.</summary>
public static class MetaKeys
{
    public const string SchemaVersion = "schema_version";
    public const string ToolVersion = "tool_version";
    /// <summary>
    /// Hash of indexing-affecting settings only. Live ranking/output settings
    /// belong to result analysis identity and do not stale the stored corpus.
    /// </summary>
    public const string ConfigHash = "config_hash";
    public const string IndexedAtUtc = "indexed_at_utc";
    public const string FileCount = "file_count";
    public const string ChunkCount = "chunk_count";
    public const string SymbolCount = "symbol_count";
    public const string EdgeCount = "edge_count";

    /// <summary>SHA-256 over all (path, content_hash) pairs: identifies the index state.</summary>
    public const string StateHash = "state_hash";

    /// <summary>
    /// Catch-all fingerprint of every index-time producer (scanner, decoder,
    /// parser, chunker, tokenizer, graph) whose output is stored on disk (Q4,
    /// ADR 0015). Query commands reject an index whose stored value is stale, the
    /// same way an outdated on-disk schema is rejected.
    /// </summary>
    public const string AnalysisProducerVersion = "analysis_producer_version";
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

    /// <summary>
    /// Drops and recreates all data tables (used by <c>--full</c>, config or
    /// schema-version change). Recreating rather than deleting rows lets a
    /// rebuild migrate an index across on-disk schema versions.
    /// </summary>
    public void Reset()
    {
        Execute(IndexSchema.DropDataTables);
        Execute(IndexSchema.Ddl);
    }

    /// <summary>
    /// Whether the on-disk schema matches <see cref="IndexSchema.Version"/>.
    /// False for an index written by an older tool version; query commands
    /// treat that as "no usable index" and ask for a re-index.
    /// </summary>
    public bool IsSchemaCurrent =>
        GetMeta(MetaKeys.SchemaVersion) == IndexSchema.Version.ToString();

    /// <summary>
    /// Whether the stored analysis producers match the running build (Q4). A
    /// parser, chunker, decoder, tokenizer or graph change alters stored analysis
    /// without changing a byte of source, so the index must be rebuilt before its
    /// contents can be trusted — and before any receipt derived from them is
    /// honoured.
    /// </summary>
    public bool IsProducerCurrent =>
        GetMeta(MetaKeys.AnalysisProducerVersion) == Identity.ProducerVersions.AnalysisProducerVersion;

    /// <summary>Whether the index records a complete SHA-256 content-state fingerprint.</summary>
    public bool HasValidStateHash
    {
        get
        {
            string? value = GetMeta(MetaKeys.StateHash);
            return value is { Length: 64 } && value.All(Uri.IsHexDigit);
        }
    }

    /// <summary>
    /// Whether the indexed corpus was scanned under the current indexing-only
    /// configuration. Live ranking/synonym changes intentionally do not make the
    /// stored corpus stale.
    /// </summary>
    public bool IsIndexConfigCurrent(RepoctxConfig config) =>
        GetMeta(MetaKeys.ConfigHash) == ConfigStore.ComputeIndexHash(config);

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

    /// <summary>Inserts a file, its chunks and its symbols (with symbol chunks).</summary>
    public void InsertFile(
        string path, FileKindLabel labels, long sizeBytes, int lineCount, int tokenCount,
        string contentHash, IReadOnlyList<Chunk> chunks, IReadOnlyList<Symbol> symbols,
        SqliteTransaction transaction)
    {
        long fileId;
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                "INSERT INTO files(path, kind, language, size_bytes, line_count, content_hash, token_count) " +
                "VALUES($p, $k, $l, $s, $lc, $h, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$k", labels.Kind);
            cmd.Parameters.AddWithValue("$l", labels.Language);
            cmd.Parameters.AddWithValue("$s", sizeBytes);
            cmd.Parameters.AddWithValue("$lc", lineCount);
            cmd.Parameters.AddWithValue("$h", contentHash);
            cmd.Parameters.AddWithValue("$t", tokenCount);
            fileId = (long)cmd.ExecuteScalar()!;
        }

        foreach (Chunk chunk in chunks)
        {
            InsertChunk(fileId, chunk, transaction);
        }

        foreach (Symbol symbol in symbols)
        {
            InsertSymbol(fileId, symbol, transaction);
            InsertChunk(fileId, SymbolChunk.From(symbol), transaction);
        }
    }

    private void InsertSymbol(long fileId, Symbol symbol, SqliteTransaction transaction)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "INSERT INTO symbols(file_id, name, kind, start_line, end_line, signature, doc) " +
            "VALUES($f, $n, $k, $s, $e, $sig, $doc)";
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.Parameters.AddWithValue("$n", symbol.Name);
        cmd.Parameters.AddWithValue("$k", symbol.Kind.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$s", symbol.StartLine);
        cmd.Parameters.AddWithValue("$e", symbol.EndLine);
        cmd.Parameters.AddWithValue("$sig", symbol.Signature);
        cmd.Parameters.AddWithValue("$doc", (object?)symbol.Doc ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the total number of symbols in the index.</summary>
    public int CountSymbols()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM symbols";
        return Convert.ToInt32(cmd.ExecuteScalar());
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
    /// <param name="symbolsOnly">When true, only symbol chunks are considered.</param>
    public IReadOnlyList<SearchHit> Search(string matchExpression, int top, bool symbolsOnly = false)
    {
        var best = new List<SearchHit>();
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "WITH matched AS MATERIALIZED (" +
                "  SELECT f.path, f.kind AS file_kind, c.kind AS chunk_kind, " +
                "         c.start_line, c.end_line, c.heading, bm25(chunks_fts) AS score " +
                "  FROM chunks_fts " +
                "  JOIN chunks c ON c.id = chunks_fts.rowid " +
                "  JOIN files f ON f.id = c.file_id " +
                "  WHERE chunks_fts MATCH $q " +
                (symbolsOnly ? "AND c.kind = 'symbol' " : string.Empty) +
                "), ranked AS (" +
                "  SELECT *, row_number() OVER (" +
                "    PARTITION BY path ORDER BY score ASC, start_line ASC, end_line ASC, " +
                "    chunk_kind COLLATE BINARY ASC, coalesce(heading, '') COLLATE BINARY ASC" +
                "  ) AS file_rank FROM matched" +
                ") " +
                "SELECT path, file_kind, chunk_kind, start_line, end_line, heading, score " +
                "FROM ranked WHERE file_rank = 1 " +
                "ORDER BY score ASC, path COLLATE BINARY ASC, start_line ASC, end_line ASC, " +
                "chunk_kind COLLATE BINARY ASC, coalesce(heading, '') COLLATE BINARY ASC " +
                "LIMIT $cap";
            cmd.Parameters.AddWithValue("$q", matchExpression);
            cmd.Parameters.AddWithValue("$cap", top);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                best.Add(new SearchHit
                {
                    Path = reader.GetString(0),
                    Kind = reader.GetString(1),
                    ChunkKind = reader.GetString(2),
                    StartLine = reader.GetInt32(3),
                    EndLine = reader.GetInt32(4),
                    Heading = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Score = -reader.GetDouble(6), // bm25: lower is better -> flip.
                    Reasons = ["fts"],
                });
            }
        }

        return best;
    }

    /// <summary>
    /// Returns <i>all</i> matching chunks rather than the single best one per
    /// file, so context generation can retain several pieces of evidence from
    /// the same file (Q2). A per-file cap keeps one repetitive file from
    /// consuming the whole global limit, and both caps are applied after a
    /// deterministic ordering so the cut is stable.
    /// </summary>
    /// <param name="matchExpression">The FTS5 MATCH expression.</param>
    /// <param name="perFileCap">Maximum hits retained per file.</param>
    /// <param name="globalCap">Maximum hits returned overall.</param>
    /// <param name="symbolsOnly">When true, only symbol chunks are considered.</param>
    public IReadOnlyList<SearchHit> SearchEvidence(
        string matchExpression, int perFileCap, int globalCap, bool symbolsOnly = false)
    {
        var all = new List<SearchHit>();
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "WITH matched AS MATERIALIZED (" +
                "  SELECT f.path, f.kind AS file_kind, c.kind AS chunk_kind, " +
                "         c.start_line, c.end_line, c.heading, bm25(chunks_fts) AS score " +
                "  FROM chunks_fts " +
                "  JOIN chunks c ON c.id = chunks_fts.rowid " +
                "  JOIN files f ON f.id = c.file_id " +
                "  WHERE chunks_fts MATCH $q " +
                (symbolsOnly ? "AND c.kind = 'symbol' " : "AND c.kind <> 'symbol' ") +
                "), ranked AS (" +
                "  SELECT *, row_number() OVER (" +
                "    PARTITION BY path ORDER BY score ASC, start_line ASC, end_line ASC, " +
                "    chunk_kind COLLATE BINARY ASC, coalesce(heading, '') COLLATE BINARY ASC" +
                "  ) AS file_rank FROM matched" +
                ") " +
                "SELECT path, file_kind, chunk_kind, start_line, end_line, heading, score " +
                "FROM ranked WHERE file_rank <= $per_file " +
                "ORDER BY score ASC, path COLLATE BINARY ASC, start_line ASC, end_line ASC, " +
                "chunk_kind COLLATE BINARY ASC, coalesce(heading, '') COLLATE BINARY ASC " +
                "LIMIT $cap";
            cmd.Parameters.AddWithValue("$q", matchExpression);
            cmd.Parameters.AddWithValue("$per_file", Math.Max(perFileCap, 0));
            cmd.Parameters.AddWithValue("$cap", Math.Max(globalCap, 0));
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                all.Add(new SearchHit
                {
                    Path = reader.GetString(0),
                    Kind = reader.GetString(1),
                    ChunkKind = reader.GetString(2),
                    StartLine = reader.GetInt32(3),
                    EndLine = reader.GetInt32(4),
                    Heading = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Score = -reader.GetDouble(6), // bm25: lower is better -> flip.
                    Reasons = ["fts"],
                });
            }
        }

        return all;
    }

    /// <summary>Removes all graph edges (the graph is fully recomputed each index).</summary>
    public void ClearEdges() => Execute("DELETE FROM edges;");

    public void InsertEdge(long srcFileId, long dstFileId, string kind, SqliteTransaction transaction)
    {
        if (srcFileId == dstFileId)
        {
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "INSERT OR IGNORE INTO edges(src_file_id, dst_file_id, kind) VALUES($s, $d, $k)";
        cmd.Parameters.AddWithValue("$s", srcFileId);
        cmd.Parameters.AddWithValue("$d", dstFileId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.ExecuteNonQuery();
    }

    public int CountEdges()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM edges";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private const string FileRowColumns =
        "id, path, language, kind, size_bytes, line_count, token_count, content_hash";

    private static FileRow ReadFileRow(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7));

    /// <summary>All indexed files with id, path, language, kind and metrics.</summary>
    public IReadOnlyList<FileRow> GetFiles()
    {
        var rows = new List<FileRow>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {FileRowColumns} FROM files ORDER BY path";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadFileRow(reader));
        }

        return rows;
    }

    /// <summary>Top-level type definitions (class/interface/struct/record/enum) for C# resolution.</summary>
    public IReadOnlyList<TypeDef> GetTypeDefiners()
    {
        var defs = new List<TypeDef>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT s.name, s.file_id, f.path FROM symbols s JOIN files f ON f.id = s.file_id " +
            "WHERE s.kind IN ('class','interface','struct','record','enum')";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            defs.Add(new TypeDef(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return defs;
    }

    /// <summary>Finds a file by its repo-relative path.</summary>
    public FileRow? FindFile(string relativePath)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {FileRowColumns} FROM files WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", relativePath);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFileRow(reader) : null;
    }

    /// <summary>A file's symbols ordered by position (for outlines).</summary>
    public IReadOnlyList<SymbolRow> GetSymbols(long fileId)
    {
        var rows = new List<SymbolRow>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT name, kind, start_line, end_line, signature, doc FROM symbols " +
            "WHERE file_id = $f ORDER BY start_line, id";
        cmd.Parameters.AddWithValue("$f", fileId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SymbolRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>
    /// Reconstructs the source text of a line range from the file's content
    /// chunks (symbol chunks hold synthetic search text and are skipped).
    /// Content chunks tile a file contiguously, so joining the overlapping
    /// ones and trimming to the requested lines yields the exact source.
    /// Returns null when the file has no content chunks in the range.
    /// </summary>
    public SourceSlice? GetSourceSlice(string path, int startLine, int endLine)
    {
        var pieces = new List<(int Start, string Content)>();
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.start_line, c.content FROM chunks c JOIN files f ON f.id = c.file_id " +
                "WHERE f.path = $p AND c.kind != 'symbol' AND c.end_line >= $s AND c.start_line <= $e " +
                "ORDER BY c.start_line, c.id";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$s", startLine);
            cmd.Parameters.AddWithValue("$e", endLine);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pieces.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        if (pieces.Count == 0)
        {
            return null;
        }

        int firstLine = pieces[0].Start;
        string[] lines = string.Join('\n', pieces.Select(p => p.Content)).Split('\n');
        int from = Math.Max(startLine - firstLine, 0);
        int to = Math.Min(endLine - firstLine, lines.Length - 1);
        if (to < from)
        {
            return null;
        }

        return new SourceSlice(
            string.Join('\n', lines[from..(to + 1)]),
            firstLine + from,
            firstLine + to);
    }

    /// <summary>Neighbour paths of a file along an edge kind and direction.</summary>
    public IReadOnlyList<string> GetNeighbors(long fileId, string kind, bool outgoing)
    {
        var paths = new List<string>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = outgoing
            ? "SELECT f.path FROM edges e JOIN files f ON f.id = e.dst_file_id " +
              "WHERE e.src_file_id = $id AND e.kind = $k ORDER BY f.path"
            : "SELECT f.path FROM edges e JOIN files f ON f.id = e.src_file_id " +
              "WHERE e.dst_file_id = $id AND e.kind = $k ORDER BY f.path";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$k", kind);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>Returns the content of a chunk identified by file and start line.</summary>
    public string? GetChunkText(string path, int startLine)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        // Symbol chunks can share a start line with code chunks; prefer the
        // code chunk so snippets show source, and order for determinism.
        cmd.CommandText =
            "SELECT c.content FROM chunks c JOIN files f ON f.id = c.file_id " +
            "WHERE f.path = $p AND c.start_line = $s " +
            "ORDER BY (c.kind = 'symbol') ASC, c.id ASC LIMIT 1";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", startLine);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>File metrics for the architecture summary.</summary>
    public IReadOnlyList<FileMetric> GetFileMetrics()
    {
        var rows = new List<FileMetric>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, language, kind, line_count FROM files ORDER BY path";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FileMetric(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return rows;
    }

    /// <summary>Most-imported files (centrality): dependents desc, then path asc.</summary>
    public IReadOnlyList<Centrality> GetMostImported(int top)
    {
        var rows = new List<Centrality>();
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT f.path, count(*) AS n FROM edges e JOIN files f ON f.id = e.dst_file_id " +
            "WHERE e.kind = 'import' GROUP BY e.dst_file_id ORDER BY n DESC, f.path ASC LIMIT $t";
        cmd.Parameters.AddWithValue("$t", top);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Centrality(reader.GetString(0), reader.GetInt32(1)));
        }

        return rows;
    }

    /// <summary>Paths that have no incoming import edges (candidate entrypoints/roots).</summary>
    public HashSet<string> GetPathsWithoutDependents()
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var hasIncoming = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM files";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                all.Add(reader.GetString(0));
            }
        }

        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT DISTINCT f.path FROM edges e JOIN files f ON f.id = e.dst_file_id WHERE e.kind = 'import'";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hasIncoming.Add(reader.GetString(0));
            }
        }

        all.ExceptWith(hasIncoming);
        return all;
    }

    /// <summary>Paths that have at least one outgoing import edge.</summary>
    public HashSet<string> GetPathsWithOutgoing()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT f.path FROM edges e JOIN files f ON f.id = e.src_file_id WHERE e.kind = 'import'";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
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

/// <summary>A file row: id, path, language, kind and metrics.</summary>
public readonly record struct FileRow(
    long Id, string Path, string Language, string Kind, long SizeBytes,
    int LineCount, int TokenCount, string ContentHash);

/// <summary>A stored symbol as served in outlines.</summary>
public readonly record struct SymbolRow(
    string Name, string Kind, int StartLine, int EndLine, string Signature, string? Doc);

/// <summary>A reconstructed source line range (1-based inclusive).</summary>
public readonly record struct SourceSlice(string Text, int StartLine, int EndLine);

/// <summary>A top-level type definition used for C# name-based edge resolution.</summary>
public readonly record struct TypeDef(string Name, long FileId, string Path);

/// <summary>Per-file metric for the architecture summary.</summary>
public readonly record struct FileMetric(string Path, string Language, string Kind, int LineCount);

/// <summary>A file and how many other files import it.</summary>
public readonly record struct Centrality(string Path, int Dependents);
