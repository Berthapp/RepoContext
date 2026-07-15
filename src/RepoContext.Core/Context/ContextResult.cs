using RepoContext.Core.Outline;

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

    /// <summary>Plus the best-matching source slice, so no file read is needed.</summary>
    Slices,
}

/// <summary>Options controlling the context pipeline.</summary>
public sealed record ContextOptions
{
    public int Top { get; init; } = 8;

    /// <summary>
    /// Optional token budget. The bundle is packed so the tokens the agent
    /// will spend consuming it (full reads for <see cref="ContextDetail.Paths"/>,
    /// the included content otherwise) stay within budget; at least one item
    /// is always returned.
    /// </summary>
    public int? BudgetTokens { get; init; }

    public ContextDetail Detail { get; init; } = ContextDetail.Paths;

    /// <summary>
    /// Files the agent already has, path → content hash as previously emitted.
    /// A candidate whose stored hash still matches is returned as an
    /// <c>unchanged</c> marker (path, score, reasons — no content, zero token
    /// charge). Supplied by the caller, so determinism is preserved: identical
    /// inputs still yield identical output.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Known { get; init; }
}

/// <summary>A single file in a context bundle, with its reasons.</summary>
public sealed record ContextItem
{
    public required string Path { get; init; }

    public required string Kind { get; init; }

    public required double Score { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Short content hash — echo it back via <c>known</c> to skip unchanged files.</summary>
    public required string Hash { get; init; }

    /// <summary>
    /// What consuming this item costs at the bundle's detail level: the full
    /// file for paths, the included content otherwise, 0 when unchanged.
    /// </summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Full-file read cost, when it differs from <see cref="EstimatedTokens"/>.</summary>
    public int? FileTokens { get; init; }

    /// <summary>True when the caller's known hash still matches; no content included.</summary>
    public bool Unchanged { get; init; }

    /// <summary>Symbol skeleton (outline detail).</summary>
    public IReadOnlyList<OutlineSymbol>? Symbols { get; init; }

    /// <summary>Symbols beyond the per-item cap (outline detail).</summary>
    public int? SymbolsOmitted { get; init; }

    /// <summary>Source slice (slices detail) covering exactly StartLine..EndLine.</summary>
    public string? Snippet { get; init; }
}

/// <summary>The result of <c>repoctx context</c>.</summary>
public sealed record ContextResult
{
    public required string Query { get; init; }

    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>Short state hash of the index this bundle was computed from.</summary>
    public required string State { get; init; }

    public required ContextDetail Detail { get; init; }

    /// <summary>
    /// Active token-calibration label (profile name or <c>x&lt;factor&gt;</c>),
    /// null when counts are raw o200k (ADR 0012).
    /// </summary>
    public string? TokenProfile { get; init; }

    public required IReadOnlyList<ContextItem> Items { get; init; }

    public required int TotalCandidates { get; init; }

    /// <summary>Scored candidates that did not fit the top/budget limits.</summary>
    public required int Omitted { get; init; }

    public required int EstimatedTokens { get; init; }
}
