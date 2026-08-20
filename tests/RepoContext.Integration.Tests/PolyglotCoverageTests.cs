using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end coverage of the "every file type" promise (ADR 0020) against a
/// repository whose code is in languages no bundled grammar covers and whose
/// remaining files are infrastructure, schema and tabular artifacts.
/// </summary>
/// <remarks>
/// The claim under test is narrow and checkable: every indexed text file either
/// has an outline, or there is a stated reason it cannot have one. A file that
/// is silently structureless is the failure this suite exists to catch.
/// </remarks>
public sealed class PolyglotCoverageTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("polyglot-repo");
        Assert.Equal(0, ws.Run("init").ExitCode);
        Assert.Equal(0, ws.Run("index").ExitCode);
        return ws;
    }

    private static IReadOnlyList<(string Name, string Kind)> Symbols(FixtureWorkspace ws, string path)
    {
        CliResult result = ws.Run("outline", path, "--format", "json");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        return
        [
            .. doc.RootElement.GetProperty("symbols").EnumerateArray()
                .Select(s => (
                    s.GetProperty("name").GetString() ?? string.Empty,
                    s.GetProperty("kind").GetString() ?? string.Empty))
        ];
    }

    [Theory]
    [InlineData("services/pricing/pricing.py", "PriceCalculator", "class")]
    [InlineData("services/gateway/gateway.go", "Session", "struct")]
    [InlineData("services/gateway/Handler.java", "RefundHandler", "class")]
    [InlineData("schema/billing.proto", "Billing", "interface")]
    [InlineData("schema/billing.sql", "refunds", "table")]
    [InlineData("infra/main.tf", "resource.aws_s3_bucket.refund_audit", "key")]
    [InlineData("infra/Dockerfile", "builder", "section")]
    [InlineData("docs/design.rst", "Refund design", "section")]
    [InlineData("docs/traceability.csv", "columns", "key")]
    [InlineData("services/pricing/app.properties", "pricing", "key")]
    public void EveryFileTypeInTheRepositoryHasAnOutline(string path, string symbol, string kind)
    {
        using FixtureWorkspace ws = Indexed();

        Assert.Contains((symbol, kind), Symbols(ws, path));
    }

    [Fact]
    public void NoIndexedTextFileIsLeftWithoutStructure()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult architecture = ws.Run("architecture", "--format", "json");
        Assert.Equal(0, architecture.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(architecture.StdOut);

        // The fixture is the promise in miniature: ten files, ten languages or
        // formats, and an outline for each. A regression that drops one of the
        // extractors shows up here as an empty outline.
        IReadOnlyList<string> paths =
        [
            "services/pricing/pricing.py", "services/gateway/gateway.go",
            "services/gateway/Handler.java", "schema/billing.proto", "schema/billing.sql",
            "infra/main.tf", "infra/Dockerfile", "docs/design.rst",
            "docs/traceability.csv", "services/pricing/app.properties",
        ];
        foreach (string path in paths)
        {
            Assert.NotEmpty(Symbols(ws, path));
        }

        Assert.True(doc.RootElement.GetProperty("total_files").GetInt32() >= paths.Count);
    }

    [Fact]
    public void TheLanguageMixNamesTheLanguagesRatherThanCallingThemNone()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("architecture", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        List<string> languages =
        [
            .. doc.RootElement.GetProperty("languages").EnumerateArray()
                .Select(l => l.GetProperty("language").GetString() ?? string.Empty)
        ];

        Assert.Contains("python", languages);
        Assert.Contains("go", languages);
        Assert.Contains("java", languages);
        Assert.Contains("proto", languages);
    }

    [Fact]
    public void SymbolSearchReachesTheNewLanguages()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("search", "refund allowed", "--symbols", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Contains(
            doc.RootElement.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "services/pricing/pricing.py");
    }

    [Fact]
    public void ATicketLinksThePolyglotServicesAndTheTraceabilityMatrix()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("trace", "PAY-142", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        List<string> paths =
        [
            .. doc.RootElement.GetProperty("mentions").EnumerateArray()
                .Select(m => m.GetProperty("path").GetString() ?? string.Empty)
        ];

        Assert.Equal(
            [
                "docs/design.rst",
                "docs/traceability.csv",
                "services/gateway/gateway.go",
                "services/pricing/pricing.py",
            ],
            paths);
    }

    [Fact]
    public void ADocumentIsLinkedToThePolyglotFilesItNames()
    {
        using FixtureWorkspace ws = Indexed();

        CliResult result = ws.Run("related", "services/pricing/pricing.py", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut);
        Assert.Contains(
            doc.RootElement.GetProperty("results").EnumerateArray(),
            e => e.GetProperty("relation").GetString() == "referenced_by"
                && e.GetProperty("path").GetString() == "docs/design.rst");
    }
}
