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
    public void GraphReasons_AreCappedWithASummary()
    {
        using var repo = new FixtureRepo("sample-ts");
        for (int i = 1; i <= 5; i++)
        {
            repo.Write($"src/gadget/feature{i}.ts",
                $"import {{ shared }} from \"./shared\";\nexport function gadgetFeature{i}() {{ return shared(); }}\n");
        }

        repo.Write("src/gadget/shared.ts", "export function shared() { return 1; }\n");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "gadget feature");
        ContextItem hub = result.Items.Single(i => i.Path == "src/gadget/shared.ts");

        // Five importers link to the hub; only two full-path graph reasons
        // survive, the rest fold into one "graph:+N" summary placed right
        // after them.
        var reasons = hub.Reasons.ToList();
        Assert.Equal(2, reasons.Count(IsGraphReason));
        Assert.Equal("graph:+3", Assert.Single(reasons, r => r.StartsWith("graph:+", StringComparison.Ordinal)));
        Assert.Equal(reasons.FindLastIndex(IsGraphReason) + 1, reasons.IndexOf("graph:+3"));
    }

    [Fact]
    public void GraphReasonCap_NeverDropsThePenaltyReason()
    {
        using var repo = new FixtureRepo("sample-ts");
        for (int i = 1; i <= 4; i++)
        {
            repo.Write($"vendor/widget/plugin{i}.ts",
                $"import {{ widgetCore }} from \"./core\";\nexport function widgetPlugin{i}() {{ return widgetCore(); }}\n");
        }

        repo.Write("vendor/widget/core.ts", "export function widgetCore() { return 1; }\n");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ContextResult result = Run(store, "widget plugin");
        ContextItem hub = result.Items.Single(i => i.Path == "vendor/widget/core.ts");

        // The penalty explains the low score; the cap must only ever fold
        // graph reasons, never evidence of a different kind.
        Assert.Contains("penalty:vendor-or-generated", hub.Reasons);
        Assert.Contains("graph:+2", hub.Reasons);
        Assert.Equal(2, hub.Reasons.Count(IsGraphReason));
    }

    private static bool IsGraphReason(string reason) =>
        reason.StartsWith("imports:", StringComparison.Ordinal)
        || reason.StartsWith("imported-by:", StringComparison.Ordinal)
        || reason.StartsWith("tested-by:", StringComparison.Ordinal)
        || reason.StartsWith("test-of:", StringComparison.Ordinal);

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
