using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the token-savings dashboard (ADR 0011): query commands
/// append to <c>.repoctx/stats.jsonl</c> and <c>repoctx stats</c> aggregates it.
/// </summary>
public class StatsDashboardTests
{
    private const string StatsFile = ".repoctx/stats.jsonl";

    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    [Fact]
    public void Queries_AreRecorded_AndAggregatedIntoTheDashboard()
    {
        using FixtureWorkspace ws = Indexed();

        Assert.Equal(0, ws.Run(
            "context", "change the login logic", "--detail", "slices", "--format", "json").ExitCode);
        Assert.Equal(0, ws.Run("outline", "src/auth/login.ts", "--format", "json").ExitCode);
        Assert.Equal(0, ws.Run("search", "login", "--format", "json").ExitCode);

        string log = ws.PathOf(StatsFile);
        Assert.True(File.Exists(log));
        Assert.Equal(3, File.ReadAllLines(log).Length);

        CliResult stats = ws.Run("stats", "--format", "json");
        Assert.Equal(0, stats.ExitCode);

        using JsonDocument doc = JsonDocument.Parse(stats.StdOut);
        JsonElement root = doc.RootElement;
        Assert.Equal("stats", root.GetProperty("command").GetString());
        Assert.Equal(3, root.GetProperty("calls").GetInt32());
        Assert.True(root.GetProperty("served_tokens").GetInt64() > 0);
        // The slices bundle and the outline both replace full file reads.
        Assert.True(root.GetProperty("replaced_tokens").GetInt64() > 0);
        Assert.Equal(
            root.GetProperty("replaced_tokens").GetInt64() - root.GetProperty("served_tokens").GetInt64(),
            root.GetProperty("saved_tokens").GetInt64());

        var commands = root.GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("command").GetString())
            .ToList();
        Assert.Equal(["context", "outline", "search"], commands);

        JsonElement day = Assert.Single(root.GetProperty("days").EnumerateArray());
        Assert.Equal(3, day.GetProperty("calls").GetInt32());

        // The stats command itself is never recorded.
        Assert.Equal(3, File.ReadAllLines(log).Length);
    }

    [Fact]
    public void OutlineWithoutSymbols_DoesNotClaimAReplacedRead()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("outline", "docs/architecture.md", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument response = JsonDocument.Parse(result.StdOut);
        Assert.Empty(response.RootElement.GetProperty("symbols").EnumerateArray());

        string line = Assert.Single(File.ReadAllLines(ws.PathOf(StatsFile)));
        using JsonDocument record = JsonDocument.Parse(line);
        Assert.Equal(0, record.RootElement.GetProperty("replaced").GetInt32());
    }

    [Fact]
    public void ContextWithKnownHash_RecordsTheAvoidedReRead()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult first = ws.Run("context", "change the login logic", "--format", "json");
        using JsonDocument firstDoc = JsonDocument.Parse(first.StdOut);
        JsonElement login = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        string hash = login.GetProperty("hash").GetString()!;
        // --known is an assertion that the caller holds the file content, not
        // permission to echo a hash learned from a pointer-only response.
        Assert.NotEmpty(File.ReadAllText(ws.PathOf("src/auth/login.ts")));

        ws.Run("context", "change the login logic",
            "--known", $"src/auth/login.ts@{hash}", "--format", "json");

        string[] lines = File.ReadAllLines(ws.PathOf(StatsFile));
        Assert.Equal(2, lines.Length);
        using JsonDocument record = JsonDocument.Parse(lines[1]);
        Assert.True(record.RootElement.GetProperty("unchanged").GetInt32() >= 1);
        Assert.True(record.RootElement.GetProperty("replaced").GetInt32() > 0);
    }

    [Fact]
    public void StatsOutput_IsDeterministicForTheSameLog()
    {
        using FixtureWorkspace ws = Indexed();
        ws.Run("search", "login", "--format", "json");

        CliResult first = ws.Run("stats", "--format", "json");
        CliResult second = ws.Run("stats", "--format", "json");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);
    }

    [Fact]
    public void Stats_WithoutAnyUsage_ReportsAnEmptyDashboard()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult text = ws.Run("stats");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("no usage recorded yet", text.StdOut);

        CliResult json = ws.Run("stats", "--format", "json");
        using JsonDocument doc = JsonDocument.Parse(json.StdOut);
        Assert.Equal(0, doc.RootElement.GetProperty("calls").GetInt32());
    }

    [Fact]
    public void Recording_CanBeDisabledWithEnvironmentVariable()
    {
        using FixtureWorkspace ws = Indexed();

        var env = new Dictionary<string, string> { ["REPOCTX_NO_STATS"] = "1" };
        Assert.Equal(0, ws.RunWithEnv(env, "search", "login", "--format", "json").ExitCode);

        Assert.False(File.Exists(ws.PathOf(StatsFile)));
    }

    [Fact]
    public void HtmlFormat_RendersASelfContainedDashboard()
    {
        using FixtureWorkspace ws = Indexed();
        ws.Run("context", "change the login logic", "--detail", "slices", "--format", "json");

        CliResult first = ws.Run("stats", "--format", "html");
        Assert.Equal(0, first.ExitCode);
        Assert.StartsWith("<!doctype html>", first.StdOut);
        Assert.Contains("<svg", first.StdOut);
        Assert.Contains("reads replaced", first.StdOut);
        // Theme tokens must be inherited by body as well as the dashboard so
        // the outer canvas follows both the light and dark palettes.
        Assert.Contains(":root{color-scheme:light dark;--plane:#f9f9f7;", first.StdOut);
        Assert.Contains(
            "@media (prefers-color-scheme:dark){:root:not([data-theme=light]){--plane:#0d0d0d;",
            first.StdOut);
        Assert.Contains(":root[data-theme=dark]{--plane:#0d0d0d;", first.StdOut);
        Assert.Contains("body{background:var(--plane)}", first.StdOut);
        Assert.DoesNotContain(".viz-root{--plane:", first.StdOut);
        // Self-contained: no external resources (the no-network principle).
        Assert.DoesNotContain("http://", first.StdOut);
        Assert.DoesNotContain("https://", first.StdOut);

        // Deterministic given the same log.
        Assert.Equal(first.StdOut, ws.Run("stats", "--format", "html").StdOut);
    }

    [Fact]
    public void Open_WritesTheHtmlDashboardFile()
    {
        using FixtureWorkspace ws = Indexed();
        ws.Run("search", "login", "--format", "json");

        // REPOCTX_NO_LAUNCH suppresses the browser launch (headless test/CI).
        var env = new Dictionary<string, string> { ["REPOCTX_NO_LAUNCH"] = "1" };
        CliResult result = ws.RunWithEnv(env, "stats", "--open");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stats.html", result.StdOut);
        string html = File.ReadAllText(ws.PathOf(".repoctx/stats.html"));
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Token savings", html);
    }

    [Fact]
    public void Stats_OutsideAnInitializedRepo_ExitsWithNoIndex()
    {
        string dir = Path.Combine(Path.GetTempPath(), "repoctx-itests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(2, CliHarness.RunIn(dir, "stats").ExitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
