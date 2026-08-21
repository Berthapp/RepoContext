using RepoContext.Core.Indexing;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>Where a traced term was declared: one symbol declaration.</summary>
public sealed record TraceDefinition(
    string Path,
    string FileKind,
    string Name,
    string SymbolKind,
    int StartLine,
    int EndLine,
    string Signature);

/// <summary>One file that mentions the traced term, with the lines it appears on.</summary>
public sealed record TraceMention(
    string Path,
    string FileKind,
    IReadOnlyList<int> Lines,
    int FileTokens,
    IReadOnlyList<string> Reasons);

/// <summary>The result of <c>repoctx trace</c>.</summary>
public sealed record TraceResult(
    string Query,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<TraceDefinition> Definitions,
    IReadOnlyList<TraceMention> Mentions,
    int TotalMentions,
    int OmittedMentions,
    int ProjectedReadTokens,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// Answers "where does this thing live in the repository" for a single term:
/// a ticket or requirement key, a link, a symbol name, or a path (ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// This is the lookup an agent otherwise performs by grepping the working tree
/// and reading whatever comes back. Both halves of that are expensive: the
/// search is linear in repository size, and the reading is linear in the size
/// of every file it touched. Here the answer comes from an index built for it,
/// and it is delivered as file/line coordinates plus what a full read
/// <i>would</i> have cost - so the agent decides what to open, if anything.
/// </para>
/// <para>
/// It is deliberately exact rather than fuzzy. <c>ABC-123</c> means that work
/// item, not "documents about ABC". Fuzzy matching is what <c>search</c> and
/// <c>context</c> are for, and mixing the two would make neither trustworthy.
/// </para>
/// </remarks>
public static class Trace
{
    /// <summary>Line numbers listed per file before the rest are summarised away.</summary>
    private const int MaxLinesPerFile = 5;

    /// <summary>Alternatives offered when a key resolves to nothing.</summary>
    private const int MaxSuggestions = 5;

    /// <summary>
    /// Traces <paramref name="term"/> across the index.
    /// <paramref name="top"/> caps the files returned; <paramref name="scope"/>
    /// narrows the search to part of the repository.
    /// </summary>
    public static TraceResult Query(
        IndexStore store, string term, int top, PathScope? scope = null, TokenScale scale = default)
    {
        string trimmed = (term ?? string.Empty).Trim();
        var resolved = new List<string>();
        var mentions = new Dictionary<string, MentionAccumulator>(StringComparer.Ordinal);

        void Collect(string kind, IReadOnlyList<RefMention> rows, string label)
        {
            if (rows.Count == 0)
            {
                return;
            }

            if (!resolved.Contains(label, StringComparer.Ordinal))
            {
                resolved.Add(label);
            }

            foreach (RefMention row in rows)
            {
                if (!mentions.TryGetValue(row.Path, out MentionAccumulator? accumulator))
                {
                    // Calibrated here, like every other per-file figure the tool
                    // reports: an uncalibrated per-file count would disagree with
                    // this response's own total and with outline/context.
                    accumulator = new MentionAccumulator(
                        row.Path, row.Kind, scale.Apply(row.FileTokens));
                    mentions[row.Path] = accumulator;
                }

                accumulator.Add(kind, row.Line);
            }
        }

        if (trimmed.Length == 0)
        {
            return Empty(trimmed);
        }

        if (IsLink(trimmed))
        {
            string link = NormalizeLink(trimmed);
            Collect(RefKind.Link, store.FindRefs(RefKind.Link, link, scope), link);
        }
        else
        {
            string key = trimmed.ToUpperInvariant();
            Collect(RefKind.Key, store.FindRefs(RefKind.Key, key, scope), key);
            Collect(RefKind.Symbol, store.FindRefs(RefKind.Symbol, trimmed, scope), trimmed);

            // A term that names an indexed file is traced as that file, so a
            // document mentioning only its basename is still found. What the
            // term denotes is looked up without the scope - the scope decides
            // what may be *returned*, not what a path means - while the mentions
            // it gathers are scoped like every other result.
            string path = RelativePath(trimmed);
            if (store.FindFile(path) is { } file)
            {
                Collect(RefKind.Path, store.FindPathRefs(file.Path, scope), file.Path);

                // Recorded even when nothing mentions it: "this file is indexed,
                // and nothing points at it" is an answer, and a different one
                // from "I did not recognise that term". Only for a file the
                // caller's scope actually includes.
                if (store.FindFile(file.Path, scope) is not null
                    && !resolved.Contains(file.Path, StringComparer.Ordinal))
                {
                    resolved.Add(file.Path);
                }
            }
            else
            {
                Collect(RefKind.Path, store.FindRefs(RefKind.Path, path, scope), path);
            }
        }

        IReadOnlyList<TraceDefinition> definitions = IsLink(trimmed)
            ? []
            :
            [
                .. store.FindSymbolDefinitions(trimmed, scope)
                    .Select(d => new TraceDefinition(
                        d.Path, d.FileKind, d.Name, d.SymbolKind, d.StartLine, d.EndLine, d.Signature))
            ];

        List<TraceMention> ordered =
        [
            .. mentions.Values
                .OrderBy(m => m.Path, StringComparer.Ordinal)
                .Select(m => m.ToMention())
        ];

        int cap = Math.Max(top, 0);
        List<TraceMention> kept = [.. ordered.Take(cap)];
        int projected = kept.Sum(m => m.FileTokens)
            + definitions
                .Where(d => !kept.Any(m => m.Path == d.Path))
                .Select(d => d.Path)
                .Distinct(StringComparer.Ordinal)
                .Sum(path => scale.Apply(store.FindFile(path, scope)?.TokenCount ?? 0));

        IReadOnlyList<string> suggestions = ordered.Count == 0 && definitions.Count == 0
            ? Suggest(store, trimmed)
            : [];

        return new TraceResult(
            trimmed,
            resolved,
            definitions,
            kept,
            ordered.Count,
            ordered.Count - kept.Count,
            projected,
            suggestions);
    }

    private static TraceResult Empty(string term) =>
        new(term, [], [], [], 0, 0, 0, []);

    /// <summary>
    /// Strips the leading <c>./</c>, <c>../</c> and separators of a path written
    /// relative to somewhere else.
    /// </summary>
    /// <remarks>
    /// Only the prefix is removed; the dot of <c>.gitignore</c> is part of its
    /// name, and trimming that made every dot-file untraceable.
    /// </remarks>
    private static string RelativePath(string term)
    {
        string path = term.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)
            || path.StartsWith("../", StringComparison.Ordinal))
        {
            path = path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..];
        }

        return path.TrimStart('/');
    }

    /// <summary>
    /// Keys that share the traced prefix. A mistyped or not-yet-referenced
    /// ticket is a common case, and answering it with the keys that do exist
    /// costs a handful of tokens instead of a failed second query.
    /// </summary>
    private static IReadOnlyList<string> Suggest(IndexStore store, string term)
    {
        int dash = term.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return [];
        }

        string prefix = term[..(dash + 1)].ToUpperInvariant();
        return store.SuggestRefValues(RefKind.Key, prefix, MaxSuggestions);
    }

    private static bool IsLink(string term) =>
        term.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || term.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mirrors the index-time link normalisation so a traced URL matches what was stored.</summary>
    private static string NormalizeLink(string url)
    {
        string trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '>', '"', '\'', '`');
        int fragment = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (fragment > 0)
        {
            trimmed = trimmed[..fragment];
        }

        int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return trimmed;
        }

        int hostEnd = trimmed.IndexOf('/', schemeEnd + 3);
        string prefix = hostEnd < 0 ? trimmed : trimmed[..hostEnd];
        string rest = hostEnd < 0 ? string.Empty : trimmed[hostEnd..];
        return prefix.ToLowerInvariant() + rest.TrimEnd('/');
    }

    /// <summary>Collects the lines and reference kinds under which one file mentioned the term.</summary>
    private sealed class MentionAccumulator(string path, string fileKind, int fileTokens)
    {
        private readonly SortedSet<int> _lines = [];
        private readonly SortedSet<string> _kinds = new(StringComparer.Ordinal);

        public string Path { get; } = path;

        public void Add(string kind, int line)
        {
            _kinds.Add(kind);
            if (line > 0)
            {
                _lines.Add(line);
            }
        }

        public TraceMention ToMention() => new(
            Path,
            fileKind,
            [.. _lines.Take(MaxLinesPerFile)],
            fileTokens,
            [.. _kinds]);
    }
}
