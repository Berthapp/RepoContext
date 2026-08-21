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

    /// <summary>
    /// <c>**/</c> means "zero or more directories here" wherever it appears.
    /// Collapsing it to <c>*/</c> - as the first implementation did - required
    /// at least one intermediate directory, so <c>artifacts/**/*.json</c>
    /// silently missed <c>artifacts/ticket.json</c>.
    /// </summary>
    [Fact]
    public void ADoubleStarSegment_MatchesWithAndWithoutTheDirectory()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["artifacts/**/*.json"]));

        Assert.Equal(
            [
                "artifacts/*.json", "artifacts/*.json/*",
                "artifacts/*/*.json", "artifacts/*/*.json/*",
            ],
            scope.Patterns);
    }

    /// <summary>
    /// The same rule at the start of a pattern: <c>--path "**/services"</c> has
    /// to reach a top-level <c>services/</c>, not only a nested one.
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
    public void SeveralDoubleStarSegments_AreBoundedAndDeduplicated()
    {
        PathScope scope = Assert.IsType<PathScope>(PathScope.From(["**/a/**/b"]));

        Assert.Equal(
            [
                "a/b", "a/b/*",
                "a/*/b", "a/*/b/*",
                "*/a/b", "*/a/b/*",
                "*/a/*/b", "*/a/*/b/*",
            ],
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
