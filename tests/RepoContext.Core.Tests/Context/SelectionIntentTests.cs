using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Query;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Context;

public sealed class SelectionIntentTests
{
    private const string Anchor = "src/feature/checkout.ts";
    private const string Dependency = "src/feature/price.ts";
    private const string Dependent = "src/api/order.ts";
    private const string Test = "src/feature/__tests__/checkout.test.ts";
    private const string Document = "docs/checkout.md";

    private static FixtureRepo Fixture()
    {
        var repo = new FixtureRepo("sample-ts");
        repo.Write(Anchor, "import { price } from './price';\nexport function checkout() {\n  const total = price();\n  return total > 0 ? total : 0;\n}\n");
        repo.Write(Dependency, "export function price() { return 42; }\n");
        repo.Write(Dependent, "import { checkout } from '../feature/checkout';\nexport function order() { return checkout(); }\n");
        repo.Write(Test, "import { checkout } from '../checkout';\nexport function verifiesTotal() { return checkout() === 42; }\n");
        repo.Write(Document, "# Checkout behavior\nSee [implementation](../src/feature/checkout.ts).\nThe total must be nonnegative.\n");
        return repo;
    }

    private static ContextEngine Engine(IndexStore store) => new(store,
        RepoctxConfig.CreateDefault() with { Include = ["."] });

