using RepoContext.Core.Outline;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Context;

/// <summary>
/// How much of each returned file the bundle carries (M6, ADR 0010) —
/// progressive disclosure so an agent only ever pays for the level it needs.
/// </summary>
public enum ContextDetail
{
    /// <summary>Pointers only: path, lines, reasons, real full-read token cost.</summary>
    Paths,

    /// <summary>Plus each file's symbol skeleton (signatures and doc summaries).</summary>
    Outline,

    /// <summary>
    /// Plus the best-matching source spans, often avoiding a full-file read.
    /// </summary>
    Slices,
}

/// <summary>Why positive-scoring candidates did not make it into the bundle (Q3).</summary>
public sealed record OmissionReasons
{
    /// <summary>Cut by the <c>--top</c> cap on new entries.</summary>
    public int Top { get; init; }

    /// <summary>Cut by the hard model-visible response ceiling.</summary>
    public int ResponseBudget { get; init; }

    /// <summary>Cut by the downstream full-file-read ceiling.</summary>
    public int ProjectedReadBudget { get; init; }

    /// <summary>
    /// Cut by the legacy <c>--budget-tokens</c> "charged work" cap. Reported
    /// separately from <see cref="ResponseBudget"/> because the two have
    /// different cost bases during the compatibility window (ADR 0016).
    /// </summary>
    public int BudgetTokens { get; init; }

    /// <summary>Scored at or below zero and therefore never eligible.</summary>
    public int NonpositiveScore { get; init; }

    /// <summary>Total omitted candidates across every reason.</summary>
    public int Total => Top + ResponseBudget + ProjectedReadBudget + BudgetTokens + NonpositiveScore;
}

/// <summary>Options controlling the context pipeline.</summary>
public sealed record ContextOptions
{
    /// <summary>
    /// Caps <i>new</i> entries in the bundle. Reused units acknowledged from
    /// <see cref="Seen"/> never consume a slot, so <c>Top = N</c> can always
    /// still deliver up to N genuinely new items.
    /// </summary>
    public int Top { get; init; } = 8;

    /// <summary>
    /// The legacy (v2) token budget, kept with its documented cost basis for the
    /// compatibility window: <see cref="ContextDetail.Paths"/> charges projected
    /// full reads, embedded detail charges its content basis. It is a "charged
    /// work" cap, not a promise about the size of the serialized response — use
    /// <see cref="ResponseBudgetTokens"/> for that.
    /// </summary>
    public int? BudgetTokens { get; init; }

    /// <summary>
    /// A hard ceiling on the exact model-visible response, tokenized at the
    /// surface boundary the caller renders to (CLI stdout including its trailing
    /// newline, or the MCP text content block). Unlike
    /// <see cref="BudgetTokens"/> this admits no first-item exception: if the
    /// smallest useful successful payload does not fit, the engine reports a
    /// <see cref="ContextResult.Shortfall"/> instead of overrunning.
    /// </summary>
    public int? ResponseBudgetTokens { get; init; }

    /// <summary>
    /// A hard ceiling on the full-file reads implied by delivered pointers.
    /// Embedded and reused evidence contribute zero.
    /// </summary>
    public int? ProjectedReadBudgetTokens { get; init; }

    public ContextDetail Detail { get; init; } = ContextDetail.Paths;

    /// <summary>
    /// Files the caller asserts it holds <b>in full</b>, path → content hash.
    /// This is an explicit whole-file possession claim: a matching candidate is
    /// acknowledged in <see cref="ContextResult.Reused"/> and carries no content.
    /// </summary>
    /// <remarks>
    /// Never derive an entry here from a partial response. A slice or outline
    /// proves possession of a range, not of the file; that is what
    /// <see cref="Seen"/> receipts are for. An eventual rename to
    /// <c>--known-file</c> is documented in ADR 0015.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Known { get; init; }

