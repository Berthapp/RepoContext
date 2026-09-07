namespace RepoContext.Core.Identity;

/// <summary>
/// Version stamps for every deterministic producer whose behaviour can change
/// the analysed result or a reuse receipt (Q4, ADR 0015). These are bumped by
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
    /// <summary>
    /// Scanner + file classification (which files, kind/language labels). v2
    /// names the artifact formats ADR 0020 gave structure to - AsciiDoc,
    /// reStructuredText, properties, INI, Makefile, Dockerfile, HCL, Gherkin -
    /// and stops a language label alone promoting a file to <c>source</c>,
    /// which had made a CSV matrix and an exported HTML page source files.
    /// </summary>
    public const int Scanner = 2;

    /// <summary>Byte decoding and newline normalisation feeding chunk/symbol text.</summary>
    public const int Decoder = 1;

    /// <summary>
    /// Tree-sitter parser + symbol extraction (ADR 0001/0005). v2 adds
    /// structure symbols for non-code artifacts - Markdown/AsciiDoc/HTML
    /// headings, YAML/JSON keys, Gherkin scenarios, SQL objects (ADR 0019).
    /// v3 completes the coverage: declarations for every source language
    /// without a bundled grammar, and structure for the remaining text
    /// formats - XML, reStructuredText, properties, delimited tables,
    /// Makefile, Dockerfile, HCL (ADR 0020).
    /// </summary>
    public const int Parser = 3;

    /// <summary>Line/heading chunker and synthetic symbol chunks (ADR 0004/0005).</summary>
    public const int Chunker = 1;

    /// <summary>Offline BPE tokenizer (<c>o200k_base</c>, ADR 0010).</summary>
    public const int Tokenizer = 1;

    /// <summary>
    /// Import/test graph construction (ADR 0006). v2 stores per-file references
    /// and adds cross-artifact reference edges (ADR 0019). v3/v4 exempt the
    /// reference kinds the graph is resolved from - type uses, then module
    /// imports - from the artifact bound, which changes the references a file
    /// stores and therefore the edges resolved from them.
    /// v6 narrows C# syntax edges by indexed project references and rejects
    /// unsupported or malformed TS module configuration during resolution.
    /// v7 preserves unique C# syntax links when build imports leave project scope unknown.
    /// </summary>
    public const int Graph = 7;

    /// <summary>Canonical indexed content-state fingerprint layout (ADR 0015).</summary>
    public const int StateFingerprint = 2;

    /// <summary>
    /// Weighted ranking + diversity + vendor penalty formula (ADR 0006). v2
    /// seeds candidates from work-item keys and links, and expands over
    /// reference edges (ADR 0019).
    /// </summary>
    public const int Ranking = 3;

    /// <summary>
    /// Evidence selection and receipt canonicalisation (Q1/Q2, ADR 0015). Bumped
    /// when the delivered evidence for the same query/content would change, which
    /// must invalidate outstanding receipts even though the source is unchanged.
    /// </summary>
    public const int Evidence = 3;

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
