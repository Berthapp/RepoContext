using System.Text.RegularExpressions;

namespace RepoContext.Core.Parsing;

/// <summary>
/// Extracts the structure of a non-code artifact - a requirement document, an
/// exported ticket, an API contract, a feature file, a traceability matrix - as
/// symbols, so the same commands that explain code (<c>outline</c>,
/// <c>search --symbols</c>, <c>context</c>) also explain the documents an agent
/// has to reconcile the code with (ADR 0019, extended in ADR 0020).
/// </summary>
/// <remarks>
/// The scanning is line based and parser-free; see <see cref="StructureAnchors"/>
/// for why. Every rule depends only on the file extension and the bytes of the
/// file, so two runs over the same content produce byte-identical symbols - the
/// invariant the whole tool rests on.
/// </remarks>
public static partial class StructureExtractor
{
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

        string[] lines = StructureAnchors.SplitLines(content);
        if (lines.Length == 0)
        {
            return [];
        }

        List<StructureAnchor> anchors = format switch
        {
            ArtifactFormat.Markdown => MarkdownAnchors(lines),
            ArtifactFormat.AsciiDoc => AsciiDocAnchors(lines),
            ArtifactFormat.ReStructuredText => ReStructuredTextAnchors(lines),
            ArtifactFormat.Html => HtmlAnchors(lines),
            ArtifactFormat.Xml => XmlAnchors(lines),
            ArtifactFormat.Yaml => YamlAnchors(lines),
            ArtifactFormat.Json => JsonAnchors(lines),
            ArtifactFormat.Gherkin => GherkinAnchors(lines),
            ArtifactFormat.Ini => IniAnchors(lines),
            ArtifactFormat.Properties => PropertiesAnchors(lines),
            ArtifactFormat.Delimited => DelimitedAnchors(lines, relativePath),
            ArtifactFormat.Makefile => MakefileAnchors(lines),
            ArtifactFormat.Dockerfile => DockerfileAnchors(lines),
            ArtifactFormat.Hcl => HclAnchors(lines),
            ArtifactFormat.Sql => SqlAnchors(lines),
            _ => [],
        };

