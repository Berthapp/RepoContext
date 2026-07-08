namespace RepoContext.Core.Context;

/// <summary>Options controlling the context pipeline.</summary>
public sealed record ContextOptions
{
    public int Top { get; init; } = 8;

    /// <summary>Optional token budget (files are dropped once exceeded).</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>Whether to include a code snippet per file.</summary>
    public bool Snippets { get; init; }
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

    public int EstimatedTokens { get; init; }

    public string? Snippet { get; init; }
}

/// <summary>The result of <c>repoctx context</c>.</summary>
public sealed record ContextResult
{
    public required string Query { get; init; }

    public required IReadOnlyList<string> Terms { get; init; }

    public required IReadOnlyList<ContextItem> Items { get; init; }

    public required int TotalCandidates { get; init; }

    public required int EstimatedTokens { get; init; }
}
