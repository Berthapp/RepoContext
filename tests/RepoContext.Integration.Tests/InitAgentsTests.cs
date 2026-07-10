namespace RepoContext.Integration.Tests;

/// <summary>Tests for <c>repoctx init</c> writing agent-instruction files (F1).</summary>
public class InitAgentsTests
{
    [Fact]
    public void Init_WithAgents_CreatesClaudeAndAgentsFiles()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult result = ws.Run("init", "--agents");

        Assert.Equal(0, result.ExitCode);
        foreach (string name in new[] { "CLAUDE.md", "AGENTS.md" })
        {
            string content = File.ReadAllText(ws.PathOf(name));
            Assert.Contains("BEGIN RepoContext", content);
            Assert.Contains("repoctx context", content);
            Assert.Contains($"created {name}", result.StdOut);
        }
    }

    [Fact]
    public void Init_WithoutFlag_LeavesAgentFilesUntouched_AndHintsOption()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        // stdout is redirected in the harness, so init stays non-interactive.
        CliResult result = ws.Run("init");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(ws.PathOf("CLAUDE.md")));
        Assert.False(File.Exists(ws.PathOf("AGENTS.md")));
        Assert.Contains("--agents", result.StdOut);
    }

    [Fact]
    public void Init_WithNoAgents_LeavesFilesUntouched_AndNoHint()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult result = ws.Run("init", "--no-agents");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(ws.PathOf("CLAUDE.md")));
        Assert.DoesNotContain("Tip:", result.StdOut);
    }

    [Fact]
    public void Init_AgentsAndNoAgents_IsInvalidArguments()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        Assert.Equal(3, ws.Run("init", "--agents", "--no-agents").ExitCode);
    }

    [Fact]
    public void Init_WithAgents_IsIdempotent()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        Assert.Equal(0, ws.Run("init", "--agents").ExitCode);
        string first = File.ReadAllText(ws.PathOf("CLAUDE.md"));

        CliResult second = ws.Run("init", "--force", "--agents");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("unchanged CLAUDE.md", second.StdOut);
        Assert.Equal(first, File.ReadAllText(ws.PathOf("CLAUDE.md")));
    }
}
