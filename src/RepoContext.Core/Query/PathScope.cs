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
            foreach (string candidate in Expand(normalized))
            {
                // Every pattern also selects the subtree below it, so naming a
                // directory - however it is spelled - includes its files.
                globs.Add(candidate);
                globs.Add(candidate + "/*");
            }
        }

        return globs.Count == 0 ? null : new PathScope(globs, inputs);
    }

    /// <summary>
    /// The GLOB patterns one written pattern stands for.
    /// </summary>
    /// <remarks>
    /// A leading <c>**/</c> is gitignore for "at any depth, including here", and
    /// GLOB has no optional segment - so it becomes two patterns rather than
    /// one. Collapsing it to <c>*/</c> instead, as this first did, silently
    /// excluded every match at the repository root.
    /// </remarks>
    private static IEnumerable<string> Expand(string normalized)
    {
        if (normalized.StartsWith("**/", StringComparison.Ordinal))
        {
            string rest = normalized[3..];
            if (rest.Length > 0)
            {
                yield return Collapse(rest);
                yield return "*/" + Collapse(rest);
                yield break;
            }
        }

        yield return Collapse(normalized);
    }

    /// <summary>Under GLOB a single <c>*</c> already crosses directory separators.</summary>
    private static string Collapse(string pattern)
    {
        string collapsed = pattern;
        while (collapsed.Contains("**", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("**", "*", StringComparison.Ordinal);
        }

        return collapsed;
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
