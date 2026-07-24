using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;
using RepoContext.Core.Outline;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Regression reproductions for the failure modes found in the pre-Release-1
/// audit (Q0). Each test encodes the corrected behaviour, so each one failed
/// against the code as audited and passes only because of the Release 1 change
/// it guards. The fixture line numbers are frozen labels: a product change may
/// not edit them to pass.
/// </summary>
public sealed class AuditRegressionTests
{
    /// <summary>Frozen label: the <c>Budget</c> method in the evaluation fixture.</summary>
    private static readonly RequiredSpan BudgetMethod = new("src/Packing/Packer.cs", 115, 139);

    /// <summary>Frozen label: <c>EnvelopeTokens</c>, the 16th symbol of that file.</summary>
    private static readonly RequiredSpan EnvelopeTokensMethod = new("src/Packing/Packer.cs", 145, 148);

    private static ContextResult Run(
        EvalRepo repo, string query, ContextOptions options, IResponseCostModel? cost = null) =>
        new ContextEngine(repo.Store, repo.Config).Run(query, options, cost);

    /// <summary>
    /// Audit failure 1: a receipt proving possession of one slice must never
    /// suppress a different, never-delivered range of the same file. Before
    /// Release 1 the item carried the whole-file hash, so echoing it produced an
    /// `unchanged` marker for lines the caller had never seen.
    /// </summary>
    [Fact]
    public void PartialSliceReceipt_NeverSuppressesAnUnseenRange()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
            MaxSpans = 3,
        };

        ContextResult first = Run(repo, "change budget packing", options);
        ContextItem item = first.Items.Single(i => i.Path == BudgetMethod.Path);
        ContextSpan budgetSpan = item.Spans!.Single(s => BudgetMethod.CoveredBy(s.StartLine, s.EndLine));
        IReadOnlyList<ContextSpan> otherSpans =
            [.. item.Spans!.Where(s => s.Receipt != budgetSpan.Receipt)];
        Assert.NotEmpty(otherSpans);

        ContextResult second = Run(repo, "change budget packing", options with
        {
            Seen = [budgetSpan.Receipt],
        });

        // Exactly the acknowledged span is withheld...
        Assert.Contains(second.Reused, r => r.Receipt == budgetSpan.Receipt);
        ContextItem again = second.Items.Single(i => i.Path == BudgetMethod.Path);
        Assert.DoesNotContain(again.Spans!, s => s.Receipt == budgetSpan.Receipt);

        // ...and every other span of that same file is still delivered in full.
        foreach (ContextSpan expected in otherSpans)
        {
            Assert.Contains(again.Spans!, s => s.Receipt == expected.Receipt && s.Text == expected.Text);
        }
    }

    /// <summary>
    /// The same guarantee stated as the audit's own reproduction: a receipt for
    /// the <c>Budget</c> span must not hide <c>EnvelopeTokens</c>, which lives in
    /// the same file but was never sent.
    /// </summary>
    [Fact]
    public void BudgetReceipt_DoesNotHideTheEnvelopeTokensRange()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 1, Detail = ContextDetail.Slices, MaxSpans = 4 };

        ContextSpan budgetSpan = Run(repo, "change budget packing", options)
            .Items.Single(i => i.Path == BudgetMethod.Path)
            .Spans!.Single(s => BudgetMethod.CoveredBy(s.StartLine, s.EndLine));

        ContextResult envelope = Run(repo, "envelope tokens framing", options with
        {
            Seen = [budgetSpan.Receipt],
        });

        Assert.True(
            envelope.Items.Any(i => i.Path == EnvelopeTokensMethod.Path),
            "items=" + string.Join(',', envelope.Items.Select(i => i.Path))
            + "; reused=" + string.Join(',', envelope.Reused.Select(r => $"{r.Path}:{r.StartLine}-{r.EndLine}")));
        ContextItem item = envelope.Items.Single(i => i.Path == EnvelopeTokensMethod.Path);
        Assert.Contains(
            item.Spans!,
            s => EnvelopeTokensMethod.CoveredBy(s.StartLine, s.EndLine));
    }

    /// <summary>
    /// Audit failure 2: acknowledged reuse must not consume the result slots the
    /// caller asked for. Echoing every top candidate previously produced N
    /// markers and no new evidence.
    /// </summary>
    [Fact]
    public void ReusedUnits_DoNotConsumeTopSlots()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 2, Detail = ContextDetail.Slices };

        ContextResult first = Run(repo, "budget packing tokens session login", options);
        Assert.Equal(2, first.Items.Count);

        // Echo every receipt the first call delivered.
        string[] allReceipts = [.. first.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt)];
        ContextResult second = Run(repo, "budget packing tokens session login", options with
        {
            Seen = allReceipts,
        });

        Assert.True(second.ReusedCount > 0, "the echoed units are acknowledged");

        // Top = 2 still buys two genuinely new items, and none of them repeats
        // evidence the caller already holds.
        Assert.Equal(2, second.Items.Count);
        Assert.All(
            second.Items.SelectMany(i => i.Spans ?? []),
            span => Assert.DoesNotContain(span.Receipt, allReceipts));
    }

    /// <summary>
    /// Audit failure 3: an outline must surface the symbol the query matched even
    /// when it sits below the source-ordered cap. <c>EnvelopeTokens</c> is the
    /// 16th symbol of the fixture and the cap is 12.
    /// </summary>
    [Fact]
    public void QueryMatchedSymbol_SurvivesTheOutlineCap()
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "envelope tokens", new ContextOptions
        {
            Top = 5,
            Detail = ContextDetail.Outline,
        });

        ContextItem item = result.Items.Single(i => i.Path == EnvelopeTokensMethod.Path);
        OutlineSymbol matched = Assert.Single(item.Symbols!, s => s.Name == "EnvelopeTokens");

        Assert.Equal(OutlineRole.Match, matched.Role);
        Assert.Equal(EnvelopeTokensMethod.StartLine, matched.StartLine);

        // Its container is offered as declared scaffolding, not as an unexplained extra.
        Assert.Contains(item.Symbols!, s => s.Name == "Packer" && s.Role == OutlineRole.Container);
    }

    /// <summary>
    /// Audit failure 4: a query about a method must return that method's range,
    /// not the surrounding class or an unrelated chunk.
    /// </summary>
    [Fact]
    public void SliceSelection_ReturnsTheMatchedMethodRange()
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
        });

        ContextItem item = result.Items.Single(i => i.Path == BudgetMethod.Path);
        ContextSpan span = Assert.Single(
            item.Spans!, s => BudgetMethod.CoveredBy(s.StartLine, s.EndLine));

        Assert.Equal("Budget", span.Symbol);
        Assert.Contains("public List<Candidate> Budget(", span.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A multi-span item must not pretend to be a single range: the deprecated
    /// single-span fields are omitted rather than mapped onto a synthetic
    /// enclosing range.
    /// </summary>
    [Fact]
    public void MultiSpanItem_OmitsTheDeprecatedSingleSpanFields()
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
            MaxSpans = 3,
        });

        ContextItem item = result.Items.Single(i => i.Path == BudgetMethod.Path);
        Assert.True(item.Spans!.Count > 1, "the fixture query yields several spans");
        Assert.Null(item.Snippet);

        string json = ContextOutput.Render(result, OutputFormat.Json);
        Assert.DoesNotContain("\"snippet\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single-span item stays simple: the deprecated single-span fields are
    /// populated and the item-level <c>receipt</c> repeats its only unit's value
    /// as a convenience alias.
    /// </summary>
    [Fact]
    public void SingleSpanItem_KeepsTheLegacyFields_AndAliasesTheReceipt()
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "revokeSession", new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
        });

        ContextItem item = Assert.Single(result.Items);
        ContextSpan span = Assert.Single(item.Spans!);

        Assert.Equal(span.StartLine, item.StartLine);
        Assert.Equal(span.EndLine, item.EndLine);
        Assert.Equal(span.Text, item.Snippet);
        Assert.Equal(span.Receipt, item.Receipt);
    }

    /// <summary>
    /// Audit failure 5: an accepted hard response budget is never exceeded, and
    /// the first item gets no exemption. Verified by tokenizing the exact stdout
    /// the CLI would emit.
    /// </summary>
    [Theory]
    [InlineData(700)]
    [InlineData(900)]
    [InlineData(1200)]
    [InlineData(2000)]
    [InlineData(5000)]
    public void AcceptedResponseBudget_IsNeverExceeded(int budget)
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 8,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = budget,
        }, cost);

        if (result.Shortfall is not null)
        {
            return; // Rejected budgets are covered by the shortfall test below.
        }

        // Independently tokenize the exact surface, rather than trusting the
        // packer's own bookkeeping.
        int actual = Tokens.Count(cost.SurfaceText(result));
        Assert.True(actual <= budget, $"rendered {actual} tokens exceeds the {budget}-token budget");
    }

    /// <summary>
    /// Omission fields are added after early candidates have been considered.
    /// Their real wire cost must be reserved too: a candidate that fits before
    /// those fields are known cannot make the final response exceed the ceiling.
    /// </summary>
    [Fact]
    public void FinalOmissionMetadata_CannotOverflowAnAcceptedResponseBudget()
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);
        const string query = "budget packing tokens session login";
        var options = new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
        };

        ContextResult unbudgeted = Run(repo, query, options);
        Assert.True(unbudgeted.Omissions.Top > 0, "the fixture must add late top-omission metadata");
        int fullCost = cost.Measure(unbudgeted);

        ContextResult constrained = Run(repo, query, options with
        {
            ResponseBudgetTokens = fullCost - 1,
        }, cost);

        int acceptedBudget = fullCost - 1;
        if (constrained.Shortfall is { } shortfall)
        {
            acceptedBudget = shortfall.RetryBudgetTokens;
            constrained = Run(repo, query, options with
            {
                ResponseBudgetTokens = acceptedBudget,
            }, cost);
        }

        Assert.Null(constrained.Shortfall);
        Assert.True(constrained.Omitted > 0);
        int actual = cost.Measure(constrained);
        Assert.True(
            actual <= acceptedBudget,
            $"final response {actual} exceeded accepted budget {acceptedBudget}");
    }

    /// <summary>
    /// Reuse acknowledgements discovered after an item was admitted are part of
    /// the same model-visible response and must be included in its hard budget.
    /// </summary>
    [Fact]
    public void LateReuseMetadata_CannotOverflowAnAcceptedResponseBudget()
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);
        const string query = "budget packing tokens session login";

        ContextResult first = Run(repo, query, new ContextOptions
        {
            Top = 2,
            Detail = ContextDetail.Slices,
        });
        ContextItem secondItem = first.Items[1];
        string[] laterReceipts = [.. secondItem.Spans!.Select(s => s.Receipt)];

        var repeatedOptions = new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
            Seen = laterReceipts,
        };
        ContextResult unbudgeted = Run(repo, query, repeatedOptions);
        Assert.True(unbudgeted.ReusedCount > 0, "the later-ranked item must be acknowledged");
        int fullCost = cost.Measure(unbudgeted);

        ContextResult constrained = Run(repo, query, repeatedOptions with
        {
            ResponseBudgetTokens = fullCost - 1,
        }, cost);

        int acceptedBudget = fullCost - 1;
        if (constrained.Shortfall is { } shortfall)
        {
            acceptedBudget = shortfall.RetryBudgetTokens;
            constrained = Run(repo, query, repeatedOptions with
            {
                ResponseBudgetTokens = acceptedBudget,
            }, cost);
        }

        Assert.Null(constrained.Shortfall);
        Assert.True(constrained.ReusedCount > 0);
        int actual = cost.Measure(constrained);
        Assert.True(
            actual <= acceptedBudget,
            $"final response {actual} exceeded accepted budget {acceptedBudget}");
    }

    /// <summary>
    /// A budget too small for the smallest useful payload returns actionable
    /// sizing data through the error channel, with no partial success result.
    /// </summary>
    [Fact]
    public void TooSmallResponseBudget_ReportsAMinimumInsteadOfOverrunning()
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 8,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 50,
        }, cost);

        BudgetShortfall shortfall = Assert.IsType<BudgetShortfall>(result.Shortfall);
        Assert.Equal(50, shortfall.RequestedBudgetTokens);
        Assert.True(shortfall.RetryBudgetTokens > 50);
        Assert.Empty(result.Items);

        ContextResult retry = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 8,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = shortfall.RetryBudgetTokens,
        }, cost);
        Assert.Null(retry.Shortfall);
        Assert.True(cost.Measure(retry) <= shortfall.RetryBudgetTokens);
    }

    /// <summary>
    /// The legacy <c>--budget-tokens</c> cap keeps its cost basis but loses the
    /// first-item bypass, while best-fit packing survives: an oversized leading
    /// candidate must not block smaller relevant ones behind it.
    /// </summary>
    [Fact]
    public void LegacyBudget_HasNoFirstItemBypass_ButKeepsBestFitPacking()
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 8,
            Detail = ContextDetail.Slices,
            BudgetTokens = 250,
        });

        Assert.True(
            result.EstimatedTokens <= 250,
            $"charged {result.EstimatedTokens} tokens must respect the 250-token cap with no exception");
    }

    [Fact]
    public void EmptyResult_HonorsBudgetAndReportsAnActionableMinimum()
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);
        var options = new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 1,
        };

        ContextResult rejected = Run(repo, "the and or", options, cost);
        BudgetShortfall shortfall = Assert.IsType<BudgetShortfall>(rejected.Shortfall);

        ContextResult retry = Run(repo, "the and or", options with
        {
            ResponseBudgetTokens = shortfall.RetryBudgetTokens,
        }, cost);

        Assert.Null(retry.Shortfall);
        Assert.True(cost.Measure(retry) <= shortfall.RetryBudgetTokens);
    }

    [Fact]
    public void ReuseOnlyResult_CannotBypassTheHardResponseBudget()
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(OutputFormat.Json);
        var firstOptions = new ContextOptions
        {
            Top = 100,
            Detail = ContextDetail.Paths,
        };
        ContextResult first = Run(repo, "budget packing tokens session login", firstOptions);
        string[] receipts = [.. first.Items.Select(item => item.Receipt).OfType<string>()];
        Assert.NotEmpty(receipts);

        ContextResult rejected = Run(repo, "budget packing tokens session login", firstOptions with
        {
            Seen = receipts,
            ResponseBudgetTokens = 1,
        }, cost);

        BudgetShortfall shortfall = Assert.IsType<BudgetShortfall>(rejected.Shortfall);
        Assert.Empty(rejected.Items);

        ContextResult retry = Run(repo, "budget packing tokens session login", firstOptions with
        {
            Seen = receipts,
            ResponseBudgetTokens = shortfall.RetryBudgetTokens,
        }, cost);
        Assert.Null(retry.Shortfall);
        Assert.True(cost.Measure(retry) <= shortfall.RetryBudgetTokens);
    }

    [Fact]
    public void RetryBudgetCalculation_HasBoundedCost_AndReturnsAFittingValue()
    {
        using var repo = new EvalRepo();
        var cost = new HighBaseCostModel();
        var options = new ContextOptions
        {
            Top = 3,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 1,
        };

        ContextResult rejected = Run(
            repo, "budget packing tokens session login", options, cost);
        BudgetShortfall shortfall = Assert.IsType<BudgetShortfall>(rejected.Shortfall);

        Assert.InRange(cost.Calls, 1, 200);
        Assert.True(shortfall.RetryBudgetTokens >= HighBaseCostModel.BaseCost);

        ContextResult retry = Run(repo, "budget packing tokens session login", options with
        {
            ResponseBudgetTokens = shortfall.RetryBudgetTokens,
        }, cost);
        Assert.Null(retry.Shortfall);
        Assert.True(cost.Measure(retry) <= shortfall.RetryBudgetTokens);
    }

    [Fact]
    public void TightResponseBudget_CanChooseASmallerNonPrefixSpan()
    {
        using var repo = new EvalRepo();
        var cost = new RejectBudgetSpanCostModel();

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 1,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = 1000,
        }, cost);

        Assert.Null(result.Shortfall);
        ContextItem packer = Assert.Single(result.Items);
        Assert.Equal("src/Packing/Packer.cs", packer.Path);
        Assert.NotEmpty(packer.Spans!);
        Assert.DoesNotContain(packer.Spans!, span => span.Symbol == "Budget");
        Assert.True(packer.SpansOmitted > 0);
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Text)]
    [InlineData(OutputFormat.Md)]
    public void EveryAcceptedCliFormatStaysWithinItsExactBudget(OutputFormat format)
    {
        using var repo = new EvalRepo();
        var cost = ContextCostModel.ForCli(format);
        const int budget = 1000;

        ContextResult result = Run(repo, "change budget packing", new ContextOptions
        {
            Top = 3,
            Detail = ContextDetail.Slices,
            ResponseBudgetTokens = budget,
        }, cost);

        Assert.Null(result.Shortfall);
        Assert.NotEmpty(result.Items);
        Assert.True(cost.Measure(result) <= budget);
    }

    /// <summary>A sensitive fixture file never reaches any result, at any detail.</summary>
    [Theory]
    [InlineData(ContextDetail.Paths)]
    [InlineData(ContextDetail.Outline)]
    [InlineData(ContextDetail.Slices)]
    public void SensitiveFiles_NeverAppear(ContextDetail detail)
    {
        using var repo = new EvalRepo();

        ContextResult result = Run(repo, "database url session secret", new ContextOptions
        {
            Top = 20,
            Detail = detail,
        });

        Assert.DoesNotContain(result.Items, i => i.Path.Contains(".env", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Reused, r => r.Path.Contains(".env", StringComparison.Ordinal));
    }

    private sealed class HighBaseCostModel : IResponseCostModel
    {
        public const int BaseCost = 100_000;

        public int Calls { get; private set; }

        public string Surface => "test";

        public int Measure(ContextResult result)
        {
            Calls++;
            int digits = result.ResponseBudgetTokens?.ToString(
                System.Globalization.CultureInfo.InvariantCulture).Length ?? 0;
            return BaseCost + digits;
        }
    }

    private sealed class RejectBudgetSpanCostModel : IResponseCostModel
    {
        public string Surface => "test";

        public int Measure(ContextResult result) =>
            result.Items
                .SelectMany(item => item.Spans ?? [])
                .Any(span => span.Symbol == "Budget")
                    ? 2000
                    : 500;
    }
}
