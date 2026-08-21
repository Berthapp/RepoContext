namespace RepoContext.Core.Scanning;

/// <summary>Coarse classification of an indexed file (spec F2).</summary>
public enum FileKind
{
    Source,
    Test,
    Doc,
    Config,
    Other,
}

/// <summary>
/// Language identifier used for chunking, parsing and the language mix
/// reported by <c>architecture</c> and <c>prime</c>.
/// </summary>
/// <remarks>
/// Only the first four are parsed by a bundled tree-sitter grammar; the rest
/// are recognised so a polyglot repository is described honestly (ADR 0020).
/// Before this, every Python, Go or Java file was reported as language
/// <c>none</c>.
/// </remarks>
public enum SourceLanguage
{
    None,
    TypeScript,
    Tsx,
    JavaScript,
    CSharp,
    Markdown,
    Json,
    Python,
    Go,
    Java,
    Kotlin,
    Scala,
    Groovy,
    Rust,
    Ruby,
    Php,
    Swift,
    Dart,
    C,
    Cpp,
    Shell,
    PowerShell,
    Lua,
    Elixir,
    Perl,
    R,
    Sql,
    Proto,
    GraphQl,
    Yaml,
    Xml,
    Html,
    Toml,
    Csv,
    AsciiDoc,
    ReStructuredText,
    Ini,
    Properties,
    Makefile,
    Dockerfile,
    Hcl,
    Gherkin,
}

