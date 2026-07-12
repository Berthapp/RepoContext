namespace RepoContext.Core.Storage;

/// <summary>The SQLite schema for the index (data model, spec chapter 7).</summary>
internal static class IndexSchema
{
    /// <summary>
    /// Bumped when the on-disk schema changes in a way that requires a rebuild.
    /// Distinct from <see cref="Core.RepoContextInfo.SchemaVersion"/> (the JSON
    /// output contract version). v4: <c>files.token_count</c> (M6, ADR 0010).
    /// </summary>
    public const int Version = 4;

    public const string Ddl = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files (
            id           INTEGER PRIMARY KEY,
            path         TEXT NOT NULL UNIQUE,
            kind         TEXT NOT NULL,
            language     TEXT NOT NULL,
            size_bytes   INTEGER NOT NULL,
            line_count   INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            token_count  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunks (
            id         INTEGER PRIMARY KEY,
            file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            kind       TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line   INTEGER NOT NULL,
            heading    TEXT,
            content    TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);

        CREATE TABLE IF NOT EXISTS symbols (
            id         INTEGER PRIMARY KEY,
            file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            name       TEXT NOT NULL,
            kind       TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line   INTEGER NOT NULL,
            signature  TEXT NOT NULL,
            doc        TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);

        CREATE TABLE IF NOT EXISTS edges (
            src_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            dst_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            kind        TEXT NOT NULL,
            PRIMARY KEY (src_file_id, dst_file_id, kind)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_edges_src ON edges(src_file_id);
        CREATE INDEX IF NOT EXISTS idx_edges_dst ON edges(dst_file_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            content,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    /// <summary>
    /// Drops every data table so <see cref="Ddl"/> can recreate the current
    /// shape. Needed when <see cref="Version"/> changed: the DDL is
    /// IF-NOT-EXISTS and cannot alter existing tables. The meta table is kept
    /// (it is version-stable and rewritten after every index run).
    /// </summary>
    public const string DropDataTables = """
        DROP TABLE IF EXISTS chunks_fts;
        DROP TABLE IF EXISTS edges;
        DROP TABLE IF EXISTS symbols;
        DROP TABLE IF EXISTS chunks;
        DROP TABLE IF EXISTS files;
        """;
}
