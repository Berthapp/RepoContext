using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end coverage of the artifact layer (ADR 0019) against a repository
/// shaped like the one this feature exists for: exported tickets, a
/// specification page, a requirements file, acceptance criteria, the
/// implementation and its test.
/// </summary>
/// <remarks>
/// The workflow these tests stand for is "reconcile a ticket with the
/// documentation and the code, then write the missing tests". Every assertion
/// below is a step an agent would otherwise perform by grepping the working
/// tree and reading whatever came back.
/// </remarks>
public sealed class ArtifactRepositoryTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("artifact-repo");

        // Guard against a fixture file that never reached the checkout: an
        // ignored directory name silently costs the corpus its artifacts, and
        // the resulting failures point at the product rather than the fixture.
        string[] required = ["exports/jira/PAY-142.json", "exports/confluence/refund-policy.md"];
        foreach (string path in required)
        {
            Assert.True(File.Exists(ws.PathOf(path)), $"missing fixture file: {path}");
        }

        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);
        return ws;
    }

    private static IReadOnlyList<string> Paths(JsonElement element, string property) =>
        [.. element.GetProperty(property).EnumerateArray()
            .Select(e => e.GetProperty("path").GetString() ?? string.Empty)];

    [Fact]
    public void Trace_LinksATicketAcrossTicketDocumentRequirementCodeAndTest()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "PAY-142", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        JsonElement root = doc.RootElement;
        Assert.Equal("trace", root.GetProperty("command").GetString());
        Assert.Equal(
            [
                "exports/confluence/refund-policy.md",
                "exports/jira/PAY-142.json",
                "exports/jira/PAY-155.json",
                "requirements/REQ-payments.yaml",
                "src/billing/refund.ts",
                "tests/billing/refund.test.ts",
            ],
            Paths(root, "mentions"));

        // Every mention carries the line it was found on and what reading the
        // file would have cost instead.
        JsonElement code = root.GetProperty("mentions").EnumerateArray()
            .Single(m => m.GetProperty("path").GetString() == "src/billing/refund.ts");
        Assert.NotEmpty(code.GetProperty("lines").EnumerateArray());
        Assert.True(code.GetProperty("file_tokens").GetInt32() > 0);
        Assert.True(root.GetProperty("projected_read_tokens").GetInt32() > 0);
    }

    [Fact]
    public void Trace_ResolvesALinkSharedByATicketAndItsSpecification()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "trace",
            "https://example.atlassian.net/wiki/spaces/PAY/pages/4711/Refund+Policy",
            "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(
            ["exports/confluence/refund-policy.md", "exports/jira/PAY-142.json"],
            Paths(doc.RootElement, "mentions"));
    }

    [Fact]
    public void Trace_ResolvesASymbolToItsDeclarationAndTheDocumentsNamingIt()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "AuditLog", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(["src/billing/audit.ts"], Paths(doc.RootElement, "definitions"));
        Assert.Contains("exports/confluence/refund-policy.md", Paths(doc.RootElement, "mentions"));
    }

    [Fact]
    public void Trace_OfAPath_FindsDocumentsThatNameItByFileNameAlone()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "src/billing/audit.ts", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);

        // The specification names the full path; the point of the suffix match
        // is that "audit.ts" alone would have been found too.
        Assert.Contains("exports/confluence/refund-policy.md", Paths(doc.RootElement, "mentions"));
    }

    [Fact]
    public void Trace_OfAnUnknownKey_OffersTheKeysThatDoExist()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "PAY-999", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(0, doc.RootElement.GetProperty("mention_count").GetInt32());
        Assert.Equal(
            ["PAY-100", "PAY-142", "PAY-155"],
            doc.RootElement.GetProperty("suggestions").EnumerateArray()
                .Select(e => e.GetString()));
    }

    [Fact]
    public void Trace_RespectsThePathScope()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "trace", "PAY-142", "--path", "src", "--path", "tests", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(
            ["src/billing/refund.ts", "tests/billing/refund.test.ts"],
            Paths(doc.RootElement, "mentions"));
    }

    /// <summary>
    /// A dot-prefixed name is a file name, not a path prefix. Stripping the dot
    /// turned <c>.editorconfig</c> into <c>editorconfig</c>, which resolves to
    /// nothing - so a dotfile was reported as unknown rather than as indexed.
    /// </summary>
    [Fact]
    public void Trace_OfADotfile_ResolvesToTheIndexedFile()
    {
        using FixtureWorkspace ws = Indexed();
        File.WriteAllText(ws.PathOf(".editorconfig"), "root = true\n");
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult result = ws.Run("trace", ".editorconfig", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(
            [".editorconfig"],
            doc.RootElement.GetProperty("resolved").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// Every token figure in one response has to be on the same scale, or the
    /// per-file numbers contradict the total the agent budgets against.
    /// </summary>
    [Fact]
    public void Trace_TokenFiguresUseTheConfiguredCalibration()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult raw = ws.Run("trace", "PAY-142", "--format", "json");
        Assert.Equal(0, raw.ExitCode);

        string configPath = ws.PathOf("repoctx.config.json");
        File.WriteAllText(
            configPath,
            File.ReadAllText(configPath).Replace(
                "\"profile\": \"o200k\"", "\"profile\": \"claude\"", StringComparison.Ordinal));

        CliResult scaled = ws.Run("trace", "PAY-142", "--format", "json");
        Assert.Equal(0, scaled.ExitCode);

        using JsonDocument rawDoc = JsonDocument.Parse(raw.StdOut);
        using JsonDocument scaledDoc = JsonDocument.Parse(scaled.StdOut);

        static (int Sum, int Total) Figures(JsonDocument doc) =>
            (doc.RootElement.GetProperty("mentions").EnumerateArray()
                .Sum(m => m.GetProperty("file_tokens").GetInt32()),
             doc.RootElement.GetProperty("projected_read_tokens").GetInt32());

        (int rawSum, int rawTotal) = Figures(rawDoc);
        (int scaledSum, int scaledTotal) = Figures(scaledDoc);

        Assert.Equal(rawSum, rawTotal);
        Assert.Equal(scaledSum, scaledTotal);
        Assert.True(scaledTotal > rawTotal, "the claude profile scales counts up");
    }

    /// <summary>
    /// A path written relative to somewhere else still names the same file. The
    /// prefix is stripped; the dot of a dot-file is not.
    /// </summary>
    [Fact]
    public void Trace_OfARelativePath_ResolvesToTheIndexedFile()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "../src/billing/refund.ts", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Equal(
            ["src/billing/refund.ts"],
            doc.RootElement.GetProperty("resolved").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// The scope binds the resolved file too. Naming a path exactly used to
    /// report it as resolved regardless of scope, because the file lookup did
    /// not take one - so an excluded file reappeared with zero mentions.
    /// </summary>
    [Fact]
    public void Trace_ScopeExcludesEvenAnExactlyNamedFile()
    {
        using FixtureWorkspace ws = Indexed();

        // "features" holds neither the file nor anything that mentions it.
        CliResult result = ws.Run(
            "trace", "src/billing/refund.ts", "--path", "features", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Empty(doc.RootElement.GetProperty("resolved").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("mentions").EnumerateArray());

        // Within a scope that does hold them, the mentions still arrive.
        CliResult scoped = ws.Run(
            "trace", "src/billing/refund.ts", "--path", "exports", "--format", "json");
        Assert.Equal(0, scoped.ExitCode);
        using JsonDocument scopedDoc = JsonDocument.Parse(scoped.StdOut);
        Assert.Equal(
            ["exports/confluence/refund-policy.md", "exports/jira/PAY-142.json"],
            Paths(scopedDoc.RootElement, "mentions"));
    }

    [Fact]
    public void Related_ReportsTheDocumentsThatDescribeAFile()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("related", "src/billing/refund.ts", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        List<string> referencedBy =
        [
            .. doc.RootElement.GetProperty("results").EnumerateArray()
                .Where(e => e.GetProperty("relation").GetString() == "referenced_by")
                .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
        ];

        Assert.Equal(
            ["exports/confluence/refund-policy.md", "exports/jira/PAY-142.json"],
            referencedBy.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Outline_ExplainsAnArtifactWithoutReadingIt()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult yaml = ws.Run("outline", "requirements/REQ-payments.yaml", "--format", "json");
        Assert.Equal(0, yaml.ExitCode);
        using JsonDocument yamlDoc = JsonDocument.Parse(yaml.StdOut);
        Assert.Contains(
            yamlDoc.RootElement.GetProperty("symbols").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "requirements.REQ-4711");

        CliResult feature = ws.Run("outline", "features/refund.feature", "--format", "json");
        Assert.Equal(0, feature.ExitCode);
        using JsonDocument featureDoc = JsonDocument.Parse(feature.StdOut);
        Assert.Equal(
            ["Refunds", "Refund inside the window", "Refund outside the window"],
            featureDoc.RootElement.GetProperty("symbols").EnumerateArray()
                .Select(s => s.GetProperty("name").GetString()));
    }

    [Fact]
    public void Context_SeededByATicketKey_PullsTheWholeWorkItemTogether()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("context", "write tests for PAY-142", "--top", "4", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        List<JsonElement> items = [.. doc.RootElement.GetProperty("results").EnumerateArray()];

        Assert.Contains(items, i => i.GetProperty("path").GetString() == "src/billing/refund.ts");
        Assert.All(
            items.Where(i => i.GetProperty("path").GetString()!.StartsWith("exports/", StringComparison.Ordinal)),
            i => Assert.Contains(
                i.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()),
                reason => reason == "ref:PAY-142"));
    }

    [Fact]
    public void Context_PathScope_ExcludesEvenGraphNeighbours()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run(
            "context", "refund window", "--top", "8", "--path", "src", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        IReadOnlyList<string> paths = Paths(doc.RootElement, "results");

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.StartsWith("src/", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_ReportsTheSpecificationsAChangeMayHaveInvalidated()
    {
        using FixtureWorkspace ws = Indexed();
        string implementation = ws.PathOf("src/billing/refund.ts");
        File.WriteAllText(
            implementation,
            File.ReadAllText(implementation).Replace("30", "45", StringComparison.Ordinal));

        CliResult result = ws.Run("changed", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        IReadOnlyList<string> impacted = Paths(doc.RootElement, "impacted");

        Assert.Contains("exports/confluence/refund-policy.md", impacted);
        Assert.Contains("exports/jira/PAY-142.json", impacted);
    }

    [Fact]
    public void GitIgnoredArtifacts_AreReIncludedByRepoctxignore()
    {
        using var ws = new FixtureWorkspace("artifact-repo");

        // The layout this feature exists for: tickets and pages are fetched
        // into the working tree and never committed, so git ignores them.
        File.WriteAllText(ws.PathOf(".gitignore"), "exports/\n");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult withoutExports = ws.Run("trace", "PAY-142", "--format", "json");
        Assert.DoesNotContain("exports/jira/PAY-142.json", withoutExports.StdOut, StringComparison.Ordinal);

        File.WriteAllText(ws.PathOf(".repoctxignore"), "!exports/\n");
        Assert.Equal(0, ws.Run("index").ExitCode);

        CliResult withExports = ws.Run("trace", "PAY-142", "--format", "json");
        Assert.Contains("exports/jira/PAY-142.json", withExports.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_NamesTheFilesTheSizeLimitExcluded()
    {
        using FixtureWorkspace ws = Indexed();
        // A realistic large export: coverage is subtractive, so the one rule
        // that silently drops a text file has to say so (ADR 0017/0019).
        File.WriteAllText(
            ws.PathOf("exports/confluence/huge-export.md"),
            "# Export\n\n" + string.Concat(Enumerable.Repeat("Refund policy text. PAY-142.\n", 30_000)));

        CliResult result = ws.Run("index");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("exceed indexing.maxFileSizeKb", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("exports/confluence/huge-export.md", result.StdErr, StringComparison.Ordinal);

        // ... and it is genuinely absent, rather than half-indexed.
        CliResult trace = ws.Run("trace", "PAY-142", "--format", "json");
        Assert.DoesNotContain("huge-export.md", trace.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_RebuildsTheGraphWithoutReReadingTheRepository()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult reindex = ws.Run("index");

        Assert.Equal(0, reindex.ExitCode);

        // A no-op run still hashes every file once, and nothing beyond that:
        // the graph is rebuilt from stored references (ADR 0019).
        Assert.Contains("0 files parsed", reindex.StdOut, StringComparison.Ordinal);
        long bytes = long.Parse(
            reindex.StdOut.Split(" bytes read", StringSplitOptions.None)[0].Split(' ')[^1],
            System.Globalization.CultureInfo.InvariantCulture);
        long onDisk = Directory
            .GetFiles(ws.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".repoctx", StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);

        Assert.Equal(onDisk, bytes);
    }
}
