using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RepoContext.Core.Parsing;
using RepoContext.Core.Scanning;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>
/// Recomputes the file graph (import + test edges) from the current index.
/// The graph is rebuilt in full on every index run (not incremental) — see ADR 0006.
/// </summary>
public sealed partial class GraphBuilder
{
    private static readonly string[] TsExtensions =
        [".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];

    private readonly IndexStore _store;
    private readonly string _repoRoot;
    private readonly ILanguageParser _parser;

    public GraphBuilder(IndexStore store, string repoRoot, ILanguageParser parser)
    {
        _store = store;
        _repoRoot = repoRoot;
        _parser = parser;
    }

    public int Rebuild()
    {
        IReadOnlyList<FileRow> files = _store.GetFiles();
        var idByPath = files.ToDictionary(f => f.Path, f => f.Id, StringComparer.Ordinal);
        var pathSet = new HashSet<string>(idByPath.Keys, StringComparer.Ordinal);
        IReadOnlyList<TypeDef> typeDefs = _store.GetTypeDefiners();

        _store.ClearEdges();
        using (SqliteTransaction tx = _store.BeginTransaction())
        {
            foreach (FileRow file in files)
            {
                SourceLanguage lang = FileClassifier.DetectLanguage(file.Path);
                string content = ReadContent(file.Path);
                if (content.Length == 0)
                {
                    continue;
                }

                if (lang is SourceLanguage.TypeScript or SourceLanguage.Tsx or SourceLanguage.JavaScript)
                {
                    AddTsImportEdges(file, content, pathSet, idByPath, tx);
                }
                else if (lang == SourceLanguage.CSharp)
                {
                    AddCSharpImportEdges(file, content, typeDefs, tx);
                }
            }

            AddTestEdges(files, idByPath, tx);
            tx.Commit();
        }

        return _store.CountEdges();
    }

    private void AddTsImportEdges(
        FileRow file, string content, HashSet<string> pathSet,
        Dictionary<string, long> idByPath, SqliteTransaction tx)
    {
        SourceLanguage lang = FileClassifier.DetectLanguage(file.Path);
        foreach (string specifier in _parser.ExtractImportSpecifiers(lang, content))
        {
            if (!specifier.StartsWith('.'))
            {
                continue; // bare/external specifier
            }

            string? target = ResolveTsImport(file.Path, specifier, pathSet);
            if (target is not null && idByPath.TryGetValue(target, out long dst))
            {
                _store.InsertEdge(file.Id, dst, EdgeKind.Import, tx);
            }
        }
    }

    private void AddCSharpImportEdges(
        FileRow file, string content, IReadOnlyList<TypeDef> typeDefs, SqliteTransaction tx)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierRegex().Matches(content))
        {
            referenced.Add(m.Value);
        }

        foreach (IGrouping<string, TypeDef> group in typeDefs
            .Where(d => referenced.Contains(d.Name) && d.Path != file.Path)
            .GroupBy(d => d.Name, StringComparer.Ordinal))
        {
            TypeDef nearest = group
                .OrderBy(d => DirectoryDistance(file.Path, d.Path))
                .ThenBy(d => d.Path, StringComparer.Ordinal)
                .First();
            _store.InsertEdge(file.Id, nearest.FileId, EdgeKind.Import, tx);
        }
    }

    private void AddTestEdges(
        IReadOnlyList<FileRow> files, Dictionary<string, long> idByPath, SqliteTransaction tx)
    {
        var byId = files.ToDictionary(f => f.Id, f => f);
        foreach (FileRow test in files.Where(f => f.Kind == "test"))
        {
            var targets = new HashSet<long>();

            // 1. Name convention (login.test.ts -> login.ts, FooTests.cs -> Foo.cs).
            foreach (string candidate in NameConventionTargets(test.Path))
            {
                if (idByPath.TryGetValue(candidate, out long id))
                {
                    targets.Add(id);
                }
            }

            // 2. Import edges from the test file to source files.
            foreach (string importedPath in _store.GetNeighbors(test.Id, EdgeKind.Import, outgoing: true))
            {
                if (idByPath.TryGetValue(importedPath, out long id) && byId[id].Kind != "test")
                {
                    targets.Add(id);
                }
            }

            foreach (long target in targets)
            {
                _store.InsertEdge(test.Id, target, EdgeKind.Test, tx);
            }
        }
    }

    /// <summary>Candidate source paths a test file may correspond to by naming convention.</summary>
    public static IEnumerable<string> NameConventionTargets(string testPath)
    {
        string dir = DirName(testPath);
        string file = testPath[(testPath.LastIndexOf('/') + 1)..];
        string ext = Path.GetExtension(file);
        string stem = file[..^ext.Length];

        // Strip .test / .spec (TS/JS) and Tests / Test suffixes (C#).
        var bases = new List<string>();
        foreach (string suffix in new[] { ".test", ".spec" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                bases.Add(stem[..^suffix.Length]);
            }
        }

        foreach (string suffix in new[] { "Tests", "Test" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                bases.Add(stem[..^suffix.Length]);
            }
        }

        foreach (string baseName in bases)
        {
            // Same directory, and (for __tests__) the parent directory.
            yield return Join(dir, baseName + ext);
            if (dir.EndsWith("/__tests__", StringComparison.Ordinal))
            {
                yield return Join(dir[..^"/__tests__".Length], baseName + ext);
            }
        }
    }

    private static string? ResolveTsImport(string fromPath, string specifier, HashSet<string> pathSet)
    {
        string baseDir = DirName(fromPath);
        string combined = Normalize(Join(baseDir, specifier));

        foreach (string ext in TsExtensions)
        {
            string candidate = combined + ext;
            if (pathSet.Contains(candidate))
            {
                return candidate;
            }
        }

        if (pathSet.Contains(combined))
        {
            return combined;
        }

        foreach (string ext in TsExtensions)
        {
            string candidate = combined + "/index" + ext;
            if (pathSet.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int DirectoryDistance(string a, string b)
    {
        string[] da = DirName(a).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] db = DirName(b).Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < da.Length && common < db.Length && da[common] == db[common])
        {
            common++;
        }

        return da.Length - common + (db.Length - common);
    }

    private string ReadContent(string relativePath)
    {
        try
        {
            return File.ReadAllText(Path.Combine(_repoRoot, relativePath));
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string DirName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Join(string dir, string rel) =>
        dir.Length == 0 ? rel : dir + "/" + rel;

    private static string Normalize(string path)
    {
        var stack = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else
            {
                stack.Add(segment);
            }
        }

        return string.Join('/', stack);
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierRegex();
}

/// <summary>Edge kind labels.</summary>
public static class EdgeKind
{
    public const string Import = "import";
    public const string Test = "test";
}
