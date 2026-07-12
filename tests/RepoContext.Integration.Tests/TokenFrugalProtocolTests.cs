using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the M6 token-frugal protocol (ADR 0010): outline,
/// changed, context detail levels and known-state dedupe through the real CLI.
/// </summary>
public class TokenFrugalProtocolTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Fact]
    public void Outline_Json_ListsSymbolsWithHashAndCost()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("outline", "src/auth/login.ts", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement root = doc.RootElement;
        Assert.Equal("outline", root.GetProperty("command").GetString());
        Assert.Equal("src/auth/login.ts", root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("estimated_tokens").GetInt32() > 0);
        Assert.Equal(12, root.GetProperty("hash").GetString()!.Length);

        var names = root.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("loginUser", names);
    }

    [Fact]
    public void Outline_UnknownFile_FailsWithError()
    {
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(1, ws.Run("outline", "src/nope.ts").ExitCode);
    }

    [Fact]
    public void Changed_CleanTree_ReportsCurrent()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("changed", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.False(doc.RootElement.GetProperty("stale").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Changed_AfterAnEdit_ReportsModifiedAndImpacted()
    {
        using FixtureWorkspace ws = Indexed();
        string session = ws.PathOf("src/auth/session.ts");
        File.WriteAllText(session, File.ReadAllText(session) + "\n// edited\n");

        CliResult result = ws.Run("changed", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.True(doc.RootElement.GetProperty("stale").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("changed").EnumerateArray(),
            c => c.GetProperty("path").GetString() == "src/auth/session.ts"
                 && c.GetProperty("status").GetString() == "modified");
        Assert.Contains(doc.RootElement.GetProperty("impacted").EnumerateArray(),
            i => i.GetProperty("path").GetString() == "src/auth/login.ts");
    }

    [Fact]
    public void Context_SlicesDetail_EmbedsSourceAtSliceCost()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "context", "change the login logic", "--detail", "slices", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal("slices", doc.RootElement.GetProperty("detail").GetString());
        JsonElement first = doc.RootElement.GetProperty("results")[0];
        Assert.False(string.IsNullOrEmpty(first.GetProperty("snippet").GetString()));
        Assert.True(first.GetProperty("estimated_tokens").GetInt32() > 0);
        Assert.True(first.GetProperty("file_tokens").GetInt32() > 0);
    }

    [Fact]
    public void Context_Known_ReturnsZeroCostUnchangedMarkers()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult first = ws.Run("context", "change the login logic", "--format", "json");
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement login = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        string hash = login.GetProperty("hash").GetString()!;

        CliResult second = ws.Run(
            "context", "change the login logic",
            "--known", $"src/auth/login.ts@{hash}", "--format", "json");
        Assert.Equal(0, second.ExitCode);

        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        JsonElement marker = secondDoc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        Assert.True(marker.GetProperty("unchanged").GetBoolean());
        Assert.Equal(0, marker.GetProperty("estimated_tokens").GetInt32());
    }

    [Fact]
    public void Context_SnippetsFlag_IsAnAliasForSliceDetail()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("context", "login", "--snippets", "--format", "json");
        Assert.Equal(0, result.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal("slices", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public void Architecture_Depth1_IsACheapOrientation()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult brief = ws.Run("architecture", "--depth", "1", "--format", "json");
        CliResult full = ws.Run("architecture", "--format", "json");
        Assert.Equal(0, brief.ExitCode);

        Assert.True(brief.StdOut.Length < full.StdOut.Length,
            "depth 1 must be smaller than the default depth-3 summary");
        using JsonDocument doc = JsonDocument.Parse(brief.StdOut);
        Assert.Equal(1, doc.RootElement.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void OutdatedIndexSchema_IsRefusedAsNoIndex()
    {
        using FixtureWorkspace ws = Indexed();

        // Simulate an index written by an older tool version.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={ws.PathOf(".repoctx/index.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value = '3' WHERE key = 'schema_version'";
            cmd.ExecuteNonQuery();
        }

        CliResult result = ws.Run("search", "login");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("outdated", result.StdErr, StringComparison.OrdinalIgnoreCase);

        // Re-indexing rebuilds and recovers.
        Assert.Equal(0, ws.Run("index").ExitCode);
        Assert.Equal(0, ws.Run("search", "login").ExitCode);
    }
}
