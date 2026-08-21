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

    /// <summary>Text files skipped for exceeding <c>indexing.maxFileSizeKb</c>.</summary>
    public int SkippedTooLarge { get; init; }

    /// <summary>Files skipped because they are binary - the one category that cannot be described.</summary>
    public int SkippedBinary { get; init; }

    /// <summary>
    /// Ignore files whose rules could not be read this run, so the directories
    /// they exclude were indexed instead. Not a gap in coverage but a surplus,
    /// which is why it is reported apart from <see cref="Unreadable"/>.
    /// </summary>
    public IReadOnlyList<string> UnreadableIgnoreFiles { get; init; } = [];

    /// <summary>
    /// Files that could not be opened this run, whether during the scan or
    /// afterwards. An already-indexed one keeps whatever the index holds; a new
    /// one is absent from it. Counted separately from <see cref="Unchanged"/>,
    /// which means "verified identical" - here nothing was verified.
    /// </summary>
    public int Unreadable { get; init; }

    /// <summary>The first few skipped paths, so the report names something actionable.</summary>
    public IReadOnlyList<string> SkippedTooLargeSample { get; init; } = [];

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

        // Deduplicated on purpose: a file can fail the scan and the index pass
        // alike, and a report that counts it twice is a report nobody trusts.
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
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
                bool known = existing.TryGetValue(file.RelativePath, out FileRecord record);

                // A file that cannot be read this run - locked, or removed
                // between the scan and now - keeps whatever the index already
                // holds. Deleting a known file because it was briefly locked
                // loses coverage the next run has to rediscover, and reporting
                // it as unchanged would claim a verification that did not
                // happen; an unknown one is simply left out. This applies to
                // both passes: hashing and reading can each fail.
                if (digests[i].Hash is not { } hash)
                {
                    unreadable.Add(file.RelativePath);
                    Keep(known, file.RelativePath, seen, ref indexedFiles);
                    continue;
                }

                if (known && record.ContentHash == hash)
                {
                    seen.Add(file.RelativePath);
                    indexedFiles++;
                    unchanged++;
                    continue;
                }

                // Read before touching the index, so a failure here cannot leave
                // the file half-removed.
                if (ReadText(file.AbsolutePath) is not { } content)
                {
                    unreadable.Add(file.RelativePath);
                    Keep(known, file.RelativePath, seen, ref indexedFiles);
                    continue;
                }

                seen.Add(file.RelativePath);
                indexedFiles++;
                if (known)
                {
                    store.DeleteFile(record.Id, tx);
                    changed++;
                }
                else
                {
                    added++;
                }

                filesParsed++;
                IReadOnlyList<Chunk> chunks = Chunker.Chunk(file.Language, content);
                IReadOnlyList<Symbol> symbols =
                    SymbolExtraction.For(parser, file.Language, file.RelativePath, content);
                IReadOnlyList<FileReference> references =
                    referenceExtractor.Extract(file, content, parser);
                int lineCount = CountLines(content);
                int tokenCount = Tokens.Count(content);
                var labels = new FileKindLabel(Label(file.Kind), Label(file.Language));
                store.InsertFile(
                    file.RelativePath, labels, file.SizeBytes, lineCount, tokenCount, hash,
                    chunks, symbols, tx, references);
            }

            // Anything the scan could not look at - an unreadable file, or a
            // whole subtree under a directory it could not enter - never reached
            // the loop above. It is held on to here for the same reason: silence
            // from the scan is not evidence of deletion.
            foreach (string path in existing.Keys)
            {
                if (!seen.Contains(path) && scanner.WasSkipped(path))
                {
                    Keep(known: true, path, seen, ref indexedFiles);
                    unreadable.Add(path);
                }
            }

            // Anything the scan could not open is reported whether or not it was
            // ever indexed: a new file the tool cannot read is exactly what the
            // report exists to surface, and on a full rebuild there is no index
            // to recognise it from - without this a lost subtree would vanish
            // with exit code 0 and no warning. A path that was nevertheless
            // indexed this run is not counted: the two states are exclusive in
            // the report, whatever a transient failure did in between.
            foreach (string path in scanner.UnreadablePaths)
            {
                if (!seen.Contains(path))
                {
                    unreadable.Add(path);
                }
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
            SkippedTooLarge = scanner.OversizedCount,
            SkippedTooLargeSample = scanner.OversizedSample,
            SkippedBinary = scanner.BinaryCount,
            UnreadableIgnoreFiles = [.. scanner.UnreadableIgnoreFiles.Order(StringComparer.Ordinal)],
            Unreadable = unreadable.Count,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Retains an already-indexed file that could not be read, so the prune pass
    /// leaves its row alone. A file that was never indexed has nothing to
    /// retain; it is still counted as unreadable by the caller, because a file
    /// the tool could not open is exactly what the report exists to surface.
    /// </summary>
    private static void Keep(bool known, string relativePath, HashSet<string> seen, ref int indexedFiles)
    {
        if (!known)
        {
            return;
        }

        seen.Add(relativePath);
        indexedFiles++;
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
