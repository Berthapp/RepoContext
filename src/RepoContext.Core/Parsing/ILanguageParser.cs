using RepoContext.Core.Scanning;

namespace RepoContext.Core.Parsing;

/// <summary>Extracts symbols from source files (M2).</summary>
public interface ILanguageParser : IDisposable
{
    /// <summary>Whether this parser can handle the given language.</summary>
    bool Supports(SourceLanguage language);

    /// <summary>
    /// Extracts symbols from <paramref name="content"/>. <paramref name="relativePath"/>
    /// is used for path-based heuristics (e.g. Next.js route files).
    /// </summary>
    IReadOnlyList<Symbol> Parse(SourceLanguage language, string relativePath, string content);

    /// <summary>
    /// Returns the raw module specifiers imported by the file (TS/JS static
    /// imports and re-exports). Empty for languages without module imports.
    /// </summary>
    IReadOnlyList<string> ExtractImportSpecifiers(SourceLanguage language, string content);
}
