using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>End-to-end tests for M2 symbol extraction and symbol search.</summary>
public class SymbolSearchTests
{
    [Fact]
    public void Index_ReportsSymbolCount()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        CliResult index = ws.Run("index");
        Assert.Equal(0, index.ExitCode);
        Assert.Contains("symbols:", index.StdOut);
    }

    [Fact]
    public void SymbolSearch_SplitMatch_LoginFindsLoginUser()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        // "login" (a split token) must find the loginUser symbol.
        CliResult result = ws.Run("search", "login", "--symbols");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("loginUser", result.StdOut);
        Assert.Contains("src/auth/login.ts", result.StdOut);
    }

    [Fact]
    public void SymbolSearch_Json_CarriesSymbolNameInHeading()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult result = ws.Run("search", "session", "--symbols", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement sessionHit = doc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/session.ts");
        Assert.Equal("symbol", sessionHit.GetProperty("chunk_kind").GetString());
        // The heading carries a symbol name (best symbol per file).
        Assert.Equal("Session", sessionHit.GetProperty("heading").GetString());
    }
}