    /// <summary>
    /// Receipts for evidence units the caller already received (Q1). A unit whose
    /// recomputed receipt matches is acknowledged in
    /// <see cref="ContextResult.Reused"/> rather than re-sent, while unseen units
    /// of the same file remain fully deliverable. Invalid or non-matching entries
    /// fail closed and suppress nothing.
    /// </summary>
    public IReadOnlyList<string>? Seen { get; init; }

    /// <summary>Maximum non-overlapping source spans embedded per file (slices detail).</summary>
    public int MaxSpans { get; init; } = 3;

    /// <summary>Deterministic bound on how many reused units are echoed back.</summary>
    public int MaxReusedListed { get; init; } = 20;

    /// <summary>
    /// Whether embedded slices and memories are charged in their JSON-serialized
    /// form. Text/Markdown surfaces deliver raw text and disable this so the
    /// legacy charged-work budget remains faithful to the selected surface.
    /// </summary>
    public bool SerializedCharging { get; init; } = true;

    /// <summary>
    /// Opt-in lossy transform: strip full-line comments and collapse blank
    /// runs in embedded slices. Items whose text was altered carry
    /// <see cref="ContextItem.Stripped"/> so line ranges are known to be
    /// approximate (ADR 0012).
    /// </summary>
    public bool StripComments { get; init; }

    /// <summary>
    /// Agent memories visible to this call — long-term entries plus the
    /// active session's short-term ones (ADR 0013). Loaded by the caller (the
    /// engine does no file I/O), so determinism holds: the memory store is
    /// input exactly like <see cref="Known"/>. Relevant entries are folded
    /// into the bundle; null or empty disables memory items.
    /// </summary>
    public IReadOnlyList<Memory.MemoryEntry>? Memories { get; init; }

    /// <summary>
    /// The canonical option string used for <c>evidence_id</c>. Omitted and
    /// explicitly-defaulted options normalise to the same value here, so two
    /// requests that mean the same thing share an identity (Q4). Raw
    /// <see cref="Known"/>, <see cref="Seen"/>, and memory inputs are
    /// deliberately excluded: only their effective delivered/reused selections
    /// belong to the identity, and those are hashed by the engine's evidence
    /// records. Unmatched session history must not churn an unchanged result.
    /// </summary>
    public string CanonicalForm() => Canonical.JoinRecords(
    [
        $"top={Top}",
        $"detail={Detail.ToString().ToLowerInvariant()}",
        $"budget={Invariant(BudgetTokens)}",
        $"response_budget={Invariant(ResponseBudgetTokens)}",
        $"read_budget={Invariant(ProjectedReadBudgetTokens)}",
        $"max_spans={MaxSpans}",
        $"max_reused_listed={MaxReusedListed}",
        $"serialized_charging={SerializedCharging}",
        $"strip_comments={StripComments}",
    ]);

    private static string Invariant(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
}

/// <summary>
/// One delivered source span: an exact, unambiguous line range plus the receipt
/// that proves possession of precisely those lines (Q1/Q2).
/// </summary>
public sealed record ContextSpan
{
    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required string Text { get; init; }

    /// <summary>Per-span receipt. Echo it via <c>--seen</c> to suppress only this span.</summary>
    public required string Receipt { get; init; }

    /// <summary>The symbol this span was aligned to, when it was.</summary>
    public string? Symbol { get; init; }

    /// <summary>Whether the delivered text was comment-stripped.</summary>
    internal bool Stripped { get; init; }

    /// <summary>
    /// Internal marginal-relevance order used when a hard budget can fit only a
    /// subset of this file's spans. Renderers deliberately omit it and retain
    /// source order on the wire.
    /// </summary>
    internal int SelectionRank { get; init; }
}

/// <summary>A caller-held evidence unit acknowledged instead of re-sent (Q1).</summary>
public sealed record ReusedUnit
{
    public required string Path { get; init; }

    public required string Receipt { get; init; }

