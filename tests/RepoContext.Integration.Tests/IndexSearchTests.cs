using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>End-to-end tests for the M1 init/index/search pipeline (spec F1/F3/F5).</summary>
public class IndexSearchTests
{
    [Fact]
    public void Init_CreatesConfigAndGitignore_AndGuardsAgainstReinit()
    {
        using var ws = new FixtureWorkspace("sample-ts");

        CliResult init = ws.Run("init");
        Assert.Equal(0, init.ExitCode);
        Assert.True(File.Exists(ws.PathOf("repoctx.config.json")));
        Assert.Contains(".repoctx/", File.ReadAllText(ws.PathOf(".gitignore")));

        // Re-init without --force fails; with --force succeeds.
        Assert.Equal(1, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("init", "--force").ExitCode);
    }

    [Fact]
    public void Index_BeforeInit_ReturnsNoIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(2, ws.Run("index").ExitCode);
    }

    [Fact]
    public void Search_BeforeIndex_ReturnsNoIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        Assert.Equal(2, ws.Run("search", "login").ExitCode);
    }

    [Fact]
    public void FullFlow_IndexesAndSearches()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult search = ws.Run("search", "login");
        Assert.Equal(0, search.ExitCode);
        Assert.Contains("src/auth/login.ts", search.StdOut);
    }

    [Fact]
    public void SearchJson_HasSchemaVersion_AndIsDeterministic()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult a = ws.Run("search", "login", "--format", "json");
        CliResult b = ws.Run("search", "login", "--format", "json");
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut); // byte-identical determinism

        using JsonDocument doc = JsonDocument.Parse(a.StdOut);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("search", doc.RootElement.GetProperty("command").GetString());
        Assert.True(doc.RootElement.GetProperty("results").GetArrayLength() > 0);
    }

    [Fact]
    public void SensitiveMarker_NeverAppearsInSearchOutput()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        // Even searching for the marker returns nothing and never echoes it from the index.
        CliResult result = ws.Run("search", "SECRET", "--format", "json");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("SECRET_MARKER_DO_NOT_INDEX", result.Output);
    }

    [Fact]
    public void Index_IsIncremental_AfterEdit()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        File.WriteAllText(ws.PathOf("src/brandnewfile.ts"), "export function uniqueSymbolXyz() {}\n");
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult search = ws.Run("search", "uniqueSymbolXyz");
        Assert.Contains("src/brandnewfile.ts", search.StdOut);
    }
}
