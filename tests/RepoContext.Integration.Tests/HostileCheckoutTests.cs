using RepoContext.Cli.Output;

namespace RepoContext.Integration.Tests;

/// <summary>
/// A cloned repository is untrusted input. Each test commits what a hostile
/// checkout could commit - a symbolic link, a configuration - and pins that an
/// ordinary command neither writes outside the repository nor reads from
/// outside it.
/// </summary>
public sealed class HostileCheckoutTests : IDisposable
{
    private readonly string _outside = Directory.CreateTempSubdirectory("repoctx-outside-").FullName;

    public void Dispose() => Directory.Delete(_outside, recursive: true);

    private string Victim(string name, string content = "original\n")
    {
        string path = Path.Combine(_outside, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a symbolic link, or returns false where that is not permitted.</summary>
    private static bool TryLink(string link, string target, bool directory = false)
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
            // Windows needs developer mode or elevation for this.
            return false;
        }
    }

    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);
        return ws;
    }

    [Fact]
    public void AnIncludeOutsideTheRepository_IsRefused()
    {
        Victim("credentials.txt", "TOP-SECRET-KEY=hunter2\n");
        using FixtureWorkspace ws = Indexed();
        string config = File.ReadAllText(ws.PathOf("repoctx.config.json"));
        string escape = Path.GetRelativePath(ws.Root, _outside).Replace('\\', '/');
        File.WriteAllText(ws.PathOf("repoctx.config.json"),
            config.Replace("\"include\": []", $"\"include\": [\".\", \"{escape}\"]", StringComparison.Ordinal));

        CliResult index = ws.Run("index");
        CliResult search = ws.Run("search", "hunter2");

        Assert.NotEqual(0, index.ExitCode);
        Assert.Contains("inside the repository", index.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", search.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedDashboard_IsNotWrittenThrough()
    {
        string victim = Victim("profile");
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(0, ws.Run("search", "login").ExitCode);
        if (!TryLink(ws.PathOf(".repoctx/stats.html"), victim))
        {
            return;
        }

        CliResult stats = ws.RunWithEnv(
            new Dictionary<string, string> { ["REPOCTX_NO_LAUNCH"] = "1", ["REPOCTX_NO_STATS"] = string.Empty },
            "stats", "--open");

        Assert.Equal(1, stats.ExitCode);
        Assert.Contains("symbolic link", stats.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stats.StdErr, StringComparison.Ordinal);
        Assert.Equal("original\n", File.ReadAllText(victim));
    }

    [Fact]
    public void ALinkedDatabaseSideFile_StopsTheQuery()
    {
        string victim = Victim("app.sqlite-wal");
        using FixtureWorkspace ws = Indexed();
        if (!TryLink(ws.PathOf(".repoctx/index.db-wal"), victim))
        {
            return;
        }

        CliResult search = ws.Run("search", "login");

        Assert.Equal(1, search.ExitCode);
        Assert.Contains("index.db-wal", search.StdErr, StringComparison.Ordinal);
        Assert.Equal("original\n", File.ReadAllText(victim));
    }

    [Fact]
    public void ALinkedIndexDirectory_IsRefusedBeforeAnythingIsCreatedThere()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        if (!TryLink(ws.PathOf(".repoctx"), _outside, directory: true))
        {
            return;
        }

        CliResult init = ws.Run("init");

        Assert.Equal(1, init.ExitCode);
        Assert.Contains("symbolic link", init.StdErr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
        Assert.False(File.Exists(ws.PathOf("repoctx.config.json")));
    }

    [Fact]
    public void AStateFilePlantedAsALink_IsLeftAloneAndTheQueryStillAnswers()
    {
        // Session persistence is best effort: refusing the write must cost only
        // the reuse bookkeeping, never the answer.
        string victim = Victim("session-victim");
        using FixtureWorkspace ws = Indexed();
        if (!TryLink(ws.PathOf(".repoctx/sessions/s1.json"), victim)
            || !TryLink(ws.PathOf(".repoctx/sessions/s1.json.tmp"), victim))
        {
            return;
        }

        CliResult context = ws.Run("context", "change the login logic", "--session", "s1");

        Assert.Equal(0, context.ExitCode);
        Assert.Contains("login", context.StdOut, StringComparison.Ordinal);
        Assert.Equal("original\n", File.ReadAllText(victim));
    }

    [Fact]
    public void AnInstructionFileLinkedOutOfTheRepository_IsNotAppendedTo()
    {
        string victim = Victim("bashrc");
        using var ws = new FixtureWorkspace("sample-ts");
        if (!TryLink(ws.PathOf("CLAUDE.md"), victim))
        {
            return;
        }

        CliResult integrate = ws.Run("integrate", "--client", "claude-code");

        Assert.Equal(1, integrate.ExitCode);
        Assert.Contains("outside the repository", integrate.StdErr, StringComparison.Ordinal);
        Assert.Equal("original\n", File.ReadAllText(victim));
    }

    [Fact]
    public void AnInstructionFileLinkedInsideTheRepository_StillWorks()
    {
        // CLAUDE.md -> AGENTS.md is a common setup, and a legitimate one.
        using var ws = new FixtureWorkspace("sample-ts");
        File.WriteAllText(ws.PathOf("AGENTS.md"), "# Agents\n");
        if (!TryLink(ws.PathOf("CLAUDE.md"), "AGENTS.md"))
        {
            return;
        }

        CliResult integrate = ws.Run("integrate", "--client", "claude-code");

        Assert.Equal(0, integrate.ExitCode);
        Assert.Contains("RepoContext", File.ReadAllText(ws.PathOf("AGENTS.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsDirectoryLinkedIntoTheHome_GetsNoHooks()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init").ExitCode);
        if (!TryLink(ws.PathOf(".claude"), _outside, directory: true))
        {
            return;
        }

        CliResult integrate = ws.Run("integrate", "--client", "claude-code", "--guard");

        Assert.NotEqual(0, integrate.ExitCode);
        Assert.False(File.Exists(Path.Combine(_outside, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(_outside, "skills")));
    }

    [Theory]
    [InlineData("plain text\nwith\ttabs\r\n", "plain text\nwith\ttabs\r\n")]
    [InlineData("title\u001b]0;pwned\u0007", "title␛]0;pwned␇")]
    [InlineData("evil();\rgood();\n", "evil();␍good();\n")]
    [InlineData("del\u007f c1\u009b", "del␡ c1�")]
    public void TerminalText_NeutralizesControlSequences(string input, string expected)
    {
        Assert.Equal(expected, TerminalText.Neutralize(input));
    }

    [Fact]
    public void TerminalText_ReturnsCleanTextUnchanged()
    {
        const string clean = "export function login() {}\n";

        Assert.Same(clean, TerminalText.Neutralize(clean));
    }
}
