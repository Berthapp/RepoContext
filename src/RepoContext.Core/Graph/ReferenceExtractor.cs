using System.Text.RegularExpressions;
using RepoContext.Core.Configuration;
using RepoContext.Core.Parsing;
using RepoContext.Core.Scanning;

namespace RepoContext.Core.Graph;

/// <summary>The kinds of reference stored per file (ADR 0019).</summary>
public static class RefKind
{
    /// <summary>A raw module specifier of a TS/JS import or re-export.</summary>
    public const string Import = "import";

    /// <summary>A type-like identifier used by a C# file; resolved against declared types.</summary>
    public const string Type = "type";

    /// <summary>A repository path named by the file (a link target, a path in prose or a comment).</summary>
    public const string Path = "path";

    /// <summary>An external work-item key: a Jira ticket, a requirement id.</summary>
    public const string Key = "key";

    /// <summary>An absolute http(s) link - a Confluence page, a ticket URL, an external spec.</summary>
    public const string Link = "link";

    /// <summary>A code-symbol-like name mentioned by a document.</summary>
    public const string Symbol = "symbol";
}

/// <summary>One reference found in a file, with the line it was first seen on.</summary>
public readonly record struct FileReference(string Kind, string Value, int Line);

/// <summary>
/// Extracts, at index time, the references a file makes to other things:
/// modules, types, repository paths, external work-item keys, links, and the
/// code symbols a document names (ADR 0019).
/// </summary>
/// <remarks>
/// <para>
/// Extracting references while the file's bytes are already in memory is what
/// makes the graph affordable on a large repository. Before this, every index
/// run re-read every file from disk to recompute edges; now unchanged files
/// contribute their stored references and are never opened again.
/// </para>
/// <para>
/// It is also what makes an artifact repository navigable. A team that keeps
/// tickets, specifications and acceptance criteria next to the code asks
/// questions that span all of them - "what belongs to ABC-123", "what
/// implements this requirement", "which test covers this scenario". Those are
/// answered from the reference index rather than by reading documents.
/// </para>
/// </remarks>
public sealed partial class ReferenceExtractor
{
    /// <summary>
    /// Regex timeout for the configured key patterns. User-supplied patterns are
    /// data, and a pathological one must degrade one file's references rather
    /// than hang the index.
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Uppercase prefixes that form <c>ABC-123</c>-shaped tokens without being
    /// work-item keys: encodings, standards and hardware names. Without this,
    /// every mention of <c>UTF-8</c> or <c>SHA-256</c> would become a traceable
    /// "ticket". Teams whose keys collide with one of these can pin their own
    /// shape through <c>artifacts.keyPatterns</c>.
    /// </summary>
    private static readonly HashSet<string> NonKeyPrefixes = new(StringComparer.Ordinal)
    {
        "UTF", "UTF8", "SHA", "MD", "CRC", "ISO", "IEC", "RFC", "IEEE", "ANSI", "ASCII",
        "CP", "AES", "RSA", "ECDSA", "TLS", "SSL", "HTTP", "HTTPS", "IPV", "IP", "RGB",
        "RGBA", "UTC", "GMT", "ES", "CSS", "HTML", "XML", "JSON", "PDF", "JPEG", "PNG",
        "MP", "H", "X", "ARM", "AMD", "GB", "MB", "KB", "TB", "BM", "SQL", "NET", "COM",
        "USB", "PCI", "API", "UI", "UX", "CVE", "CWE",
    };