        return StructureAnchors.Materialize(anchors, lines);
    }

    /// <summary>The artifact formats whose structure is extracted.</summary>
    private enum ArtifactFormat
    {
        None,
        Markdown,
        AsciiDoc,
        ReStructuredText,
        Html,
        Xml,
        Yaml,
        Json,
        Gherkin,
        Ini,
        Properties,
        Delimited,
        Makefile,
        Dockerfile,
        Hcl,
        Sql,
    }

    private static ArtifactFormat Format(string relativePath)
    {
        string name = Path.GetFileName(relativePath);
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();

        // Extensionless build files are named, not typed.
        if (ext.Length == 0 || ext == ".in")
        {
            string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            if (stem is "makefile" or "gnumakefile")
            {
                return ArtifactFormat.Makefile;
            }

            if (stem is "dockerfile" or "containerfile")
            {
                return ArtifactFormat.Dockerfile;
            }
        }

        if (name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactFormat.Dockerfile;
        }

        return ext switch
        {
            ".md" or ".mdx" or ".markdown" => ArtifactFormat.Markdown,
            ".adoc" or ".asciidoc" => ArtifactFormat.AsciiDoc,
            ".rst" => ArtifactFormat.ReStructuredText,
            ".html" or ".htm" or ".xhtml" => ArtifactFormat.Html,
            ".xml" or ".xsd" or ".xsl" or ".xslt" or ".wsdl" or ".svg" or ".resx" or ".plist"
                or ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets" or ".nuspec"
                or ".config" or ".storyboard" => ArtifactFormat.Xml,
            ".yaml" or ".yml" => ArtifactFormat.Yaml,
            ".json" or ".jsonl" or ".ndjson" or ".jsonc" or ".json5" => ArtifactFormat.Json,
            ".feature" => ArtifactFormat.Gherkin,
            ".toml" or ".ini" or ".cfg" or ".editorconfig" => ArtifactFormat.Ini,
            ".properties" or ".env" or ".conf" => ArtifactFormat.Properties,
            ".csv" or ".tsv" => ArtifactFormat.Delimited,
            ".mk" or ".make" or ".mak" => ArtifactFormat.Makefile,
            ".tf" or ".tfvars" or ".hcl" or ".nomad" => ArtifactFormat.Hcl,
            ".sql" or ".ddl" => ArtifactFormat.Sql,
            _ => ArtifactFormat.None,
        };
    }

    private static List<StructureAnchor> MarkdownAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
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

            string name = StructureAnchors.Clean(trimmed[level..]);
            if (name.Length > 0)
            {
                anchors.Add(new StructureAnchor(i, level, name, SymbolKind.Section, trimmed.Trim()));
            }
        }

        return anchors;
    }

    private static List<StructureAnchor> AsciiDocAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
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

            string name = StructureAnchors.Clean(trimmed[level..]);
            if (name.Length > 0)
            {
                anchors.Add(new StructureAnchor(i, level, name, SymbolKind.Section, trimmed.Trim()));
            }
        }

        return anchors;
    }

    /// <summary>
    /// reStructuredText marks a heading by underlining it. The punctuation
    /// character is not fixed by the format - each document assigns its own
    /// hierarchy - so levels are assigned in order of first appearance, which is
    /// exactly what the format specifies.
    /// </summary>
    private static List<StructureAnchor> ReStructuredTextAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        var levels = new List<char>();
        for (int i = 1; i < lines.Length; i++)
        {
            string underline = lines[i].TrimEnd();
            string title = lines[i - 1].TrimEnd();
            if (title.Length == 0 || underline.Length < title.Length || underline.Length < 3)
            {
                continue;
            }

            char marker = underline[0];
            if (!"=-`:'\"~^_*+#<>".Contains(marker, StringComparison.Ordinal)
                || underline.Any(c => c != marker))
            {
                continue;
            }

            int level = levels.IndexOf(marker);
            if (level < 0)
            {
                levels.Add(marker);
                level = levels.Count - 1;
            }

            anchors.Add(new StructureAnchor(
                i - 1, level + 1, StructureAnchors.Clean(title), SymbolKind.Section, title.Trim()));
        }

        return anchors;
    }

    private static List<StructureAnchor> HtmlAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in HtmlHeadingRegex().Matches(lines[i]))
            {
                int level = match.Groups[1].Value[0] - '0';
                string name = StructureAnchors.Clean(TagRegex().Replace(match.Groups[2].Value, " "));
                if (name.Length > 0)
                {
                    anchors.Add(new StructureAnchor(i, level, name, SymbolKind.Section, name));
                }
            }
        }

        return anchors;
    }

    /// <summary>
    /// XML structure: the opening elements of the top levels, labelled with the
    /// element's identifying attribute when it has one. This is what makes a
    /// project file, a WSDL or an exported page navigable - <c>ItemGroup</c>
    /// alone says nothing, <c>PackageReference Include="Serilog"</c> does.
    /// </summary>
    private static List<StructureAnchor> XmlAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        var path = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            foreach (Match match in XmlElementRegex().Matches(line))
            {
                string element = match.Groups[2].Value;
                bool closing = match.Groups[1].Value == "/";
                bool selfClosing = match.Value.EndsWith("/>", StringComparison.Ordinal);

                if (closing)
                {
                    if (path.Count > 0)
                    {
                        path.RemoveAt(path.Count - 1);
                    }

                    continue;
                }

                string label = element;
                if (XmlIdentityRegex().Match(match.Value) is { Success: true } identity)
                {
                    label = element + " " + identity.Groups[2].Value;
                }

                // One level deeper than JSON/YAML on purpose: an XML document
                // wraps everything in a root element, so its third level is the
                // second level of any other format - and the level that carries
                // the interesting items (a PackageReference, a WSDL operation).
                if (path.Count <= MaxKeyDepth)
                {
                    anchors.Add(new StructureAnchor(
                        i,
                        path.Count + 1,
                        label,
                        SymbolKind.Key,
                        StructureAnchors.Clip(StructureAnchors.Clean(line))));
                }

                if (!selfClosing)
                {
                    path.Add(element);
                }
            }
        }

        return anchors;
    }

    private static List<StructureAnchor> YamlAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        var path = new List<(int Indent, string Name)>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = YamlKeyRegex().Match(lines[i]);
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

            anchors.Add(new StructureAnchor(
                i, path.Count, string.Join('.', path.Select(p => p.Name)),
                SymbolKind.Key, lines[i].Trim()));
        }

        return anchors;
    }

    private static List<StructureAnchor> JsonAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
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
                    anchors.Add(new StructureAnchor(
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

    private static List<StructureAnchor> GherkinAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = GherkinRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            string keyword = match.Groups[1].Value;
            string title = StructureAnchors.Clean(match.Groups[2].Value);
            bool isFeature = keyword is "Feature" or "Rule";
            anchors.Add(new StructureAnchor(
                i,
                isFeature ? 1 : 2,
                title.Length > 0 ? title : keyword,
                isFeature ? SymbolKind.Section : SymbolKind.Scenario,
                lines[i].Trim()));
        }

        return anchors;
    }

    private static List<StructureAnchor> IniAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = IniSectionRegex().Match(lines[i]);
            if (match.Success)
            {
                string name = match.Groups[1].Value.Trim();
                if (name.Length > 0)
                {
                    anchors.Add(new StructureAnchor(i, 1, name, SymbolKind.Key, lines[i].Trim()));
                }
            }
        }

        return anchors;
    }

    /// <summary>
    /// Flat <c>key = value</c> settings files. Only the key prefix before the
    /// first separator is grouped, so a hundred <c>spring.datasource.*</c>
    /// entries read as one entry rather than a hundred.
    /// </summary>
    private static List<StructureAnchor> PropertiesAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = PropertyKeyRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            string key = match.Groups[1].Value;
            int dot = key.IndexOf('.', StringComparison.Ordinal);
            string group = dot > 0 ? key[..dot] : key;
            if (seen.Add(group))
            {
                anchors.Add(new StructureAnchor(i, 1, group, SymbolKind.Key, lines[i].Trim()));
            }
        }

        return anchors;
    }

    /// <summary>
    /// A delimited table is one thing an agent needs to know before reading it:
    /// its columns. Emitting a symbol per row would turn a traceability matrix
    /// into thousands of useless outline entries.
    /// </summary>
    private static List<StructureAnchor> DelimitedAnchors(string[] lines, string relativePath)
    {
        char separator = relativePath.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        int header = Array.FindIndex(lines, line => line.Trim().Length > 0);
        if (header < 0 || !lines[header].Contains(separator, StringComparison.Ordinal))
        {
            return [];
        }

        string columns = string.Join(
            ", ",
            lines[header].Split(separator).Select(c => Unquote(c.Trim())).Where(c => c.Length > 0));
        if (columns.Length == 0)
        {
            return [];
        }

        return
        [
            new StructureAnchor(
                header, 1, "columns", SymbolKind.Key,
                StructureAnchors.Clip("columns: " + columns)),
        ];
    }

    private static List<StructureAnchor> MakefileAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = MakeTargetRegex().Match(lines[i]);
            if (match.Success && match.Groups[1].Value != ".PHONY")
            {
                anchors.Add(new StructureAnchor(
                    i, 1, match.Groups[1].Value, SymbolKind.Function, lines[i].Trim()));
            }
        }

        return anchors;
    }

    private static List<StructureAnchor> DockerfileAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = DockerStageRegex().Match(lines[i]);
            if (match.Success)
            {
                string image = match.Groups[1].Value;
                string stage = match.Groups[2].Success ? match.Groups[2].Value : image;
                anchors.Add(new StructureAnchor(
                    i, 1, stage, SymbolKind.Section, lines[i].Trim()));
            }
        }

        return anchors;
    }

    /// <summary>HCL/Terraform blocks: <c>resource "aws_s3_bucket" "logs" {</c>.</summary>
    private static List<StructureAnchor> HclAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = HclBlockRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            string name = string.Join(
                '.',
                new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value }
                    .Where(part => part.Length > 0));
            anchors.Add(new StructureAnchor(i, 1, name, SymbolKind.Key, lines[i].Trim()));
        }

        return anchors;
    }

    private static List<StructureAnchor> SqlAnchors(string[] lines)
    {
        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = SqlObjectRegex().Match(lines[i]);
            if (match.Success)
            {
                string name = Unquote(match.Groups[2].Value.Trim());
                if (name.Length > 0)
                {
                    anchors.Add(new StructureAnchor(
                        i, 1, name, SymbolKind.Table, lines[i].Trim()));
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

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"<(/?)([A-Za-z_][\w.:-]*)\b[^<>]*?(/?)>")]
    private static partial Regex XmlElementRegex();

    [GeneratedRegex(@"\b(name|id|Include|key|type|Sdk|Version)\s*=\s*""([^""]{1,80})""")]
    private static partial Regex XmlIdentityRegex();

    [GeneratedRegex(@"^( *)([A-Za-z_""'][^:#]*?)\s*:(?:\s|$)")]
    private static partial Regex YamlKeyRegex();

    [GeneratedRegex(@"^\s*""([^""\\]{1,120})""\s*:")]
    private static partial Regex JsonKeyRegex();

    [GeneratedRegex(
        @"^\s*(Feature|Rule|Background|Scenario Outline|Scenario Template|Scenarios|Scenario|Examples|Example)\s*:\s*(.*)$")]
    private static partial Regex GherkinRegex();

    [GeneratedRegex(@"^\s*\[([^\]\[]+)\]\s*$")]
    private static partial Regex IniSectionRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][\w.\-]*)\s*[=:]")]
    private static partial Regex PropertyKeyRegex();

    [GeneratedRegex(@"^([A-Za-z_.][\w.\-/]*)\s*:(?!=)")]
    private static partial Regex MakeTargetRegex();

    [GeneratedRegex(@"(?i)^\s*FROM\s+(\S+)(?:\s+AS\s+(\S+))?")]
    private static partial Regex DockerStageRegex();

    [GeneratedRegex(
        @"^\s*([A-Za-z_]\w*)(?:\s+""([^""]*)"")?(?:\s+""([^""]*)"")?\s*\{\s*$")]
    private static partial Regex HclBlockRegex();

    [GeneratedRegex(
        @"(?i)^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?(TABLE|VIEW|PROCEDURE|FUNCTION|INDEX|TRIGGER|TYPE)\s+(?:IF\s+NOT\s+EXISTS\s+)?([^\s(;]+)")]
    private static partial Regex SqlObjectRegex();
}
