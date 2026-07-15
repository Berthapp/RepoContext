using RepoContext.Core.Indexing;
using RepoContext.Core.Outline;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Architecture;

/// <summary>A language line in the primer (LOC quantized for cache stability).</summary>
public sealed record PrimeLanguage(string Language, int ApproxLoc);

/// <summary>A top-level directory in the primer (quantized).</summary>
public sealed record PrimeDir(string Path, int ApproxLoc, int Files);

/// <summary>A key (most-imported) file with its skeleton, hash and read cost.</summary>
public sealed record PrimeFile(
    string Path, string Hash, int Tokens,
    IReadOnlyList<OutlineSymbol> Symbols, int SymbolsOmitted);

/// <summary>The result of <c>repoctx prime</c>.</summary>
public sealed record PrimeResult
{
    public required int ApproxFiles { get; init; }

    public required int ApproxLoc { get; init; }

    /// <summary>Languages ordered by name (not by size — size order churns).</summary>
    public required IReadOnlyList<PrimeLanguage> Languages { get; init; }

    /// <summary>Top-level directories ordered by path.</summary>
    public required IReadOnlyList<PrimeDir> Dirs { get; init; }

    public required IReadOnlyList<string> Entrypoints { get; init; }

    /// <summary>Most-imported files ordered by path, with outlines.</summary>
    public required IReadOnlyList<PrimeFile> Files { get; init; }
}

/// <summary>
/// Builds the cache-stable repository primer (ADR 0012): a block meant to sit
/// at the <b>top</b> of an agent's prompt behind a cache breakpoint, where
/// cached input tokens cost a fraction of fresh ones. Prompt caching is a
/// byte-prefix match, so the primer must not change unless the code does:
/// no timestamps, no state hash, no score-dependent ordering — everything is
/// ordered by path or name, and volatile aggregates (LOC, file counts) are
/// quantized to two significant digits so an ordinary edit leaves the primer
/// byte-identical. Per-file facts (outline, hash, read cost) change only when
/// that file changes.
/// </summary>
public sealed class PrimeEngine
{
    /// <summary>Default number of key files outlined in the primer.</summary>
    public const int DefaultFiles = 5;

    private const int MaxOutlineSymbols = 12;

    private readonly IndexStore _store;
    private readonly TokenScale _scale;

    public PrimeEngine(IndexStore store, TokenScale scale = default)
    {
        _store = store;
        _scale = scale;
    }

    public PrimeResult Build(int files = DefaultFiles)
    {
        ArchitectureResult architecture = new ArchitectureEngine(_store).Build(depth: 1);

        List<PrimeFile> keyFiles = _store.GetMostImported(files)
            .Select(c => c.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(BuildFile)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

        return new PrimeResult
        {
            ApproxFiles = Quantize(architecture.TotalFiles),
            ApproxLoc = Quantize(architecture.TotalLoc),
            Languages = architecture.Languages
                .OrderBy(l => l.Language, StringComparer.Ordinal)
                .Select(l => new PrimeLanguage(l.Language, Quantize(l.Loc)))
                .ToList(),
            Dirs = architecture.Tree.Children
                .OrderBy(d => d.Path, StringComparer.Ordinal)
                .Select(d => new PrimeDir(d.Path, Quantize(d.Loc), Quantize(d.FileCount)))
                .ToList(),
            Entrypoints = architecture.Entrypoints,
            Files = keyFiles,
        };
    }

    private PrimeFile? BuildFile(string path)
    {
        if (_store.FindFile(path) is not { } row)
        {
            return null;
        }

        (IReadOnlyList<OutlineSymbol> symbols, int omitted) =
            Core.Outline.Outline.Symbols(_store, row.Id, MaxOutlineSymbols);
        return new PrimeFile(
            row.Path, Hashes.Short(row.ContentHash), _scale.Apply(row.TokenCount),
            symbols, omitted);
    }

    /// <summary>
    /// Rounds to two significant digits (away from zero at midpoints) so
    /// small edits do not move the number — the quantization that keeps the
    /// primer byte-stable across ordinary work.
    /// </summary>
    public static int Quantize(int value)
    {
        if (value < 100)
        {
            return value;
        }

        int magnitude = 1;
        for (int v = value; v >= 100; v /= 10)
        {
            magnitude *= 10;
        }

        return (int)(Math.Round(value / (double)magnitude, MidpointRounding.AwayFromZero) * magnitude);
    }
}
