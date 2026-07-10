namespace RepoContext.Core.Architecture;

/// <summary>A directory node in the architecture tree (LOC-aggregated).</summary>
public sealed record TreeNode
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required int Loc { get; init; }

    public required int FileCount { get; init; }

    public required IReadOnlyList<TreeNode> Children { get; init; }
}

/// <summary>Language distribution entry.</summary>
public readonly record struct LanguageStat(string Language, int Files, int Loc);

/// <summary>The result of <c>repoctx architecture</c> (spec F6).</summary>
public sealed record ArchitectureResult
{
    public required int TotalFiles { get; init; }

    public required int TotalLoc { get; init; }

    public required TreeNode Tree { get; init; }

    public required IReadOnlyList<LanguageStat> Languages { get; init; }

    public required IReadOnlyList<(string Path, int Dependents)> Central { get; init; }

    public required IReadOnlyList<string> Entrypoints { get; init; }
}
