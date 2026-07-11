using RepoContext.Core.Storage;

namespace RepoContext.Core.Outline;

/// <summary>A symbol as it appears in a file outline.</summary>
public sealed record OutlineSymbol(
    string Name, string Kind, int StartLine, int EndLine, string Signature, string? Doc);

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

    /// <summary>Returns the outline of a file, or null if it is not indexed.</summary>
    public static OutlineResult? Query(IndexStore store, string relativePath)
    {
        if (store.FindFile(relativePath) is not { } file)
        {
            return null;
        }

        (IReadOnlyList<OutlineSymbol> symbols, _) = Symbols(store, file.Id, int.MaxValue);
        return new OutlineResult(
            file.Path, file.Kind, file.Language, file.LineCount, file.TokenCount,
            Hashes.Short(file.ContentHash), symbols);
    }

    /// <summary>
    /// A file's outline symbols capped at <paramref name="cap"/>, plus how
    /// many were cut. Used by <c>context --detail outline</c>, where each
    /// item affords a skeleton but not a symbol-by-symbol inventory.
    /// </summary>
    public static (IReadOnlyList<OutlineSymbol> Symbols, int Omitted) Symbols(
        IndexStore store, long fileId, int cap)
    {
        IReadOnlyList<SymbolRow> rows = store.GetSymbols(fileId);
        List<OutlineSymbol> symbols = rows
            .Take(cap)
            .Select(s => new OutlineSymbol(
                s.Name, s.Kind, s.StartLine, s.EndLine, s.Signature, Summarize(s.Doc)))
            .ToList();

        return (symbols, rows.Count - symbols.Count);
    }

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
