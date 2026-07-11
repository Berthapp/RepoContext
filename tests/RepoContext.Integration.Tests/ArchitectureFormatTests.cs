using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>End-to-end tests for M4 architecture and the --format md option.</summary>
public class ArchitectureFormatTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Fact]
    public void Architecture_BeforeIndex_ReturnsNoIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        Assert.Equal(2, ws.Run("architecture").ExitCode);
    }

    [Fact]
    public void Architecture_Json_HasSchemaVersionAndCentrality()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("architecture", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(Core.RepoContextInfo.SchemaVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.True(doc.RootElement.GetProperty("total_files").GetInt32() > 0);
        Assert.Equal("src/auth/session.ts",
            doc.RootElement.GetProperty("most_imported")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Architecture_Markdown_RendersHeadings()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("architecture", "--format", "md");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("# Architecture", result.StdOut);
        Assert.Contains("## Languages", result.StdOut);
    }

    [Fact]
    public void Search_Markdown_RendersTable()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("search", "login", "--format", "md");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("# Search: login", result.StdOut);
        Assert.Contains("| # | File |", result.StdOut);
    }

    [Fact]
    public void Context_Markdown_RendersReasons()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("context", "change the login logic", "--format", "md");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("# Context:", result.StdOut);
        Assert.Contains("reasons:", result.StdOut);
    }
}
