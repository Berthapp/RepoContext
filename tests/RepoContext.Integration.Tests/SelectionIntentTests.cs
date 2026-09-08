using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests;

public sealed class SelectionIntentTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);
        return ws;
    }

    [Theory]
    [InlineData("json", false, "fix", 900)]
    [InlineData("json", true, "review", 900)]
    [InlineData("json", true, "explain", 2000)]
    [InlineData("md", false, "explain", 900)]
    [InlineData("text", false, "fix", 900)]
    public void IntentAndDiagnostics_RespectExactBudget_AndAreDeterministic(string format, bool compact, string intent, int budget)
    {
        using var ws = Indexed();
        string[] args = ["context", "review src/auth/login.ts.", "--detail", "slices", "--format", format,
            "--intent", intent, "--explain", "--response-budget-tokens", budget.ToString(CultureInfo.InvariantCulture),
            .. compact ? new[] { "--compact" } : []];
        CliResult result = ws.Run(args);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(Tokens.Count(result.StdOut), 1, budget);
        Assert.Equal(result.StdOut, ws.Run(args).StdOut);
        if (format == "json")
        {
            using JsonDocument doc = JsonDocument.Parse(result.StdOut);
            Assert.Equal(intent, doc.RootElement.GetProperty("intent").GetString());
            Assert.Contains(doc.RootElement.GetProperty("results").EnumerateArray()
                .SelectMany(item => item.GetProperty("reasons").EnumerateArray()),
                reason => reason.GetString()!.StartsWith("intent:" + intent + ":", StringComparison.Ordinal));
            JsonElement selection = doc.RootElement.GetProperty("selection");
            Assert.Equal(selection.GetProperty("omitted").GetInt32(),
                selection.GetProperty("unlisted").GetInt32() + selection.GetProperty("samples").GetArrayLength());
            Assert.True(doc.RootElement.GetProperty("count").GetInt32() > 0);
        }
        else
        {
            Assert.Contains("Intent: " + intent, result.StdOut, StringComparison.Ordinal);
            Assert.Contains("Selection:", result.StdOut, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("md", false)]
    [InlineData("text", false)]
    public void Shortfall_RetryIncludesDiagnostics_AndFits(string format, bool compact)
    {
        using var ws = Indexed();
        string[] args = ["context", "src/auth/login.ts", "--detail", "slices", "--format", format,
            "--intent", "fix", "--explain", .. compact ? new[] { "--compact" } : []];
        CliResult failed = ws.Run([.. args, "--response-budget-tokens", "40"]);
        Assert.Equal(3, failed.ExitCode);
        Assert.Empty(failed.StdOut);
        Match match = Regex.Match(failed.StdErr, @"retry_budget_tokens=(\d+)");
        Assert.True(match.Success, failed.StdErr);
        int budget = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        CliResult retry = ws.Run([.. args, "--response-budget-tokens", match.Groups[1].Value]);
        Assert.Equal(0, retry.ExitCode);
        Assert.InRange(Tokens.Count(retry.StdOut), 1, budget);
    }

    [Fact]
    public void DefaultContractIsUnchanged_AndUnknownIntentIsRejected()
    {
        using var ws = Indexed();
        CliResult result = ws.Run("context", "login", "--format", "json");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.False(doc.RootElement.TryGetProperty("intent", out _));
        Assert.False(doc.RootElement.TryGetProperty("selection", out _));
        CliResult invalid = ws.Run("context", "login", "--intent", "repair");
        Assert.Equal(3, invalid.ExitCode);
        Assert.Contains("Invalid --intent", invalid.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CalibratedBudget_IncludesIntentAndDiagnostics()
    {
        using var ws = Indexed();
        string configPath = ws.PathOf("repoctx.config.json");
        JsonObject config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config["tokens"] = new JsonObject { ["profile"] = "claude" };
        File.WriteAllText(configPath, config.ToJsonString());
        CliResult result = ws.Run("context", "src/auth/login.ts", "--intent", "fix", "--explain",
            "--detail", "slices", "--format", "json", "--compact", "--response-budget-tokens", "1200");
        Assert.Equal(0, result.ExitCode);
        Assert.True(Math.Ceiling(Tokens.Count(result.StdOut) * 1.2) <= 1200);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal("claude", doc.RootElement.GetProperty("token_profile").GetString());
        Assert.True(doc.RootElement.TryGetProperty("selection", out _));
    }
}
