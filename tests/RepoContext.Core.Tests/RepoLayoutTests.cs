using RepoContext.Core;

namespace RepoContext.Core.Tests;

/// <summary>Resolving caller-supplied paths against the repository root.</summary>
public class RepoLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-layout-").FullName;
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (string link in _links)
        {
            try
            {
                Directory.Delete(link);
            }
            catch (IOException)
            {
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Creates a directory symlink, or returns null where that is not permitted.</summary>
    private string? Link(string target)
    {
        string link = Path.Combine(
            Path.GetTempPath(), "repoctx-link-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // Windows needs developer mode or elevation for this; the behaviour
            // under test is then simply not exercised on that machine.
            return null;
        }

        _links.Add(link);
        return link;
    }

    [Fact]
    public void ToRelativePath_AcceptsAPathInsideTheRepository()
    {
        RepoLayout layout = RepoLayout.For(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        Assert.Equal("src/app.ts", layout.ToRelativePath("app.ts", Path.Combine(_root, "src")));
        Assert.Equal(
            "src/app.ts", layout.ToRelativePath(Path.Combine(_root, "src", "app.ts"), _root));
    }

    [Fact]
    public void ToRelativePath_RejectsAPathOutsideTheRepository()
    {
        RepoLayout layout = RepoLayout.For(_root);

        Assert.Null(layout.ToRelativePath("../elsewhere/app.ts", _root));
        Assert.Null(layout.ToRelativePath(
            Path.Combine(Path.GetTempPath(), "somewhere-else", "app.ts"), _root));
    }

    [Fact]
    public void ToRelativePath_AcceptsTheSameLocationSpelledThroughASymlink()
    {
        // macOS reaches /var and /tmp through links to /private/…, so the path a
        // client supplies and the root a command discovers routinely differ as
        // strings while naming one file. Rejecting that as "outside the
        // repository" makes a suggested command unusable for no reason.
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        if (Link(_root) is not { } linked)
        {
            return;
        }

        RepoLayout real = RepoLayout.For(_root);
        RepoLayout aliased = RepoLayout.For(linked);

        Assert.Equal(
            "src/app.ts",
            real.ToRelativePath(Path.Combine(linked, "src", "app.ts"), Path.Combine(_root, "src")));
        Assert.Equal(
            "src/app.ts",
            aliased.ToRelativePath(Path.Combine(_root, "src", "app.ts"), Path.Combine(linked, "src")));
    }

    [Fact]
    public void ToRelativePath_StillRejectsALinkThatLeavesTheRepository()
    {
        // The fallback resolves both sides before comparing, so a link out of the
        // repository resolves to somewhere outside the resolved root and stays
        // rejected. Being lenient about spelling must not become being lenient
        // about location.
        string outside = Directory.CreateTempSubdirectory("repoctx-outside-").FullName;
        try
        {
            if (Link(outside) is not { } escape)
            {
                return;
            }

            RepoLayout layout = RepoLayout.For(_root);

            Assert.Null(layout.ToRelativePath(Path.Combine(escape, "secret.env"), _root));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
