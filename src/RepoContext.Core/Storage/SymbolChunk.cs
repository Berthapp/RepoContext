using RepoContext.Core.Indexing;
using RepoContext.Core.Parsing;

namespace RepoContext.Core.Storage;

/// <summary>Builds the FTS chunk that makes a symbol searchable.</summary>
internal static class SymbolChunk
{
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
