namespace RepoContext.Core.Storage;

/// <summary>A single search result: the best-matching chunk of a file.</summary>
public sealed record SearchHit
{
    public required string Path { get; init; }

    public required string Kind { get; init; }

    public required string ChunkKind { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public string? Heading { get; init; }

    /// <summary>Positive relevance score (higher is better).</summary>
    public required double Score { get; init; }

    /// <summary>Machine-readable reasons for the hit.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }
}
