using RepoContext.Core;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Tests.Context;

/// <summary>Server-side known-set persistence (ADR 0012).</summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-session-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("default", true)]
    [InlineData("feature-42_a.b", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    public void IsValidName_AcceptsOnlyPortableFileNames(string name, bool valid) =>
        Assert.Equal(valid, SessionStore.IsValidName(name));

    [Fact]
    public void IsValidName_RejectsOverlongNames() =>
        Assert.False(SessionStore.IsValidName(new string('a', 65)));

    [Fact]
    public void Load_MissingSession_IsEmpty()
    {
        RepoLayout layout = RepoLayout.For(_root);

        Assert.Empty(SessionStore.Load(layout, "nope"));
    }

    [Fact]
    public void Save_RecordsExactReceipts_WithoutPromotingPartialEvidence()
    {
        RepoLayout layout = RepoLayout.For(_root);
        string spanReceipt = Receipt.For(
            "a.ts", "full-hash-a", "slices", EvidenceUnitKind.Span,
            1, 1, string.Empty, "const a = 1;");
        string pointerReceipt = Receipt.For(
            "c.ts", "full-hash-c", "paths", EvidenceUnitKind.Pointer,
            0, 0, string.Empty, string.Empty);
        var result = new ContextResult
        {
            Query = "q",
            Terms = ["q"],
            State = "abc",
            ContentState = "abc",
            AnalysisState = "analysis",
            EvidenceId = "evidence",
            Detail = ContextDetail.Slices,
            Top = 3,
            Items =
            [
                Item("a.ts", "hash-a") with
                {
                    Snippet = "const a = 1;",
                    Spans =
                    [
                        new ContextSpan
                        {
                            StartLine = 1,
                            EndLine = 1,
                            Text = "const a = 1;",
                            Receipt = spanReceipt,
                        },
                    ],
                    Receipt = spanReceipt,
                },
                Item("c.ts", "hash-c") with { Receipt = pointerReceipt },
            ],
            Reused = [],
            ReusedCount = 0,
            TotalCandidates = 2,
            Omitted = 0,
            Omissions = new OmissionReasons(),
            EstimatedTokens = 0,
            ContentTokens = 0,
            ProjectedReadTokens = 0,
        };

        SessionStore.Save(layout, "s1", result);
        SessionState state = SessionStore.LoadState(layout, "s1");

        Assert.Empty(state.Known);
        Assert.Equal(
            new[] { pointerReceipt, spanReceipt }.Order(StringComparer.Ordinal),
            state.Seen);
    }

    [Fact]
    public void Save_MergesExplicitWholeFileAssertions_NewHashWins()
    {
        RepoLayout layout = RepoLayout.For(_root);
        SessionStore.Save(
            layout, "s1", Bundle(),
            new Dictionary<string, string> { ["a.ts"] = "old" });
        SessionStore.Save(
            layout, "s1", Bundle(),
            new Dictionary<string, string>
            {
                ["a.ts"] = "new",
                ["d.ts"] = "hash-d",
            });

        IReadOnlyDictionary<string, string> known = SessionStore.Load(layout, "s1");

        Assert.Equal("new", known["a.ts"]);
        Assert.Equal("hash-d", known["d.ts"]);
    }

    [Fact]
    public async Task Save_ParallelWriters_MergesAllKnownAssertionsAndReceipts()
    {
        const int writerCount = 64;
        RepoLayout layout = RepoLayout.For(_root);
        using var start = new ManualResetEventSlim(initialState: false);
        var receipts = new string[writerCount];
        Task[] writers = Enumerable.Range(0, writerCount).Select(i => Task.Run(() =>
        {
            string path = $"src/file-{i:D2}.cs";
            string receipt = Receipt.For(
                path, $"hash-{i:D2}", "slices", EvidenceUnitKind.Span,
                i + 1, i + 1, string.Empty, $"line {i}");
            receipts[i] = receipt;
            start.Wait();
            SessionStore.Save(
                layout,
                "parallel",
                Bundle(),
                new Dictionary<string, string> { [path] = $"hash-{i:D2}" },
                [receipt]);
        })).ToArray();

        start.Set();
        await Task.WhenAll(writers);

        SessionState state = SessionStore.LoadState(layout, "parallel");
        Assert.Equal(writerCount, state.Known.Count);
        Assert.Equal(
            receipts.Order(StringComparer.Ordinal),
            state.Seen);
        Assert.All(Enumerable.Range(0, writerCount), i =>
            Assert.Equal($"hash-{i:D2}", state.Known[$"src/file-{i:D2}.cs"]));
    }

    [Fact]
    public void Load_CorruptFile_IsTreatedAsEmpty()
    {
        RepoLayout layout = RepoLayout.For(_root);
        string path = SessionStore.PathFor(layout, "bad");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json");

        Assert.Empty(SessionStore.Load(layout, "bad"));
    }

    [Fact]
    public void Load_LegacyUnversionedKnownSet_IsNotTrusted()
    {
        RepoLayout layout = RepoLayout.For(_root);
        string path = SessionStore.PathFor(layout, "legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"known":{"a.ts":"slice-derived-hash"}}""");

        SessionState state = SessionStore.LoadState(layout, "legacy");

        Assert.Empty(state.Known);
        Assert.Empty(state.Seen);
    }

    private static ContextResult Bundle(params ContextItem[] items) => new()
    {
        Query = "q",
        Terms = ["q"],
        State = "abc",
        ContentState = "abc",
        AnalysisState = "analysis",
        EvidenceId = "evidence",
        Detail = ContextDetail.Slices,
        Top = items.Length,
        Items = items,
        Reused = [],
        ReusedCount = 0,
        TotalCandidates = items.Length,
        Omitted = 0,
        Omissions = new OmissionReasons(),
        EstimatedTokens = 0,
        ContentTokens = 0,
        ProjectedReadTokens = 0,
    };

    private static ContextItem Item(string path, string hash) => new()
    {
        Path = path,
        Kind = "source",
        Score = 1,
        StartLine = 1,
        EndLine = 1,
        Reasons = ["fts"],
        Hash = hash,
    };
}
