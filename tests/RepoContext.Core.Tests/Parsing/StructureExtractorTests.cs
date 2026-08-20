using RepoContext.Core.Parsing;

namespace RepoContext.Core.Tests.Parsing;

/// <summary>
/// The artifact structure extractor (ADR 0019): a specification, an exported
/// ticket or a feature file must be outlineable, or an agent has to read it in
/// full to find out what is in it.
/// </summary>
public class StructureExtractorTests
{
    [Fact]
    public void Markdown_HeadingsBecomeSectionsClosedByTheNextPeer()
    {
        const string content = """
            # Refund Policy

            Owner: Payments.

            ## Window

            A payment may be refunded within 30 days.

            ### Exceptions

            None.

            ## Audit

            Every refund is recorded.
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("docs/policy.md", content);

        Assert.Equal(
            ["Refund Policy", "Window", "Exceptions", "Audit"],
            symbols.Select(s => s.Name));
        Assert.All(symbols, s => Assert.Equal(SymbolKind.Section, s.Kind));

        // "Window" is closed by the next heading of the same level, so it
        // contains its own subsection but not the one that follows it.
        Symbol window = symbols.Single(s => s.Name == "Window");
        Assert.Equal(5, window.StartLine);
        Assert.Equal(12, window.EndLine);
        Assert.Equal("A payment may be refunded within 30 days.", window.Doc);
    }

    [Fact]
    public void Markdown_HeadingInsideAFencedBlockIsSampleTextNotStructure()
    {
        const string content = """
            # Real

            ```sh
            # not a heading
            ```

            ## Also real
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("README.md", content);

        Assert.Equal(["Real", "Also real"], symbols.Select(s => s.Name));
    }

    [Fact]
    public void Yaml_KeysAreDottedPathsLimitedToTwoLevels()
    {
        const string content = """
            requirements:
              REQ-4711:
                title: Refund window
                nested:
                  deeper: ignored
            other: value
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("requirements.yaml", content);

        Assert.Equal(
            ["requirements", "requirements.REQ-4711", "other"],
            symbols.Select(s => s.Name));
        Assert.All(symbols, s => Assert.Equal(SymbolKind.Key, s.Kind));
    }

    [Fact]
    public void Json_BracesInsideStringsDoNotShiftTheNesting()
    {
        const string content = """
            {
              "key": "PAY-142",
              "summary": "Use {braces} in prose",
              "fields": {
                "status": "open"
              }
            }
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("ticket.json", content);

        Assert.Equal(
            ["key", "summary", "fields", "fields.status"],
            symbols.Select(s => s.Name));
    }

    [Fact]
    public void Gherkin_FeatureContainsItsScenarios()
    {
        const string content = """
            Feature: Refunds

              Scenario: Inside the window
                Given a payment captured 5 days ago

              Scenario: Outside the window
                Given a payment captured 40 days ago
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("features/refund.feature", content);

        Assert.Equal(
            ["Refunds", "Inside the window", "Outside the window"],
            symbols.Select(s => s.Name));
        Assert.Equal(SymbolKind.Section, symbols[0].Kind);
        Assert.Equal(SymbolKind.Scenario, symbols[1].Kind);

        // The feature spans the whole file; each scenario ends where the next begins.
        Assert.Equal(7, symbols[0].EndLine);
        Assert.Equal(5, symbols[1].EndLine);
    }

    [Fact]
    public void Ini_SectionsAndSql_ObjectsAreExtracted()
    {
        Assert.Equal(
            ["tool.poetry"],
            StructureExtractor.Extract("pyproject.toml", "[tool.poetry]\nname = \"x\"\n")
                .Select(s => s.Name));

        Assert.Equal(
            ["refunds", "refunds_by_day"],
            StructureExtractor.Extract(
                "schema.sql",
                "CREATE TABLE IF NOT EXISTS refunds (id INT);\nCREATE VIEW refunds_by_day AS SELECT 1;\n")
                .Select(s => s.Name));
    }

    [Fact]
    public void Html_HeadingsAreExtractedForExportedPages()
    {
        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract(
            "export/page.html",
            "<h1>Refund Policy</h1>\n<p>text</p>\n<h2><span>Window</span></h2>\n");

        Assert.Equal(["Refund Policy", "Window"], symbols.Select(s => s.Name));
    }

    [Fact]
    public void UnknownFormats_AndEmptyContent_YieldNothing()
    {
        Assert.False(StructureExtractor.Supports("src/app.ts"));
        Assert.Empty(StructureExtractor.Extract("src/app.ts", "export const a = 1;"));
        Assert.Empty(StructureExtractor.Extract("docs/policy.md", string.Empty));
    }

    [Fact]
    public void Extraction_IsDeterministic()
    {
        const string content = "# A\n\ntext\n\n## B\n\nmore\n";

        Assert.Equal(
            StructureExtractor.Extract("docs/a.md", content),
            StructureExtractor.Extract("docs/a.md", content));
    }
}
