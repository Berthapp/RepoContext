using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Context;

/// <summary>Tests for the M6 token-frugal context protocol (ADR 0010).</summary>
public class TokenFrugalContextTests
{
    private static ContextResult Run(IndexStore store, string query, ContextOptions? options = null) =>
        new ContextEngine(store, RepoctxConfig.CreateDefault())
            .Run(query, options ?? new ContextOptions { Top = 20 });

    [Fact]
    public void PathsDetail_ChargesTheRealFullFileTokenCount()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "change the login logic");
        ContextItem login = result.Items.Single(i => i.Path == "src/auth/login.ts");

        FileRow row = store.FindFile("src/auth/login.ts")!.Value;
        Assert.Equal(row.TokenCount, login.EstimatedTokens);
        Assert.Equal(Hashes.Short(row.ContentHash), login.Hash);
        Assert.Null(login.FileTokens); // no content included, nothing to contrast
        Assert.Equal(Hashes.ShortLength, result.State.Length);
    }

    [Fact]
    public void OutlineDetail_CarriesSymbolSkeletons_AtOutlineCost()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "change the login logic",
            new ContextOptions { Top = 5, Detail = ContextDetail.Outline });
        ContextItem login = result.Items.Single(i => i.Path == "src/auth/login.ts");

        Assert.NotNull(login.Symbols);
        Assert.Contains(login.Symbols, s => s.Name == "loginUser");
        Assert.Equal(login.FileTokens, store.FindFile("src/auth/login.ts")!.Value.TokenCount);
        Assert.True(login.EstimatedTokens > 0);
    }

    [Fact]
    public void KnownHash_TurnsItemsIntoZeroCostUnchangedMarkers()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult first = Run(store, "change the login logic");
        ContextItem login = first.Items.Single(i => i.Path == "src/auth/login.ts");
        Assert.False(login.Unchanged);

        ContextResult second = Run(store, "change the login logic", new ContextOptions
        {
            Top = 20,
            Detail = ContextDetail.Slices,
            Known = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/auth/login.ts"] = login.Hash,
            },
        });

        ContextItem marker = second.Items.Single(i => i.Path == "src/auth/login.ts");
        Assert.True(marker.Unchanged);
        Assert.Equal(0, marker.EstimatedTokens);
        Assert.Null(marker.Snippet);
        // Ranking and explanation are unaffected; only the payload is dropped.
        Assert.NotEmpty(marker.Reasons);
    }

    [Fact]
    public void KnownHash_ThatNoLongerMatches_ReturnsTheFullItem()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "change the login logic", new ContextOptions
        {
            Top = 20,
            Known = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/auth/login.ts"] = "deadbeef1234",
            },
        });

        Assert.False(result.Items.Single(i => i.Path == "src/auth/login.ts").Unchanged);
    }

    [Fact]
    public void SliceBudget_IsPackedNotTruncated_AndRespected()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        const int budget = 300;
        ContextResult result = Run(store, "change the login logic",
            new ContextOptions { Top = 20, Detail = ContextDetail.Slices, BudgetTokens = budget });

        Assert.NotEmpty(result.Items);
        Assert.True(result.EstimatedTokens <= budget || result.Items.Count == 1,
            $"bundle cost {result.EstimatedTokens} must fit the {budget}-token budget");
    }

    [Fact]
    public void Omitted_CountsScoredCandidatesBeyondTheLimits()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult all = Run(store, "change the login logic", new ContextOptions { Top = 20 });
        ContextResult one = Run(store, "change the login logic", new ContextOptions { Top = 1 });

        Assert.Equal(0, all.Omitted);
        Assert.Equal(all.Items.Count - 1, one.Omitted);
    }

    [Fact]
    public void StateHash_IsStableForTheSameIndex_AndMovesWhenContentChanges()
    {
        using var repo = new FixtureRepo("sample-ts");
        string original;
        using (IndexStore store = IndexHelper.BuildIndex(repo))
        {
            ContextResult a = Run(store, "login");
            ContextResult b = Run(store, "login");
            Assert.Equal(a.State, b.State);
            original = a.State;
        }

        repo.Write("src/auth/extra.ts", "export const extra = 1;\n");
        using (IndexStore reindexed = IndexHelper.BuildIndex(repo))
        {
            Assert.NotEqual(original, Run(reindexed, "login").State);
        }

        // Re-indexing the identical tree lands on the identical state.
        using IndexStore again = IndexHelper.BuildIndex(repo);
        string after = Run(again, "login").State;
        repo.Delete("src/auth/extra.ts");
        using IndexStore reverted = IndexHelper.BuildIndex(repo);
        Assert.Equal(original, Run(reverted, "login").State);
        Assert.NotEqual(original, after);
    }
}
