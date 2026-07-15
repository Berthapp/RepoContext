using System.Security.Cryptography;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Scanning;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Indexing;

/// <summary>A working-tree file that differs from the index.</summary>
public sealed record ChangedFile(string Path, string Status)
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";

    /// <summary>Delta hunks vs the indexed content (<c>--patch</c>, modified files only).</summary>
    public IReadOnlyList<PatchHunk>? Hunks { get; init; }

    /// <summary>What receiving the hunks costs — compare with <see cref="FileTokens"/>.</summary>
    public int? PatchTokens { get; init; }

    /// <summary>The full re-read this patch replaces (indexed count, calibrated).</summary>
    public int? FileTokens { get; init; }
}

/// <summary>An indexed file whose dependency or test links point at a change.</summary>
public sealed record ImpactedFile(string Path, IReadOnlyList<string> Reasons);

/// <summary>The result of <c>repoctx changed</c>.</summary>
public sealed record ChangedResult(
    string State, bool Stale,
    IReadOnlyList<ChangedFile> Changed, IReadOnlyList<ImpactedFile> Impacted);

/// <summary>
/// Diffs the working tree against the index by content hash (M6, ADR 0010) —
/// the same scan-and-hash pass the incremental indexer uses, without writing.
/// Answers the question an agent has after editing: "what is stale, and which
/// files that I have already seen are affected?" — so it re-reads those
/// instead of everything. Deterministic for a given tree and index.
/// </summary>
public static class ChangeDetector
{
    public static ChangedResult Run(
        RepoLayout layout, RepoctxConfig config, IndexStore store,
        bool patch = false, TokenScale scale = default)
    {
        Dictionary<string, FileRecord> existing = store.GetExistingFiles();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ChangedFile>();

        foreach (ScannedFile file in new FileScanner(layout.Root, config).Scan())
        {
            seen.Add(file.RelativePath);
            if (!existing.TryGetValue(file.RelativePath, out FileRecord record))
            {
                changed.Add(new ChangedFile(file.RelativePath, ChangedFile.Added));
                continue;
            }

            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.AbsolutePath)));
            if (record.ContentHash != hash)
            {
                changed.Add(patch
                    ? WithPatch(store, file, scale)
                    : new ChangedFile(file.RelativePath, ChangedFile.Modified));
            }
        }

        foreach (string path in existing.Keys)
        {
            if (!seen.Contains(path))
            {
                changed.Add(new ChangedFile(path, ChangedFile.Deleted));
            }
        }

        changed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var changedPaths = new HashSet<string>(changed.Select(c => c.Path), StringComparer.Ordinal);
        var impact = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (ChangedFile file in changed)
        {
            // Added files have no edges in the index yet; the graph knows only
            // modified and deleted ones.
            if (file.Status == ChangedFile.Added || !existing.TryGetValue(file.Path, out FileRecord record))
            {
                continue;
            }

            Collect(impact, changedPaths,
                store.GetNeighbors(record.Id, EdgeKind.Import, outgoing: false), "imports:" + file.Path);
            Collect(impact, changedPaths,
                store.GetNeighbors(record.Id, EdgeKind.Test, outgoing: false), "test-of:" + file.Path);
        }

        List<ImpactedFile> impacted = impact
            .Select(e => new ImpactedFile(e.Key, ReasonCompression.Compress(e.Value)))
            .ToList();

        string state = store.GetMeta(MetaKeys.StateHash) ?? string.Empty;
        return new ChangedResult(Hashes.Short(state), changed.Count > 0, changed, impacted);
    }

    /// <summary>
    /// Attaches delta hunks to a modified file: the indexed text is
    /// reconstructed from content chunks, the working-tree text read fresh,
    /// and only the differing line ranges are carried — after editing, the
    /// hunks replace a full re-read. Trailing newlines are normalized away on
    /// both sides so representation differences never produce phantom hunks.
    /// Files without content chunks (nothing to diff against) stay plain.
    /// </summary>
    private static ChangedFile WithPatch(IndexStore store, ScannedFile file, TokenScale scale)
    {
        var plain = new ChangedFile(file.RelativePath, ChangedFile.Modified);
        if (store.FindFile(file.RelativePath) is not { } row
            || store.GetSourceSlice(file.RelativePath, 1, int.MaxValue) is not { } indexed)
        {
            return plain;
        }

        string current = File.ReadAllText(file.AbsolutePath);
        IReadOnlyList<PatchHunk> hunks = LineDiff.Hunks(
            indexed.Text.TrimEnd('\n'), current.TrimEnd('\n'));
        return plain with
        {
            Hunks = hunks,
            PatchTokens = scale.Apply(LineDiff.PatchTokens(hunks)),
            FileTokens = scale.Apply(row.TokenCount),
        };
    }

    private static void Collect(
        SortedDictionary<string, List<string>> impact, HashSet<string> changedPaths,
        IReadOnlyList<string> dependents, string reason)
    {
        foreach (string path in dependents)
        {
            if (changedPaths.Contains(path))
            {
                continue; // Already reported as changed itself.
            }

            if (!impact.TryGetValue(path, out List<string>? reasons))
            {
                reasons = [];
                impact[path] = reasons;
            }

            reasons.Add(reason);
        }
    }
}
