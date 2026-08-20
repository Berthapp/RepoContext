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
                "artifacts/confluence/refund-policy.md",
                "artifacts/jira/PAY-142.json",
                "artifacts/jira/PAY-155.json",
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
            ["artifacts/confluence/refund-policy.md", "artifacts/jira/PAY-142.json"],
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
        Assert.Contains("artifacts/confluence/refund-policy.md", Paths(doc.RootElement, "mentions"));
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
            ["artifacts/confluence/refund-policy.md", "artifacts/jira/PAY-142.json"],
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
            items.Where(i => i.GetProperty("path").GetString()!.StartsWith("artifacts/", StringComparison.Ordinal)),
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

        Assert.Contains("artifacts/confluence/refund-policy.md", impacted);
        Assert.Contains("artifacts/jira/PAY-142.json", impacted);
    }

    [Fact]
    public void Index_NamesTheFilesTheSizeLimitExcluded()
    {
        using FixtureWorkspace ws = Indexed();
        // A realistic large export: coverage is subtractive, so the one rule
        // that silently drops a text file has to say so (ADR 0017/0019).
        File.WriteAllText(
            ws.PathOf("artifacts/confluence/huge-export.md"),
            "# Export\n\n" + string.Concat(Enumerable.Repeat("Refund policy text. PAY-142.\n", 30_000)));

        CliResult result = ws.Run("index");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("exceed indexing.maxFileSizeKb", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("artifacts/confluence/huge-export.md", result.StdErr, StringComparison.Ordinal);

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
