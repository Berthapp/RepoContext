using RepoContext.Core.Context;
using RepoContext.Core.Outline;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

public class UsageMeterTests
{
    [Fact]
    public void ReplacedTokens_CountsDeliveredContentAndUnchangedMarkers()
    {
        ContextResult result = Result(
            // A pointer: the agent still reads the file itself — replaces nothing.
            Item("src/pointer.ts", fileTokens: null),
            // A slice/outline item: full-read cost carried on the item.
            Item("src/slice.ts", fileTokens: 500, snippet: "const x = 1;"),
            // FileTokens alone are metadata, not delivered skeleton content.
            Item("docs/no-symbols.md", fileTokens: 700),
            // An unchanged marker: the avoided re-read is resolved via the index.
            Item("src/known.ts", fileTokens: null, unchanged: true));

        var fullReads = new Dictionary<string, int> { ["src/known.ts"] = 300 };
        int replaced = UsageMeter.ReplacedTokens(
            result, path => fullReads.TryGetValue(path, out int tokens) ? tokens : null);

        Assert.Equal(800, replaced);
    }

    [Fact]
    public void ReplacedTokens_UnresolvableUnchangedFileCountsZero()
    {
        ContextResult result = Result(Item("src/gone.ts", fileTokens: null, unchanged: true));

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

    private static ContextResult Result(params ContextItem[] items) => new()
    {
        Query = "q",
        Terms = ["q"],
        State = "abcdef123456",
        Detail = ContextDetail.Slices,
        Items = items,
        TotalCandidates = items.Length,
        Omitted = 0,
        EstimatedTokens = items.Sum(i => i.EstimatedTokens),
    };

    private static ContextItem Item(
        string path, int? fileTokens, string? snippet = null, bool unchanged = false) => new()
    {
        Path = path,
        Kind = "code",
        Score = 1.0,
        StartLine = 1,
        EndLine = 10,
        Reasons = ["fts:q"],
        Hash = "abcdef123456",
        EstimatedTokens = unchanged ? 0 : 50,
        FileTokens = fileTokens,
        Unchanged = unchanged,
        Snippet = snippet,
    };
}
