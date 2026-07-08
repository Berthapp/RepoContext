using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoContext.Core.Configuration;

/// <summary>
/// The persisted <c>repoctx.config.json</c> contract (product doc chapter 14).
/// Keys are camelCase. Defaults come from <see cref="CreateDefault"/>.
/// </summary>
public sealed record RepoctxConfig
{
    public IReadOnlyList<string> Include { get; init; } = [];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool RespectGitignore { get; init; } = true;

    public IReadOnlyList<string> SensitiveFiles { get; init; } = [];

    public IndexingOptions Indexing { get; init; } = new();

    public RankingOptions Ranking { get; init; } = new();

    /// <summary>The default configuration written by <c>repoctx init</c>.</summary>
    public static RepoctxConfig CreateDefault() => new()
    {
        Include = ["src", "app", "lib", "docs"],
        Exclude = ["node_modules", "dist", "bin", "obj", ".next", ".git"],
        RespectGitignore = true,
        SensitiveFiles = [".env*", "*.secret.*", "appsettings.Production.json"],
        Indexing = new IndexingOptions
        {
            MaxFileSizeKb = 512,
            IncludeTests = true,
            IncludeDocs = true,
        },
        Ranking = new RankingOptions
        {
            Weights = new RankingWeights { Fts = 0.4, Symbol = 0.3, Graph = 0.2, Path = 0.1 },
            Synonyms = new Dictionary<string, IReadOnlyList<string>>(),
        },
    };

    /// <summary>Serializer options shared by config read/write (stable, indented).</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        // Deterministic, human-friendly output.
        NewLine = "\n",
    };
}

public sealed record IndexingOptions
{
    public int MaxFileSizeKb { get; init; } = 512;

    public bool IncludeTests { get; init; } = true;

    public bool IncludeDocs { get; init; } = true;
}

public sealed record RankingOptions
{
    public RankingWeights Weights { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Synonyms { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record RankingWeights
{
    public double Fts { get; init; } = 0.4;

    public double Symbol { get; init; } = 0.3;

    public double Graph { get; init; } = 0.2;

    public double Path { get; init; } = 0.1;
}
