using System.Text.RegularExpressions;

namespace RepoContext.Core.Parsing;

/// <summary>
/// Extracts declarations from source files in the languages the bundled
/// tree-sitter grammars do not cover - Python, Go, Java, Kotlin, Rust, Ruby,
/// PHP, Swift, C/C++, Scala, shell, and the interface languages that sit next
/// to them (ADR 0020).
/// </summary>
/// <remarks>
/// <para>
/// Until now a Python or Go file was indexed as anonymous line blocks: it had
/// no outline, contributed nothing to symbol search, and could not be linked to
/// from a document. In a repository that mixes languages - which is most of
/// them - that is a hole in exactly the place an agent looks first.
/// </para>
/// <para>
/// The patterns are deliberately conservative. A missed declaration costs an
/// agent one extra call; a wrong one sends it to the wrong place and costs the
/// tokens of reading it. Where a language's grammar cannot be approximated
/// safely by lines (C++ function bodies, Dart methods), only the declarations
/// that can are emitted.
/// </para>
/// </remarks>
public static partial class DeclarationExtractor
{
    /// <summary>Whether this extractor produces symbols for the given path.</summary>
    public static bool Supports(string relativePath) => Language(relativePath) != CodeLanguage.None;

    /// <summary>
    /// Extracts declarations for <paramref name="relativePath"/>, or an empty
    /// list when the language is not one this extractor knows.
    /// </summary>
    public static IReadOnlyList<Symbol> Extract(string relativePath, string content)
    {
        CodeLanguage language = Language(relativePath);
        if (language == CodeLanguage.None || content.Length == 0)
        {
            return [];
        }

        string[] lines = StructureAnchors.SplitLines(content);
        if (lines.Length == 0)
        {
            return [];
        }

        var anchors = new List<StructureAnchor>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (Match(language, lines[i]) is { } found)
            {
                anchors.Add(new StructureAnchor(
                    i,
                    StructureAnchors.Indent(lines[i]),
                    found.Name,
                    found.Kind,
                    StructureAnchors.Clip(StructureAnchors.Clean(lines[i]))));
            }
        }

