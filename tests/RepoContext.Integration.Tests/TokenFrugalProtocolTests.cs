using System.Text.Json;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

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
        Assert.All(
            root.GetProperty("symbols").EnumerateArray(),
            symbol => Assert.Equal(43, symbol.GetProperty("receipt").GetString()!.Length));
    }

    [Fact]
    public void Outline_UnknownFile_FailsWithError()
    {
        using FixtureWorkspace ws = Indexed();
        Assert.Equal(1, ws.Run("outline", "src/nope.ts").ExitCode);
    }

    [Fact]
    public void OutlineReceipt_IsReusableByFocusedContext()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult outline = ws.Run("outline", "src/auth/login.ts", "--format", "json");
        using JsonDocument outlineDoc = JsonDocument.Parse(outline.StdOut);
        string receipt = outlineDoc.RootElement.GetProperty("symbols").EnumerateArray()
            .Single(symbol => symbol.GetProperty("name").GetString() == "loginUser")
            .GetProperty("receipt").GetString()!;

        CliResult context = ws.Run(
            "context", "loginUser", "--detail", "outline",
            "--seen", receipt, "--format", "json");
        Assert.Equal(0, context.ExitCode);

        using JsonDocument contextDoc = JsonDocument.Parse(context.StdOut);
        Assert.Contains(
            contextDoc.RootElement.GetProperty("reused").EnumerateArray(),
            unit => unit.GetProperty("receipt").GetString() == receipt);
        Assert.DoesNotContain(
            contextDoc.RootElement.GetProperty("results").EnumerateArray()
                .Where(result => result.TryGetProperty("symbols", out _))
                .SelectMany(result => result.GetProperty("symbols").EnumerateArray()),
            symbol => symbol.GetProperty("receipt").GetString() == receipt);
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
        Assert.Equal(12, doc.RootElement.GetProperty("content_state").GetString()!.Length);
        Assert.Equal(12, doc.RootElement.GetProperty("worktree_state").GetString()!.Length);
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
        Assert.NotEqual(
            doc.RootElement.GetProperty("content_state").GetString(),
            doc.RootElement.GetProperty("worktree_state").GetString());
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

    /// <summary>
    /// v3: an explicit full-file <c>--known</c> assertion is acknowledged in
    /// <c>reused</c> and carries no content. It deliberately no longer occupies a
    /// slot in <c>results</c> — that was the audited starvation bug where echoing
    /// the top candidates returned markers and no new context.
    /// </summary>
    [Fact]
    public void Context_Known_IsAcknowledgedAsReused_WithoutConsumingAResultSlot()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult first = ws.Run("context", "change the login logic", "--format", "json");
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement login = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        string hash = login.GetProperty("hash").GetString()!;
        int firstCount = firstDoc.RootElement.GetProperty("count").GetInt32();

        CliResult second = ws.Run(
            "context", "change the login logic",
            "--known", $"src/auth/login.ts@{hash}", "--format", "json");
        Assert.Equal(0, second.ExitCode);

        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        Assert.DoesNotContain(
            secondDoc.RootElement.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        Assert.Equal(1, secondDoc.RootElement.GetProperty("reused_count").GetInt32());
        Assert.Contains(
            secondDoc.RootElement.GetProperty("reused").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "src/auth/login.ts");

        // The freed slot goes to new context, not to a marker.
        Assert.True(secondDoc.RootElement.GetProperty("count").GetInt32() >= firstCount - 1);
    }

    /// <summary>
    /// v3: a receipt suppresses exactly one delivered unit. The rest of the same
    /// file must still arrive, which is what a whole-file hash could never
    /// express.
    /// </summary>
    [Fact]
    public void Context_Seen_SuppressesOnlyTheAcknowledgedSpan()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult first = ws.Run(
            "context", "change the login logic", "--detail", "slices", "--format", "json");
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement item = firstDoc.RootElement.GetProperty("results")[0];
        string path = item.GetProperty("path").GetString()!;
        JsonElement span = item.GetProperty("spans")[0];
        string receipt = span.GetProperty("receipt").GetString()!;
        int start = span.GetProperty("start_line").GetInt32();

        CliResult second = ws.Run(
            "context", "change the login logic", "--detail", "slices",
            "--seen", receipt, "--format", "json");
        Assert.Equal(0, second.ExitCode);

        using JsonDocument secondDoc = JsonDocument.Parse(second.StdOut);
        Assert.Equal(1, secondDoc.RootElement.GetProperty("reused_count").GetInt32());
        Assert.Contains(
            secondDoc.RootElement.GetProperty("reused").EnumerateArray(),
            r => r.GetProperty("receipt").GetString() == receipt
                && r.GetProperty("path").GetString() == path
                && r.GetProperty("start_line").GetInt32() == start);

        // No delivered span anywhere repeats the acknowledged unit.
        foreach (JsonElement result in secondDoc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (result.TryGetProperty("spans", out JsonElement spans))
            {
                Assert.DoesNotContain(
                    spans.EnumerateArray(), s => s.GetProperty("receipt").GetString() == receipt);
            }
        }
    }

    /// <summary>A malformed receipt is rejected outright rather than silently matching nothing.</summary>
    [Fact]
    public void Context_Seen_RejectsAMalformedReceipt()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "context", "login", "--seen", "not-a-receipt", "--format", "json");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Invalid --seen", result.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A response budget that cannot fit the smallest useful payload returns
    /// actionable sizing data on the error channel and emits no partial result.
    /// </summary>
    [Fact]
    public void Context_TooSmallResponseBudget_ReturnsAFittingRetryBudget()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "context", "change the login logic", "--detail", "slices",
            "--response-budget-tokens", "40", "--format", "json");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("retry_budget_tokens=", result.StdErr, StringComparison.Ordinal);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void Context_AcceptedResponseBudgetMatchesExactCliStdout()
    {
        using FixtureWorkspace ws = Indexed();
        const int budget = 900;

        CliResult result = ws.Run(
            "context", "change the login logic", "--detail", "slices",
            "--response-budget-tokens", budget.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--format", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(Tokens.Count(result.StdOut) <= budget);
        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        Assert.Equal(
            budget,
            document.RootElement.GetProperty("budgets").GetProperty("response_tokens").GetInt32());
    }

    [Fact]
    public void Context_BestFitReportsTheBudgetThatSkippedAHigherRankedCandidate()
    {
        using FixtureWorkspace ws = Indexed();
        const string query = "change the login logic";

        CliResult unbudgeted = ws.Run(
            "context", query, "--detail", "slices", "--top", "1", "--format", "json");
        using JsonDocument unbudgetedDoc = JsonDocument.Parse(unbudgeted.StdOut);
        string highestRanked = unbudgetedDoc.RootElement.GetProperty("results")[0]
            .GetProperty("path").GetString()!;

        CliResult constrained = ws.Run(
            "context", query, "--detail", "slices", "--top", "1",
            "--response-budget-tokens", "400", "--format", "json");
        Assert.Equal(0, constrained.ExitCode);

        using JsonDocument constrainedDoc = JsonDocument.Parse(constrained.StdOut);
        JsonElement root = constrainedDoc.RootElement;
        Assert.NotEqual(
            highestRanked,
            root.GetProperty("results")[0].GetProperty("path").GetString());
        Assert.True(
            root.GetProperty("omitted_by").GetProperty("response_budget").GetInt32() > 0);
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

    [Fact]
    public void EveryIndexBackedQueryRejectsAStaleProducer()
    {
        using FixtureWorkspace ws = Indexed();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={ws.PathOf(".repoctx/index.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE meta SET value = 'stale-producer' WHERE key = $key";
            command.Parameters.AddWithValue("$key", MetaKeys.AnalysisProducerVersion);
            command.ExecuteNonQuery();
        }

        string[][] commands =
        [
            ["search", "login"],
            ["related", "src/auth/login.ts"],
            ["outline", "src/auth/login.ts"],
            ["context", "login"],
            ["architecture"],
            ["changed"],
        ];

        foreach (string[] command in commands)
        {
            CliResult result = ws.Run(command);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains(
                "analysis version", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void QueryFreshnessSeparatesIndexingSettingsFromLiveRanking()
    {
        using FixtureWorkspace ws = Indexed();
        string configPath = ws.PathOf("repoctx.config.json");
        RepoctxConfig original = ConfigStore.Load(configPath);

        ConfigStore.Save(configPath, original with
        {
            Ranking = original.Ranking with
            {
                Weights = original.Ranking.Weights with { Fts = 0.8 },
            },
        });
        Assert.Equal(0, ws.Run("search", "login").ExitCode);

        ConfigStore.Save(configPath, original with { Include = ["."] });
        CliResult stale = ws.Run("search", "login");
        Assert.Equal(2, stale.ExitCode);
        Assert.Contains("indexing settings", stale.StdErr, StringComparison.OrdinalIgnoreCase);
    }
}
