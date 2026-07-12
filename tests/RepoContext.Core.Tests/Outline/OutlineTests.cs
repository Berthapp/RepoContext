using RepoContext.Core.Outline;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Outline;

public class OutlineTests
{
    [Fact]
    public void Query_ReturnsSymbolsWithSignatures_DocSummaries_AndRealCosts()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        OutlineResult? outline = Core.Outline.Outline.Query(store, "src/auth/login.ts");

        Assert.NotNull(outline);
        Assert.Equal("src/auth/login.ts", outline.Path);
        Assert.Equal(Hashes.ShortLength, outline.Hash.Length);
        Assert.True(outline.TokenCount > 0);

        OutlineSymbol login = outline.Symbols.Single(s => s.Name == "loginUser");
        Assert.Contains("loginUser", login.Signature);
        Assert.True(login.StartLine > 0 && login.EndLine >= login.StartLine);
        // The JSDoc is summarized to its first line.
        Assert.NotNull(login.Doc);
        Assert.DoesNotContain('\n', login.Doc);
    }

    [Fact]
    public void Query_UnknownFile_ReturnsNull()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Null(Core.Outline.Outline.Query(store, "src/nope.ts"));
    }

    [Fact]
    public void Symbols_RespectTheCap_AndReportTheCut()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        FileRow file = store.FindFile("src/auth/login.ts")!.Value;

        (IReadOnlyList<OutlineSymbol> symbols, int omitted) =
            Core.Outline.Outline.Symbols(store, file.Id, cap: 1);

        Assert.Single(symbols);
        Assert.True(omitted >= 1);
    }
}
