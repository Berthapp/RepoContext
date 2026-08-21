using RepoContext.Core.Indexing;
using RepoContext.Core.Parsing;

namespace RepoContext.Core.Storage;

/// <summary>Builds the FTS chunk that makes a symbol searchable.</summary>
internal static class SymbolChunk
{
    /// <summary>
    /// Whether a symbol gets its own searchable chunk. Only declared code
    /// symbols do.
    /// </summary>
    /// <remarks>
    /// The structure of an artifact - a heading, a YAML key, a Gherkin scenario
    /// - is a symbol for the purpose of <c>outline</c>, but it is not a symbol
    /// for the purpose of the symbol <i>channel</i> of ranking. Its text is
    /// already indexed as part of the section or block chunk it heads, so
    /// giving it a second chunk would both duplicate that content in the
    /// full-text index and let a document heading compete with declarations for
    /// the channel that is supposed to mean "something is defined here"
    /// (ADR 0019).
    /// </remarks>
    public static bool IsSearchable(SymbolKind kind) =>
        kind is not (SymbolKind.Section or SymbolKind.Key or SymbolKind.Scenario or SymbolKind.Table);

    /// <summary>
    /// The content includes the symbol name, its split tokens (so
    /// <c>login</c> matches <c>loginUser</c>), its signature and its doc.
    /// The heading carries the symbol name for display.
    /// </summary>
    public static Chunk From(Symbol symbol)
    {
        string content = string.Join('\n',
            symbol.Name,
            Identifiers.SplitJoined(symbol.Name),
            symbol.Kind.ToString().ToLowerInvariant(),
            symbol.Signature,
            symbol.Doc ?? string.Empty);

        return new Chunk
        {
            Kind = ChunkKind.Symbol,
            StartLine = symbol.StartLine,
            EndLine = symbol.EndLine,
            Content = content,
            Heading = symbol.Name,
        };
    }
}