    /// <summary>
    /// Extensions a path mention must carry to be treated as a path. Matched
    /// case-sensitively on purpose: <c>System.Text.Json</c> is a namespace, not
    /// a file, and only the lowercase <c>.json</c> spelling names one.
    /// </summary>
    private static readonly HashSet<string> PathExtensions = new(StringComparer.Ordinal)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".cs", ".go", ".py",
        ".pyi", ".rb", ".rake", ".java", ".kt", ".kts", ".scala", ".groovy", ".dart", ".rs",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx", ".php", ".swift", ".sql", ".ddl",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".lua", ".ex", ".exs", ".pl", ".pm", ".r",
        ".proto", ".graphql", ".gql",
        ".md", ".mdx", ".markdown", ".rst", ".adoc", ".asciidoc", ".txt", ".json", ".jsonl",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".properties", ".conf", ".xml", ".xsd",
        ".html", ".htm", ".csv", ".tsv", ".feature", ".csproj", ".vbproj", ".fsproj",
        ".props", ".targets", ".sln", ".slnx", ".config", ".tf", ".tfvars", ".hcl", ".mk",
    };

    /// <summary>
    /// Bound for the reference kinds the graph is resolved from, independent of
    /// the artifact bound. Sized so no hand-written file reaches it.
    /// </summary>
    private const int MaxGraphRefsPerFile = 5_000;

    private readonly ArtifactOptions _options;
    private readonly IReadOnlyList<Regex> _keyPatterns;

    public ReferenceExtractor(ArtifactOptions options)
    {
        _options = options;
        _keyPatterns = CompileKeyPatterns(options.KeyPatterns);
    }

    /// <summary>
    /// Extracts every reference of <paramref name="file"/>, deduplicated per
    /// (kind, value) at its first line, capped per kind and ordered
    /// deterministically.
    /// </summary>
    public IReadOnlyList<FileReference> Extract(ScannedFile file, string content, ILanguageParser parser)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var found = new Dictionary<(string Kind, string Value), int>();

        void Add(string kind, string value, int line)
        {
            if (value.Length == 0)
            {
                return;
            }

            found.TryAdd((kind, value), line);
        }

        if (file.Language is SourceLanguage.TypeScript or SourceLanguage.Tsx or SourceLanguage.JavaScript)
        {
            foreach (string specifier in parser.ExtractImportSpecifiers(file.Language, content))
            {
                // The specifier resolves to a file, not to a line: import edges
                // describe the module, and line 0 records "no line evidence".
                Add(RefKind.Import, specifier, 0);
            }
        }

        string[] lines = SplitLines(content);
        bool isCSharp = file.Language == SourceLanguage.CSharp;
        bool documentLike = file.Kind is FileKind.Doc or FileKind.Config or FileKind.Other;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int number = i + 1;

            if (isCSharp)
            {
                foreach (Match match in TypeNameRegex().Matches(line))
                {
                    Add(RefKind.Type, match.Value, number);
                }
            }

            foreach (Match match in UrlRegex().Matches(line))
            {
                Add(RefKind.Link, NormalizeLink(match.Value), number);
            }

            foreach (string key in Keys(line))
            {
                Add(RefKind.Key, key, number);
            }

            if (_options.LinkPaths)
            {
                foreach (Match match in PathRegex().Matches(line))
                {
                    if (NormalizePath(match.Value) is { } path)
                    {
                        Add(RefKind.Path, path, number);
                    }
                }
            }

            if (_options.LinkSymbols && documentLike)
            {
                foreach (Match match in SymbolNameRegex().Matches(line))
                {
                    if (LooksLikeCodeSymbol(match.Value))
                    {
                        Add(RefKind.Symbol, match.Value, number);
                    }
                }
            }
        }

        return Cap(found);
    }

    /// <summary>
    /// Applies the per-kind cap and a total order. Sorting by value (rather than
    /// by first occurrence) makes the retained subset independent of where in a
    /// file a reference happens to appear first, so the cap cannot silently
    /// reshuffle an index between two runs over the same content.
    /// </summary>
    private IReadOnlyList<FileReference> Cap(Dictionary<(string Kind, string Value), int> found)
    {
        return
        [
            .. found
                .GroupBy(entry => entry.Key.Kind, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => group
                    .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
                    .Take(CapFor(group.Key))
                    .Select(entry => new FileReference(group.Key, entry.Key.Value, entry.Value)))
        ];
    }

    /// <summary>
    /// The bound for one reference kind.
    /// </summary>
    /// <remarks>
    /// The kinds the graph is resolved from - <c>import</c> and <c>type</c> -
    /// are exempt from the configured artifact bound. They are not artifact
    /// references costing index size for a marginal link: they are the sole
    /// input to a file's import edges, so truncating them alphabetically drops
    /// real dependencies from the graph. The margin was nil for <c>type</c>:
    /// the largest file in this repository carries 389 distinct capitalized
    /// tokens against a default of 400. Their own bound exists only to stop a
    /// generated monster file, not to trade edges for bytes (ADR 0019 §2).
    /// </remarks>
    private int CapFor(string kind) =>
        kind is RefKind.Type or RefKind.Import
            ? MaxGraphRefsPerFile
            : Math.Max(_options.MaxRefsPerFile, 0);

    private IEnumerable<string> Keys(string line)
    {
        foreach (Match match in WorkItemKeyRegex().Matches(line))
        {
            if (!NonKeyPrefixes.Contains(match.Groups[1].Value))
            {
                yield return match.Value.ToUpperInvariant();
            }
        }

        foreach (Regex pattern in _keyPatterns)
        {
            // Materialized inside the try: Matches() is lazy, so a timeout on a
            // pathological pattern fires during enumeration, and enumerating
            // outside the try would abort the whole index run rather than
            // degrade this one file.
            List<string> matches = [];
            try
            {
                foreach (Match match in pattern.Matches(line))
                {
                    if (match.Length > 0)
                    {
                        matches.Add(match.Value.ToUpperInvariant());
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            foreach (string match in matches)
            {
                yield return match;
            }
        }
    }

    /// <summary>
    /// Compiles the configured key patterns, silently dropping invalid ones. An
    /// unusable pattern in a configuration file must not make the repository
    /// unindexable.
    /// </summary>
    private static IReadOnlyList<Regex> CompileKeyPatterns(IReadOnlyList<string> patterns)
    {
        var compiled = new List<Regex>();
        foreach (string pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout));
            }
            catch (ArgumentException)
            {
                // An invalid pattern contributes no keys; the index stays usable.
            }
        }

        return compiled;
    }

    /// <summary>
    /// Normalises a link so the same page written two ways is one reference:
    /// scheme and host lowercased, fragment and trailing punctuation removed.
    /// </summary>
    private static string NormalizeLink(string url)
    {
        string trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '>', '"', '\'', '`');
        int fragment = trimmed.IndexOf('#');
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

    /// <summary>
    /// Normalises a path mention, or returns null when the token is not
    /// plausibly a repository path (no directory and no known extension).
    /// </summary>
    private static string? NormalizePath(string raw)
    {
        string value = raw.Trim();
        int query = value.IndexOfAny(['#', '?']);
        if (query > 0)
        {
            value = value[..query];
        }

        value = value.TrimEnd('.', ',', ';', ':', ')', ']', '`', '"', '\'');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.TrimStart('/');
        if (value.Length == 0 || value.EndsWith('/'))
        {
            return null;
        }

        string extension = System.IO.Path.GetExtension(value);
        if (extension.Length == 0 || !PathExtensions.Contains(extension))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// Whether an identifier reads like code rather than prose: it carries a
    /// case hump (<c>loginUser</c>, <c>AuthService</c>) or an underscore
    /// (<c>MAX_RETRIES</c>). Plain words are excluded - a document mentioning
    /// "session" must not be linked to every file that declares one.
    /// </summary>
    private static bool LooksLikeCodeSymbol(string identifier)
    {
        if (identifier.Contains('_', StringComparison.Ordinal))
        {
            return true;
        }

        for (int i = 1; i < identifier.Length; i++)
        {
            if (char.IsUpper(identifier[i]) && !char.IsUpper(identifier[i - 1]))
            {
                return true;
            }
        }

        return false;
    }

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

    [GeneratedRegex(@"\b([A-Z][A-Z0-9]{1,9})-([0-9]{1,6})\b")]
    private static partial Regex WorkItemKeyRegex();

    [GeneratedRegex(@"https?://[^\s""'`<>()\[\]{}]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[A-Za-z0-9_][A-Za-z0-9_.\-/]*\.[A-Za-z0-9]{1,8}")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]{1,63}\b")]
    private static partial Regex TypeNameRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]{3,63}\b")]
    private static partial Regex SymbolNameRegex();
}
