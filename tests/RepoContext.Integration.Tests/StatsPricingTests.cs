using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the money view over savings (ADR 0012): with a
/// configured input rate, stats reports net saved in currency across formats;
/// without one, no money figure appears.
/// </summary>
public class StatsPricingTests
{
    [Fact]
    public void WithPricing_ReportsMoneyAcrossFormats()
    {
        using FixtureWorkspace ws = UsedWorkspace();
        SetPricing(ws, 5.0, "USD");

        Assert.Contains("net saved ($)", ws.Run("stats").StdOut);
        Assert.Contains("net saved (money)", ws.Run("stats", "--format", "md").StdOut);

        using JsonDocument json = JsonDocument.Parse(ws.Run("stats", "--format", "json").StdOut);
        Assert.Equal("USD", json.RootElement.GetProperty("currency").GetString());
        Assert.True(json.RootElement.TryGetProperty("saved_cost", out _));
        Assert.Equal(5.0, json.RootElement.GetProperty("input_per_mtok").GetDouble());

        Assert.Contains("Net saved (money)", ws.Run("stats", "--format", "html").StdOut);
    }

    [Fact]
    public void WithoutPricing_NoMoneyFigure()
    {
        using FixtureWorkspace ws = UsedWorkspace();

        Assert.DoesNotContain("net saved ($)", ws.Run("stats").StdOut);
        using JsonDocument json = JsonDocument.Parse(ws.Run("stats", "--format", "json").StdOut);
        Assert.False(json.RootElement.TryGetProperty("saved_cost", out _));
        Assert.False(json.RootElement.TryGetProperty("currency", out _));
    }

    private static FixtureWorkspace UsedWorkspace()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        // A slices call so there is a real (credited) saving in the ledger.
        ws.Run("context", "change the login logic", "--detail", "slices", "--top", "3",
            "--format", "json");
        return ws;
    }

    private static void SetPricing(FixtureWorkspace ws, double inputPerMtok, string currency)
    {
        string configPath = ws.PathOf("repoctx.config.json");
        JsonNode config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["pricing"] = new JsonObject
        {
            ["inputPerMtok"] = inputPerMtok,
            ["currency"] = currency,
        };
        File.WriteAllText(configPath, config.ToJsonString());
    }
}
