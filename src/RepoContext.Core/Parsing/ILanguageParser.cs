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
}
