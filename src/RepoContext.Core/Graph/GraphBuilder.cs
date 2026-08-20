using Microsoft.Data.Sqlite;
using RepoContext.Core.Configuration;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>
/// Recomputes the file graph (import, test and reference edges) from the
/// references stored with each file. The graph is rebuilt in full on every
/// index run (not incremental) — see ADR 0006 — but since M10 it is rebuilt
/// from the index alone: no repository file is opened (ADR 0019).
/// </summary>
/// <remarks>
/// The previous implementation re-read and re-scanned every indexed file on
/// every run, so an incremental index over a large repository still paid a full
/// pass over the working tree. References are now extracted once, while the
/// file's bytes are in memory for hashing and chunking, and the rebuild is a
/// resolution step over stored rows.
/// </remarks>
public sealed class GraphBuilder
{
    private static readonly string[] TsExtensions =
        [".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];

    /// <summary>
    /// Upper bound on reference edges contributed by one file. A release-notes
    /// document that names two hundred files would otherwise dominate graph
    /// expansion for every query that touches any of them.
    /// </summary>
    private const int MaxReferenceEdgesPerFile = 64;

    private readonly IndexStore _store;
    private readonly ArtifactOptions _artifacts;

    public GraphBuilder(IndexStore store, ArtifactOptions? artifacts = null)
    {
        _store = store;
        _artifacts = artifacts ?? new ArtifactOptions();
    }

    /// <summary>
    /// Source bytes read while rebuilding graph facts. Zero since M10: the
    /// rebuild resolves stored references and never opens a file.
    /// </summary>
    public long BytesRead => 0;

    /// <summary>Files whose stored references were resolved into edges.</summary>
    public int FilesAnalyzed { get; private set; }

    /// <summary>Cross-artifact reference edges created in the last rebuild.</summary>
    public int ReferenceEdges { get; private set; }

    public int Rebuild()
    {
        IReadOnlyList<FileRow> files = _store.GetFiles();
        var idByPath = files.ToDictionary(f => f.Path, f => f.Id, StringComparer.Ordinal);
        var pathSet = new HashSet<string>(idByPath.Keys, StringComparer.Ordinal);
        var byBasename = BuildBasenameIndex(files);
        IReadOnlyList<TypeDef> typeDefs = _store.GetTypeDefiners();
        Dictionary<long, List<FileReference>> refsByFile = _store.GetRefsByFile();
        Dictionary<string, long> uniqueSymbols = _artifacts.LinkSymbols
            ? _store.GetUniqueSymbolDefiners()
            : [];

        FilesAnalyzed = 0;
        ReferenceEdges = 0;

        _store.ClearEdges();
        using (SqliteTransaction tx = _store.BeginTransaction())
        {
            foreach (FileRow file in files)
            {
                if (!refsByFile.TryGetValue(file.Id, out List<FileReference>? references))
                {
                    continue;
                }

                FilesAnalyzed++;
                AddImportEdges(file, references, pathSet, idByPath, tx);
                AddTypeEdges(file, references, typeDefs, tx);
                AddReferenceEdges(file, references, pathSet, idByPath, byBasename, uniqueSymbols, tx);
            }

            AddTestEdges(files, idByPath, tx);
            tx.Commit();
        }

        return _store.CountEdges();
    }

    /// <summary>
    /// Repo-relative paths grouped by file name, so a path mentioned without its
    /// full prefix (<c>login.ts</c>, <c>auth/login.ts</c>) can be resolved by
    /// suffix without scanning every indexed path.
    /// </summary>
    private static Dictionary<string, List<string>> BuildBasenameIndex(IReadOnlyList<FileRow> files)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (FileRow file in files)
        {
            string name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
            if (!index.TryGetValue(name, out List<string>? paths))
            {
                paths = [];
                index[name] = paths;
            }

            paths.Add(file.Path);
        }

        return index;
    }

    private void AddImportEdges(
        FileRow file, List<FileReference> references, HashSet<string> pathSet,
        Dictionary<string, long> idByPath, SqliteTransaction tx)
    {
        foreach (FileReference reference in references)
        {
            if (reference.Kind != RefKind.Import || !reference.Value.StartsWith('.'))
            {
                continue; // bare/external specifier
            }

            string? target = ResolveTsImport(file.Path, reference.Value, pathSet);
            if (target is not null && idByPath.TryGetValue(target, out long dst) && dst != file.Id)
            {
                _store.InsertEdge(file.Id, dst, EdgeKind.Import, tx);
            }
        }
    }

    /// <summary>
    /// Resolves the type-like identifiers a C# file uses to the nearest file
    /// declaring a type of that name — the language has no import statement that
    /// names a file, so proximity is the available signal (ADR 0006).
    /// </summary>
    private void AddTypeEdges(
        FileRow file, List<FileReference> references, IReadOnlyList<TypeDef> typeDefs,
        SqliteTransaction tx)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileReference reference in references)
        {
            if (reference.Kind == RefKind.Type)
            {
                referenced.Add(reference.Value);
            }
        }

        if (referenced.Count == 0)
        {
            return;
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

    /// <summary>
    /// Links an artifact to the code it talks about: the repository paths it
    /// names and the symbols it names unambiguously. This is the edge that lets
    /// <c>related</c> answer "which specification describes this file" and lets
    /// <c>context</c> pull the implementation in when the task names a document.
    /// </summary>
    private void AddReferenceEdges(
        FileRow file, List<FileReference> references, HashSet<string> pathSet,
        Dictionary<string, long> idByPath, Dictionary<string, List<string>> byBasename,
        Dictionary<string, long> uniqueSymbols, SqliteTransaction tx)
    {
        var targets = new HashSet<long>();
        foreach (FileReference reference in references)
        {
            if (targets.Count >= MaxReferenceEdgesPerFile)
            {
                break;
            }

            long target = reference.Kind switch
            {
                RefKind.Path when _artifacts.LinkPaths =>
                    ResolvePath(reference.Value, pathSet, idByPath, byBasename),
                RefKind.Symbol when _artifacts.LinkSymbols =>
                    uniqueSymbols.GetValueOrDefault(reference.Value),
                _ => 0,
            };

            if (target != 0 && target != file.Id)
            {
                targets.Add(target);
            }
        }

        foreach (long target in targets)
        {
            _store.InsertEdge(file.Id, target, EdgeKind.Reference, tx);
            ReferenceEdges++;
        }
    }

    /// <summary>
    /// Resolves a mentioned path to an indexed file: exactly, else by a unique
    /// path suffix. Ambiguity is left unresolved — a wrong edge is worse than a
    /// missing one, because it spends an agent's budget on the wrong file.
    /// </summary>
    private static long ResolvePath(
        string value, HashSet<string> pathSet, Dictionary<string, long> idByPath,
        Dictionary<string, List<string>> byBasename)
    {
        if (pathSet.Contains(value))
        {
            return idByPath[value];
        }

        string name = value[(value.LastIndexOf('/') + 1)..];
        if (!byBasename.TryGetValue(name, out List<string>? candidates))
        {
            return 0;
        }

        string suffix = "/" + value;
        long resolved = 0;
        foreach (string candidate in candidates)
        {
            if (!candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (resolved != 0)
            {
                return 0; // ambiguous
            }

            resolved = idByPath[candidate];
        }

        return resolved;
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
}

/// <summary>Edge kind labels.</summary>
public static class EdgeKind
{
    public const string Import = "import";
    public const string Test = "test";

    /// <summary>
    /// A file names another file: a document that points at an implementation,
    /// a specification that names a module, a comment that links a path (ADR 0019).
    /// </summary>
    public const string Reference = "reference";
}
