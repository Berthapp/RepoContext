using System.Text.RegularExpressions;

namespace RepoContext.Core.Scanning;

/// <summary>
/// A gitignore-style path matcher. Used for <c>.gitignore</c>,
/// <c>.repoctxignore</c>, the config <c>exclude</c> list and
/// <c>sensitiveFiles</c>. Paths are relative to the repository root, use
/// <c>/</c> separators, and directory pruning during traversal handles
/// descendant semantics.
/// </summary>
public sealed class GitignoreMatcher
{
    private readonly record struct Rule(Regex Regex, bool Negate, bool DirOnly, bool Anchored);

    private readonly IReadOnlyList<Rule> _rules;

    private GitignoreMatcher(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>An empty matcher that ignores nothing.</summary>
    public static GitignoreMatcher Empty { get; } = new([]);

    /// <summary>Parses gitignore-syntax lines into a matcher.</summary>
    public static GitignoreMatcher Parse(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            bool negate = false;
            if (line.StartsWith('!'))
            {
                negate = true;
                line = line[1..];
            }

            bool dirOnly = line.EndsWith('/');
            if (dirOnly)
            {
                line = line[..^1];
            }

            bool anchored = line.StartsWith('/');
            if (anchored)
            {
                line = line[1..];
            }

            // A slash anywhere (other than a trailing one) anchors the pattern.
            if (line.Contains('/'))
            {
                anchored = true;
            }

            if (line.Length == 0)
            {
                continue;
            }

            rules.Add(new Rule(GlobPattern.ToRegex(line), negate, dirOnly, anchored));
        }

        return new GitignoreMatcher(rules);
    }

    /// <summary>Builds a matcher from a single set of literal glob patterns
    /// (never negated), e.g. the config <c>exclude</c> or <c>sensitiveFiles</c>.</summary>
    public static GitignoreMatcher FromGlobs(IEnumerable<string> globs) => Parse(globs);

    /// <summary>Whether the given path is ignored. Later rules win (enabling negation).</summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        string basename = normalized.Length == 0
            ? string.Empty
            : normalized[(normalized.LastIndexOf('/') + 1)..];

        bool ignored = false;
        foreach (Rule rule in _rules)
        {
            if (rule.DirOnly && !isDirectory)
            {
                continue;
            }

            string target = rule.Anchored ? normalized : basename;
            if (rule.Regex.IsMatch(target))
            {
                ignored = !rule.Negate;
            }
        }

        return ignored;
    }

    /// <summary>True when there are no rules.</summary>
    public bool IsEmpty => _rules.Count == 0;
}
