namespace RepoContext.Core.Identity;

/// <summary>
/// Version stamps for every deterministic producer whose behaviour can change
/// the analysed result or a reuse receipt (Q4, ADR 0012). These are bumped by
/// hand whenever the corresponding logic changes in a way that would make a
/// previously computed fingerprint or receipt no longer describe the same
/// evidence — for example a parser upgrade, a chunker change, a tokenizer swap,
/// a graph-resolution change, or a ranking-formula change.
/// </summary>
/// <remarks>
/// The split matters: <see cref="AnalysisProducerVersion"/> covers only the
/// index-time producers whose output is stored on disk, so a change forces a
/// re-index. <see cref="RankingVersion"/> and <see cref="RepresentationVersion"/>
/// are applied live at query/render time and never invalidate the stored index,
/// only the derived <c>analysis_state</c>/<c>representation_id</c> fingerprints.
/// </remarks>
public static class ProducerVersions
{
    /// <summary>Scanner + file classification (which files, kind/language labels).</summary>
    public const int Scanner = 1;

    /// <summary>Byte decoding and newline normalisation feeding chunk/symbol text.</summary>
    public const int Decoder = 1;

    /// <summary>Tree-sitter parser + symbol extraction (ADR 0001/0005).</summary>
    public const int Parser = 1;

    /// <summary>Line/heading chunker and synthetic symbol chunks (ADR 0004/0005).</summary>
    public const int Chunker = 1;

    /// <summary>Offline BPE tokenizer (<c>o200k_base</c>, ADR 0010).</summary>
    public const int Tokenizer = 1;

    /// <summary>Import/test graph construction (ADR 0006).</summary>
    public const int Graph = 1;

    /// <summary>Canonical indexed content-state fingerprint layout (ADR 0012).</summary>
    public const int StateFingerprint = 2;

    /// <summary>Weighted ranking + diversity + vendor penalty formula (ADR 0006).</summary>
    public const int Ranking = 1;

    /// <summary>
    /// Evidence selection and receipt canonicalisation (Q1/Q2, ADR 0012). Bumped
    /// when the delivered evidence for the same query/content would change, which
    /// must invalidate outstanding receipts even though the source is unchanged.
    /// </summary>
    public const int Evidence = 1;

    /// <summary>Output DTO shape / canonical body encoding (ADR 0009/0012).</summary>
    public const int Representation = 1;

    /// <summary>
    /// A single catch-all string covering every index-time producer. Persisted in
    /// the index meta table; query commands reject an index whose stored value is
    /// stale (the same way an outdated on-disk schema is rejected).
    /// </summary>
    public static string AnalysisProducerVersion =>
        $"scan{Scanner}.dec{Decoder}.par{Parser}.chk{Chunker}.tok{Tokenizer}.gph{Graph}.sta{StateFingerprint}";

    /// <summary>Live ranking-behaviour version applied at query time.</summary>
    public static string RankingVersion => $"rank{Ranking}";

    /// <summary>Evidence-selection/receipt version applied at query time.</summary>
    public static string EvidenceVersion => $"ev{Evidence}";

    /// <summary>Representation/encoding version applied at render time.</summary>
    public static string RepresentationVersion => $"rep{Representation}";
}
