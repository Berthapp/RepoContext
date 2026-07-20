using RepoContext.Core.Configuration;
using RepoContext.Core.Memory;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Memory;

/// <summary>
/// Deterministic recall (ADR 0013): term/tag/path scoring with reasons,
/// session scoping, filters and hash-based staleness against a real index.
/// </summary>
public class MemoryEngineTests : IDisposable
{
    private readonly FixtureRepo _repo;
    private readonly IndexStore _store;
    private readonly RepoctxConfig _config;

    public MemoryEngineTests()
    {
        _repo = new FixtureRepo("sample-ts");
        _config = RepoctxConfig.CreateDefault() with { Include = ["."] };
        _store = IndexHelper.BuildIndex(_repo, _config);
    }

    public void Dispose()
    {
        _store.Dispose();
        _repo.Dispose();
        GC.SuppressFinalize(this);
    }

    private MemoryEntry Entry(
        string text, string kind = MemoryKinds.Note, string? session = null,
        IReadOnlyList<string>? tags = null, params string[] linkedPaths)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in linkedPaths)
        {
            FileRow? row = _store.FindFile(path);
            Assert.NotNull(row);
            files[path] = Hashes.Short(row.Value.ContentHash);
        }

        return new MemoryEntry
        {
            Id = MemoryEntry.ComputeId(kind, text, session, files.Keys),
            Kind = kind,
            Text = text,
            Files = files,
            Tags = tags ?? [],
            Session = session,
            Created = "2026-07-20",
        };
    }

    [Fact]
    public void Search_ScoresTermMatches_WithReasons_Deterministically()
    {
        List<MemoryEntry> entries =
        [
            Entry("The login flow validates credentials then creates a session."),
            Entry("Payments are retried three times."),
        ];
        var options = new MemoryQueryOptions { Query = "login session" };

        MemoryQueryResult first = MemoryEngine.Search(entries, options, _config, _store);
        MemoryQueryResult second = MemoryEngine.Search(entries, options, _config, _store);

        MemoryHit hit = Assert.Single(first.Hits);
        Assert.Equal(1.0, hit.Score);
        Assert.Equal(["term:login", "term:session"], hit.Reasons);
        Assert.False(hit.Stale);
        Assert.True(hit.EstimatedTokens > 0);
        Assert.Equal(2, first.TotalEntries);

        // Same store + index + query ⇒ identical result.
        Assert.Equal(first, second, MemoryResultComparer.Instance);
    }

    [Fact]
    public void Search_MatchesTags_AndPathsAtHalfWeight()
    {
        List<MemoryEntry> entries =
        [
            Entry("Auth decision record.", tags: ["login"]),
            Entry("Unrelated text.", linkedPaths: "src/auth/login.ts"),
        ];

        MemoryQueryResult result = MemoryEngine.Search(
            entries, new MemoryQueryOptions { Query = "login" }, _config, _store);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("tag:login", Assert.Single(result.Hits[0].Reasons));
        Assert.Equal(1.0, result.Hits[0].Score);
        Assert.Equal("file:src/auth/login.ts", Assert.Single(result.Hits[1].Reasons));
        Assert.Equal(0.5, result.Hits[1].Score);
    }

    [Fact]
    public void Search_SessionScoping_HidesShortTermWithoutSession()
    {
        List<MemoryEntry> entries =
        [
            Entry("Long-term login note."),
            Entry("Short-term login scratchpad.", session: "s1"),
        ];

        MemoryQueryResult withoutSession = MemoryEngine.Search(
            entries, new MemoryQueryOptions { Query = "login" }, _config, _store);
        MemoryQueryResult withSession = MemoryEngine.Search(
            entries, new MemoryQueryOptions { Query = "login", Session = "s1" }, _config, _store);
        MemoryQueryResult otherSession = MemoryEngine.Search(
            entries, new MemoryQueryOptions { Query = "login", Session = "other" }, _config, _store);

        Assert.Single(withoutSession.Hits);
        Assert.Equal(2, withSession.Hits.Count);
        Assert.Single(otherSession.Hits);
        Assert.Equal(1, withoutSession.TotalEntries);
        Assert.Equal(2, withSession.TotalEntries);
    }

    [Fact]
    public void Search_FiltersByKindAndFile()
    {
        List<MemoryEntry> entries =
        [
            Entry("Login uses JWT.", MemoryKinds.Decision, linkedPaths: "src/auth/login.ts"),
            Entry("Login is tested end to end.", MemoryKinds.Note),
        ];

        MemoryQueryResult decisions = MemoryEngine.Search(entries,
            new MemoryQueryOptions { Query = "login", Kind = MemoryKinds.Decision }, _config, _store);
        MemoryQueryResult linked = MemoryEngine.Search(entries,
            new MemoryQueryOptions { Query = "login", File = "src/auth/login.ts" }, _config, _store);

        Assert.Equal(MemoryKinds.Decision, Assert.Single(decisions.Hits).Entry.Kind);
        Assert.Equal(MemoryKinds.Decision, Assert.Single(linked.Hits).Entry.Kind);
    }

    [Fact]
    public void Search_FlagsStale_WhenLinkedFileDrifts()
    {
        MemoryEntry entry = Entry("Login validates credentials.", linkedPaths: "src/auth/login.ts");

        // Drift the linked file and re-index: the recorded hash no longer matches.
        _repo.Write("src/auth/login.ts",
            File.ReadAllText(_repo.PathOf("src/auth/login.ts")) + "\n// drift\n");
        using IndexStore fresh = IndexHelper.BuildIndex(_repo, _config);

        MemoryQueryResult result = MemoryEngine.Search([entry],
            new MemoryQueryOptions { Query = "login" }, _config, fresh);
        MemoryQueryResult staleOnly = MemoryEngine.Search([entry],
            new MemoryQueryOptions { Query = "login", StaleOnly = true }, _config, fresh);
        MemoryQueryResult freshOnly = MemoryEngine.Search([entry],
            new MemoryQueryOptions { Query = "payments", StaleOnly = true }, _config, fresh);

        MemoryHit hit = Assert.Single(result.Hits);
        Assert.True(hit.Stale);
        Assert.Equal("src/auth/login.ts", Assert.Single(hit.StaleFiles));
        Assert.Single(staleOnly.Hits);
        Assert.Empty(freshOnly.Hits);
    }

    [Fact]
    public void Search_ListMode_OrdersByCreatedDescThenId()
    {
        MemoryEntry older = Entry("Older note.") with { Created = "2026-07-01" };
        MemoryEntry newer = Entry("Newer note.") with { Created = "2026-07-19" };

        MemoryQueryResult result = MemoryEngine.Search(
            [older, newer], new MemoryQueryOptions(), _config, _store);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("Newer note.", result.Hits[0].Entry.Text);
        Assert.Equal(0, result.Hits[0].Score);
        Assert.Empty(result.Hits[0].Reasons);
    }

    private sealed class MemoryResultComparer : IEqualityComparer<MemoryQueryResult>
    {
        public static readonly MemoryResultComparer Instance = new();

        public bool Equals(MemoryQueryResult? x, MemoryQueryResult? y) =>
            x is not null && y is not null
            && x.Query == y.Query
            && x.Terms.SequenceEqual(y.Terms)
            && x.TotalEntries == y.TotalEntries
            && x.EstimatedTokens == y.EstimatedTokens
            && x.Hits.Count == y.Hits.Count
            && x.Hits.Zip(y.Hits).All(pair =>
                pair.First.Entry.Id == pair.Second.Entry.Id
                && pair.First.Score == pair.Second.Score
                && pair.First.Reasons.SequenceEqual(pair.Second.Reasons)
                && pair.First.Stale == pair.Second.Stale);

        public int GetHashCode(MemoryQueryResult obj) => obj.TotalEntries;
    }
}
