using System.Xml;
using System.Xml.Linq;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>
/// Narrows syntax-derived type uses by namespace and ordinary SDK project references.
/// Project files are read from the same indexed snapshot as the source. MSBuild
/// expressions, conditional references and custom compile sets remain unresolved.
/// Unknown imported settings cannot disambiguate types, but preserve unique syntax links.
/// </summary>
internal sealed class CSharpTypeResolver
{
    private readonly IndexStore _store;
    private readonly Dictionary<string, List<TypeDef>> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _projectsByDirectory;
    private readonly HashSet<string> _paths;
    private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);

    public CSharpTypeResolver(IndexStore store, IReadOnlyList<FileRow> files)
    {
        _store = store;
        _paths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        _projectsByDirectory = files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => Directory(f.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList(), StringComparer.Ordinal);
        foreach (TypeDef definition in store.GetTypeDefiners())
        {
            foreach (string key in new[] { definition.Name, definition.Name[(definition.Name.LastIndexOf('.') + 1)..] }
                .Distinct(StringComparer.Ordinal))
            {
                if (!_types.TryGetValue(key, out List<TypeDef>? declarations)) _types[key] = declarations = [];
                declarations.Add(definition);
            }
        }
    }

    public ImportResolution Resolve(string from, FileReference reference, IReadOnlyList<FileReference> references)
    {
        // Types with no local declarations are normally framework or package types.
        if (!_types.TryGetValue(reference.Value, out List<TypeDef>? declarations)) return new(null);
        string[] namespaces = references.Where(r => r.Kind == "namespace").Select(r => r.Value).ToArray();
        List<TypeDef> scoped = declarations.Where(d => d.Name == reference.Value
            || namespaces.Any(ns => d.Name == ns + "." + reference.Value)).ToList();
        List<TypeDef> possible = scoped.Count > 0 ? scoped : declarations;

        Scope scope = GetScope(from);
        if (scope.Problem is { } problem && !scope.AllowUniqueSyntax) return new(null, problem);
        if (scope.Projects is not null)
        {
            var reachable = new List<TypeDef>();
            foreach (TypeDef declaration in possible)
            {
                List<string>? owners = Owners(declaration.Path);
                if (owners is null || !owners.Any(scope.Projects.Contains)) continue;
                if (owners.Count != 1) return new(null, "ambiguous-project-ownership");
                reachable.Add(declaration);
            }
            possible = reachable;
            if (possible.Count == 0) return new(null, "type-outside-project-scope");
        }
        if (possible.Any(d => d.Path == from)) return new(null);
        string[] targets = possible.Select(d => d.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 1 && Owners(targets[0]) is { Count: > 1 })
            return new(null, "ambiguous-project-ownership");
        return targets.Length == 1 ? new(targets[0]) : new(null, "ambiguous-type");
    }

    private Scope GetScope(string from)
    {
        List<string>? owners = Owners(from);
        if (owners is null) return new(null);
        if (owners.Count != 1) return new(null, "ambiguous-project-ownership");
        string owner = owners[0];
        if (_scopes.TryGetValue(owner, out Scope? cached)) return cached;
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        bool unknownImports = false;
        pending.Push(owner);
        while (pending.TryPop(out string? path))
        {
            if (!reachable.Add(path)) continue;
            Project project = Load(path);
            if (project.Problem is { } problem && !project.AllowUniqueSyntax)
                return _scopes[owner] = new(null, problem);
            unknownImports |= project.AllowUniqueSyntax;
            foreach (string reference in project.References) pending.Push(reference);
        }
        return _scopes[owner] = unknownImports
            ? new(null, "unsupported-project-import", AllowUniqueSyntax: true)
            : new(reachable);
    }

    private List<string>? Owners(string path)
    {
        string directory = Directory(path);
        while (true)
        {
            if (_projectsByDirectory.TryGetValue(directory, out List<string>? projects)) return projects;
            if (directory.Length == 0) return null;
            directory = Directory(directory);
        }
    }

    private Project Load(string path)
    {
        if (_projects.TryGetValue(path, out Project? cached)) return cached;
        if (_store.GetSourceSlice(path, 1, int.MaxValue)?.Text is not { } content)
            return Save("project-reference-not-indexed");
        try
        {
            XElement root = XElement.Parse(content);
            if (root.Name.LocalName != "Project") return Save("invalid-project-configuration");
            if (root.Attribute("Sdk") is null || HasCustomCompileSet(root))
                return Save("unsupported-project-configuration");
            bool unknownImports = root.Descendants().Any(e => e.Name.LocalName == "Import");
            foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                string directory = Directory(path);
                while (true)
                {
                    string settings = directory.Length == 0 ? name : directory + "/" + name;
                    if (_paths.Contains(settings))
                    {
                        if (_store.GetSourceSlice(settings, 1, int.MaxValue)?.Text is not { } settingsContent)
                            return Save("unsupported-project-configuration");
                        XElement settingsRoot = XElement.Parse(settingsContent);
                        if (HasCustomCompileSet(settingsRoot)
                            || settingsRoot.Descendants().Any(e => e.Name.LocalName == "ProjectReference"))
                            return Save("unsupported-project-configuration");
                        unknownImports |= settingsRoot.Descendants().Any(e => e.Name.LocalName == "Import");
                        break;
                    }
                    if (directory.Length == 0) break;
                    directory = Directory(directory);
                }
            }
            var references = new List<string>();
            foreach (XElement reference in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (include is null || include.IndexOfAny(['$', '@', '%', '*', '?', ';', ':']) >= 0
                    || include.StartsWith('/') || include.StartsWith('\\')
                    || reference.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null)
                    || reference.Attribute("ReferenceOutputAssembly") is { Value: not "true" }
                    || reference.Descendants().Any(e => e.Name.LocalName == "ReferenceOutputAssembly" && e.Value.Trim() != "true"))
                    return Save("unsupported-project-configuration");
                string? target = Combine(Directory(path), include);
                if (target is null) return Save("project-reference-not-indexed");
                references.Add(target);
            }
            return _projects[path] = new(references,
                unknownImports ? "unsupported-project-import" : null, AllowUniqueSyntax: unknownImports);
        }
        catch (XmlException) { return Save("invalid-project-configuration"); }

        Project Save(string reason) => _projects[path] = new([], reason);
    }

    private static bool HasCustomCompileSet(XElement root) => root.Descendants().Any(e =>
        e.Name.LocalName is "Compile" or "DefaultItemExcludes" or "DefaultItemExcludesInProjectFolder"
        || e.Name.LocalName is "EnableDefaultCompileItems" or "EnableDefaultItems" && e.Value.Trim() != "true"
        || e.Name.LocalName is "DirectoryBuildPropsPath" or "DirectoryBuildTargetsPath" or "ImportDirectoryBuildProps"
            or "ImportDirectoryBuildTargets");

    private static string Directory(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";

    private static string? Combine(string directory, string value)
    {
        var segments = new List<string>();
        foreach (string part in (directory + "/" + value).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(part);
        }
        return string.Join('/', segments);
    }

    private sealed record Project(IReadOnlyList<string> References, string? Problem = null, bool AllowUniqueSyntax = false);
    private sealed record Scope(HashSet<string>? Projects, string? Problem = null, bool AllowUniqueSyntax = false);
}
