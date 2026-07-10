namespace RepoContext.Core.Scanning;

/// <summary>A file selected for indexing by the scanner.</summary>
public sealed record ScannedFile
{
    public required string AbsolutePath { get; init; }

    /// <summary>Repo-relative path with <c>/</c> separators (the stable identity of a file).</summary>
    public required string RelativePath { get; init; }

    public required FileKind Kind { get; init; }

    public required SourceLanguage Language { get; init; }

    public required long SizeBytes { get; init; }
}
