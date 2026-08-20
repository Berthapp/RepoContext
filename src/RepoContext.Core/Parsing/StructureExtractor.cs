using System.Text.RegularExpressions;

namespace RepoContext.Core.Parsing;

/// <summary>
/// Extracts the structure of a non-code artifact - a requirement document, an
/// exported ticket, an API contract, a feature file - as symbols, so the same
/// commands that explain code (<c>outline</c>, <c>search --symbols</c>,
/// <c>context</c>) also explain the documents an agent has to reconcile the
/// code with (ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// The extraction is line based and deliberately parser-free. A repository of
/// exported artifacts contains half-valid YAML, JSON fragments and hand-edited
/// tables; a strict parser would return nothing for exactly the files an agent
/// most needs an outline of. Line scanning degrades gracefully: an unparsable
/// region simply contributes no anchor.
/// </para>
/// <para>
/// Every rule is deterministic and depends only on the file extension and the
/// bytes of the file, so two runs over the same content produce byte-identical
/// symbols - the invariant the whole tool rests on.
/// </para>
/// </remarks>
public static partial class StructureExtractor
{
    /// <summary>
    /// Upper bound on emitted symbols per file. A generated 40k-line contract
    /// would otherwise produce an outline nobody can afford to read, and an
    /// index whose size the user pays for on every query.
    /// </summary>
    private const int MaxSymbols = 400;

    /// <summary>Documentation summaries are cut to this many characters.</summary>
    private const int MaxDocLength = 200;

    /// <summary>Deepest structural level emitted for key/value formats.</summary>
    private const int MaxKeyDepth = 2;

    /// <summary>Whether this extractor produces symbols for the given path.</summary>
    public static bool Supports(string relativePath) => Format(relativePath) != ArtifactFormat.None;

    /// <summary>
    /// Extracts structure symbols for <paramref name="relativePath"/>, or an
    /// empty list when the format carries no structure this extractor knows.
    /// </summary>
    public static IReadOnlyList<Symbol> Extract(string relativePath, string content)
    {
        ArtifactFormat format = Format(relativePath);
        if (format == ArtifactFormat.None || content.Length == 0)
        {
            return [];
        }

        string[] lines = SplitLines(content);
        if (lines.Length == 0)
        {
            return [];
        }

        List<Anchor> anchors = format switch
        {
            ArtifactFormat.Markdown => MarkdownAnchors(lines),
            ArtifactFormat.AsciiDoc => AsciiDocAnchors(lines),
            ArtifactFormat.Html => HtmlAnchors(lines),
            ArtifactFormat.Yaml => YamlAnchors(lines),
            ArtifactFormat.Json => JsonAnchors(lines),
            ArtifactFormat.Gherkin => GherkinAnchors(lines),
            ArtifactFormat.Ini => IniAnchors(lines),
            ArtifactFormat.Sql => SqlAnchors(lines),
            _ => [],
        };

        return Materialize(anchors, lines);
    }

    /// <summary>The artifact formats whose structure is extracted.</summary>
    private enum ArtifactFormat
    {
        None,
        Markdown,
        AsciiDoc,
        Html,
        Yaml,
        Json,
        Gherkin,
        Ini,
        Sql,
    }

