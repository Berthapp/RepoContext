using System.Text.RegularExpressions;

namespace RepoContext.Core.Parsing;

/// <summary>
/// One structural anchor found by a line scanner: where it starts, how deeply
/// it nests (a lower level closes a higher one) and how it is labelled.
/// </summary>
internal readonly record struct StructureAnchor(
    int Row, int Level, string Name, SymbolKind Kind, string Signature);

/// <summary>
/// Shared machinery for the line-based extractors (ADR 0019/0020): turning
/// anchors into symbols with line ranges and summaries.
/// </summary>
/// <remarks>
/// Both extractors are deliberately line based and parser-free. A repository
/// that agents work in contains half-valid YAML, hand-edited exports, and
/// source in a dozen languages no bundled grammar covers; a strict parser
/// returns nothing for exactly the files an agent most needs an outline of.
/// Line scanning degrades gracefully - an unparsable region contributes no
/// anchor - and stays deterministic, which the whole tool rests on.
/// </remarks>
internal static partial class StructureAnchors
{
    /// <summary>
    /// Upper bound on emitted symbols per file. A generated 40k-line contract
    /// would otherwise produce an outline nobody can afford to read, and an
    /// index whose size the user pays for on every query.
    /// </summary>
    public const int MaxSymbols = 400;

    /// <summary>Summaries are cut to this many characters.</summary>
    private const int MaxDocLength = 200;

    /// <summary>
    /// Turns anchors into symbols by closing each one at the next anchor of the
    /// same or a shallower level, and summarising its body. Emitted in source
    /// order, capped, so the outline reads like the file.
    /// </summary>
    /// <param name="preferCommentAbove">
    /// Whether a comment line directly above the declaration is the better
    /// summary than the first line of the body. It is, for code: that comment
    /// says what the declaration is for, while the first body line is an
    /// implementation detail.
    /// </param>
    public static IReadOnlyList<Symbol> Materialize(
        List<StructureAnchor> anchors, string[] lines, bool preferCommentAbove = false)
    {
        var symbols = new List<Symbol>(Math.Min(anchors.Count, MaxSymbols));
        for (int i = 0; i < anchors.Count && symbols.Count < MaxSymbols; i++)
        {
            StructureAnchor anchor = anchors[i];
            int endRow = lines.Length - 1;
            for (int j = i + 1; j < anchors.Count; j++)
            {
                if (anchors[j].Level <= anchor.Level)
                {
                    endRow = anchors[j].Row - 1;
                    break;
                }
            }

            if (endRow < anchor.Row)
            {
                endRow = anchor.Row;
            }

            symbols.Add(new Symbol
            {
                Name = anchor.Name,
                Kind = anchor.Kind,
                StartLine = anchor.Row + 1,
                EndLine = endRow + 1,
                Signature = anchor.Signature,
                Doc = (preferCommentAbove ? CommentAbove(lines, anchor.Row) : null)
                    ?? Summarize(lines, anchor.Row + 1, endRow),
            });
        }

        return symbols;
    }

    /// <summary>The first meaningful line of a body - what the section is about.</summary>
    public static string? Summarize(string[] lines, int from, int toInclusive)
    {
        for (int i = from; i <= toInclusive && i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || IsDecoration(line))
            {
                continue;
            }

            return Clip(Clean(line));
        }

        return null;
    }

    /// <summary>
    /// The comment block immediately above a declaration, joined into one line.
    /// Covers <c>#</c>, <c>//</c>, <c>--</c> and <c>///</c> line comments, which
    /// is every line-comment syntax among the languages handled here.
    /// </summary>
    public static string? CommentAbove(string[] lines, int declarationRow)
    {
        var collected = new List<string>();
        for (int i = declarationRow - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 && collected.Count == 0)
            {
                continue;
            }

            string? text = CommentText(line);
            if (text is null)
            {
                break;
            }

            collected.Add(text);
        }

        if (collected.Count == 0)
        {
            return null;
        }

        collected.Reverse();
        string joined = Clean(string.Join(' ', collected.Where(t => t.Length > 0)));
        return joined.Length == 0 ? null : Clip(joined);
    }

    private static string? CommentText(string line)
    {
        foreach (string marker in (string[])["///", "//", "#", "--", "*"])
        {
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                // "#!" is a shebang and "#region" is tooling, not documentation.
                string text = line[marker.Length..].Trim();
                return text.StartsWith('!') || text.StartsWith("region", StringComparison.Ordinal)
                    ? string.Empty
                    : text;
            }
        }

        return null;
    }

    /// <summary>Markup that carries no meaning on its own (fences, rules, list bullets).</summary>
    private static bool IsDecoration(string line) =>
        line.StartsWith("```", StringComparison.Ordinal)
        || line.StartsWith("~~~", StringComparison.Ordinal)
        || line.StartsWith("---", StringComparison.Ordinal)
        || line.StartsWith("===", StringComparison.Ordinal)
        || line.StartsWith("|--", StringComparison.Ordinal)
        || line is "{" or "}" or "[" or "]" or "-" or "(" or ")";

    public static string Clean(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    public static string Clip(string value) =>
        value.Length <= MaxDocLength ? value : value[..MaxDocLength];

    /// <summary>Leading whitespace width, tabs counted as one - the nesting level of a line.</summary>
    public static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }

    public static string[] SplitLines(string content)
    {
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length == 0)
        {
            return [];
        }

        if (normalized[^1] == '\n')
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
