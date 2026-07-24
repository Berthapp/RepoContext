using Microsoft.Data.Sqlite;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Indexing;

public class IndexerTests
{
    // Scan the whole fixture so the root-level negative cases are reached.
    private static RepoctxConfig ScanAll() => RepoctxConfig.CreateDefault() with { Include = ["."] };

    private static IndexStats Index(FixtureRepo repo, RepoctxConfig config, bool full = false)
    {
        RepoLayout layout = RepoLayout.For(repo.Root);
        Directory.CreateDirectory(layout.IndexDirectory);
        return new Indexer(layout, config, "test").Run(full);
    }

    private static List<string> IndexedPaths(FixtureRepo repo)
    {
        RepoLayout layout = RepoLayout.For(repo.Root);
        var paths = new List<string>();
        // Pooling would keep a handle on the DB file after dispose, which makes
        // the byte-level marker assertion fail on Windows.
        using var conn = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM files ORDER BY path";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    [Fact]
    public void Index_ExcludesNegativeCases_AndSensitiveMarkers()
    {
        using var repo = new FixtureRepo("sample-ts");
        Index(repo, ScanAll(), full: true);

        List<string> paths = IndexedPaths(repo);
        Assert.DoesNotContain(".env", paths);
        Assert.DoesNotContain("big.generated.ts", paths);
        Assert.DoesNotContain("logo.png", paths);
        Assert.DoesNotContain(paths, p => p.Contains("node_modules"));
        Assert.Contains("src/auth/login.ts", paths);

        // The sensitive marker string must not appear anywhere in the DB.
        RepoLayout layout = RepoLayout.For(repo.Root);
        byte[] db = File.ReadAllBytes(layout.DatabasePath);
        Assert.False(ContainsSequence(db, "SECRET_MARKER_DO_NOT_INDEX"u8),
            "sensitive marker leaked into the index database");
    }

    [Fact]
    public void Index_IsIncremental_OnAddChangeDelete()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoctxConfig config = ScanAll();

        IndexStats first = Index(repo, config, full: true);
        Assert.True(first.Added > 0);
        Assert.Equal(first.Added, first.FilesParsed);
        Assert.True(first.BytesRead > 0);
        Assert.True(first.GraphFilesAnalyzed > 0);
        Assert.Equal(first.TotalEdges, first.EdgesRecomputed);

        IndexStats noChange = Index(repo, config);
        Assert.Equal(0, noChange.Added);
        Assert.Equal(0, noChange.Changed);
        Assert.Equal(0, noChange.Deleted);
        Assert.Equal(first.TotalFiles, noChange.Unchanged);
        Assert.Equal(0, noChange.FilesParsed);
        Assert.True(noChange.BytesRead > 0, "hashing and graph analysis perform local reads");

        repo.Write("src/newmod.ts", "export function brandNew() {}\n");
        repo.Write("src/auth/login.ts", File.ReadAllText(repo.PathOf("src/auth/login.ts")) + "\n// changed\n");
        repo.Delete("src/middleware.ts");

        IndexStats delta = Index(repo, config);
        Assert.Equal(1, delta.Added);
        Assert.Equal(1, delta.Changed);
        Assert.Equal(1, delta.Deleted);
        Assert.Equal(2, delta.FilesParsed);

        List<string> paths = IndexedPaths(repo);
        Assert.Contains("src/newmod.ts", paths);
        Assert.DoesNotContain("src/middleware.ts", paths);
    }

    [Fact]
    public void Index_IsDeterministic_AcrossRuns()
    {
        using var repoA = new FixtureRepo("sample-ts");
        using var repoB = new FixtureRepo("sample-ts");
        Index(repoA, ScanAll(), full: true);
        Index(repoB, ScanAll(), full: true);

        using var storeA = IndexStore.Open(RepoLayout.For(repoA.Root).DatabasePath);
        using var storeB = IndexStore.Open(RepoLayout.For(repoB.Root).DatabasePath);
        IReadOnlyList<SearchHit> a = storeA.Search("\"login\"", 10);
        IReadOnlyList<SearchHit> b = storeB.Search("\"login\"", 10);

        Assert.Equal(a.Select(h => h.Path), b.Select(h => h.Path));
        Assert.Equal(a.Select(h => h.Score), b.Select(h => h.Score));
    }

    [Fact]
    public void Index_RebuildsWhenConfigChanges()
    {
        using var repo = new FixtureRepo("sample-ts");
        Index(repo, RepoctxConfig.CreateDefault(), full: true);
        int defaultCount = IndexedPaths(repo).Count;

        IndexStats rebuilt = Index(repo, ScanAll());
        Assert.True(rebuilt.FullRebuild);
        Assert.NotEqual(defaultCount, IndexedPaths(repo).Count);
    }

    [Fact]
    public void Index_DoesNotRebuildForLiveRankingOnlyChanges()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoctxConfig initial = ScanAll();
        Index(repo, initial, full: true);

