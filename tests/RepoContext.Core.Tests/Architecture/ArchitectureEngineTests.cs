using RepoContext.Core.Architecture;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Architecture;

public class ArchitectureEngineTests
{
    private static ArchitectureResult Build(FixtureRepo repo)
    {
        using IndexStore store = IndexHelper.BuildIndex(repo);
        return new ArchitectureEngine(store).Build();
    }

    [Fact]
    public void Build_ReportsTotalsAndLanguages()
    {
        using var repo = new FixtureRepo("sample-ts");
        ArchitectureResult result = Build(repo);

        Assert.True(result.TotalFiles > 0);
        Assert.True(result.TotalLoc > 0);
        Assert.Contains(result.Languages, l => l.Language == "typescript" && l.Files > 0);
    }

    [Fact]
    public void Build_RanksMostImportedFileFirst()
    {
        using var repo = new FixtureRepo("sample-ts");
        ArchitectureResult result = Build(repo);

        // session.ts is imported by the most files in the fixture.
        Assert.Equal("src/auth/session.ts", result.Central[0].Path);
        Assert.True(result.Central[0].Dependents >= result.Central[^1].Dependents);
    }

    [Fact]
    public void Build_DetectsEntrypoints()
    {
        using var repo = new FixtureRepo("sample-ts");
        ArchitectureResult result = Build(repo);

        Assert.Contains("app/page.tsx", result.Entrypoints);
        Assert.Contains("src/middleware.ts", result.Entrypoints);
        Assert.Contains("app/api/users/route.ts", result.Entrypoints);
    }

    [Fact]
    public void Build_TreeIsDepthLimitedAndAggregatesLoc()
    {
        using var repo = new FixtureRepo("sample-ts");
        ArchitectureResult result = Build(repo);

        TreeNode src = result.Tree.Children.Single(c => c.Name == "src");
        Assert.True(src.Loc > 0);
        Assert.True(src.FileCount > 0);
        Assert.Contains(src.Children, c => c.Name == "auth");
        Assert.All(Depths(result.Tree, 0), d => Assert.True(d <= 3));
    }

    private static IEnumerable<int> Depths(TreeNode node, int depth)
    {
        yield return depth;
        foreach (TreeNode child in node.Children)
        {
            foreach (int d in Depths(child, depth + 1))
            {
                yield return d;
            }
        }
    }
}
