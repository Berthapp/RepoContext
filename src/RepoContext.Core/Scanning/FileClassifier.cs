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

/// <summary>Language identifier used for chunking and (from M2) parsing.</summary>
public enum SourceLanguage
{
    None,
    TypeScript,
    Tsx,
    JavaScript,
    CSharp,
    Markdown,
    Json,
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

        if (ext is ".md" or ".mdx" or ".rst" || name.Equals("README", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Doc;
        }

        if (IsConfig(path, name, ext))
        {
            return FileKind.Config;
        }

        if (DetectLanguage(path) != SourceLanguage.None || IsSourceExtension(ext))
        {
            return FileKind.Source;
        }

        return FileKind.Other;
    }

    /// <summary>Detects the language for chunking/parsing, or <see cref="SourceLanguage.None"/>.</summary>
    public static SourceLanguage DetectLanguage(string relativePath)
    {
        string ext = System.IO.Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".ts" or ".mts" or ".cts" => SourceLanguage.TypeScript,
            ".tsx" => SourceLanguage.Tsx,
            ".js" or ".jsx" or ".mjs" or ".cjs" => SourceLanguage.JavaScript,
            ".cs" => SourceLanguage.CSharp,
            ".md" or ".mdx" => SourceLanguage.Markdown,
            ".json" => SourceLanguage.Json,
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

        return name is "package.json" or "tsconfig.json" or ".gitignore" or ".repoctxignore"
            || name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceExtension(string ext) =>
        ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts"
            or ".cs" or ".go" or ".py" or ".rb" or ".java" or ".rs" or ".c" or ".h"
            or ".cpp" or ".hpp" or ".php" or ".swift" or ".kt";
}
