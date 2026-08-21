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
    /// <summary>
    /// Files that could not be read, so nothing could be said about them. They
    /// are neither changed nor verified current, and reporting "index is
    /// current" while silently skipping them would claim a check that did not
    /// happen.
    /// </summary>
    public IReadOnlyList<string> Unreadable { get; init; } = [];

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
    /// <summary>
    /// Delta marker for a file nothing could be established about. Distinct from
    /// every real status, so the fingerprint of "could not check" differs from
    /// both "unchanged" and any actual change.
    /// </summary>
    private const string UnreadableStatus = "unreadable";

    public static ChangedResult Run(
        RepoLayout layout, RepoctxConfig config, IndexStore store,
        bool patch = false, TokenScale scale = default)
    {
        Dictionary<string, FileRecord> existing = store.GetExistingFiles();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ChangedFile>();

        // The current content hash of every differing file, feeding worktree_state.
        // Added files are hashed too: without that, two different files added at
        // the same path would share a fingerprint.
        var delta = new List<(string Status, string Path, string? ContentHash)>();
        var unreadable = new List<string>();

        var scanner = new FileScanner(layout.Root, config);
        foreach (ScannedFile file in scanner.Scan())
        {
            if (Hash(file.AbsolutePath) is not { } hash)
            {
                // Unreadable right now says nothing about the working tree: it
                // is not a deletion, and reporting one would send an agent to
                // re-create a file that is merely locked. It is reported as
                // unreadable instead, so the answer stays honest.
                seen.Add(file.RelativePath);
                unreadable.Add(file.RelativePath);
                continue;

            }

            seen.Add(file.RelativePath);

            if (!existing.TryGetValue(file.RelativePath, out FileRecord record))
            {
                changed.Add(new ChangedFile(file.RelativePath, ChangedFile.Added));
                delta.Add((ChangedFile.Added, file.RelativePath, hash));
                continue;
            }

            if (record.ContentHash != hash)
            {
                changed.Add(patch
                    ? WithPatch(store, file, scale)
                    : new ChangedFile(file.RelativePath, ChangedFile.Modified));
                delta.Add((ChangedFile.Modified, file.RelativePath, hash));
            }
        }

        // Anything the scan could not look at is in the same position: an
        // unreadable file, or an indexed file under a directory it could not
        // enter. Reporting those as deleted would send an agent to re-create
        // files that are merely inaccessible.
        foreach (string path in existing.Keys)
        {
            if (!seen.Contains(path) && scanner.WasSkipped(path))
            {
                seen.Add(path);
                unreadable.Add(path);
            }
        }

        foreach (string path in scanner.UnreadablePaths.Concat(scanner.UnreadableDirectories))
        {
            if (seen.Add(path))
            {
                unreadable.Add(path);
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

            // Documents that describe the file are impacted too: a specification
            // or ticket that names the code an agent just changed is the first
            // thing that may now be out of date (ADR 0019).
            Collect(impact, changedPaths,
                store.GetNeighbors(record.Id, EdgeKind.Reference, outgoing: false),
                "mentions:" + file.Path);
        }

        List<ImpactedFile> impacted = impact
            .Select(e => new ImpactedFile(e.Key, ReasonCompression.Compress(e.Value)))
            .ToList();

        // An unreadable file enters the fingerprint too. Without it a modified
        // but locked file yields a worktree_state byte-identical to a verified
        // clean tree, and an agent using that as its cheap staleness key would
        // keep serving pre-edit content.
        unreadable.Sort(StringComparer.Ordinal);
        foreach (string path in unreadable)
        {
            delta.Add((UnreadableStatus, path, null));
        }

        string contentState = store.GetMeta(MetaKeys.StateHash) ?? string.Empty;
        string worktreeState = Fingerprints.WorktreeState(contentState, delta);
        return new ChangedResult(
            Hashes.Short(contentState), changed.Count > 0, changed, impacted,
            Hashes.Short(contentState), Hashes.Short(worktreeState))
        {
            FullContentState = contentState,
            FullWorktreeState = worktreeState,
            Unreadable = unreadable,
        };
    }

    /// <summary>
    /// The content hash of a file, or null when it cannot be read right now.
    /// An unreadable file used to abort the whole command.
    /// </summary>
    private static string? Hash(string absolutePath)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolutePath)));
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

    /// <summary>The text of a file, or null when it cannot be read right now.</summary>
    private static string? ReadText(string absolutePath)
    {
        try
        {
            return File.ReadAllText(absolutePath);
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

        // Re-read: the file can become unreadable between the hash above and
        // here, and losing the hunks is a lesser answer than losing the command.
        if (ReadText(file.AbsolutePath) is not { } current)
        {
            return plain;
        }

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