/// <summary>Classifies files by kind, language and binary-ness.</summary>
public static class FileClassifier
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svgz",
        ".pdf", ".zip", ".gz", ".tar", ".tgz", ".7z", ".rar", ".bz2", ".xz",
        ".dll", ".exe", ".so", ".dylib", ".a", ".o", ".lib", ".pdb",
        ".class", ".jar", ".wasm", ".pyc",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".mov", ".avi", ".mkv", ".wav", ".flac",
        ".bin", ".dat", ".db", ".sqlite",
    };

    /// <summary>Classifies a repo-relative path into a <see cref="FileKind"/>.</summary>
    public static FileKind ClassifyKind(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        string name = System.IO.Path.GetFileName(path);
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        if (IsTest(path, name))
        {
            return FileKind.Test;
        }

        if (ext is ".md" or ".mdx" or ".markdown" or ".rst" or ".adoc" or ".asciidoc"
            || name.Equals("README", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Doc;
        }

        if (IsConfig(path, name, ext))
        {
            return FileKind.Config;
        }

        // Deliberately not "has a language label": since ADR 0020 names the
        // artifact formats too, that test would classify a CSV matrix or an
        // exported HTML page as source. Source is where declarations are
        // extracted, which is exactly what this list holds.
        if (IsSourceExtension(ext))
        {
            return FileKind.Source;
        }

        return FileKind.Other;
    }

    /// <summary>Detects the language for chunking/parsing, or <see cref="SourceLanguage.None"/>.</summary>
    public static SourceLanguage DetectLanguage(string relativePath)
    {
        string name = System.IO.Path.GetFileName(relativePath);
        string ext = System.IO.Path.GetExtension(relativePath).ToLowerInvariant();

        // Build files are named rather than typed, and are the reason this
        // takes a path instead of an extension.
        if (name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Containerfile", StringComparison.OrdinalIgnoreCase))
        {
            return SourceLanguage.Dockerfile;
        }

        if (ext.Length == 0
            && System.IO.Path.GetFileNameWithoutExtension(name)
                .Equals("Makefile", StringComparison.OrdinalIgnoreCase))
        {
            return SourceLanguage.Makefile;
        }

        return ext switch
        {
            ".ts" or ".mts" or ".cts" => SourceLanguage.TypeScript,
            ".tsx" => SourceLanguage.Tsx,
            ".js" or ".jsx" or ".mjs" or ".cjs" => SourceLanguage.JavaScript,
            ".cs" => SourceLanguage.CSharp,
            ".md" or ".mdx" or ".markdown" => SourceLanguage.Markdown,
            ".json" or ".jsonl" or ".ndjson" or ".jsonc" => SourceLanguage.Json,
            ".py" or ".pyi" => SourceLanguage.Python,
            ".go" => SourceLanguage.Go,
            ".java" => SourceLanguage.Java,
            ".kt" or ".kts" => SourceLanguage.Kotlin,
            ".scala" => SourceLanguage.Scala,
            ".groovy" => SourceLanguage.Groovy,
            ".rs" => SourceLanguage.Rust,
            ".rb" or ".rake" => SourceLanguage.Ruby,
            ".php" => SourceLanguage.Php,
            ".swift" => SourceLanguage.Swift,
            ".dart" => SourceLanguage.Dart,
            ".c" or ".h" => SourceLanguage.C,
            ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" => SourceLanguage.Cpp,
            ".sh" or ".bash" or ".zsh" => SourceLanguage.Shell,
            ".ps1" or ".psm1" => SourceLanguage.PowerShell,
            ".lua" => SourceLanguage.Lua,
            ".ex" or ".exs" => SourceLanguage.Elixir,
            ".pl" or ".pm" => SourceLanguage.Perl,
            ".r" => SourceLanguage.R,
            ".sql" or ".ddl" => SourceLanguage.Sql,
            ".proto" => SourceLanguage.Proto,
            ".graphql" or ".gql" => SourceLanguage.GraphQl,
            ".yaml" or ".yml" => SourceLanguage.Yaml,
            ".xml" or ".xsd" or ".xsl" or ".xslt" or ".wsdl" or ".resx" or ".csproj"
                or ".vbproj" or ".fsproj" or ".props" or ".targets" => SourceLanguage.Xml,
            ".html" or ".htm" or ".xhtml" => SourceLanguage.Html,
            ".toml" => SourceLanguage.Toml,
            ".csv" or ".tsv" => SourceLanguage.Csv,
            ".adoc" or ".asciidoc" => SourceLanguage.AsciiDoc,
            ".rst" => SourceLanguage.ReStructuredText,
            ".ini" or ".cfg" or ".editorconfig" => SourceLanguage.Ini,
            ".properties" or ".conf" or ".env" => SourceLanguage.Properties,
            ".mk" or ".make" or ".mak" => SourceLanguage.Makefile,
            ".tf" or ".tfvars" or ".hcl" or ".nomad" => SourceLanguage.Hcl,
            ".feature" => SourceLanguage.Gherkin,
            _ => SourceLanguage.None,
        };
    }

    /// <summary>Whether the extension is a known binary type.</summary>
    public static bool IsBinaryExtension(string relativePath) =>
        BinaryExtensions.Contains(System.IO.Path.GetExtension(relativePath));

    /// <summary>Sniffs the first bytes for a NUL byte (binary content marker).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;

    private static bool IsTest(string path, string name) =>
        path.Contains("/__tests__/", StringComparison.Ordinal)
        || path.Contains("/__mocks__/", StringComparison.Ordinal)
        || path.Contains("/Tests/", StringComparison.Ordinal)
        || path.StartsWith("Tests/", StringComparison.Ordinal)
        || name.Contains(".test.", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
        || (name.EndsWith("Tests.cs", StringComparison.Ordinal))
        || (name.EndsWith("Test.cs", StringComparison.Ordinal));

    private static bool IsConfig(string path, string name, string ext)
    {
        if (ext is ".json" or ".yml" or ".yaml" or ".toml" or ".ini" or ".config"
            or ".csproj" or ".props" or ".targets" or ".editorconfig" or ".xml")
        {
            return true;
        }

        if (ext is ".properties" or ".conf" or ".tf" or ".tfvars" or ".hcl" or ".lock")
        {
            return true;
        }

        return name is "package.json" or "tsconfig.json" or ".gitignore" or ".repoctxignore"
            or "Makefile" or "Dockerfile"
            || name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extensions treated as source code. Kept in step with the languages
    /// RepoContext can actually describe (ADR 0020): a file whose declarations
    /// are extracted is source, not "other".
    /// </summary>
    private static bool IsSourceExtension(string ext) =>
        ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts"
            or ".cs" or ".go" or ".py" or ".pyi" or ".rb" or ".rake" or ".java" or ".rs"
            or ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx"
            or ".php" or ".swift" or ".kt" or ".kts" or ".scala" or ".groovy" or ".dart"
            or ".sh" or ".bash" or ".zsh" or ".ps1" or ".psm1" or ".lua" or ".ex" or ".exs"
            or ".pl" or ".pm" or ".r" or ".sql" or ".ddl" or ".proto" or ".graphql" or ".gql";
}
