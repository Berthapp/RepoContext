// M0 parser spike (throwaway). Evaluates TreeSitter.DotNet for extracting
// symbols from the TS/JS and C# fixtures, and measures parse+extract time.
using System.Diagnostics;
using TreeSitter;

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string tsFixtures = Path.Combine(repoRoot, "tests", "fixtures", "sample-ts");
string csFixtures = Path.Combine(repoRoot, "tests", "fixtures", "sample-cs");

// Resolve the grammar friendly-names actually accepted by the binding.
string csName = FirstWorkingLanguage("CSharp", "c_sharp", "c-sharp", "CSHARP");
Console.WriteLine($"C# grammar name resolved to: {csName}\n");

var tsQuery = """
    (function_declaration name: (identifier) @function)
    (generator_function_declaration name: (identifier) @function)
    (class_declaration name: (type_identifier) @class)
    (interface_declaration name: (type_identifier) @interface)
    (type_alias_declaration name: (type_identifier) @type)
    (enum_declaration name: (identifier) @enum)
    (method_definition name: (property_identifier) @method)
    (export_statement (lexical_declaration (variable_declarator
        name: (identifier) @arrow value: [(arrow_function) (function_expression)])))
    """;

// JavaScript grammar has no interface/type-alias/enum nodes and uses plain
// (identifier) for class names.
var jsQuery = """
    (function_declaration name: (identifier) @function)
    (generator_function_declaration name: (identifier) @function)
    (class_declaration name: (identifier) @class)
    (method_definition name: (property_identifier) @method)
    (export_statement (lexical_declaration (variable_declarator
        name: (identifier) @arrow value: [(arrow_function) (function_expression)])))
    """;

var csQuery = """
    (class_declaration name: (identifier) @class)
    (interface_declaration name: (identifier) @interface)
    (struct_declaration name: (identifier) @struct)
    (record_declaration name: (identifier) @record)
    (enum_declaration name: (identifier) @enum)
    (method_declaration name: (identifier) @method)
    (property_declaration name: (identifier) @property)
    """;

var results = new List<(string lang, string file, int symbols, double ms)>();

RunLanguage("TypeScript", tsQuery, EnumerateFiles(tsFixtures, ".ts"), results, skipGenerated: true);
RunLanguage("TSX", tsQuery, EnumerateFiles(tsFixtures, ".tsx"), results, skipGenerated: true);
RunLanguage("JavaScript", jsQuery, EnumerateFiles(tsFixtures, ".js"), results, skipGenerated: true, skipVendor: false);
RunLanguage(csName, csQuery, EnumerateFiles(csFixtures, ".cs"), results);

Console.WriteLine("\n==================== SUMMARY ====================");
foreach (var group in results.GroupBy(r => r.lang))
{
    int files = group.Count();
    int syms = group.Sum(r => r.symbols);
    double avg = group.Average(r => r.ms);
    double max = group.Max(r => r.ms);
    Console.WriteLine($"{group.Key,-12} files={files,-3} symbols={syms,-4} avg={avg,6:F3} ms/file  max={max,6:F3} ms");
}
double overallAvg = results.Average(r => r.ms);
Console.WriteLine($"\nOVERALL avg = {overallAvg:F3} ms/file across {results.Count} files "
    + $"({(overallAvg < 10 ? "PASS < 10ms" : "FAIL >= 10ms")})");

void RunLanguage(string langName, string queryText, IEnumerable<string> files,
    List<(string, string, int, double)> sink, bool skipGenerated = false, bool skipVendor = false)
{
    using var language = new Language(langName);
    using var parser = new Parser(language);
    using var query = new Query(language, queryText);

    Console.WriteLine($"---- {langName} ----");
    foreach (string file in files)
    {
        string name = Path.GetFileName(file);
        if (skipGenerated && name.Contains(".generated.")) continue;
        if (skipVendor && name.Contains(".min.")) continue;

        string source = File.ReadAllText(file);
        var sw = Stopwatch.StartNew();
        using var tree = parser.Parse(source)!;
        var captures = query.Execute(tree.RootNode).Captures.ToList();
        sw.Stop();

        double ms = sw.Elapsed.TotalMilliseconds;
        sink.Add((langName, file, captures.Count, ms));

        string rel = Path.GetRelativePath(repoRoot, file);
        Console.WriteLine($"  {rel,-52} {captures.Count,3} symbols  {ms,6:F3} ms");
        foreach (var c in captures.Take(4))
        {
            var n = c.Node;
            Console.WriteLine($"        {c.Name,-10} {n.Text,-22} L{n.StartPosition.Row + 1}");
        }
    }
}

string FirstWorkingLanguage(params string[] names)
{
    foreach (string n in names)
    {
        try { using var l = new Language(n); return n; }
        catch { /* try next */ }
    }
    throw new InvalidOperationException("No C# grammar name worked: " + string.Join(", ", names));
}

static IEnumerable<string> EnumerateFiles(string root, string ext) =>
    Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories)
            .Where(f => !f.Contains("/node_modules/"))
            .OrderBy(f => f, StringComparer.Ordinal)
        : [];

static string FindRepoRoot(string start)
{
    for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
    {
        if (File.Exists(Path.Combine(d.FullName, "RepoContext.slnx"))) return d.FullName;
    }
    throw new InvalidOperationException("repo root not found from " + start);
}
