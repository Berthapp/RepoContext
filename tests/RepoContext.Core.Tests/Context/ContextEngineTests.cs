using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Context;

public class ContextEngineTests
{
    private static ContextResult Run(IndexStore store, string query, ContextOptions? options = null) =>
        new ContextEngine(store, RepoctxConfig.CreateDefault())
            .Run(query, options ?? new ContextOptions { Top = 20 });

    [Fact]
    public void RankingScenario_LoginBeforeSessionBeforeMiddleware()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "change the login logic");
        List<string> paths = result.Items.Select(i => i.Path).ToList();

        int login = paths.IndexOf("src/auth/login.ts");
        int session = paths.IndexOf("src/auth/session.ts");
        int middleware = paths.IndexOf("src/middleware.ts");

        Assert.Equal("src/auth/login.ts", paths[0]);
        Assert.True(login >= 0 && session >= 0 && middleware >= 0, "all three files present");
        Assert.True(login < session, "login before session");
        Assert.True(session < middleware, "session before middleware");

        // The linked test file is included.
        Assert.Contains("src/auth/__tests__/login.test.ts", paths);
        // A sensitive/negative file never appears.
        Assert.DoesNotContain(paths, p => p.Contains(".env") || p.Contains("big.generated"));
    }

    [Fact]
    public void EveryItem_HasReasons()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "change the login logic");
        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, i => Assert.NotEmpty(i.Reasons));
    }

    [Fact]
    public void Budget_ReducesFileCount()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult full = Run(store, "change the login logic", new ContextOptions { Top = 20 });
        ContextResult budgeted = Run(store, "change the login logic",
            new ContextOptions { Top = 20, BudgetTokens = 300 });

        Assert.True(budgeted.Items.Count < full.Items.Count);
        Assert.True(budgeted.EstimatedTokens <= 300 || budgeted.Items.Count == 1);
    }

    [Fact]
    public void IsDeterministic_AcrossRuns()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult a = Run(store, "change the login logic");
        ContextResult b = Run(store, "change the login logic");

        Assert.Equal(a.Items.Select(i => i.Path), b.Items.Select(i => i.Path));
        Assert.Equal(a.Items.Select(i => i.Score), b.Items.Select(i => i.Score));
    }

    [Fact]
    public void VendorPenalty_HitsVendorDirectory_NotSimilarlyNamedFiles()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("src/vendorPricing.ts", "export function vendorPricing() { return 1; }\n");
        repo.Write("vendor/pricing.ts", "export function vendorPricing() { return 2; }\n");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "vendorPricing");

        ContextItem source = result.Items.Single(i => i.Path == "src/vendorPricing.ts");
        Assert.DoesNotContain("penalty:vendor-or-generated", source.Reasons);

        ContextItem vendored = result.Items.Single(i => i.Path == "vendor/pricing.ts");
        Assert.Contains("penalty:vendor-or-generated", vendored.Reasons);
    }

    [Fact]
    public void Snippets_AreIncludedWhenRequested()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "login",
            new ContextOptions { Top = 3, Snippets = true });

        Assert.Contains(result.Items, i => !string.IsNullOrEmpty(i.Snippet));
    }
}
