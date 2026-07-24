using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests;

/// <summary>
/// Query-time token calibration end-to-end (ADR 0012): the index stores raw
/// o200k counts; a config profile scales what responses report — with no
/// re-index between runs.
/// </summary>
public class TokenCalibrationTests
{
    [Fact]
    public void ClaudeProfile_ScalesReportedCounts_WithoutReindex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult raw = ws.Run("context", "login", "--top", "3", "--format", "json");
        Assert.Equal(0, raw.ExitCode);
        using JsonDocument rawDoc = JsonDocument.Parse(raw.StdOut);
        Assert.False(rawDoc.RootElement.TryGetProperty("token_profile", out _));
        int rawFirst = rawDoc.RootElement.GetProperty("results")[0]
            .GetProperty("estimated_tokens").GetInt32();
        int rawProjected = rawDoc.RootElement.GetProperty("results")[0]
            .GetProperty("projected_read_tokens").GetInt32();
        Assert.True(rawFirst > 0);
        Assert.True(rawProjected > 0);

        SetTokenProfile(ws, "claude");

        CliResult scaled = ws.Run("context", "login", "--top", "3", "--format", "json");
        Assert.Equal(0, scaled.ExitCode);
        using JsonDocument scaledDoc = JsonDocument.Parse(scaled.StdOut);
        Assert.Equal("claude", scaledDoc.RootElement.GetProperty("token_profile").GetString());
        int scaledFirst = scaledDoc.RootElement.GetProperty("results")[0]
            .GetProperty("estimated_tokens").GetInt32();
        int scaledProjected = scaledDoc.RootElement.GetProperty("results")[0]
            .GetProperty("projected_read_tokens").GetInt32();
        Assert.Equal((int)Math.Ceiling(rawFirst * 1.2), scaledFirst);
        Assert.Equal((int)Math.Ceiling(rawProjected * 1.2), scaledProjected);
    }

    [Fact]
    public void ClaudeProfile_ScalesV3Content_AndEnforcesCalibratedHardBudget()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult raw = ws.Run(
            "context", "login", "--detail", "slices", "--top", "3", "--format", "json");
        Assert.Equal(0, raw.ExitCode);
        using JsonDocument rawDoc = JsonDocument.Parse(raw.StdOut);
        JsonElement rawItem = rawDoc.RootElement.GetProperty("results").EnumerateArray()
            .First(item => item.GetProperty("content_tokens").GetInt32() > 0);
        string path = rawItem.GetProperty("path").GetString()!;
        int rawContent = rawItem.GetProperty("content_tokens").GetInt32();
        int rawFile = rawItem.GetProperty("file_tokens").GetInt32();

        SetTokenProfile(ws, "claude");

        CliResult scaled = ws.Run(
            "context", "login", "--detail", "slices", "--top", "3", "--format", "json");
        Assert.Equal(0, scaled.ExitCode);
        using JsonDocument scaledDoc = JsonDocument.Parse(scaled.StdOut);
        JsonElement scaledItem = scaledDoc.RootElement.GetProperty("results").EnumerateArray()
            .First(item => item.GetProperty("path").GetString() == path);
        Assert.Equal(
            (int)Math.Ceiling(rawContent * 1.2),
            scaledItem.GetProperty("content_tokens").GetInt32());
        Assert.Equal(
            (int)Math.Ceiling(rawFile * 1.2),
            scaledItem.GetProperty("file_tokens").GetInt32());

        CliResult tooSmall = ws.Run(
            "context", "login", "--detail", "slices", "--top", "3",
            "--response-budget-tokens", "1", "--format", "json");
        Assert.Equal(3, tooSmall.ExitCode);
        Match retryMatch = Regex.Match(
            tooSmall.StdErr, @"retry_budget_tokens=(\d+)", RegexOptions.CultureInvariant);
        Assert.True(retryMatch.Success);
        int retryBudget = int.Parse(
            retryMatch.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        CliResult retry = ws.Run(
            "context", "login", "--detail", "slices", "--top", "3",
            "--response-budget-tokens", retryBudget.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--format", "json");
        Assert.Equal(0, retry.ExitCode);
        Assert.True(
            (int)Math.Ceiling(Tokens.Count(retry.StdOut) * 1.2) <= retryBudget,
            "The exact CLI surface, including its newline, must fit after Claude calibration.");
    }

    [Fact]
    public void ClaudeProfile_ScalesOutlineFullReadCost()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");

        CliResult raw = ws.Run("outline", "src/auth/login.ts", "--format", "json");
        int rawTokens = JsonDocument.Parse(raw.StdOut).RootElement
            .GetProperty("estimated_tokens").GetInt32();

        SetTokenProfile(ws, "claude");

        CliResult scaled = ws.Run("outline", "src/auth/login.ts", "--format", "json");
        int scaledTokens = JsonDocument.Parse(scaled.StdOut).RootElement
            .GetProperty("estimated_tokens").GetInt32();
        Assert.Equal((int)Math.Ceiling(rawTokens * 1.2), scaledTokens);
    }

    private static void SetTokenProfile(FixtureWorkspace ws, string profile)
    {
        string configPath = ws.PathOf("repoctx.config.json");
        JsonNode config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["tokens"] = new JsonObject { ["profile"] = profile };
        File.WriteAllText(configPath, config.ToJsonString());
    }
}