    private static ArtifactFormat Format(string relativePath)
    {
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".mdx" or ".markdown" => ArtifactFormat.Markdown,
            ".adoc" or ".asciidoc" => ArtifactFormat.AsciiDoc,
            ".html" or ".htm" or ".xhtml" => ArtifactFormat.Html,
            ".yaml" or ".yml" => ArtifactFormat.Yaml,
            ".json" or ".jsonl" or ".ndjson" => ArtifactFormat.Json,
            ".feature" => ArtifactFormat.Gherkin,
            ".toml" or ".ini" or ".cfg" => ArtifactFormat.Ini,
            ".sql" => ArtifactFormat.Sql,
            _ => ArtifactFormat.None,
        };
    }

    /// <summary>
    /// One structural anchor: where it starts, how deeply it nests (a lower
    /// level closes a higher one) and how it is labelled.
    /// </summary>
    private readonly record struct Anchor(
        int Row, int Level, string Name, SymbolKind Kind, string Signature);

    /// <summary>
    /// Turns anchors into symbols by closing each one at the next anchor of the
    /// same or a shallower level, and summarising the first prose line of its
    /// body. Emitted in source order, capped, so the outline reads like the file.
    /// </summary>
    private static IReadOnlyList<Symbol> Materialize(List<Anchor> anchors, string[] lines)
    {
        var symbols = new List<Symbol>(Math.Min(anchors.Count, MaxSymbols));
        for (int i = 0; i < anchors.Count && symbols.Count < MaxSymbols; i++)
        {
            Anchor anchor = anchors[i];
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
                Doc = Summarize(lines, anchor.Row + 1, endRow),
            });
        }

        return symbols;
    }

    /// <summary>The first prose line of a section body - what the section is about.</summary>
    private static string? Summarize(string[] lines, int from, int toInclusive)
    {
        for (int i = from; i <= toInclusive && i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || IsDecoration(line))
            {
                continue;
            }

            line = WhitespaceRegex().Replace(line, " ");
            return line.Length <= MaxDocLength ? line : line[..MaxDocLength];
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
        || line is "{" or "}" or "[" or "]" or "-";

    private static List<Anchor> MarkdownAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                // A heading inside a fenced code block is sample text, not structure.
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#')
            {
                level++;
            }

            if (level is < 1 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            {
                continue;
            }

            string name = Clean(trimmed[level..]);
            if (name.Length == 0)
            {
                continue;
            }

            anchors.Add(new Anchor(i, level, name, SymbolKind.Section, trimmed.Trim()));
        }

        return anchors;
    }

    private static List<Anchor> AsciiDocAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '=')
            {
                level++;
            }

            if (level is < 1 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            {
                continue;
            }

            string name = Clean(trimmed[level..]);
            if (name.Length > 0)
            {
                anchors.Add(new Anchor(i, level, name, SymbolKind.Section, trimmed.Trim()));
            }
        }

        return anchors;
    }

    private static List<Anchor> HtmlAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in HtmlHeadingRegex().Matches(lines[i]))
            {
                int level = match.Groups[1].Value[0] - '0';
                string name = Clean(TagRegex().Replace(match.Groups[2].Value, " "));
                if (name.Length > 0)
                {
                    anchors.Add(new Anchor(i, level, name, SymbolKind.Section, name));
                }
            }
        }

        return anchors;
    }

    private static List<Anchor> YamlAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        var path = new List<(int Indent, string Name)>();
        foreach ((string line, int i) in lines.Select((line, i) => (line, i)))
        {
            Match match = YamlKeyRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            int indent = match.Groups[1].Value.Length;
            string key = Unquote(match.Groups[2].Value.Trim());
            if (key.Length == 0)
            {
                continue;
            }

            while (path.Count > 0 && path[^1].Indent >= indent)
            {
                path.RemoveAt(path.Count - 1);
            }

            path.Add((indent, key));
            if (path.Count > MaxKeyDepth)
            {
                continue;
            }

            anchors.Add(new Anchor(
                i, path.Count, string.Join('.', path.Select(p => p.Name)),
                SymbolKind.Key, line.Trim()));
        }

        return anchors;
    }

    private static List<Anchor> JsonAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        var path = new List<(int Depth, string Name)>();
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match match = JsonKeyRegex().Match(line);
            if (match.Success)
            {
                string key = match.Groups[1].Value;
                while (path.Count > 0 && path[^1].Depth >= depth)
                {
                    path.RemoveAt(path.Count - 1);
                }

                path.Add((depth, key));
                if (path.Count <= MaxKeyDepth && key.Length > 0)
                {
                    anchors.Add(new Anchor(
                        i, path.Count, string.Join('.', path.Select(p => p.Name)),
                        SymbolKind.Key, line.Trim()));
                }
            }

            depth += NestingDelta(line);
            if (depth < 0)
            {
                depth = 0;
            }
        }

        return anchors;
    }

    /// <summary>
    /// How much a line opens or closes JSON nesting, ignoring braces inside
    /// string literals (a description containing "{" must not shift the depth).
    /// </summary>
    private static int NestingDelta(string line)
    {
        int delta = 0;
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    delta++;
                    break;
                case '}' or ']':
                    delta--;
                    break;
                default:
                    break;
            }
        }

        return delta;
    }

    private static List<Anchor> GherkinAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = GherkinRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            string keyword = match.Groups[1].Value;
            string title = Clean(match.Groups[2].Value);
            bool isFeature = keyword is "Feature" or "Rule";
            string name = title.Length > 0 ? title : keyword;
            anchors.Add(new Anchor(
                i,
                isFeature ? 1 : 2,
                name,
                isFeature ? SymbolKind.Section : SymbolKind.Scenario,
                lines[i].Trim()));
        }

        return anchors;
    }

    private static List<Anchor> IniAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = IniSectionRegex().Match(lines[i]);
            if (match.Success)
            {
                string name = match.Groups[1].Value.Trim();
                if (name.Length > 0)
                {
                    anchors.Add(new Anchor(i, 1, name, SymbolKind.Key, lines[i].Trim()));
                }
            }
        }

        return anchors;
    }

    private static List<Anchor> SqlAnchors(string[] lines)
    {
        var anchors = new List<Anchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = SqlObjectRegex().Match(lines[i]);
            if (match.Success)
            {
                string name = Unquote(match.Groups[2].Value.Trim());
                if (name.Length > 0)
                {
                    anchors.Add(new Anchor(i, 1, name, SymbolKind.Table, lines[i].Trim()));
                }
            }
        }

        return anchors;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '`' && value[^1] == '`')
                || (value[0] == '[' && value[^1] == ']')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string Clean(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    private static string[] SplitLines(string content)
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

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"^( *)([A-Za-z_""'][^:#]*?)\s*:(?:\s|$)")]
    private static partial Regex YamlKeyRegex();

    [GeneratedRegex(@"^\s*""([^""\\]{1,120})""\s*:")]
    private static partial Regex JsonKeyRegex();

    [GeneratedRegex(
        @"^\s*(Feature|Rule|Background|Scenario Outline|Scenario Template|Scenarios|Scenario|Examples|Example)\s*:\s*(.*)$")]
    private static partial Regex GherkinRegex();

    [GeneratedRegex(@"^\s*\[([^\]\[]+)\]\s*$")]
    private static partial Regex IniSectionRegex();

    [GeneratedRegex(
        @"(?i)^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?(TABLE|VIEW|PROCEDURE|FUNCTION|INDEX|TRIGGER|TYPE)\s+(?:IF\s+NOT\s+EXISTS\s+)?([^\s(;]+)")]
    private static partial Regex SqlObjectRegex();
}
