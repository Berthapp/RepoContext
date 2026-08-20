using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
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

    /// <summary>Source bytes read for hashing during this scan.</summary>
    public long BytesRead { get; init; }

    /// <summary>Added/changed files whose chunks and symbols were recomputed.</summary>
    public int FilesParsed { get; init; }

    /// <summary>Dependency/test edges rebuilt during this run.</summary>
    public int EdgesRecomputed { get; init; }

    /// <summary>Source files analyzed while rebuilding dependency/test facts.</summary>
    public int GraphFilesAnalyzed { get; init; }

    /// <summary>Cross-artifact references stored in the index (ADR 0019).</summary>
    public int TotalRefs { get; init; }

    /// <summary>Reference edges (document names file/symbol) in the rebuilt graph.</summary>
    public int ReferenceEdges { get; init; }

    /// <summary>Wall-clock duration, reported separately from deterministic goldens.</summary>
    public long ElapsedMilliseconds { get; init; }
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
        var stopwatch = Stopwatch.StartNew();
        using IndexStore store = IndexStore.Open(_layout.DatabasePath);

        string configHash = ConfigStore.ComputeIndexHash(_config);
        bool configChanged = store.GetMeta(MetaKeys.ConfigHash) != configHash;
        bool schemaChanged =
            store.GetMeta(MetaKeys.SchemaVersion) != IndexSchema.Version.ToString();

        // A producer change (parser, chunker, decoder, tokenizer, graph) alters
        // stored analysis without changing any source byte, so the incremental
        // content-hash diff would wrongly report everything as unchanged (Q4).
        bool producerChanged =
            store.GetMeta(MetaKeys.AnalysisProducerVersion) != ProducerVersions.AnalysisProducerVersion;
        bool rebuild = full || configChanged || schemaChanged || producerChanged;

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
        int filesParsed = 0;
        int indexedFiles = 0;

        // One parallel pass computes every content hash. It is the only pass
        // that has to touch each file: unchanged files are then skipped without
        // being opened again, and the graph is rebuilt from stored references
        // rather than from a second full read of the working tree (ADR 0019).
        FileDigest[] digests = HashAll(scanned);
        long bytesRead = digests.Sum(digest => digest.Bytes);

        var referenceExtractor = new ReferenceExtractor(_config.Artifacts);
        using ILanguageParser parser = new TreeSitterParser();
        using (SqliteTransaction tx = store.BeginTransaction())
        {
            for (int i = 0; i < scanned.Count; i++)
            {
                ScannedFile file = scanned[i];
                if (digests[i].Hash is not { } hash)
                {
                    // Unreadable (removed or locked between scan and hash): it is
                    // not part of this index state, so it is treated as absent.
                    continue;
                }

                seen.Add(file.RelativePath);
                indexedFiles++;

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

                if (ReadText(file.AbsolutePath) is not { } content)
                {
                    continue;
                }

                filesParsed++;
                IReadOnlyList<Chunk> chunks = Chunker.Chunk(file.Language, content);
                IReadOnlyList<Symbol> symbols = parser.Supports(file.Language)
                    ? parser.Parse(file.Language, file.RelativePath, content)
                    : StructureExtractor.Extract(file.RelativePath, content);
                IReadOnlyList<FileReference> references =
                    referenceExtractor.Extract(file, content, parser);
                int lineCount = CountLines(content);
                int tokenCount = Tokens.Count(content);
                var labels = new FileKindLabel(Label(file.Kind), Label(file.Language));
                store.InsertFile(
                    file.RelativePath, labels, file.SizeBytes, lineCount, tokenCount, hash,
                    chunks, symbols, tx, references);
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

        var graphBuilder = new GraphBuilder(store, _config.Artifacts);
        int totalEdges = graphBuilder.Rebuild();

        int totalChunks = store.CountChunks();
        int totalSymbols = store.CountSymbols();
        int totalRefs = store.CountRefs();
        store.SetMeta(MetaKeys.StateHash, ComputeStateHash(store));
        store.SetMeta(MetaKeys.AnalysisProducerVersion, ProducerVersions.AnalysisProducerVersion);
        store.SetMeta(MetaKeys.SchemaVersion, IndexSchema.Version.ToString());
        store.SetMeta(MetaKeys.ToolVersion, _toolVersion);
        store.SetMeta(MetaKeys.ConfigHash, configHash);
        store.SetMeta(MetaKeys.IndexedAtUtc, DateTimeOffset.UtcNow.ToString("O"));
        store.SetMeta(MetaKeys.FileCount, indexedFiles.ToString());
        store.SetMeta(MetaKeys.ChunkCount, totalChunks.ToString());
        store.SetMeta(MetaKeys.SymbolCount, totalSymbols.ToString());
        store.SetMeta(MetaKeys.EdgeCount, totalEdges.ToString());
        store.SetMeta(MetaKeys.RefCount, totalRefs.ToString());
        stopwatch.Stop();

        return new IndexStats
        {
            Added = added,
            Changed = changed,
            Deleted = deleted,
            Unchanged = unchanged,
            TotalFiles = indexedFiles,
            TotalChunks = totalChunks,
            TotalSymbols = totalSymbols,
            TotalEdges = totalEdges,
            FullRebuild = rebuild,
            BytesRead = bytesRead,
            FilesParsed = filesParsed,
            EdgesRecomputed = totalEdges,
            GraphFilesAnalyzed = graphBuilder.FilesAnalyzed,
            TotalRefs = totalRefs,
            ReferenceEdges = graphBuilder.ReferenceEdges,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>One file's content hash and the bytes read to compute it.</summary>
    private readonly record struct FileDigest(string? Hash, long Bytes);

    /// <summary>
    /// Hashes every scanned file in parallel, preserving scan order in the
    /// result. Hashing is I/O bound and embarrassingly parallel, and it is the
    /// pass that decides what the rest of the run has to do at all — on a large
    /// repository it dominates an incremental index. Results are written to a
    /// pre-sized array by index, so the output is identical to a sequential run.
    /// </summary>
    private static FileDigest[] HashAll(IReadOnlyList<ScannedFile> scanned)
    {
        var digests = new FileDigest[scanned.Count];
        Parallel.For(0, scanned.Count, i =>
        {
            try
            {
                using FileStream stream = File.OpenRead(scanned[i].AbsolutePath);
                byte[] hash = SHA256.HashData(stream);
                digests[i] = new FileDigest(Convert.ToHexStringLower(hash), stream.Length);
            }
            catch (IOException)
            {
                digests[i] = new FileDigest(null, 0);
            }
            catch (UnauthorizedAccessException)
            {
                digests[i] = new FileDigest(null, 0);
            }
        });

        return digests;
    }

    /// <summary>Reads and decodes a file, or null when it became unreadable.</summary>
    private static string? ReadText(string absolutePath)
    {
        try
        {
            return DecodeUtf8(File.ReadAllBytes(absolutePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>content_state</c> fingerprint: SHA-256 over the sorted
    /// (path, content_hash) pairs, a stable identifier for "which file contents
    /// the index knows". Agents compare it across calls to detect staleness
    /// without re-reading anything (ADR 0010). Canonicalisation lives in
    /// <see cref="Fingerprints.ContentState"/>. Schema v3 uses length-prefixed
    /// records; the deprecated <c>state</c> field remains an alias of the value.
    /// </summary>
    private static string ComputeStateHash(IndexStore store) =>
        Fingerprints.ContentState(
            store.GetExistingFiles().Select(e => (e.Key, e.Value.ContentHash)));

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
