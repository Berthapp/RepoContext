using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RepoContext.Core.Configuration;
using RepoContext.Core.Parsing;
using RepoContext.Core.Scanning;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Indexing;

/// <summary>Outcome statistics of an indexing run.</summary>
public sealed record IndexStats
{
    public int Added { get; init; }

    public int Changed { get; init; }

    public int Deleted { get; init; }

    public int Unchanged { get; init; }

    public int TotalFiles { get; init; }

    public int TotalChunks { get; init; }

    public int TotalSymbols { get; init; }

    public int TotalEdges { get; init; }

    public bool FullRebuild { get; init; }
}

/// <summary>
/// Builds or incrementally updates the index: scans the repository, diffs
/// against the stored files by content hash, and writes chunks + FTS rows.
/// </summary>
public sealed class Indexer
{
    private readonly RepoLayout _layout;
    private readonly RepoctxConfig _config;
    private readonly string _toolVersion;

    public Indexer(RepoLayout layout, RepoctxConfig config, string toolVersion)
    {
        _layout = layout;
        _config = config;
        _toolVersion = toolVersion;
    }

    public IndexStats Run(bool full)
    {
        using IndexStore store = IndexStore.Open(_layout.DatabasePath);

        string configHash = ConfigStore.ComputeHash(_config);
        bool configChanged = store.GetMeta(MetaKeys.ConfigHash) is { } prev && prev != configHash;
        bool schemaChanged = store.GetMeta(MetaKeys.SchemaVersion) is { } sv
            && sv != IndexSchema.Version.ToString();
        bool rebuild = full || configChanged || schemaChanged;

        if (rebuild)
        {
            store.Reset();
        }

        Dictionary<string, FileRecord> existing = rebuild
            ? new Dictionary<string, FileRecord>(StringComparer.Ordinal)
            : store.GetExistingFiles();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var scanner = new FileScanner(_layout.Root, _config);
        IReadOnlyList<ScannedFile> scanned = scanner.Scan();

        int added = 0, changed = 0, unchanged = 0, deleted = 0;

        using ILanguageParser parser = new TreeSitterParser();
        using (SqliteTransaction tx = store.BeginTransaction())
        {
            foreach (ScannedFile file in scanned)
            {
                seen.Add(file.RelativePath);

                byte[] bytes = File.ReadAllBytes(file.AbsolutePath);
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

                if (existing.TryGetValue(file.RelativePath, out FileRecord record))
                {
                    if (record.ContentHash == hash)
                    {
                        unchanged++;
                        continue;
                    }

                    store.DeleteFile(record.Id, tx);
                    changed++;
                }
                else
                {
                    added++;
                }

                string content = DecodeUtf8(bytes);
                IReadOnlyList<Chunk> chunks = Chunker.Chunk(file.Language, content);
                IReadOnlyList<Symbol> symbols = parser.Supports(file.Language)
                    ? parser.Parse(file.Language, file.RelativePath, content)
                    : [];
                int lineCount = CountLines(content);
                int tokenCount = Tokens.Count(content);
                var labels = new FileKindLabel(Label(file.Kind), Label(file.Language));
                store.InsertFile(
                    file.RelativePath, labels, file.SizeBytes, lineCount, tokenCount, hash,
                    chunks, symbols, tx);
            }

            foreach ((string path, FileRecord record) in existing)
            {
                if (!seen.Contains(path))
                {
                    store.DeleteFile(record.Id, tx);
                    deleted++;
                }
            }

            tx.Commit();
        }

        int totalEdges = new Graph.GraphBuilder(store, _layout.Root, parser).Rebuild();

        int totalChunks = store.CountChunks();
        int totalSymbols = store.CountSymbols();
        store.SetMeta(MetaKeys.StateHash, ComputeStateHash(store));
        store.SetMeta(MetaKeys.SchemaVersion, IndexSchema.Version.ToString());
        store.SetMeta(MetaKeys.ToolVersion, _toolVersion);
        store.SetMeta(MetaKeys.ConfigHash, configHash);
        store.SetMeta(MetaKeys.IndexedAtUtc, DateTimeOffset.UtcNow.ToString("O"));
        store.SetMeta(MetaKeys.FileCount, scanned.Count.ToString());
        store.SetMeta(MetaKeys.ChunkCount, totalChunks.ToString());
        store.SetMeta(MetaKeys.SymbolCount, totalSymbols.ToString());
        store.SetMeta(MetaKeys.EdgeCount, totalEdges.ToString());

        return new IndexStats
        {
            Added = added,
            Changed = changed,
            Deleted = deleted,
            Unchanged = unchanged,
            TotalFiles = scanned.Count,
            TotalChunks = totalChunks,
            TotalSymbols = totalSymbols,
            TotalEdges = totalEdges,
            FullRebuild = rebuild,
        };
    }

    /// <summary>
    /// SHA-256 over the sorted (path, content_hash) pairs: a stable identifier
    /// for "what the index knows". Agents compare it across calls to detect
    /// staleness without re-reading anything (ADR 0010).
    /// </summary>
    private static string ComputeStateHash(IndexStore store)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((string path, FileRecord record) in store.GetExistingFiles()
            .OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            sb.Append(path).Append('\n').Append(record.ContentHash).Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        // Strip a UTF-8 BOM so chunk content and heading detection see the
        // file exactly as an editor shows it. The content hash keeps the BOM.
        ReadOnlySpan<byte> span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
        }

        return new System.Text.UTF8Encoding(false, false).GetString(span);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        int count = 1;
        foreach (char c in content)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        // A trailing newline does not start a new line.
        return content[^1] == '\n' ? count - 1 : count;
    }

    private static string Label(FileKind kind) => kind.ToString().ToLowerInvariant();

    private static string Label(SourceLanguage language) => language.ToString().ToLowerInvariant();
}
