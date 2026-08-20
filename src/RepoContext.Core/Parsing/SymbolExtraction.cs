using RepoContext.Core.Scanning;

namespace RepoContext.Core.Parsing;

/// <summary>
/// Chooses how a file's symbols are extracted, so every indexed text file has
/// exactly one answer to "what is in here" (ADR 0020).
/// </summary>
/// <remarks>
/// The order is precision first: a real grammar where one is bundled, then the
/// line-based declaration patterns for the other languages, then artifact
/// structure. A file matching none of them is still indexed - its content and
/// its references are - it simply has no skeleton to show.
/// </remarks>
public static class SymbolExtraction
{
    /// <summary>Extracts the symbols of one file, or an empty list when it has no structure.</summary>
    public static IReadOnlyList<Symbol> For(
        ILanguageParser parser, SourceLanguage language, string relativePath, string content)
    {
        if (parser.Supports(language))
        {
            return parser.Parse(language, relativePath, content);
        }

        if (DeclarationExtractor.Supports(relativePath))
        {
            return DeclarationExtractor.Extract(relativePath, content);
        }

        return StructureExtractor.Extract(relativePath, content);
    }

    /// <summary>Whether any extractor can describe the structure of this path.</summary>
    public static bool HasStructure(SourceLanguage language, string relativePath) =>
        language is SourceLanguage.TypeScript or SourceLanguage.Tsx or SourceLanguage.JavaScript
            or SourceLanguage.CSharp
        || DeclarationExtractor.Supports(relativePath)
        || StructureExtractor.Supports(relativePath);
}