        RepoctxConfig reranked = initial with
        {
            Ranking = initial.Ranking with
            {
                Weights = initial.Ranking.Weights with { Fts = 0.9 },
            },
        };

        IndexStats result = Index(repo, reranked);

        Assert.False(result.FullRebuild);
        Assert.Equal(result.TotalFiles, result.Unchanged);
    }

    [Fact]
    public void Index_MissingProducerMetadataForcesRebuildAndRepairsDerivedRows()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoctxConfig config = ScanAll();
        Index(repo, config, full: true);
        string database = RepoLayout.For(repo.Root).DatabasePath;

        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE symbols SET signature = 'tampered';" +
                "DELETE FROM meta WHERE key = $producer;";
            command.Parameters.AddWithValue("$producer", MetaKeys.AnalysisProducerVersion);
            command.ExecuteNonQuery();
        }

        IndexStats rebuilt = Index(repo, config);

        Assert.True(rebuilt.FullRebuild);
        using IndexStore store = IndexStore.Open(database);
        Assert.DoesNotContain(
            store.GetFiles().SelectMany(file => store.GetSymbols(file.Id)),
            symbol => symbol.Signature == "tampered");
    }

    [Fact]
    public void SearchCapsPerFileBeforeGlobalLimit()
    {
        using var repo = new FixtureRepo("sample-ts");
        string repetitive = string.Join(
            '\n',
            Enumerable.Range(1, 500).Select(i => $"# Repeated {i}\nneedle"));
        repo.Write("docs/repetitive.md", repetitive);
        repo.Write("docs/useful.md", "# Useful\nneedle target");
        Index(repo, ScanAll(), full: true);

        using IndexStore store = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath);
        IReadOnlyList<SearchHit> evidence =
            store.SearchEvidence("\"needle\"", perFileCap: 2, globalCap: 10);
        IReadOnlyList<SearchHit> publicResults = store.Search("\"needle\"", top: 2);

        Assert.Contains(evidence, hit => hit.Path == "docs/useful.md");
        Assert.True(evidence.Count(hit => hit.Path == "docs/repetitive.md") <= 2);
        Assert.Contains(publicResults, hit => hit.Path == "docs/useful.md");
    }

    [Fact]
    public void ContextEvidenceChannels_DoNotDoubleCountSymbolChunks()
    {
        using var repo = new FixtureRepo("sample-ts");
        Index(repo, ScanAll(), full: true);

        using IndexStore store = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath);
        IReadOnlyList<SearchHit> general =
            store.SearchEvidence("\"login\"", perFileCap: 8, globalCap: 100);
        IReadOnlyList<SearchHit> symbols =
            store.SearchEvidence("\"login\"", perFileCap: 8, globalCap: 100, symbolsOnly: true);

        Assert.NotEmpty(general);
        Assert.NotEmpty(symbols);
        Assert.DoesNotContain(general, hit => hit.ChunkKind == "symbol");
        Assert.All(symbols, hit => Assert.Equal("symbol", hit.ChunkKind));
    }

    [Fact]
    public void SearchOrdering_DoesNotDependOnSQLiteRowIds()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoctxConfig config = ScanAll();
        Index(repo, config, full: true);
        string path = repo.PathOf("src/auth/session.ts");
        string original = File.ReadAllText(path);

        IReadOnlyList<string> before;
        using (IndexStore store = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath))
        {
            before = Describe(store.SearchEvidence("\"session\"", 8, 100));
        }

        File.WriteAllText(path, original + "\n// force delete and reinsert\n");
        Index(repo, config);
        File.WriteAllText(path, original);
        Index(repo, config);

        using IndexStore reopened = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath);
        Assert.Equal(before, Describe(reopened.SearchEvidence("\"session\"", 8, 100)));

        static IReadOnlyList<string> Describe(IReadOnlyList<SearchHit> hits) =>
            [.. hits.Select(hit =>
                $"{hit.Path}|{hit.ChunkKind}|{hit.StartLine}|{hit.EndLine}|{hit.Heading}|{hit.Score:R}")];
    }

    [Fact]
    public void Index_StripsUtf8Bom_FromChunkContent()
    {
        using var repo = new FixtureRepo("sample-ts");
        byte[] content = [0xEF, 0xBB, 0xBF, .. "# Bom Heading\n\nSome text.\n"u8];
        File.WriteAllBytes(repo.PathOf("docs-bom.md"), content);
        Index(repo, ScanAll(), full: true);

        // With the BOM stripped, the first line is recognised as a markdown
        // heading and surfaces as the chunk heading.
        using var store = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath);
        IReadOnlyList<SearchHit> hits = store.Search("\"bom\"", 10);
        Assert.Contains(hits, h => h.Path == "docs-bom.md" && h.Heading == "Bom Heading");
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;
}
