using RepoContext.Core.Context;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>The task class a labelled query belongs to, used for stratified gates.</summary>
public enum TaskClass
{
    /// <summary>"Where is X?" — find the right file.</summary>
    Locate,

    /// <summary>"How does X work?" — survey structure.</summary>
    Explain,

    /// <summary>"Change X" — needs the exact implementation span.</summary>
    Fix,

    /// <summary>"What breaks if X changes?" — needs dependency/test edges.</summary>
    Impact,
}

/// <summary>The language stratum of a task, so one language cannot hide another.</summary>
public enum LanguageStratum
{
    CSharp,
    TypeScript,
}

/// <summary>
/// One labelled evaluation task (Q0). Labels are frozen and belong to the
/// evaluation, not to the product: a product change may never edit its own
/// labels to pass a gate.
/// </summary>
public sealed record EvalTask
{
    public required string Id { get; init; }

    public required string Query { get; init; }

    public required TaskClass Class { get; init; }

    public required LanguageStratum Language { get; init; }

    /// <summary>Files that must appear, in expected relevance order.</summary>
    public required IReadOnlyList<string> MustFindFiles { get; init; }

    /// <summary>Symbols that must be delivered when the task runs at outline detail.</summary>
    public IReadOnlyList<string> MustFindSymbols { get; init; } = [];

    /// <summary>
    /// Line ranges that must be covered by delivered spans when the task runs at
    /// slices detail. A task that names a range asserts the agent should not need
    /// a full-file read to see it.
    /// </summary>
    public IReadOnlyList<RequiredSpan> MustCoverSpans { get; init; } = [];

    /// <summary>Paths that must never appear in any result, at any detail.</summary>
    public IReadOnlyList<string> ForbiddenPaths { get; init; } = [];

    /// <summary>The detail level this task is evaluated at.</summary>
    public ContextDetail Detail { get; init; } = ContextDetail.Paths;

    /// <summary>Declared token budget the task is evaluated under, if any.</summary>
    public int? ResponseBudgetTokens { get; init; }

    /// <summary>Whether a full-file read should still be necessary after one call.</summary>
    public bool FullReadExpected { get; init; }
}

/// <summary>A labelled source range that delivered evidence must cover.</summary>
public readonly record struct RequiredSpan(string Path, int StartLine, int EndLine)
{
    /// <summary>Whether a delivered span covers this required range entirely.</summary>
    public bool CoveredBy(int start, int end) => start <= StartLine && end >= EndLine;
}