    public int? StartLine { get; init; }

    public int? EndLine { get; init; }

    /// <summary>The symbol identity for a reused outline symbol.</summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Full-file read tokens objectively avoided by this acknowledgement.
    /// Positive only for an explicit matching full-file <c>known</c> assertion;
    /// a span, symbol, or pointer receipt does not prove that a file read was
    /// avoided and therefore carries zero.
    /// </summary>
    public int AvoidedReadTokens { get; init; }
}

/// <summary>
/// Reported when a requested hard response budget cannot fit a useful successful
/// payload. This is an error channel: no partial success result is emitted
/// alongside it (Q3).
/// </summary>
public sealed record BudgetShortfall
{
    /// <summary>The response budget the caller asked for.</summary>
    public required int RequestedBudgetTokens { get; init; }

    /// <summary>
    /// A deterministically computed budget guaranteed to fit a useful successful
    /// retry. It may be conservatively larger than the mathematical minimum so
    /// error handling remains bounded on large repositories.
    /// </summary>
    public required int RetryBudgetTokens { get; init; }
}

/// <summary>A single file in a context bundle, with its reasons.</summary>
public sealed record ContextItem
{
    public required string Path { get; init; }

    public required string Kind { get; init; }

    public required double Score { get; init; }

    /// <summary>
    /// First line of the single delivered span. Deprecated in v3: meaningful only
    /// when <see cref="Spans"/> holds exactly one span. Multi-span items omit it
    /// rather than inventing a synthetic enclosing range.
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>Last line of the single delivered span. Deprecated in v3; see <see cref="StartLine"/>.</summary>
    public required int EndLine { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    /// Short content hash of the whole file. Echo it via <c>--known</c> only when
    /// you hold the <b>entire</b> file; to reuse a delivered range or symbol use
    /// its receipt with <c>--seen</c> instead.
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Deprecated (v2 meaning): what consuming this item costs at the bundle's
    /// detail level. New consumers read <see cref="ContentTokens"/> and
    /// <see cref="ProjectedReadTokens"/>, which separate embedded evidence from
    /// downstream reads instead of conflating them.
    /// </summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Deprecated (v2): full-file read cost when it differs from <see cref="EstimatedTokens"/>.</summary>
    public int? FileTokens { get; init; }

    /// <summary>Tokens of evidence embedded in this item; zero for a pointer or a marker.</summary>
    public int ContentTokens { get; init; }

    /// <summary>Full-file read tokens implied by this item; zero unless it is a pointer.</summary>
    public int ProjectedReadTokens { get; init; }

    /// <summary>
    /// Set when another bundle item carries byte-identical content (copied or
    /// generated files): read that one instead — this item costs nothing.
    /// </summary>
    public string? DuplicateOf { get; init; }

    /// <summary>True when the slice was comment-stripped (line ranges approximate).</summary>
    public bool Stripped { get; init; }

    /// <summary>Symbol skeleton (outline detail); each entry carries its own receipt.</summary>
    public IReadOnlyList<OutlineSymbol>? Symbols { get; init; }

    /// <summary>Symbols beyond the per-item cap (outline detail).</summary>
    public int? SymbolsOmitted { get; init; }

    /// <summary>
    /// The delivered source spans (slices detail). Present whenever any source
    /// text is embedded, including the single-span case.
    /// </summary>
    public IReadOnlyList<ContextSpan>? Spans { get; init; }

    /// <summary>Relevant spans removed while fitting an active budget.</summary>
    public int? SpansOmitted { get; init; }

    /// <summary>Deprecated (v2): the single span's text; omitted when there are several.</summary>
    public string? Snippet { get; init; }

    /// <summary>
    /// Convenience alias repeating the receipt of the item's only evidence unit.
    /// A multi-unit item omits this: suppressing one span must never be
    /// expressible as suppressing the whole file.
    /// </summary>
    public string? Receipt { get; init; }
}

/// <summary>The result of <c>repoctx context</c>.</summary>
public sealed record ContextResult
{
    public required string Query { get; init; }

    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>Deprecated (v2): short <c>content_state</c>. Use <see cref="ContentState"/>.</summary>
    public required string State { get; init; }

