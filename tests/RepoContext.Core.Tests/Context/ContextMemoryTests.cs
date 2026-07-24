using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Memory;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Context;

/// <summary>
/// Memories folded into context bundles (ADR 0013): selection by term and
/// candidate linkage, the budget reserve, the cap and the opt-out.
/// </summary>
public class ContextMemoryTests : IDisposable
{
    private readonly FixtureRepo _repo;
    private readonly IndexStore _store;
    private readonly RepoctxConfig _config =
        RepoctxConfig.CreateDefault() with { Include = ["."] };

    public ContextMemoryTests()
    {
        _repo = new FixtureRepo("sample-ts");
        _store = IndexHelper.BuildIndex(_repo, _config);
    }

    public void Dispose()
    {
        _store.Dispose();
        _repo.Dispose();
        GC.SuppressFinalize(this);
    }

    private ContextResult Run(string query, ContextOptions options) =>
        new ContextEngine(_store, _config).Run(query, options);

    private MemoryEntry Entry(string text, params string[] linkedPaths)
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
            Id = MemoryEntry.ComputeId(MemoryKinds.Note, text, null, files.Keys),
            Kind = MemoryKinds.Note,
            Text = text,
            Files = files,
            Tags = [],
            Session = null,
            Created = "2026-07-20",
        };
    }

    [Fact]
    public void Run_FoldsMatchingMemoryIn_WithLinkedReason_AndChargesIt()
    {
        MemoryEntry memory = Entry("Login validates credentials via session store.", "src/auth/login.ts");
        ContextResult baseline = Run(
            "change the login logic",
            new ContextOptions { Top = 5 });

        ContextResult result = Run("change the login logic", new ContextOptions
        {
            Top = 5,
            Memories = [memory],
        });

        MemoryHit hit = Assert.Single(result.Memories);
        Assert.Equal(memory.Id, hit.Entry.Id);
        Assert.Contains("term:login", hit.Reasons);
        Assert.Contains("linked:src/auth/login.ts", hit.Reasons);
        Assert.True(hit.EstimatedTokens > 0);
        Assert.False(hit.Stale);
        Assert.NotEqual(baseline.FullEvidenceId, result.FullEvidenceId);
        Assert.Equal(
            result.Items.Sum(i => i.EstimatedTokens) + hit.EstimatedTokens,
            result.EstimatedTokens);
    }

    [Fact]
    public void Run_ExcludesIrrelevantMemories()
    {
        MemoryEntry unrelated = Entry("Payments are retried three times with backoff.");
        ContextResult baseline = Run(
            "change the login logic",
            new ContextOptions { Top = 5 });

        ContextResult result = Run("change the login logic", new ContextOptions
        {
            Top = 5,
            Memories = [unrelated],
        });

        Assert.Empty(result.Memories);
        Assert.Equal(baseline.FullEvidenceId, result.FullEvidenceId);
    }

    [Fact]
    public void Run_WithoutMemories_MatchesLegacyShape()
    {
        ContextResult withNull = Run("change the login logic", new ContextOptions { Top = 5 });
        ContextResult withEmpty = Run("change the login logic", new ContextOptions
        {
            Top = 5,
            Memories = [],
        });

        Assert.Empty(withNull.Memories);
        Assert.Empty(withEmpty.Memories);
        Assert.Equal(withNull.EstimatedTokens, withEmpty.EstimatedTokens);
        Assert.Equal(
            withNull.Items.Select(i => i.Path), withEmpty.Items.Select(i => i.Path));
    }

    [Fact]
    public void Run_CapsMemories_AtThree()
    {
        List<MemoryEntry> entries =
        [
            Entry("Login note one about credentials."),
            Entry("Login note two about sessions."),
            Entry("Login note three about middleware."),
            Entry("Login note four about permissions."),
        ];

        ContextResult result = Run("login", new ContextOptions { Top = 5, Memories = entries });

        Assert.Equal(3, result.Memories.Count);
    }

    [Fact]
    public void Run_Budgeted_KeepsMemoryReserve_AtOneFifth_AndTotalWithinBudget()
    {
        List<MemoryEntry> entries =
        [
            Entry("Login handles credential validation and calls the session store to persist state."),
            Entry("Login errors are mapped to typed failures before the middleware sees them."),
            Entry("Login rate limiting lives in middleware, not in the login handler itself."),
        ];
        const int budget = 600;

        ContextResult result = Run("change the login logic", new ContextOptions
        {
            Top = 8,
            BudgetTokens = budget,
            Detail = ContextDetail.Slices,
            Memories = entries,
        });

        int memoryTokens = result.Memories.Sum(m => m.EstimatedTokens);
        Assert.True(memoryTokens <= budget / 5,
            $"memory reserve exceeded: {memoryTokens} > {budget / 5}");
        Assert.NotEmpty(result.Items);

        // Files must still get the remainder: the first file item is always
        // admitted, and the grand total is items + memories.
        Assert.Equal(
            result.Items.Sum(i => i.EstimatedTokens) + memoryTokens,
            result.EstimatedTokens);
    }

    [Fact]
    public void Run_LegacyBudget_DoesNotAdmitAnOversizedFirstFileOrMemory()
    {
        ContextResult result = Run("change the login logic", new ContextOptions
        {
            Top = 1,
            BudgetTokens = 1,
            Memories = [Entry("Login validates credentials.", "src/auth/login.ts")],
        });

        Assert.Empty(result.Items);
        Assert.Empty(result.Memories);
        Assert.Equal(0, result.EstimatedTokens);
    }

    [Fact]
    public void Run_FlagsStaleMemory_WhenLinkedFileDrifted()
    {
        MemoryEntry memory = Entry("Login validates credentials.", "src/auth/login.ts");

        _repo.Write("src/auth/login.ts",
            File.ReadAllText(_repo.PathOf("src/auth/login.ts")) + "\n// drift\n");
        using IndexStore fresh = IndexHelper.BuildIndex(_repo, _config);

        ContextResult result = new ContextEngine(fresh, _config)
            .Run("change the login logic", new ContextOptions { Top = 5, Memories = [memory] });

        MemoryHit hit = Assert.Single(result.Memories);
        Assert.True(hit.Stale);
        Assert.Equal("src/auth/login.ts", Assert.Single(hit.StaleFiles));
    }

    [Fact]
    public void Run_ResponseBudget_DropsOptionalMemoryBeforeAllSourceEvidence()
    {
        var cost = new StructuralCostModel();
        ContextResult result = new ContextEngine(_store, _config).Run(
            "change the login logic",
            new ContextOptions
            {
                Top = 1,
                ResponseBudgetTokens = 200,
                Memories = [Entry("Login validates credentials.", "src/auth/login.ts")],
            },
            cost);

        Assert.Null(result.Shortfall);
        Assert.Single(result.Items);
        Assert.Empty(result.Memories);
        Assert.True(cost.Measure(result) <= 200);
    }

    [Fact]
    public void Run_ResponseBudget_RejectingOnlyMemory_ReportsFittingRetry()
    {
        var cost = new StructuralCostModel();
        ContextResult result = new ContextEngine(_store, _config).Run(
            "rare-memory-term",
            new ContextOptions
            {
                Top = 1,
                ResponseBudgetTokens = 199,
                Memories = [Entry("rare-memory-term records a local invariant.")],
            },
            cost);

        BudgetShortfall shortfall = Assert.IsType<BudgetShortfall>(result.Shortfall);
        Assert.Equal(200, shortfall.RetryBudgetTokens);

        ContextResult retry = new ContextEngine(_store, _config).Run(
            "rare-memory-term",
            new ContextOptions
            {
                Top = 1,
                ResponseBudgetTokens = shortfall.RetryBudgetTokens,
                Memories = [Entry("rare-memory-term records a local invariant.")],
            },
            cost);
        Assert.Null(retry.Shortfall);
        Assert.Single(retry.Memories);
        Assert.True(cost.Measure(retry) <= shortfall.RetryBudgetTokens);
    }

    /// <summary>
    /// Small deterministic oracle: metadata costs 100, and every delivered file
    /// or memory costs another 100. It makes memory/file budget competition
    /// explicit without depending on a CLI renderer in this core test.
    /// </summary>
    private sealed class StructuralCostModel : IResponseCostModel
    {
        public int Measure(ContextResult result) =>
            100 + (result.Items.Count * 100) + (result.Memories.Count * 100);

        public string Surface => "test";
    }
}
