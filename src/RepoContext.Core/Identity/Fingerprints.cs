namespace RepoContext.Core.Identity;

/// <summary>
/// The versioned state fingerprints defined by Q4 (ADR 0012). Each answers a
/// different question, and conflating them is what made the pre-Release-1
/// single <c>state_hash</c> unsafe:
/// <list type="bullet">
/// <item><description><c>content_state</c> — which file contents are indexed.</description></item>
/// <item><description><c>analysis_state</c> — that content <i>plus</i> every
/// configuration and producer version that shapes the analysed result, so a
/// parser or ranking change invalidates derived answers even when no byte of
/// source moved.</description></item>
/// <item><description><c>worktree_state</c> — the indexed base plus a detected
/// local delta; only worktree-sensitive commands compute it.</description></item>
/// <item><description><c>evidence_id</c> — the identity of the selected
/// evidence for one analysed query, independent of how it is encoded.</description></item>
/// <item><description><c>representation_id</c> — the identity of the encoded
/// body, hashed with its own identity field omitted so it never
/// self-references.</description></item>
/// </list>
/// </summary>
public static class Fingerprints
{
    /// <summary>Deletion marker used in the worktree delta layout.</summary>
    public const string DeletedContentMarker = "-";

    /// <summary>
    /// SHA-256 over length-prefixed, sorted <c>(path, content_hash)</c> pairs.
    /// The deprecated top-level <c>state</c> remains an alias of this value, but
    /// schema v3 deliberately replaces the newline-delimited v2 byte layout so a
    /// legal newline in a Unix file name cannot forge a record boundary.
    /// </summary>
    /// <param name="entries">Path/content-hash pairs in any order; sorted here.</param>
    public static string ContentState(IEnumerable<(string Path, string ContentHash)> entries)
    {
        IEnumerable<string> records = entries
            .Select(e => (Path: Canonical.NormalizePath(e.Path), e.ContentHash))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => Canonical.JoinRecords([e.Path, e.ContentHash]));

        return Canonical.Hash(
            "content_state.v3",
            Canonical.JoinRecords(records));
    }

    /// <summary>
    /// Content plus everything else that determines the analysed result:
    /// the persisted index-time producer fingerprint, the on-disk index schema,
    /// the live ranking/evidence behaviour versions, and the effective config.
    /// </summary>
    public static string AnalysisState(
        string contentState,
        string analysisProducerVersion,
        int indexSchemaVersion,
        string configHash) =>
        Canonical.Hash(
            "analysis_state.v1",
            contentState,
            analysisProducerVersion,
            indexSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProducerVersions.RankingVersion,
            ProducerVersions.EvidenceVersion,
            configHash);

    /// <summary>
    /// The indexed base <paramref name="contentState"/> plus the detected local
    /// delta. Entries are length-prefixed
    /// <c>(status, path, current_content_hash)</c> records sorted ordinally by
    /// path; a deleted file uses <see cref="DeletedContentMarker"/> so "deleted"
    /// and "added with empty content" cannot collide.
    /// </summary>
    public static string WorktreeState(
        string contentState, IEnumerable<(string Status, string Path, string? ContentHash)> delta)
    {
        IEnumerable<string> records = delta
            .Select(d => (d.Status, Path: Canonical.NormalizePath(d.Path), d.ContentHash))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Status, StringComparer.Ordinal)
            .Select(d => Canonical.JoinRecords(
            [
                d.Status,
                d.Path,
                d.ContentHash ?? DeletedContentMarker,
            ]));

        return Canonical.Hash("worktree_state.v1", contentState, Canonical.JoinRecords(records));
    }

    /// <summary>
    /// The identity of the selected evidence: analysis state, the canonical
    /// analysed query, the canonical core options (omitted and explicit defaults
    /// normalise to the same value), and the ordered evidence unit records with
    /// their reasons. Independent of output format and encoding.
    /// </summary>
    public static string EvidenceId(
        string analysisState, string canonicalQuery, string canonicalOptions,
        IEnumerable<string> orderedEvidenceRecords) =>
        Canonical.Hash(
            "evidence_id.v1",
            analysisState,
            canonicalQuery,
            canonicalOptions,
            Canonical.JoinRecords(orderedEvidenceRecords));

    /// <summary>
    /// The identity of the encoded body. <paramref name="canonicalBody"/> must be
    /// the rendered representation with its own <c>representation_id</c> field
    /// omitted — that omission is what keeps this free of self-reference.
    /// </summary>
    public static string RepresentationId(
        string evidenceId, int outputSchemaVersion, string format, string profile,
        string encoding, string surface, string canonicalBody) =>
        Canonical.Hash(
            "representation_id.v1",
            evidenceId,
            outputSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProducerVersions.RepresentationVersion,
            format,
            profile,
            encoding,
            surface,
            Canonical.HashUtf8(canonicalBody));

    /// <summary>
    /// Benchmark-only identity that additionally covers the MCP protocol and
    /// tool-schema versions. Deliberately excludes the volatile JSON-RPC request
    /// ID so two identical calls compare equal.
    /// </summary>
    public static string TransportProfileId(
        string representationId, string protocolVersion, string toolSchemaVersion) =>
        Canonical.Hash(
            "transport_profile_id.v1", representationId, protocolVersion, toolSchemaVersion);
}

/// <summary>The surface a response is measured and identified at (Q3 boundaries).</summary>
public static class Surfaces
{
    /// <summary>The core result document, without CLI newline or transport wrapper.</summary>
    public const string Core = "core";

    /// <summary>Exact CLI stdout, including its trailing newline.</summary>
    public const string Cli = "cli";

    /// <summary>The model-visible MCP text content block.</summary>
    public const string McpText = "mcp-text";
}
