namespace RepoContext.Core.Indexing;

/// <summary>The kind of a content chunk. Symbol chunks are added in M2.</summary>
public enum ChunkKind
{
    Preamble,
    MarkdownHeading,
    Block,
    Symbol,
}

/// <summary>A unit of indexed, searchable content within a file.</summary>
public sealed record Chunk
{
    public required ChunkKind Kind { get; init; }

    /// <summary>1-based inclusive start line.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based inclusive end line.</summary>
    public required int EndLine { get; init; }

    public required string Content { get; init; }

    /// <summary>Heading text for markdown-heading chunks; otherwise null.</summary>
    public string? Heading { get; init; }
}
