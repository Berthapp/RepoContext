using System.Security.Cryptography;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
using RepoContext.Core.Scanning;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Indexing;

/// <summary>A working-tree file that differs from the index.</summary>
public sealed record ChangedFile(string Path, string Status)
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary>An indexed file whose dependency or test links point at a change.</summary>
public sealed record ImpactedFile(string Path, IReadOnlyList<string> Reasons);

/// <summary>The result of <c>repoctx changed</c>.</summary>
/// <param name="State">Deprecated (v2): short <c>content_state</c>.</param>
/// <param name="Stale">Whether the working tree differs from the index at all.</param>
/// <param name="Changed">The differing files.</param>
/// <param name="Impacted">Indexed files whose links point at a change.</param>
/// <param name="ContentState">Short fingerprint of the indexed file contents (Q4).</param>
/// <param name="WorktreeState">
/// Short fingerprint of the indexed base plus this detected local delta (Q4).
/// <c>changed</c> is the one worktree-sensitive command, so it is the only one
/// that computes this; index-backed query commands never scan the tree.
/// </param>
public sealed record ChangedResult(
    string State, bool Stale,
    IReadOnlyList<ChangedFile> Changed, IReadOnlyList<ImpactedFile> Impacted,
    string ContentState, string WorktreeState)
{
    /// <summary>Full internal indexed-content fingerprint.</summary>
    public string FullContentState { get; init; } = string.Empty;

    /// <summary>Full internal indexed-base-plus-local-delta fingerprint.</summary>
    public string FullWorktreeState { get; init; } = string.Empty;
}

/// <summary>
/// Diffs the working tree against the index by content hash (M6, ADR 0010) —
/// the same scan-and-hash pass the incremental indexer uses, without writing.
/// Answers the question an agent has after editing: "what is stale, and which
/// files that I have already seen are affected?" — so it re-reads those
/// instead of everything. Deterministic for a given tree and index.
/// </summary>
public static class ChangeDetector
{
    public static ChangedResult Run(RepoLayout layout, RepoctxConfig config, IndexStore store)
    {
        Dictionary<string, FileRecord> existing = store.GetExistingFiles();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ChangedFile>();

        // The current content hash of every differing file, feeding worktree_state.
        // Added files are hashed too: without that, two different files added at
        // the same path would share a fingerprint.
        var delta = new List<(string Status, string Path, string? ContentHash)>();

        foreach (ScannedFile file in new FileScanner(layout.Root, config).Scan())
        {
            seen.Add(file.RelativePath);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.AbsolutePath)));

            if (!existing.TryGetValue(file.RelativePath, out FileRecord record))
            {
                changed.Add(new ChangedFile(file.RelativePath, ChangedFile.Added));
                delta.Add((ChangedFile.Added, file.RelativePath, hash));
                continue;
            }

            if (record.ContentHash != hash)
            {
                changed.Add(new ChangedFile(file.RelativePath, ChangedFile.Modified));
                delta.Add((ChangedFile.Modified, file.RelativePath, hash));
            }
        }

        foreach (string path in existing.Keys)
        {
            if (!seen.Contains(path))
            {
                changed.Add(new ChangedFile(path, ChangedFile.Deleted));
                delta.Add((ChangedFile.Deleted, path, null));
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

        string contentState = store.GetMeta(MetaKeys.StateHash) ?? string.Empty;
        string worktreeState = Fingerprints.WorktreeState(contentState, delta);
        return new ChangedResult(
            Hashes.Short(contentState), changed.Count > 0, changed, impacted,
            Hashes.Short(contentState), Hashes.Short(worktreeState))
        {
            FullContentState = contentState,
            FullWorktreeState = worktreeState,
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
