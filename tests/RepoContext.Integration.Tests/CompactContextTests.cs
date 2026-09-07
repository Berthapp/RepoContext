using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoContext.Core;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests;

public sealed class CompactContextTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);
        return ws;
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("outline")]
    [InlineData("slices")]
    public void Compact_PreservesEvidenceAndReceipts_WithDistinctRepresentation(string detail)
    {
        using FixtureWorkspace ws = Indexed();
        string[] args = ["context", "change login", "--detail", detail, "--format", "json"];
        CliResult original = ws.Run(args);
        CliResult compact = ws.Run([.. args, "--compact"]);
        Assert.Equal(0, original.ExitCode);
        Assert.Equal(0, compact.ExitCode);
        var oldDocument = JsonNode.Parse(original.StdOut)!.AsObject();
        var newDocument = JsonNode.Parse(compact.StdOut)!.AsObject();
        Assert.Equal(RepoContextInfo.SchemaVersion, (int)oldDocument["schema_version"]!);
        Assert.Equal(RepoContextInfo.CompactContextSchemaVersion, (int)newDocument["schema_version"]!);
        Assert.NotEqual((string?)oldDocument["representation_id"], (string?)newDocument["representation_id"]);
        Assert.True(Tokens.Count(compact.StdOut) < Tokens.Count(original.StdOut));
        Assert.Equal(compact.StdOut, ws.Run([.. args, "--compact"]).StdOut);

        // Only the documented compatibility fields and representation change.
        oldDocument.Remove("schema_version");
        newDocument.Remove("schema_version");
        oldDocument.Remove("representation_id");
        newDocument.Remove("representation_id");
        oldDocument.Remove("state");
        oldDocument.Remove("estimated_tokens");
        Assert.False(newDocument.ContainsKey("state"));
        Assert.False(newDocument.ContainsKey("estimated_tokens"));
        foreach (JsonNode? item in oldDocument["results"]!.AsArray())
        {
            foreach (string field in new[] { "estimated_tokens", "file_tokens", "start_line", "end_line", "snippet" })
                item!.AsObject().Remove(field);
        }
        Assert.True(JsonNode.DeepEquals(oldDocument, newDocument));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receipts_AreReusableAcrossFormats(bool compactFirst)
    {
        using FixtureWorkspace ws = Indexed();
        string[] args = ["context", "loginUser", "--detail", "slices", "--format", "json"];
        CliResult first = ws.Run(compactFirst ? [.. args, "--compact"] : args);
        Assert.Equal(0, first.ExitCode);
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        string receipt = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .SelectMany(item => item.GetProperty("spans").EnumerateArray())
            .First().GetProperty("receipt").GetString()!;
        string[] followup = [.. args, "--seen", receipt];
        CliResult second = ws.Run(compactFirst ? followup : [.. followup, "--compact"]);
        Assert.Equal(0, second.ExitCode);
        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        Assert.Contains(secondDoc.RootElement.GetProperty("reused").EnumerateArray(),
            unit => unit.GetProperty("receipt").GetString() == receipt);
        Assert.DoesNotContain(secondDoc.RootElement.GetProperty("results").EnumerateArray()
            .SelectMany(item => item.GetProperty("spans").EnumerateArray()),
            span => span.GetProperty("receipt").GetString() == receipt);
    }

    [Theory]
    [InlineData("paths", 600, false)]
    [InlineData("outline", 900, false)]
    [InlineData("slices", 900, false)]
    [InlineData("slices", 2000, false)]
    [InlineData("slices", 900, true)]
    public void Compact_EnforcesExactResponseBudget(string detail, int budget, bool calibrated)
    {
        using FixtureWorkspace ws = Indexed();
        if (calibrated)
        {
            string configPath = ws.PathOf("repoctx.config.json");
            var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            config["tokens"] = new JsonObject { ["profile"] = "claude" };
            File.WriteAllText(configPath, config.ToJsonString());
        }
        CliResult response = ws.Run("context", "change login", "--detail", detail,
            "--format", "json", "--compact", "--response-budget-tokens",
            budget.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(0, response.ExitCode);
        Assert.True(Math.Ceiling(Tokens.Count(response.StdOut) * (calibrated ? 1.2 : 1)) <= budget,
            response.StdOut);
        using JsonDocument doc = JsonDocument.Parse(response.StdOut);
        if (calibrated)
            Assert.Equal("claude", doc.RootElement.GetProperty("token_profile").GetString());
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() > 0);
        Assert.Equal(5, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Compact_ShortfallProvidesFittingRetry()
    {
        using FixtureWorkspace ws = Indexed();
        string[] args = ["context", "login", "--detail", "slices", "--format", "json", "--compact"];
        CliResult failed = ws.Run([.. args, "--response-budget-tokens", "40"]);
        Assert.Equal(3, failed.ExitCode);
        Assert.Empty(failed.StdOut);
        Match match = Regex.Match(failed.StdErr, @"retry_budget_tokens=(\d+)");
        Assert.True(match.Success);
        int retryBudget = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        CliResult retry = ws.Run([.. args, "--response-budget-tokens", match.Groups[1].Value]);
        Assert.Equal(0, retry.ExitCode);
        Assert.True(Tokens.Count(retry.StdOut) <= retryBudget);
    }

    [Fact]
    public void Compact_RequiresJson()
    {
        CliResult response = CliHarness.Run("context", "login", "--format", "md", "--compact");
        Assert.Equal(3, response.ExitCode);
        Assert.Contains("--compact requires --format json", response.StdErr, StringComparison.Ordinal);
    }
}