    /// <summary>Short fingerprint of which file contents are indexed (Q4).</summary>
    public required string ContentState { get; init; }

    /// <summary>Full internal indexed-content fingerprint.</summary>
    public string FullContentState { get; init; } = string.Empty;

    /// <summary>Short fingerprint of content plus config and producer versions (Q4).</summary>
    public required string AnalysisState { get; init; }

    /// <summary>Full internal content/config/producer fingerprint.</summary>
    public string FullAnalysisState { get; init; } = string.Empty;

    /// <summary>Short display identity of the selected evidence for this analysed query (Q4).</summary>
    public required string EvidenceId { get; init; }

    /// <summary>
    /// Full internal evidence identity. Renderers use this value when deriving a
    /// representation identity; the short display hash is never the sole input.
    /// </summary>
    public string FullEvidenceId { get; init; } = string.Empty;

    public required ContextDetail Detail { get; init; }

    /// <summary>The cap on new entries that was in force.</summary>
    public required int Top { get; init; }

    /// <summary>The active legacy charged-work cap, when supplied.</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>The active exact model-visible response cap, when supplied.</summary>
    public int? ResponseBudgetTokens { get; init; }

    /// <summary>The active projected downstream full-read cap, when supplied.</summary>
    public int? ProjectedReadBudgetTokens { get; init; }

    public required IReadOnlyList<ContextItem> Items { get; init; }

    /// <summary>Deterministic, budgeted prefix of the acknowledged reused units.</summary>
    public required IReadOnlyList<ReusedUnit> Reused { get; init; }

    /// <summary>Total valid matched receipts, including any beyond <see cref="Reused"/>.</summary>
    public required int ReusedCount { get; init; }

    /// <summary>Number of distinct files represented by all matched reuse units.</summary>
    public int ReusedFilesCount { get; init; }

    /// <summary>
    /// Full-file read tokens avoided by explicit matching full-file possession
    /// claims, including claims beyond the bounded listed prefix.
    /// </summary>
    public int ReusedReadTokens { get; init; }

    /// <summary>
    /// Active token-calibration label (profile name or <c>x&lt;factor&gt;</c>),
    /// null when counts are raw o200k (ADR 0012).
    /// </summary>
    public string? TokenProfile { get; init; }

    /// <summary>
    /// Relevant agent memories folded into the bundle (ADR 0013): distilled
    /// prior knowledge at a fraction of a re-discovery's cost, each with
    /// reasons and a stale flag. Empty when none matched or memory was
    /// disabled.
    /// </summary>
    public IReadOnlyList<Memory.MemoryHit> Memories { get; init; } = [];

    public required int TotalCandidates { get; init; }

    /// <summary>Scored candidates that are neither delivered nor acknowledged.</summary>
    public required int Omitted { get; init; }

    /// <summary>The breakdown behind <see cref="Omitted"/>.</summary>
    public required OmissionReasons Omissions { get; init; }

    /// <summary>Deprecated (v2): the legacy blended cost. Use the explicit counters.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Total embedded evidence tokens across all items.</summary>
    public required int ContentTokens { get; init; }

    /// <summary>Total full-file read tokens implied by delivered pointers.</summary>
    public required int ProjectedReadTokens { get; init; }

    /// <summary>
    /// Set when a hard response budget could not fit the smallest useful payload.
    /// Callers must surface this as an error and emit no partial result.
    /// </summary>
    public BudgetShortfall? Shortfall { get; init; }

    /// <summary>Reused units beyond the echoed prefix, when positive.</summary>
    public int ReusedOmitted => Math.Max(ReusedCount - Reused.Count, 0);
}
