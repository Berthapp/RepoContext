using RepoContext.Core.Storage;

namespace RepoContext.Core.Graph;

/// <summary>How a related file connects to the target.</summary>
public enum Relation
{
    Imports,
    ImportedBy,
    Tests,
    TestedBy,
}

/// <summary>A related file plus its relation and machine-readable reason.</summary>
public sealed record RelatedEntry(string Path, Relation Relation, IReadOnlyList<string> Reasons);

/// <summary>The result of <c>repoctx related</c>.</summary>
public sealed record RelatedResult(string Path, string Kind, IReadOnlyList<RelatedEntry> Entries);

/// <summary>Answers <c>related</c> queries from the graph (spec F4).</summary>
public static class Related
{
    /// <summary>Returns related files for <paramref name="relativePath"/>, or null if not indexed.</summary>
    public static RelatedResult? Query(IndexStore store, string relativePath)
    {
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

        return new RelatedResult(file.Path, file.Kind, entries);
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
