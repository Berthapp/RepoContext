using RepoContext.Core.Storage;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Outline;

/// <summary>A symbol as it appears in a file outline.</summary>
public sealed record OutlineSymbol(
    string Name, string Kind, int StartLine, int EndLine, string Signature, string? Doc)
{
    /// <summary>
    /// Why this symbol is in a query-aware outline (Q2):
    /// <see cref="OutlineRole.Match"/> for a query-matched symbol,
    /// <see cref="OutlineRole.Container"/> for scaffolding proven by source-range
    /// containment, or null for plain source-ordered structure.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Per-symbol reuse receipt (Q1), populated for context outlines. Echo it via
    /// <c>--seen</c> to suppress exactly this symbol on a later call.
    /// </summary>
    public string? Receipt { get; init; }
}

/// <summary>Declared explanations for why an outline symbol was selected.</summary>
public static class OutlineRole
{
    /// <summary>The symbol the query matched.</summary>
    public const string Match = "match";

    /// <summary>
    /// Scaffolding: a symbol whose source range contains a matched symbol. Release 1
    /// limits scaffolding to containment proven by indexed source ranges — import
    /// and type-use scaffolding needs facts the index does not yet carry (A3).
    /// </summary>
    public const string Container = "container";
}

/// <summary>A symbol the query matched, used to pin query-aware outlines.</summary>
public readonly record struct SymbolMatch(string Name, int StartLine, int EndLine);

/// <summary>
/// The skeleton of a file: identity, real token cost of a full read, and its
/// symbols with signatures and doc summaries — everything an agent needs to
/// decide whether (and which part of) the file is worth reading.
/// </summary>
public sealed record OutlineResult(
    string Path, string Kind, string Language, int Lines, int TokenCount, string Hash,
    IReadOnlyList<OutlineSymbol> Symbols);

/// <summary>Answers <c>outline</c> queries from the symbols table (M6, ADR 0010).</summary>
public static class Outline
{
    /// <summary>Doc summaries are cut to one line of at most this many characters.</summary>
    private const int MaxDocLength = 140;

    /// <summary>
    /// Returns the outline of a file, or null if it is not indexed. Standalone
    /// outlines stay source ordered and query-blind in Release 1; a later
    /// <c>--focus</c> can opt into the query-aware selector below.
    /// </summary>
    public static OutlineResult? Query(IndexStore store, string relativePath)
    {
        if (store.FindFile(relativePath) is not { } file)
        {
            return null;
        }

        (IReadOnlyList<OutlineSymbol> rawSymbols, _) = Symbols(store, file.Id, int.MaxValue);
        IReadOnlyList<OutlineSymbol> symbols = rawSymbols
            .Select(symbol => symbol with
            {
                Receipt = Receipt.For(
                    file.Path,
                    file.ContentHash,
                    ContextDetail.Outline.ToString().ToLowerInvariant(),
                    EvidenceUnitKind.Symbol,
                    symbol.StartLine,
                    symbol.EndLine,
                    symbol.Name,
                    DeliveredEvidence(symbol)),
            })
            .ToList();
        return new OutlineResult(
            file.Path, file.Kind, file.Language, file.LineCount, file.TokenCount,
            Hashes.Short(file.ContentHash), symbols);
    }

    /// <summary>
    /// A file's outline symbols capped at <paramref name="cap"/>, plus how many
    /// were cut.
    /// </summary>
    /// <remarks>
    /// When <paramref name="matches"/> is supplied the selection becomes
    /// query-aware (Q2): matched symbols are pinned first, then the containers
    /// that source-range containment proves, then plain structure in source
    /// order. This is what stops a query-matched symbol from vanishing merely
    /// because it sits below the first <paramref name="cap"/> symbols of the
    /// file. The <i>emitted</i> order stays source order so the skeleton still
    /// reads like the file.
    /// </remarks>
    public static (IReadOnlyList<OutlineSymbol> Symbols, int Omitted) Symbols(
        IndexStore store, long fileId, int cap, IReadOnlyList<SymbolMatch>? matches = null)
    {
        IReadOnlyList<SymbolRow> rows = store.GetSymbols(fileId);
        if (rows.Count == 0)
        {
            return ([], 0);
        }

        List<(SymbolRow Row, int Rank, string? Role, int Order)> ranked = [];
        for (int i = 0; i < rows.Count; i++)
        {
            SymbolRow row = rows[i];
            string? role = null;
            int rank = 2;

            if (matches is { Count: > 0 })
            {
                if (matches.Any(m => IsSame(m, row)))
                {
                    rank = 0;
                    role = OutlineRole.Match;
                }
                else if (matches.Any(m => Contains(row, m)))
                {
                    rank = 1;
                    role = OutlineRole.Container;
                }
            }

            ranked.Add((row, rank, role, i));
        }

        List<(SymbolRow Row, string? Role, int Order)> selected = ranked
            .OrderBy(e => e.Rank)
            .ThenBy(e => e.Order)
            .Take(cap)
            .Select(e => (e.Row, e.Role, e.Order))
            .OrderBy(e => e.Order)
            .ToList();

        List<OutlineSymbol> symbols = selected
            .Select(e => new OutlineSymbol(
                e.Row.Name, e.Row.Kind, e.Row.StartLine, e.Row.EndLine, e.Row.Signature,
                Summarize(e.Row.Doc))
            {
                Role = e.Role,
            })
            .ToList();

        return (symbols, rows.Count - symbols.Count);
    }

    /// <summary>
    /// The canonical source evidence of one outline symbol, used for its receipt.
    /// Query-relative selection roles are deliberately excluded: a receipt from
    /// standalone <c>outline</c> must remain reusable when focused context later
    /// labels the same symbol as a match or container.
    /// </summary>
    public static string DeliveredEvidence(OutlineSymbol symbol) =>
        Canonical.JoinRecords(
        [
            symbol.Signature,
            symbol.Doc ?? string.Empty,
        ]);

    private static bool IsSame(SymbolMatch match, SymbolRow row) =>
        row.StartLine == match.StartLine
        && string.Equals(row.Name, match.Name, StringComparison.Ordinal);

    /// <summary>Strict source-range containment: the container is wider on at least one side.</summary>
    private static bool Contains(SymbolRow candidate, SymbolMatch inner) =>
        candidate.StartLine <= inner.StartLine
        && candidate.EndLine >= inner.EndLine
        && (candidate.StartLine < inner.StartLine || candidate.EndLine > inner.EndLine);

    /// <summary>First line of the doc, capped — an outline explains, it does not document.</summary>
    private static string? Summarize(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            return null;
        }

        string line = doc.TrimStart();
        int newline = line.IndexOf('\n');
        if (newline >= 0)
        {
            line = line[..newline];
        }

        line = line.TrimEnd();
        return line.Length <= MaxDocLength ? line : line[..MaxDocLength];
    }
}
