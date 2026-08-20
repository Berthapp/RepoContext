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
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["artifacts/**/*.json", "docs/*"]));

        // Under GLOB a single '*' already crosses '/', so '**' would only add a
        // second, redundant wildcard.
        Assert.Equal(["artifacts/*/*.json", "docs/*"], scope.Patterns);
    }

    [Fact]
    public void SeveralPatterns_AreKeptInTheOrderTheyWereGiven()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["src", "docs/*.md"]));

        Assert.Equal(["src", "src/*", "docs/*.md"], scope.Patterns);
        Assert.Equal(["src", "docs/*.md"], scope.Inputs);
    }
}