        return StructureAnchors.Materialize(anchors, lines, preferCommentAbove: true);
    }

    /// <summary>The languages handled here, grouped by how their declarations read.</summary>
    private enum CodeLanguage
    {
        None,
        Python,
        Go,
        Jvm,
        Rust,
        Ruby,
        Php,
        Swift,
        CFamily,
        Shell,
        PowerShell,
        Lua,
        Elixir,
        Perl,
        R,
        Dart,
        Proto,
        GraphQl,
    }

    private static CodeLanguage Language(string relativePath)
    {
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".py" or ".pyi" => CodeLanguage.Python,
            ".go" => CodeLanguage.Go,
            ".java" or ".kt" or ".kts" or ".scala" or ".groovy" => CodeLanguage.Jvm,
            ".rs" => CodeLanguage.Rust,
            ".rb" or ".rake" => CodeLanguage.Ruby,
            ".php" => CodeLanguage.Php,
            ".swift" => CodeLanguage.Swift,
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" => CodeLanguage.CFamily,
            ".sh" or ".bash" or ".zsh" => CodeLanguage.Shell,
            ".ps1" or ".psm1" => CodeLanguage.PowerShell,
            ".lua" => CodeLanguage.Lua,
            ".ex" or ".exs" => CodeLanguage.Elixir,
            ".pl" or ".pm" => CodeLanguage.Perl,
            ".r" => CodeLanguage.R,
            ".dart" => CodeLanguage.Dart,
            ".proto" => CodeLanguage.Proto,
            ".graphql" or ".gql" => CodeLanguage.GraphQl,
            _ => CodeLanguage.None,
        };
    }

    private readonly record struct Declaration(string Name, SymbolKind Kind);

    private static Declaration? Match(CodeLanguage language, string line) => language switch
    {
        CodeLanguage.Python => Keyword(PythonRegex(), line),
        CodeLanguage.Go => Go(line),
        CodeLanguage.Jvm => Keyword(JvmRegex(), line) ?? Single(JvmMethodRegex(), line, SymbolKind.Method),
        CodeLanguage.Rust => Keyword(RustRegex(), line),
        CodeLanguage.Ruby => Keyword(RubyRegex(), line),
        CodeLanguage.Php => Keyword(PhpRegex(), line),
        CodeLanguage.Swift => Keyword(SwiftRegex(), line),
        CodeLanguage.CFamily => Keyword(CFamilyRegex(), line),
        CodeLanguage.Shell => Single(ShellRegex(), line, SymbolKind.Function),
        CodeLanguage.PowerShell => Single(PowerShellRegex(), line, SymbolKind.Function),
        CodeLanguage.Lua => Single(LuaRegex(), line, SymbolKind.Function),
        CodeLanguage.Elixir => Keyword(ElixirRegex(), line),
        CodeLanguage.Perl => Single(PerlRegex(), line, SymbolKind.Function),
        CodeLanguage.R => Single(RRegex(), line, SymbolKind.Function),
        CodeLanguage.Dart => Keyword(DartRegex(), line),
        CodeLanguage.Proto => Keyword(ProtoRegex(), line),
        CodeLanguage.GraphQl => Keyword(GraphQlRegex(), line),
        _ => null,
    };

    /// <summary>
    /// Go writes every named type with one keyword, so what it declares is on
    /// the rest of the line: <c>type Session struct</c> is a struct, not an alias.
    /// </summary>
    private static Declaration? Go(string line)
    {
        if (Keyword(GoRegex(), line) is not { } declaration)
        {
            return null;
        }

        if (declaration.Kind != SymbolKind.TypeAlias)
        {
            return declaration;
        }

        return declaration with
        {
            Kind = GoUnderlyingRegex().Match(line) switch
            {
                { Success: true } m when m.Groups[1].Value == "struct" => SymbolKind.Struct,
                { Success: true } m when m.Groups[1].Value == "interface" => SymbolKind.Interface,
                _ => SymbolKind.TypeAlias,
            },
        };
    }

    /// <summary>A pattern whose first group is the declaring keyword and second the name.</summary>
    private static Declaration? Keyword(Regex regex, string line)
    {
        Match match = regex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        string name = match.Groups[2].Value;
        return name.Length == 0
            ? null
            : new Declaration(name, KindOf(match.Groups[1].Value));
    }

    /// <summary>A pattern with a single name group and a fixed kind.</summary>
    private static Declaration? Single(Regex regex, string line, SymbolKind kind)
    {
        Match match = regex.Match(line);
        return match.Success && match.Groups[1].Value.Length > 0
            ? new Declaration(match.Groups[1].Value, kind)
            : null;
    }

    /// <summary>
    /// Maps a declaring keyword onto the shared symbol vocabulary, so an outline
    /// of a Go file reads like an outline of a C# one.
    /// </summary>
    private static SymbolKind KindOf(string keyword) => keyword switch
    {
        "class" or "object" or "defmodule" or "module" or "mixin" => SymbolKind.Class,
        "interface" or "protocol" or "trait" or "service" => SymbolKind.Interface,
        "struct" or "message" or "extension" or "input" => SymbolKind.Struct,
        "record" or "impl" => SymbolKind.Record,
        "enum" or "union" => SymbolKind.Enum,
        "type" or "typealias" or "scalar" => SymbolKind.TypeAlias,
        "def" or "defp" or "func" or "fun" or "fn" or "function" or "rpc" or "sub" =>
            SymbolKind.Function,
        _ => SymbolKind.Function,
    };

    [GeneratedRegex(@"^\s*(?:async\s+)?(class|def)\s+([A-Za-z_]\w*)")]
    private static partial Regex PythonRegex();

    // Methods carry a receiver in parentheses: "func (s *Session) Close()".
    [GeneratedRegex(@"^\s*(func|type)\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)")]
    private static partial Regex GoRegex();

    [GeneratedRegex(
        @"^\s*(?:@\w+\s+)*(?:(?:public|private|protected|internal|open|sealed|abstract|final|static|data|inner|case|override|suspend)\s+)*(class|interface|enum|record|object|trait|fun)\s+([A-Za-z_]\w*)")]
    private static partial Regex JvmRegex();

    // A Java method needs at least one modifier to be told apart from a call.
    // Requiring one misses package-private methods and matches nothing else,
    // which is the right way round: a wrong symbol costs a wasted read.
    [GeneratedRegex(
        @"^\s*(?:@\w+\s+)*(?:public|private|protected|static|final|abstract|synchronized|native|default)\s+(?:[\w<>\[\],.?]+\s+)+([A-Za-z_]\w*)\s*\([^;]*\)\s*(?:throws [\w,.\s]+)?\{?\s*$")]
    private static partial Regex JvmMethodRegex();

    [GeneratedRegex(@"^\s*type\s+[A-Za-z_]\w*(?:\[[^\]]*\])?\s+(struct|interface)\b")]
    private static partial Regex GoUnderlyingRegex();

    [GeneratedRegex(
        @"^\s*(?:(?:pub(?:\([^)]*\))?|async|unsafe|const|default)\s+)*(fn|struct|enum|trait|type|impl|union)\s+([A-Za-z_]\w*)")]
    private static partial Regex RustRegex();

    [GeneratedRegex(@"^\s*(class|module|def)\s+([A-Za-z_][\w:]*[?!]?)")]
    private static partial Regex RubyRegex();

    [GeneratedRegex(
        @"^\s*(?:(?:final|abstract|public|private|protected|static|readonly)\s+)*(class|interface|trait|enum|function)\s+&?([A-Za-z_]\w*)")]
    private static partial Regex PhpRegex();

    [GeneratedRegex(
        @"^\s*(?:@\w+\s+)*(?:(?:public|private|internal|fileprivate|open|final|static|override|mutating)\s+)*(class|struct|enum|protocol|extension|func|typealias)\s+([A-Za-z_]\w*)")]
    private static partial Regex SwiftRegex();

    // Only tagged types: a C function definition cannot be told from a call or a
    // prototype by one line often enough to be worth the wrong answers.
    [GeneratedRegex(@"^\s*(?:typedef\s+)?(struct|class|enum|union|namespace)\s+([A-Za-z_]\w*)")]
    private static partial Regex CFamilyRegex();

    [GeneratedRegex(@"^\s*(?:function\s+)?([A-Za-z_][\w-]*)\s*\(\)\s*\{")]
    private static partial Regex ShellRegex();

    [GeneratedRegex(@"(?i)^\s*function\s+([A-Za-z_][\w-]*)")]
    private static partial Regex PowerShellRegex();

    [GeneratedRegex(@"^\s*(?:local\s+)?function\s+([A-Za-z_][\w.:]*)")]
    private static partial Regex LuaRegex();

    [GeneratedRegex(@"^\s*(defmodule|defp|def)\s+([A-Za-z_][\w.?!]*)")]
    private static partial Regex ElixirRegex();

    [GeneratedRegex(@"^\s*sub\s+([A-Za-z_]\w*)")]
    private static partial Regex PerlRegex();

    [GeneratedRegex(@"^\s*([A-Za-z_.][\w.]*)\s*(?:<-|=)\s*function\s*\(")]
    private static partial Regex RRegex();

    [GeneratedRegex(@"^\s*(?:abstract\s+)?(class|mixin|enum|extension|typedef)\s+([A-Za-z_]\w*)")]
    private static partial Regex DartRegex();

    [GeneratedRegex(@"^\s*(message|service|enum|rpc)\s+([A-Za-z_]\w*)")]
    private static partial Regex ProtoRegex();

    [GeneratedRegex(@"^\s*(?:extend\s+)?(type|interface|enum|input|union|scalar)\s+([A-Za-z_]\w*)")]
    private static partial Regex GraphQlRegex();
}
