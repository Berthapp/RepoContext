using RepoContext.Core.Scanning;

namespace RepoContext.Core.Tests.Scanning;

public class GitignoreMatcherTests
{
    [Theory]
    [InlineData("node_modules", "node_modules", true, true)]
    [InlineData("node_modules", "src/node_modules", true, true)]
    [InlineData("node_modules", "src/app.ts", false, false)]
    [InlineData("*.min.js", "vendor.min.js", false, true)]
    [InlineData("*.min.js", "app/vendor.min.js", false, true)]
    [InlineData("*.min.js", "app.js", false, false)]
    [InlineData(".env*", ".env", false, true)]
    [InlineData(".env*", ".env.local", false, true)]
    public void BasenamePatterns_MatchAtAnyDepth(string pattern, string path, bool isDir, bool expected)
    {
        var matcher = GitignoreMatcher.Parse([pattern]);
        Assert.Equal(expected, matcher.IsIgnored(path, isDir));
    }

    [Fact]
    public void AnchoredPattern_MatchesOnlyFromRoot()
    {
        var matcher = GitignoreMatcher.Parse(["/build"]);
        Assert.True(matcher.IsIgnored("build", isDirectory: true));
        Assert.False(matcher.IsIgnored("src/build", isDirectory: true));
    }

    [Fact]
    public void DirectoryOnlyPattern_DoesNotMatchFiles()
    {
        var matcher = GitignoreMatcher.Parse(["dist/"]);
        Assert.True(matcher.IsIgnored("dist", isDirectory: true));
        Assert.False(matcher.IsIgnored("dist", isDirectory: false));
    }

    [Fact]
    public void Negation_ReincludesLaterMatch()
    {
        var matcher = GitignoreMatcher.Parse(["*.log", "!keep.log"]);
        Assert.True(matcher.IsIgnored("debug.log", isDirectory: false));
        Assert.False(matcher.IsIgnored("keep.log", isDirectory: false));
    }

    [Fact]
    public void CommentsAndBlankLines_AreIgnored()
    {
        var matcher = GitignoreMatcher.Parse(["# a comment", "", "  ", "temp"]);
        Assert.True(matcher.IsIgnored("temp", isDirectory: true));
    }

    [Fact]
    public void DoubleStar_CrossesDirectories()
    {
        var matcher = GitignoreMatcher.Parse(["docs/**/tmp"]);
        Assert.True(matcher.IsIgnored("docs/a/b/tmp", isDirectory: true));
        Assert.True(matcher.IsIgnored("docs/tmp", isDirectory: true));
    }

    /// <summary>
    /// Ignore files are repository content. With a backtracking engine this one
    /// line kept an index run busy indefinitely on a single 60-character name.
    /// </summary>
    [Fact]
    public void AHostilePattern_MatchesInLinearTime()
    {
        var matcher = GitignoreMatcher.Parse(["**a**a**a**a**a**a**a**a**a**a**a**b"]);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            Assert.False(matcher.IsIgnored("d/" + new string('a', 60 + i), isDirectory: false));
        }

        Assert.True(matcher.IsIgnored("d/aaaaaaaaaaab", isDirectory: false));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
