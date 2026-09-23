using RepoContext.Core;

namespace RepoContext.Core.Tests;

/// <summary>
/// Writes stay inside the repository even when the checkout contains hostile
/// symbolic links (git checks links out verbatim).
/// </summary>
public class SafePathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-safe-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("repoctx-outside-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private string In(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates a symbolic link, or returns false where that is not permitted.</summary>
    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            // Windows needs developer mode or elevation for this; the behaviour
            // under test is then simply not exercised on that machine.
            return false;
        }
    }

    [Fact]
    public void IsContained_AcceptsPlainPathsAndRejectsEscapes()
    {
        Assert.True(SafePaths.IsContained(_root, In("CLAUDE.md")));
        Assert.True(SafePaths.IsContained(_root, In("does/not/exist/yet.json")));
        Assert.False(SafePaths.IsContained(_root, Path.Combine(_root, "..", "elsewhere.txt")));
        Assert.False(SafePaths.IsContained(_root, Path.Combine(_outside, "x.txt")));
    }

    [Fact]
    public void IsContained_FollowsADirectoryLinkOutOfTheRepository()
    {
        if (!TryLink(In(".claude"), _outside, directory: true))
        {
            return;
        }

        Assert.False(SafePaths.IsContained(_root, In(".claude/settings.json")));
    }

    [Fact]
    public void IsContained_RecognizesADanglingLinkOutOfTheRepository()
    {
        // File.Exists is false for a dangling link, and writing to it creates
        // the target: the classic way a check that only looks at existing files
        // lets a write escape.
        if (!TryLink(In("CLAUDE.md"), Path.Combine(_outside, "created-by-write.txt"), directory: false))
        {
            return;
        }

        Assert.False(SafePaths.IsContained(_root, In("CLAUDE.md")));
    }

    [Fact]
    public void IsContained_AcceptsALinkThatStaysInsideTheRepository()
    {
        // CLAUDE.md -> AGENTS.md is a common, legitimate setup.
        File.WriteAllText(In("AGENTS.md"), "shared\n");
        if (!TryLink(In("CLAUDE.md"), "AGENTS.md", directory: false))
        {
            return;
        }

        Assert.True(SafePaths.IsContained(_root, In("CLAUDE.md")));
    }

    [Fact]
    public void IsContained_AppliesDotDotToTheResolvedLocationNotTheSpelling()
    {
        // `hop` points outside; `sneaky -> hop/../inside` reads as "inside the
        // repository" when normalized as text, but physically is a sibling of
        // the directory `hop` points into.
        string deep = Path.Combine(_outside, "deep");
        Directory.CreateDirectory(Path.Combine(_outside, "inside"));
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(In("inside"));
        if (!TryLink(In("hop"), deep, directory: true)
            || !TryLink(In("sneaky"), Path.Combine("hop", "..", "inside"), directory: true))
        {
            return;
        }

        Assert.False(SafePaths.IsContained(_root, In("sneaky/file.txt")));
    }

    [Fact]
    public void IsContained_TreatsALinkCycleAsOutside()
    {
        if (!TryLink(In("a"), In("b"), directory: true) || !TryLink(In("b"), In("a"), directory: true))
        {
            return;
        }

        Assert.False(SafePaths.IsContained(_root, In("a/file.txt")));
    }

    [Fact]
    public void EnsureNoLinks_AcceptsRealAndNotYetExistingEntries()
    {
        Directory.CreateDirectory(In(".repoctx/sessions"));

        SafePaths.EnsureNoLinks(In(".repoctx"), In(".repoctx/sessions/s1.json"));
        SafePaths.EnsureNoLinks(In(".repoctx"), In(".repoctx/not-yet/created.json"));
    }

    [Fact]
    public void EnsureNoLinks_RejectsALinkedIndexDirectory()
    {
        if (!TryLink(In(".repoctx"), _outside, directory: true))
        {
            return;
        }

        Assert.Throws<UnsafePathException>(
            () => SafePaths.EnsureNoLinks(In(".repoctx"), In(".repoctx/index.db")));
    }

    [Fact]
    public void EnsureNoLinks_RejectsALinkedLeaf_EvenWhenItDangles()
    {
        Directory.CreateDirectory(In(".repoctx"));
        if (!TryLink(In(".repoctx/stats.html"), Path.Combine(_outside, "victim.txt"), directory: false))
        {
            return;
        }

        Assert.Throws<UnsafePathException>(
            () => SafePaths.EnsureNoLinks(In(".repoctx"), In(".repoctx/stats.html")));
        Assert.False(File.Exists(Path.Combine(_outside, "victim.txt")));
    }

    [Fact]
    public void WriteAllTextAtomic_ReplacesContentAndLeavesNoTemporaryFile()
    {
        string target = In("state.json");
        File.WriteAllText(target, "old");

        SafePaths.WriteAllTextAtomic(target, "new ü");

        Assert.Equal("new ü", File.ReadAllText(target));
        Assert.Equal([target], Directory.GetFiles(_root));
        Assert.False(File.ReadAllBytes(target).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }

    [Fact]
    public void WriteAllTextAtomic_NeverWritesThroughALinkAtTheStagingName()
    {
        // Every store stages its write at `<file>.tmp`, and a link planted there
        // used to receive the content. It is now removed, not followed.
        string victim = Path.Combine(_outside, "victim.txt");
        File.WriteAllText(victim, "original");
        if (!TryLink(In("state.json.tmp"), victim, directory: false))
        {
            return;
        }

        SafePaths.WriteAllTextAtomic(In("state.json"), "payload");

        Assert.Equal("original", File.ReadAllText(victim));
        Assert.Equal("payload", File.ReadAllText(In("state.json")));
        Assert.False(File.Exists(In("state.json.tmp")));
    }

    [Fact]
    public void WriteAllTextAtomic_FailsWhenTheStagingNameCannotBeCleared()
    {
        // Stores pin their fail-open and fail-closed behaviour with exactly this
        // failure, so it has to stay an ordinary, catchable I/O error.
        Directory.CreateDirectory(In("state.json.tmp"));

        Exception error = Assert.ThrowsAny<Exception>(() => SafePaths.WriteAllTextAtomic(In("state.json"), "x"));

        Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        Assert.False(File.Exists(In("state.json")));
    }
}
