using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoContext.Core.Configuration;

/// <summary>
/// The persisted <c>repoctx.config.json</c> contract (product doc chapter 14).
/// Keys are camelCase. Defaults come from <see cref="CreateDefault"/>.
/// </summary>
public sealed record RepoctxConfig
{
    /// <summary>
    /// Directories (or files) to scan, relative to the repository root. Each
    /// entry is scanned recursively. An empty list - the default - scans the
    /// whole repository, so a root containing several projects indexes all of
    /// them without further configuration.
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>
    /// Gitignore-style exclusion patterns. A bare name (<c>node_modules</c>)
    /// matches at any depth, which is what keeps per-project build output out
    /// of a multi-project repository.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = [.. DefaultExclude];

    public bool RespectGitignore { get; init; } = true;

    public IReadOnlyList<string> SensitiveFiles { get; init; } =
        [".env*", "*.secret.*", "appsettings.Production.json"];

    public IndexingOptions Indexing { get; init; } = new();

    /// <summary>
    /// How non-code artifacts (specs, tickets, exported documentation) are
    /// structured and cross-linked with the code (ADR 0019).
    /// </summary>
    public ArtifactOptions Artifacts { get; init; } = new();

    public RankingOptions Ranking { get; init; } = new();

    public TokenOptions Tokens { get; init; } = new();

    public PricingOptions Pricing { get; init; } = new();

    /// <summary>The default configuration written by <c>repoctx init</c>.</summary>
    public static RepoctxConfig CreateDefault() => new();

    /// <summary>
    /// Directory names that are generated build output in practically every
    /// ecosystem. They are matched by basename at any depth, so each project in
    /// a multi-project repository is covered. Ordered for a readable config
    /// file; the order does not affect matching.
    /// </summary>
    /// <remarks>
    /// Vendored source (<c>vendor/</c>) is deliberately absent: RepoContext
    /// indexes it and lets ranking apply the vendor penalty (ADR 0006), which
    /// keeps it findable when it is genuinely the answer.
    /// </remarks>
    private static readonly string[] DefaultExclude =
    [
        ".git",
        "node_modules",
        "dist",
        "build",
        "out",
        "bin",
        "obj",
        "target",
        ".next",
        ".nuxt",
        ".svelte-kit",
        ".turbo",
        ".angular",
        ".gradle",
        ".venv",
        "venv",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".tox",
        "coverage",
        ".idea",
        ".vs",
    ];

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

/// <summary>
/// Artifact awareness (ADR 0019): the repository of a working agent team holds
/// more than code - requirement documents, exported tickets, API contracts,
/// acceptance criteria. These settings decide which cross-artifact references
/// are extracted at index time and therefore what <c>trace</c>, <c>related</c>
/// and <c>context</c> can link together.
/// </summary>
public sealed record ArtifactOptions
{
    /// <summary>
    /// Extra regular expressions for external work-item keys, in addition to the
    /// built-in Jira/requirement shape (<c>ABC-123</c>). Each match becomes a
    /// <c>key</c> reference that <c>trace</c> resolves across code, tests and
    /// documents. Invalid or non-terminating patterns are ignored rather than
    /// failing the index.
    /// </summary>
    public IReadOnlyList<string> KeyPatterns { get; init; } = [];

    /// <summary>Link a document to the files whose repo-relative path it names.</summary>
    public bool LinkPaths { get; init; } = true;

    /// <summary>
    /// Link a document to the file that uniquely defines a symbol it names.
    /// Ambiguous names (defined in more than one file) are never linked.
    /// </summary>
    public bool LinkSymbols { get; init; } = true;

    /// <summary>
    /// Upper bound on stored artifact references per file and reference kind -
    /// the paths, keys, links and symbols a file names. A bound is required: a
    /// generated or vendored artifact can otherwise contribute unbounded rows to
    /// an index whose size is a cost the user pays for.
    /// </summary>
    /// <remarks>
    /// The type references of a C# file are deliberately outside this bound.
    /// They are not an artifact link that trades index size for a marginal
    /// result: they are the sole input to that file's import edges, so
    /// truncating them drops real dependencies from the graph. They carry their
    /// own, far larger internal bound (ADR 0019 §2).
    /// </remarks>
    public int MaxRefsPerFile { get; init; } = 400;
}

/// <summary>
/// Query-time token-count calibration (ADR 0012). The index stores raw
/// <c>o200k_base</c> counts; these options scale what budgets charge and
/// what responses report so the figures match the consuming model family.
/// </summary>
public sealed record TokenOptions
{
    /// <summary>
    /// Calibration profile: <c>o200k</c>/<c>openai</c> (raw counts, default)
    /// or <c>claude</c> (~1.2×). Unknown names fall back to raw counts.
    /// </summary>
    public string Profile { get; init; } = "o200k";

    /// <summary>
    /// Explicit multiplier in <c>(0, 100]</c>; overrides
    /// <see cref="Profile"/> when valid. Invalid values fall back to raw counts.
    /// </summary>
    public double? Factor { get; init; }
}

/// <summary>
/// Optional token pricing used by <c>repoctx stats</c> to express net savings
/// as money (ADR 0012). Prices change; RepoContext ships no built-in rates.
/// </summary>
public sealed record PricingOptions
{
    /// <summary>Price per million input tokens, in <see cref="Currency"/>; null disables the estimate.</summary>
    public double? InputPerMtok { get; init; }

    public string Currency { get; init; } = "USD";
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
