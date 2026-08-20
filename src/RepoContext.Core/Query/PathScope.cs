namespace RepoContext.Core.Query;

/// <summary>
/// A narrowing of a query to part of the repository (ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// A large repository is worked on by more than one agent, each responsible for
/// one area. Without a scope every one of them pays, on every call, for
/// candidates from areas they will never touch. <c>--path services/api</c> is
/// the cheapest possible relevance filter: it removes the noise before ranking
/// rather than after, and it removes it from the token bill too.
/// </para>
/// <para>
/// The patterns use SQLite <c>GLOB</c> semantics, which is also the engine that
/// evaluates them - there is exactly one implementation, so an in-memory filter
/// can never disagree with the stored query. <c>*</c> matches any run of
/// characters including <c>/</c>, <c>?</c> matches one character. A pattern
/// without wildcards is a directory (or file) prefix: <c>services/api</c>
/// selects that path and everything below it.
/// </para>
/// </remarks>
public sealed class PathScope
{
    private PathScope(IReadOnlyList<string> patterns, IReadOnlyList<string> inputs)
    {
        Patterns = patterns;
        Inputs = inputs;
    }

    /// <summary>The normalized GLOB patterns, in the order they were supplied.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>The patterns as the caller wrote them, for echoing back in results.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>
    /// Builds a scope, or null when nothing usable was supplied (which means
    /// "the whole repository" and costs no query complexity at all).
    /// </summary>
    public static PathScope? From(IEnumerable<string>? patterns)
    {
        if (patterns is null)
        {
            return null;
        }

        var globs = new List<string>();
        var inputs = new List<string>();
        foreach (string raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized = Normalize(raw);
            if (normalized.Length == 0)
            {
                continue;
            }

            inputs.Add(raw.Trim());
            if (normalized.AsSpan().IndexOfAny('*', '?', '[') >= 0)
            {
                // "**" is spelled by users who think in gitignore; under GLOB a
                // single "*" already crosses directory separators.
                globs.Add(normalized.Replace("**", "*", StringComparison.Ordinal));
            }
            else
            {
                globs.Add(normalized);
                globs.Add(normalized + "/*");
            }
        }

        return globs.Count == 0 ? null : new PathScope(globs, inputs);
    }

    private static string Normalize(string raw)
    {
        string value = raw.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Trim('/');
    }
}