    [Theory]
    [InlineData(ContextIntent.Fix, Test, "test")]
    [InlineData(ContextIntent.Explain, Dependency, "dependency")]
    [InlineData(ContextIntent.Review, Dependent, "dependent")]
    public void Intent_PromotesDirectCompanions_AndPreservesAnchorSpans(
        ContextIntent intent, string companion, string role)
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        ContextResult original = Engine(store).Run(Anchor, options);
        ContextResult result = Engine(store).Run(Anchor, options with { Intent = intent });
        Assert.Equal(Anchor, result.Items[0].Path);
        Assert.Equal(companion, result.Items[1].Path);
        Assert.Contains($"intent:{intent.ToString().ToLowerInvariant()}:{role}", result.Items[1].Reasons);
        Assert.Equal(original.Items.Single(i => i.Path == Anchor).Spans, result.Items[0].Spans);
        Assert.NotEqual(original.EvidenceId, result.EvidenceId);
        Assert.Equal(result.EvidenceId, Engine(store).Run(Anchor, options with { Intent = intent }).EvidenceId);
        if (intent == ContextIntent.Explain) Assert.Equal(Document, result.Items[2].Path);
        if (intent == ContextIntent.Review) Assert.Equal(Test, result.Items[2].Path);
    }

    [Fact]
    public void Explain_ListsRankingLoss_WithoutChangingUnbudgetedEvidence()
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        var options = new ContextOptions { Top = 1, Detail = ContextDetail.Slices };
        ContextResult original = Engine(store).Run(Anchor, options);
        ContextResult result = Engine(store).Run(Anchor, options with { Explain = true });
        Assert.Null(original.Selection);
        Assert.Null(original.Intent);
        Assert.Equal(original.EvidenceId, result.EvidenceId);
        Assert.Equal(original.Items.Select(i => i.Path), result.Items.Select(i => i.Path));
        SelectionDiagnostics diagnostics = Assert.IsType<SelectionDiagnostics>(result.Selection);
        Assert.Equal(result.Omitted, diagnostics.Omitted);
        Assert.Equal(result.TotalCandidates - result.Omissions.NonpositiveScore, diagnostics.Eligible);
        Assert.NotEmpty(diagnostics.Samples);
        Assert.Equal(diagnostics.Omitted, diagnostics.Samples.Count + diagnostics.Unlisted);
        Assert.All(diagnostics.Samples, sample =>
        {
            Assert.Equal("top", sample.Reason);
            Assert.True(sample.Rank > 1);
            Assert.DoesNotContain(result.Items, i => i.Path == sample.Path);
            Assert.Equal(new SelectionLookup("outline", sample.Path), sample.NextLookup);
        });
    }

    [Theory]
    [InlineData(ContextIntent.Fix)]
    [InlineData(ContextIntent.Explain)]
    [InlineData(ContextIntent.Review)]
    public void BroadQueries_DoNotGuessAnImplementationTarget(ContextIntent intent)
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        var options = new ContextOptions { Detail = ContextDetail.Slices };
        ContextResult original = Engine(store).Run("change checkout behavior", options);
        ContextResult result = Engine(store).Run("change checkout behavior", options with { Intent = intent });
        Assert.Equal(original.Items.Select(i => i.Path), result.Items.Select(i => i.Path));
        Assert.All(result.Items, item => Assert.DoesNotContain(item.Reasons,
            reason => reason.StartsWith("intent:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true, false, "budget_tokens")]
    [InlineData(false, true, "projected_read_budget")]
    [InlineData(true, true, "budget_tokens")]
    public void Explain_ClassifiesNonResponseBudgets_WithConsistentPrecedence(bool legacy, bool read, string reason)
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        ContextResult result = Engine(store).Run(Anchor, new ContextOptions
        {
            Explain = true, Top = 20,
            BudgetTokens = legacy ? 1 : null,
            ProjectedReadBudgetTokens = read ? 1 : null,
        });
        Assert.Empty(result.Items);
        Assert.NotEmpty(result.Selection!.Samples);
        Assert.All(result.Selection.Samples, s => Assert.Equal(reason, s.Reason));
        Assert.Equal(result.Omitted, legacy ? result.Omissions.BudgetTokens : result.Omissions.ProjectedReadBudget);
    }

    [Fact]
    public void IntentAndDiagnostics_RespectScope_AndKeepGlobalSelectionRanksAcrossReuse()
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        var options = new ContextOptions
        {
            Intent = ContextIntent.Review, Explain = true, Top = 1,
            Scope = PathScope.From(["src/feature"]),
        };
        ContextResult first = Engine(store).Run(Anchor, options);
        ContextResult next = Engine(store).Run(Anchor, options with
        {
            Known = new Dictionary<string, string> { [Anchor] = first.Items[0].Hash },
        });
        Assert.Contains(next.Reused, r => r.Path == Anchor);
        Assert.Single(next.Items);
        Assert.DoesNotContain(next.Selection!.Samples, s => s.Path == Anchor);
        Assert.Equal(options.Scope!.Patterns, next.Selection.Scope);
        Assert.All(next.Items.Select(i => i.Path).Concat(next.Selection.Samples.Select(s => s.Path)),
            path => Assert.StartsWith("src/feature/", path, StringComparison.Ordinal));
        Assert.All(next.Selection.Samples, s => Assert.True(s.Rank >= 3));
    }

    [Fact]
    public void Diagnostics_AreBounded_AndEmptyQueriesDoNotInventEvidence()
    {
        using var repo = Fixture();
        for (int i = 0; i < 15; i++) repo.Write($"src/many/needle{i}.ts", $"export const needle{i} = {i};\n");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        ContextResult result = Engine(store).Run("needle", new ContextOptions { Explain = true, Top = 1 });
        Assert.Equal(8, result.Selection!.Samples.Count);
        Assert.True(result.Selection.Unlisted > 0);
        ContextResult empty = Engine(store).Run("zzzzzzmissing", new ContextOptions { Explain = true, Intent = ContextIntent.Fix });
        Assert.Empty(empty.Items);
        Assert.Empty(empty.Selection!.Samples);
        Assert.Equal(0, empty.Selection.Candidates);
    }

    [Fact]
    public void ResponseOmissions_DistinguishSkippedHighRanks_FromTheTopCutoff()
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        ContextResult result = Engine(store).Run(Anchor, new ContextOptions
        {
            Top = 2, Explain = true, ResponseBudgetTokens = 400,
        }, new ExpensiveAnchorCost());
        Assert.Null(result.Shortfall);
        Assert.Equal(2, result.Items.Count);
        SelectionOmission skipped = Assert.Single(result.Selection!.Samples, s => s.Path == Anchor);
        Assert.Equal(1, skipped.Rank);
        Assert.Equal("response_budget", skipped.Reason);
        Assert.True(result.Omissions.ResponseBudget > 0);
        Assert.All(result.Selection.Samples.Where(s => s.Path != Anchor), s => Assert.Equal("top", s.Reason));
    }

    private sealed class ExpensiveAnchorCost : IResponseCostModel
    {
        public string Surface => "test";
        public int Measure(ContextResult result) => 50 + result.Items.Sum(i => i.Path == Anchor ? 2000 : 100);
    }

    [Fact]
    public void ExactSpanReceipts_AreReusableAcrossIntentsAndDiagnostics()
    {
        using var repo = Fixture();
        using IndexStore store = IndexHelper.BuildIndex(repo);
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        ContextResult first = Engine(store).Run(Anchor, options with { Intent = ContextIntent.Fix });
        string receipt = first.Items[0].Spans![0].Receipt;
        ContextResult next = Engine(store).Run(Anchor, options with
        {
            Intent = ContextIntent.Review, Explain = true, Seen = [receipt],
        });
        Assert.Contains(next.Reused, unit => unit.Receipt == receipt);
        Assert.DoesNotContain(next.Items.SelectMany(item => item.Spans ?? []), span => span.Receipt == receipt);
    }

    [Fact]
    public void AmbiguousSymbolsAndPathPrefixes_DoNotChooseAnArbitraryTarget()
    {
        using var repo = Fixture();
        repo.Write("src/alternate.ts", "export function checkout() { return 0; }\n");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        foreach (string query in new[] { "checkout", Anchor + ".backup", "other/" + Anchor })
        {
            ContextResult result = Engine(store).Run(query, new ContextOptions { Intent = ContextIntent.Fix });
            Assert.All(result.Items, item => Assert.DoesNotContain(item.Reasons,
                reason => reason.StartsWith("intent:", StringComparison.Ordinal)));
        }
        ContextResult exact = Engine(store).Run("review `./" + Anchor.Replace('/', '\\') + "`",
            new ContextOptions { Intent = ContextIntent.Review, Top = 2 });
        Assert.Equal(Dependent, exact.Items[1].Path);
        Assert.Contains("intent:review:dependent", exact.Items[1].Reasons);
    }
}
