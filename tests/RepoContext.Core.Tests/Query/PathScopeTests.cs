using RepoContext.Core.Query;

namespace RepoContext.Core.Tests.Query;

/// <summary>
/// The <c>--path</c> scope (ADR 0019). Its patterns are handed to SQLite
/// <c>GLOB</c> verbatim, so what is pinned here is the normalization that
/// happens before that.
/// </summary>
public class PathScopeTests
{
    [Fact]
    public void NoUsablePattern_MeansNoScope()
    {
        Assert.Null(PathScope.From(null));
        Assert.Null(PathScope.From([]));
        Assert.Null(PathScope.From(["  ", string.Empty]));
    }

    [Fact]
    public void APlainPath_SelectsItAndEverythingBelowIt()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["services/api"]));

        Assert.Equal(["services/api", "services/api/*"], scope.Patterns);
        Assert.Equal(["services/api"], scope.Inputs);
    }

    [Fact]
    public void LeadingAndTrailingSeparators_AndBackslashes_AreNormalized()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["./services\\api/"]));

        Assert.Equal(["services/api", "services/api/*"], scope.Patterns);
    }

    [Fact]
    public void GlobPatterns_ArePassedThroughWithDoubleStarCollapsed()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["artifacts/**/*.json"]));

        // Under GLOB a single '*' already crosses '/', so '**' would only add a
        // second, redundant wildcard - and every pattern also selects its subtree.
        Assert.Equal(["artifacts/*/*.json", "artifacts/*/*.json/*"], scope.Patterns);
    }

    /// <summary>
    /// A leading <c>**/</c> means "at any depth, including here". Collapsing it
    /// to <c>*/</c> - as the first implementation did - matched nothing at the
    /// repository root, so <c>--path "**/services"</c> silently returned
    /// nothing for a top-level <c>services/</c>.
    /// </summary>
    [Fact]
    public void ALeadingDoubleStar_AlsoMatchesAtTheRoot()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["**/services"]));

        Assert.Equal(
            ["services", "services/*", "*/services", "*/services/*"],
            scope.Patterns);
    }

    [Fact]
    public void SeveralPatterns_AreKeptInTheOrderTheyWereGiven()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["src", "docs/*.md"]));

        Assert.Equal(["src", "src/*", "docs/*.md", "docs/*.md/*"], scope.Patterns);
        Assert.Equal(["src", "docs/*.md"], scope.Inputs);
    }
}
