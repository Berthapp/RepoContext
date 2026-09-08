using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>How a related file connects to the target.</summary>
public enum Relation
{
    Imports,
    ImportedBy,
    Tests,
    TestedBy,

    /// <summary>This file names another file (a spec pointing at an implementation).</summary>
    References,

    /// <summary>Another file names this one (the spec that describes it).</summary>
    ReferencedBy,
}

/// <summary>A related file plus its relation and machine-readable reason.</summary>
public sealed record RelatedEntry(string Path, Relation Relation, IReadOnlyList<string> Reasons);

/// <summary>The result of <c>repoctx related</c>.</summary>
public sealed record RelatedResult(string Path, string Kind, IReadOnlyList<RelatedEntry> Entries)
{
    public IReadOnlyList<UnresolvedReference> Unresolved { get; init; } = [];
}

/// <summary>Answers <c>related</c> queries from the graph (spec F4).</summary>
public static class Related
{
    /// <summary>Returns related files for <paramref name="relativePath"/>, or null if not indexed.</summary>
    public static RelatedResult? Query(IndexStore store, string relativePath)
    {
        using var snapshot = store.BeginReadSnapshot();
        if (store.FindFile(relativePath) is not { } file)
        {
            return null;
        }

        var entries = new List<RelatedEntry>();
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Import, outgoing: true),
            Relation.Imports, "import-edge");
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Import, outgoing: false),
            Relation.ImportedBy, "reverse-import-edge");
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Test, outgoing: true),
            Relation.Tests, "test-link");
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Test, outgoing: false),
            Relation.TestedBy, "test-link");
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Reference, outgoing: true),
            Relation.References, "reference-edge");
        Add(entries, store.GetNeighbors(file.Id, EdgeKind.Reference, outgoing: false),
            Relation.ReferencedBy, "reverse-reference-edge");

        // Both edges and diagnostics describe one committed generation. Resolution
        // uses indexed references/configuration, without reopening working-tree files.
        var unresolved = new List<UnresolvedReference>();
        List<FileReference> references = store.GetRefsByFile(file.Id, file.Id).GetValueOrDefault(file.Id) ?? [];
        if (references.Any(r => r.Kind is RefKind.Import or RefKind.Type))
        {
            IReadOnlyList<FileRow> files = store.GetFiles();
            var imports = new TsImportResolver(store, files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal));
            CSharpTypeResolver? types = references.Any(r => r.Kind == RefKind.Type) ? new(store, files) : null;
            foreach (FileReference reference in references)
            {
                ImportResolution resolution = reference.Kind switch
                {
                    RefKind.Import => imports.Resolve(file.Path, reference.Value),
                    RefKind.Type => types!.Resolve(file.Path, reference, references),
                    _ => default,
                };
                if (resolution.Reason is { } reason)
                    unresolved.Add(new(reference.Kind, reference.Value, reference.Line, reason));
            }
        }
        for (int i = 0; i < entries.Count; i++)
        {
            RelatedEntry entry = entries[i];
            string source = entry.Relation == Relation.ImportedBy ? entry.Path : file.Path;
            if (entry.Relation is Relation.Imports or Relation.ImportedBy && source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                entries[i] = entry with { Reasons = [.. entry.Reasons, "csharp-syntax"] };
        }
        return new RelatedResult(file.Path, file.Kind, entries)
        {
            Unresolved = unresolved.OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Value, StringComparer.Ordinal).ToArray(),
        };
    }

    private static void Add(
        List<RelatedEntry> entries, IReadOnlyList<string> paths, Relation relation, string reason)
    {
        foreach (string path in paths)
        {
            entries.Add(new RelatedEntry(path, relation, [reason]));
        }
    }
}
