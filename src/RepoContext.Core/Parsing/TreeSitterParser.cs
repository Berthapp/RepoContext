using RepoContext.Core.Scanning;
using TreeSitter;
using TsQuery = TreeSitter.Query;

namespace RepoContext.Core.Parsing;

/// <summary>
/// tree-sitter based <see cref="ILanguageParser"/> for TS/TSX/JS/C# (ADR 0001).
/// Languages, parsers and queries are created lazily and reused. Not thread-safe.
/// </summary>
public sealed class TreeSitterParser : ILanguageParser
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    };

    private readonly Dictionary<SourceLanguage, LanguageContext> _contexts = [];

    public bool Supports(SourceLanguage language) => language is
        SourceLanguage.TypeScript or SourceLanguage.Tsx or
        SourceLanguage.JavaScript or SourceLanguage.CSharp;

    public IReadOnlyList<Symbol> Parse(SourceLanguage language, string relativePath, string content)
    {
        if (!Supports(language))
        {
            return [];
        }

        LanguageContext ctx = GetContext(language);
        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        using Tree? tree = ctx.Parser.Parse(content);
        if (tree is null)
        {
            return [];
        }

        var symbols = new List<Symbol>();
        foreach (QueryMatch match in ctx.Query.Execute(tree.RootNode).Matches)
        {
            Node? nameNode = null;
            Node? defNode = null;
            string? kindCapture = null;

            foreach (QueryCapture capture in match.Captures)
            {
                if (capture.Name == "def")
                {
                    defNode = capture.Node;
                }
                else
                {
                    nameNode = capture.Node;
                    kindCapture = capture.Name;
                }
            }

            if (nameNode is null || defNode is null || kindCapture is null)
            {
                continue;
            }

            Node def = defNode;
            Node name = nameNode;
            symbols.Add(new Symbol
            {
                Name = name.Text,
                Kind = MapKind(kindCapture),
                StartLine = def.StartPosition.Row + 1,
                EndLine = def.EndPosition.Row + 1,
                Signature = DocExtractor.Signature(def.Text),
                Doc = DocExtractor.DocAbove(language, lines, def.StartPosition.Row),
            });
        }

        ApplyRouteHeuristic(language, relativePath, lines, symbols);
        symbols.Sort(static (a, b) => a.StartLine != b.StartLine
            ? a.StartLine.CompareTo(b.StartLine)
            : string.CompareOrdinal(a.Name, b.Name));
        return symbols;
    }

    private LanguageContext GetContext(SourceLanguage language)
    {
        if (_contexts.TryGetValue(language, out LanguageContext? ctx))
        {
            return ctx;
        }

        var tsLang = new Language(GrammarName(language));
        var parser = new Parser(tsLang);
        var query = new TsQuery(tsLang, QueryFor(language));
        ctx = new LanguageContext(tsLang, parser, query);
        _contexts[language] = ctx;
        return ctx;
    }

    private static string GrammarName(SourceLanguage language) => language switch
    {
        SourceLanguage.TypeScript => "TypeScript",
        SourceLanguage.Tsx => "TSX",
        SourceLanguage.JavaScript => "JavaScript",
        SourceLanguage.CSharp => "c-sharp",
        _ => throw new NotSupportedException(language.ToString()),
    };

    private static string QueryFor(SourceLanguage language) => language switch
    {
        SourceLanguage.JavaScript => JavaScriptQuery,
        SourceLanguage.CSharp => CSharpQuery,
        _ => TypeScriptQuery,
    };

    private static SymbolKind MapKind(string capture) => capture switch
    {
        "class" => SymbolKind.Class,
        "interface" => SymbolKind.Interface,
        "struct" => SymbolKind.Struct,
        "record" => SymbolKind.Record,
        "enum" => SymbolKind.Enum,
        "method" => SymbolKind.Method,
        "property" => SymbolKind.Property,
        "type" => SymbolKind.TypeAlias,
        _ => SymbolKind.Function,
    };

    private static void ApplyRouteHeuristic(
        SourceLanguage language, string relativePath, string[] lines, List<Symbol> symbols)
    {
        if (language is SourceLanguage.TypeScript or SourceLanguage.Tsx or SourceLanguage.JavaScript)
        {
            string normalized = "/" + relativePath.Replace('\\', '/');
            string name = Path.GetFileName(relativePath);
            bool isRouteFile = normalized.Contains("/app/", StringComparison.Ordinal)
                && (name == "route.ts" || name == "route.tsx" || name == "route.js");
            if (!isRouteFile)
            {
                return;
            }

            for (int i = 0; i < symbols.Count; i++)
            {
                if (symbols[i].Kind == SymbolKind.Function && HttpMethods.Contains(symbols[i].Name))
                {
                    symbols[i] = symbols[i] with { Kind = SymbolKind.Route };
                }
            }

            return;
        }

        if (language == SourceLanguage.CSharp)
        {
            foreach (Symbol cls in symbols.Where(s => s.Kind == SymbolKind.Class).ToList())
            {
                bool controller = cls.Name.EndsWith("Controller", StringComparison.Ordinal)
                    || HasApiControllerAttribute(lines, cls.StartLine);
                if (!controller)
                {
                    continue;
                }

                for (int i = 0; i < symbols.Count; i++)
                {
                    if (symbols[i].Kind == SymbolKind.Method
                        && symbols[i].StartLine > cls.StartLine
                        && symbols[i].EndLine <= cls.EndLine)
                    {
                        symbols[i] = symbols[i] with { Kind = SymbolKind.Route };
                    }
                }
            }
        }
    }

    private static bool HasApiControllerAttribute(string[] lines, int classStartLine)
    {
        for (int i = classStartLine - 2; i >= 0 && i >= classStartLine - 6; i--)
        {
            string line = lines[i].Trim();
            if (line.Contains("[ApiController]", StringComparison.Ordinal))
            {
                return true;
            }

            if (line.Length > 0 && !line.StartsWith('[') && !line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (LanguageContext ctx in _contexts.Values)
        {
            ctx.Dispose();
        }

        _contexts.Clear();
    }

    private sealed class LanguageContext(Language language, Parser parser, TsQuery query) : IDisposable
    {
        public Language Language { get; } = language;

        public Parser Parser { get; } = parser;

        public TsQuery Query { get; } = query;

        public void Dispose()
        {
            Query.Dispose();
            Parser.Dispose();
            Language.Dispose();
        }
    }

    private const string TypeScriptQuery = """
        (function_declaration name: (identifier) @function) @def
        (generator_function_declaration name: (identifier) @function) @def
        (class_declaration name: (type_identifier) @class) @def
        (abstract_class_declaration name: (type_identifier) @class) @def
        (interface_declaration name: (type_identifier) @interface) @def
        (type_alias_declaration name: (type_identifier) @type) @def
        (enum_declaration name: (identifier) @enum) @def
        (method_definition name: (property_identifier) @method) @def
        (export_statement (lexical_declaration (variable_declarator
            name: (identifier) @function
            value: [(arrow_function) (function_expression)]))) @def
        """;

    private const string JavaScriptQuery = """
        (function_declaration name: (identifier) @function) @def
        (generator_function_declaration name: (identifier) @function) @def
        (class_declaration name: (identifier) @class) @def
        (method_definition name: (property_identifier) @method) @def
        (export_statement (lexical_declaration (variable_declarator
            name: (identifier) @function
            value: [(arrow_function) (function_expression)]))) @def
        """;

    private const string CSharpQuery = """
        (class_declaration name: (identifier) @class) @def
        (interface_declaration name: (identifier) @interface) @def
        (struct_declaration name: (identifier) @struct) @def
        (record_declaration name: (identifier) @record) @def
        (enum_declaration name: (identifier) @enum) @def
        (method_declaration name: (identifier) @method) @def
        (property_declaration name: (identifier) @property) @def
        """;
}
