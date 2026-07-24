using RepoContext.Core.Context;
using RepoContext.Core.Outline;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

public class UsageMeterTests
{
    [Fact]
    public void ReplacedTokens_CountsDeliveredContentAndAcknowledgedReuse()
    {
        ContextResult result = Result(
            items:
            [
                // A pointer: the agent still reads the file itself — replaces nothing.
                Item("src/pointer.ts", fileTokens: null),
                // A slice item: full-read cost carried on the item.
                Item("src/slice.ts", fileTokens: 500, span: "const x = 1;"),
                // FileTokens alone are metadata, not delivered skeleton content.
                Item("docs/no-symbols.md", fileTokens: 700),
            ],
            // Explicit matching full-file possession objectively avoids this read.
            reused: [Reused("src/known.ts")],
            reusedReadTokens: 300);

        var fullReads = new Dictionary<string, int> { ["src/known.ts"] = 300 };
        int replaced = UsageMeter.ReplacedTokens(
            result, path => fullReads.TryGetValue(path, out int tokens) ? tokens : null);

        Assert.Equal(800, replaced);
    }

    [Fact]
    public void ReplacedTokens_DoesNotTreatPartialReceiptsAsAvoidedFullFileReads()
    {
        // Three span receipts prove only that those spans were already delivered.
        // They do not prove that a 400-token full-file read was avoided.
        ContextResult result = Result(
            items: [],
            reused:
            [
                Reused("src/big.ts", 1, 10),
                Reused("src/big.ts", 20, 30),
                Reused("src/big.ts", 40, 50),
            ]);

        Assert.Equal(0, UsageMeter.ReplacedTokens(result, _ => 400));
    }

    [Fact]
    public void ReplacedTokens_DoesNotDoubleCreditMixedSeenAndUnseenUnits()
    {
        ContextResult result = Result(
            items: [Item("src/big.ts", fileTokens: 500, span: "new evidence")],
            reused: [Reused("src/big.ts", 1, 10)]);

        Assert.Equal(500, UsageMeter.ReplacedTokens(result, _ => 500));
    }

    [Fact]
    public void ReplacedTokens_UnresolvableReusedFileCountsZero()
    {
        ContextResult result = Result(items: [], reused: [Reused("src/gone.ts")]);

        Assert.Equal(0, UsageMeter.ReplacedTokens(result, _ => null));
    }

    [Fact]
    public void OutlineReplacedTokens_RequiresDeliveredSkeleton()
    {
        var empty = new OutlineResult(
            "docs/readme.md", "doc", "markdown", 20, 500, "abcdef123456", []);
        OutlineResult withSymbol = empty with
        {
            Symbols = [new OutlineSymbol("Title", "heading", 1, 1, "# Title", null)],
        };

        Assert.Equal(0, UsageMeter.OutlineReplacedTokens(empty));
        Assert.Equal(500, UsageMeter.OutlineReplacedTokens(withSymbol));
    }

    private static ContextResult Result(
        IReadOnlyList<ContextItem> items,
        IReadOnlyList<ReusedUnit> reused,
        int reusedReadTokens = 0) => new()
        {
            Query = "q",
            Terms = ["q"],
            State = "abcdef123456",
            ContentState = "abcdef123456",
            AnalysisState = "111111222222",
            EvidenceId = "333333444444",
            Detail = ContextDetail.Slices,
            Top = 8,
            Items = items,
            Reused = reused,
            ReusedCount = reused.Count,
            ReusedReadTokens = reusedReadTokens,
            TotalCandidates = items.Count,
            Omitted = 0,
            Omissions = new OmissionReasons(),
            EstimatedTokens = items.Sum(i => i.EstimatedTokens),
            ContentTokens = items.Sum(i => i.ContentTokens),
            ProjectedReadTokens = items.Sum(i => i.ProjectedReadTokens),
        };

    private static ContextItem Item(string path, int? fileTokens, string? span = null) => new()
    {
        Path = path,
        Kind = "code",
        Score = 1.0,
        StartLine = 1,
        EndLine = 10,
        Reasons = ["fts:q"],
        Hash = "abcdef123456",
        EstimatedTokens = 50,
        ContentTokens = span is null ? 0 : 10,
        FileTokens = fileTokens,
        Spans = span is null
            ? null
            : [new ContextSpan { StartLine = 1, EndLine = 10, Text = span, Receipt = "receipt" }],
    };

    private static ReusedUnit Reused(string path, int? start = null, int? end = null) => new()
    {
        Path = path,
        Receipt = $"receipt-{path}-{start}-{end}",
        StartLine = start,
        EndLine = end,
    };
}
