using System.Text.Json;
using System.Text.Json.Nodes;

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
        Assert.True(rawFirst > 0);

        SetTokenProfile(ws, "claude");

        CliResult scaled = ws.Run("context", "login", "--top", "3", "--format", "json");
        Assert.Equal(0, scaled.ExitCode);
        using JsonDocument scaledDoc = JsonDocument.Parse(scaled.StdOut);
        Assert.Equal("claude", scaledDoc.RootElement.GetProperty("token_profile").GetString());
        int scaledFirst = scaledDoc.RootElement.GetProperty("results")[0]
            .GetProperty("estimated_tokens").GetInt32();
        Assert.Equal((int)Math.Ceiling(rawFirst * 1.2), scaledFirst);
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
