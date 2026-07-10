using RepoContext.Core.Storage;

namespace RepoContext.Core.Architecture;

/// <summary>
/// Builds the architecture summary (spec F6): a depth-limited tree with LOC
/// aggregation, language distribution, centrality (most-imported files) and an
/// entrypoint heuristic. Deterministic.
/// </summary>
public sealed class ArchitectureEngine
{
    private const int MaxDepth = 3;
    private const int TopCentral = 10;

    private static readonly HashSet<string> EntrypointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program.cs", "main.cs", "main.ts", "main.js", "main.tsx",
        "middleware.ts", "middleware.js",
        "page.tsx", "page.jsx", "layout.tsx", "layout.jsx",
        "route.ts", "route.tsx", "route.js",
    };

    private readonly IndexStore _store;

    public ArchitectureEngine(IndexStore store) => _store = store;

    public ArchitectureResult Build()
    {
        IReadOnlyList<FileMetric> files = _store.GetFileMetrics();

        var languages = files
            .GroupBy(f => f.Language)
            .Select(g => new LanguageStat(g.Key, g.Count(), g.Sum(f => f.LineCount)))
            .OrderByDescending(s => s.Loc)
            .ThenBy(s => s.Language, StringComparer.Ordinal)
            .ToList();

        var central = _store.GetMostImported(TopCentral)
            .Select(c => (c.Path, c.Dependents))
            .ToList();

        HashSet<string> roots = _store.GetPathsWithoutDependents();
        HashSet<string> withOutgoing = _store.GetPathsWithOutgoing();
        var entrypoints = files
            .Where(f => IsEntrypoint(f, roots, withOutgoing))
            .Select(f => f.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new ArchitectureResult
        {
            TotalFiles = files.Count,
            TotalLoc = files.Sum(f => f.LineCount),
            Tree = BuildTree(files),
            Languages = languages,
            Central = central,
            Entrypoints = entrypoints,
        };
    }

    private static bool IsEntrypoint(FileMetric file, HashSet<string> roots, HashSet<string> withOutgoing)
    {
        string name = System.IO.Path.GetFileName(file.Path);
        if (EntrypointNames.Contains(name))
        {
            return true;
        }

        // A source file that nothing imports but which itself pulls in others
        // reads as a root/driver (excludes leaf components with no local imports).
        return file.Kind == "source"
            && roots.Contains(file.Path)
            && withOutgoing.Contains(file.Path);
    }

    private static TreeNode BuildTree(IReadOnlyList<FileMetric> files)
    {
        // Aggregate LOC and file counts into every ancestor directory.
        var loc = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Ensure(string dir)
        {
            children.TryAdd(dir, new SortedSet<string>(StringComparer.Ordinal));
        }

        Ensure(string.Empty);
        foreach (FileMetric file in files)
        {
            string[] segments = file.Path.Split('/');
            string prefix = string.Empty;
            loc[string.Empty] = loc.GetValueOrDefault(string.Empty) + file.LineCount;
            count[string.Empty] = count.GetValueOrDefault(string.Empty) + 1;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                string dir = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
                Ensure(dir);
                children[prefix].Add(dir);
                loc[dir] = loc.GetValueOrDefault(dir) + file.LineCount;
                count[dir] = count.GetValueOrDefault(dir) + 1;
                prefix = dir;
            }
        }

        return Node(string.Empty, ".", 0, loc, count, children);
    }

    private static TreeNode Node(
        string dir, string name, int depth,
        Dictionary<string, int> loc, Dictionary<string, int> count,
        Dictionary<string, SortedSet<string>> children)
    {
        var kids = new List<TreeNode>();
        if (depth < MaxDepth && children.TryGetValue(dir, out SortedSet<string>? subs))
        {
            foreach (string sub in subs)
            {
                string subName = sub[(sub.LastIndexOf('/') + 1)..];
                kids.Add(Node(sub, subName, depth + 1, loc, count, children));
            }
        }

        return new TreeNode
        {
            Name = name,
            Path = dir,
            Loc = loc.GetValueOrDefault(dir),
            FileCount = count.GetValueOrDefault(dir),
            Children = kids,
        };
    }
}
