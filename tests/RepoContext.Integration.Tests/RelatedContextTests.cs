using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>End-to-end tests for M3 related and context commands.</summary>
public class RelatedContextTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Fact]
    public void Related_BeforeIndex_ReturnsNoIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        Assert.Equal(2, ws.Run("related", "src/auth/login.ts").ExitCode);
    }

    [Fact]
    public void Related_ShowsImportsDependentsAndTests()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("related", "src/auth/login.ts");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("src/auth/session.ts", result.StdOut);
        Assert.Contains("src/auth/__tests__/login.test.ts", result.StdOut);
    }

    [Fact]
    public void Related_Json_HasSchemaVersionAndReasons()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("related", "src/auth/session.ts", "--format", "json");
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        JsonElement first = doc.RootElement.GetProperty("results")[0];
        Assert.True(first.GetProperty("reasons").GetArrayLength() > 0);
    }

    [Fact]
    public void Context_BeforeIndex_ReturnsNoIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        Assert.Equal(2, ws.Run("context", "change the login logic").ExitCode);
    }

    [Fact]
    public void Context_Json_RanksLoginFirst_WithReasons()
    {
        using FixtureWorkspace ws = Indexed();
        CliResult result = ws.Run("context", "change the login logic", "--top", "20", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        JsonElement results = doc.RootElement.GetProperty("results");
        Assert.Equal("src/auth/login.ts", results[0].GetProperty("path").GetString());
        Assert.True(results[0].GetProperty("reasons").GetArrayLength() > 0);

        // The sensitive marker file never appears.
        var paths = results.EnumerateArray().Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.DoesNotContain(paths, p => p!.Contains(".env"));
    }

    [Fact]
    public void Context_BudgetTokens_ReducesFileCount()
    {
        using FixtureWorkspace ws = Indexed();
        int Full() => Count(ws.Run("context", "change the login logic", "--top", "20", "--format", "json"));
        int Budgeted() => Count(ws.Run("context", "change the login logic",
            "--top", "20", "--budget-tokens", "300", "--format", "json"));

        Assert.True(Budgeted() < Full());
    }

    private static int Count(CliResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        return doc.RootElement.GetProperty("results").GetArrayLength();
    }
}
