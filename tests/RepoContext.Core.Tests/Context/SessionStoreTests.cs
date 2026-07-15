using RepoContext.Core;
using RepoContext.Core.Context;

namespace RepoContext.Core.Tests.Context;

/// <summary>Server-side known-set persistence (ADR 0012).</summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-session-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("default", true)]
    [InlineData("feature-42_a.b", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    public void IsValidName_AcceptsOnlyPortableFileNames(string name, bool valid) =>
        Assert.Equal(valid, SessionStore.IsValidName(name));

    [Fact]
    public void IsValidName_RejectsOverlongNames() =>
        Assert.False(SessionStore.IsValidName(new string('a', 65)));

    [Fact]
    public void Load_MissingSession_IsEmpty()
    {
        RepoLayout layout = RepoLayout.For(_root);

        Assert.Empty(SessionStore.Load(layout, "nope"));
    }

    [Fact]
    public void Save_RecordsSlicesAndUnchanged_ButNotPointers()
    {
        RepoLayout layout = RepoLayout.For(_root);
        var result = new ContextResult
        {
            Query = "q",
            Terms = ["q"],
            State = "abc",
            Detail = ContextDetail.Slices,
            Items =
            [
                Item("a.ts", "hash-a") with { Snippet = "const a = 1;" },
                Item("b.ts", "hash-b") with { Unchanged = true },
                Item("c.ts", "hash-c"), // pointer only - caller does not hold it
            ],
            TotalCandidates = 3,
            Omitted = 0,
            EstimatedTokens = 0,
        };

        SessionStore.Save(layout, "s1", result);
        IReadOnlyDictionary<string, string> known = SessionStore.Load(layout, "s1");

        Assert.Equal(2, known.Count);
        Assert.Equal("hash-a", known["a.ts"]);
        Assert.Equal("hash-b", known["b.ts"]);
        Assert.False(known.ContainsKey("c.ts"));
    }

    [Fact]
    public void Save_MergesIntoExistingSession_NewHashWins()
    {
        RepoLayout layout = RepoLayout.For(_root);
        SessionStore.Save(layout, "s1", Bundle(Item("a.ts", "old") with { Snippet = "x" }));
        SessionStore.Save(layout, "s1", Bundle(Item("a.ts", "new") with { Snippet = "y" },
            Item("d.ts", "hash-d") with { Snippet = "z" }));

        IReadOnlyDictionary<string, string> known = SessionStore.Load(layout, "s1");

        Assert.Equal("new", known["a.ts"]);
        Assert.Equal("hash-d", known["d.ts"]);
    }

    [Fact]
    public void Load_CorruptFile_IsTreatedAsEmpty()
    {
        RepoLayout layout = RepoLayout.For(_root);
        string path = SessionStore.PathFor(layout, "bad");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");

        Assert.Empty(SessionStore.Load(layout, "bad"));
    }

    private static ContextResult Bundle(params ContextItem[] items) => new()
    {
        Query = "q",
        Terms = ["q"],
        State = "abc",
        Detail = ContextDetail.Slices,
        Items = items,
        TotalCandidates = items.Length,
        Omitted = 0,
        EstimatedTokens = 0,
    };

    private static ContextItem Item(string path, string hash) => new()
    {
        Path = path,
        Kind = "source",
        Score = 1,
        StartLine = 1,
        EndLine = 1,
        Reasons = ["fts"],
        Hash = hash,
    };
}
