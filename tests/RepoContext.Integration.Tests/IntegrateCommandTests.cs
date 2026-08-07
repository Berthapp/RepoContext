namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for <c>repoctx integrate</c> and <c>repoctx guide</c>
/// (ADR 0018).
/// </summary>
public class IntegrateCommandTests
{
    [Fact]
    public void Integrate_WithNoDetectedClient_WritesAgentsMd()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult result = ws.Run("integrate");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[agents] created AGENTS.md", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("repoctx context", File.ReadAllText(ws.PathOf("AGENTS.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Integrate_DetectsClaudeCode_AndWritesTheSkillNotTheWholeProtocol()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        Directory.CreateDirectory(ws.PathOf(".claude"));

        CliResult result = ws.Run("integrate");

        Assert.Equal(0, result.ExitCode);
        string memory = File.ReadAllText(ws.PathOf("CLAUDE.md"));
        string skill = File.ReadAllText(
            Path.Combine(ws.Root, ".claude", "skills", "repocontext", "SKILL.md"));

        // Always loaded: the pointer. Loaded on demand: everything else.
        Assert.Contains("repoctx guide", memory, StringComparison.Ordinal);
        Assert.DoesNotContain("--seen", memory, StringComparison.Ordinal);
        Assert.Contains("--seen", skill, StringComparison.Ordinal);
        Assert.True(skill.Length > memory.Length * 2);
    }

    [Fact]
    public void Integrate_IsIdempotent()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("integrate", "--client", "claude-code");
        string first = File.ReadAllText(ws.PathOf("CLAUDE.md"));

        CliResult second = ws.Run("integrate", "--client", "claude-code");

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first, File.ReadAllText(ws.PathOf("CLAUDE.md")));
        Assert.Contains("unchanged CLAUDE.md", second.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrate_Check_ExitsWithDrift_ThenSucceedsAfterApplying()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult drift = ws.Run("integrate", "--check", "--client", "agents");

        Assert.Equal(4, drift.ExitCode);
        Assert.False(File.Exists(ws.PathOf("AGENTS.md")));

        ws.Run("integrate", "--client", "agents");

        Assert.Equal(0, ws.Run("integrate", "--check", "--client", "agents").ExitCode);
    }

    [Fact]
    public void Integrate_NeverTouchesTheConfiguration()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init", "--no-agents");
        string config = File.ReadAllText(ws.PathOf("repoctx.config.json"));

        Assert.Equal(0, ws.Run("integrate", "--client", "claude-code").ExitCode);

        Assert.Equal(config, File.ReadAllText(ws.PathOf("repoctx.config.json")));
    }

    [Fact]
    public void Integrate_Remove_RestoresTheFileToWhatTheUserWrote()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        File.WriteAllText(ws.PathOf("AGENTS.md"), "# House rules\n\nRun the linter.\n");
        ws.Run("integrate", "--client", "agents");

        CliResult removed = ws.Run("integrate", "--remove", "--client", "agents");

        Assert.Equal(0, removed.ExitCode);
        Assert.Equal("# House rules\n\nRun the linter.\n", File.ReadAllText(ws.PathOf("AGENTS.md")));
    }

    [Fact]
    public void Integrate_UnknownClient_IsInvalidArguments()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult result = ws.Run("integrate", "--client", "emacs-doctor");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Known clients", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrate_List_DescribesEveryClientWithoutWriting()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult result = ws.Run("integrate", "--list");

        Assert.Equal(0, result.ExitCode);
        foreach (string id in new[] { "agents", "claude-code", "copilot", "cursor", "windsurf" })
        {
            Assert.Contains(id, result.StdOut, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(ws.PathOf("AGENTS.md")));
    }

    [Fact]
    public void Guide_PrintsTheFullProtocol_AndIsByteStable()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult first = ws.Run("guide");
        CliResult second = ws.Run("guide");

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Never pay for the same evidence twice", first.StdOut, StringComparison.Ordinal);
        Assert.Contains("repoctx context", first.StdOut, StringComparison.Ordinal);
        Assert.Equal(first.StdOut, second.StdOut);
    }
}
