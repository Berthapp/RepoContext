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
    public void Xml_ElementsAreLabelledByTheirIdentifyingAttribute()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("App.csproj", content);

        // "ItemGroup" alone says nothing; the package name is the point.
        Assert.Equal(
            ["Project Microsoft.NET.Sdk", "ItemGroup", "PackageReference Serilog"],
            symbols.Select(s => s.Name));
        Assert.All(symbols, s => Assert.Equal(SymbolKind.Key, s.Kind));
    }

    [Fact]
    public void DelimitedTables_AreDescribedByTheirColumns()
    {
        IReadOnlyList<Symbol> csv = StructureExtractor.Extract(
            "matrix.csv", "requirement,ticket,test\nREQ-1,PAY-142,refund.test.ts\n");

        Symbol columns = Assert.Single(csv);
        Assert.Equal("columns", columns.Name);
        Assert.Equal("columns: requirement, ticket, test", columns.Signature);

        // A symbol per row would turn a traceability matrix into thousands of
        // useless outline entries.
        Assert.Single(StructureExtractor.Extract(
            "matrix.tsv", "a\tb\n1\t2\n3\t4\n5\t6\n"));
    }

    [Fact]
    public void ReStructuredText_AssignsLevelsInOrderOfFirstAppearance()
    {
        const string content = """
            Title
            =====

            Section
            -------

            Another
            =======
            """;

        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract("docs/index.rst", content);

        Assert.Equal(["Title", "Section", "Another"], symbols.Select(s => s.Name));

        // "=" was seen first, so it outranks "-": Title closes at Another.
        Assert.Equal(1, symbols[0].StartLine);
        Assert.Equal(6, symbols[0].EndLine);
    }

    [Fact]
    public void PropertiesAreGroupedByTheirKeyPrefix()
    {
        IReadOnlyList<Symbol> symbols = StructureExtractor.Extract(
            "app.properties",
            "spring.datasource.url=x\nspring.datasource.user=y\nlogging.level=INFO\n");

        Assert.Equal(["spring", "logging"], symbols.Select(s => s.Name));
    }

    [Fact]
    public void BuildAndInfrastructureFilesAreCovered()
    {
        Assert.Equal(
            ["build", "test"],
            StructureExtractor.Extract("Makefile", "build:\n\tgo build\ntest:\n\tgo test\n")
                .Select(s => s.Name));

        Assert.Equal(
            ["builder", "golang:1.22"],
            StructureExtractor.Extract(
                "Dockerfile",
                "FROM node:20 AS builder\nRUN npm ci\nFROM golang:1.22\nRUN go build\n")
                .Select(s => s.Name));

        Assert.Equal(
            ["resource.aws_s3_bucket.logs", "variable.region"],
            StructureExtractor.Extract(
                "main.tf",
                "resource \"aws_s3_bucket\" \"logs\" {\n  acl = \"private\"\n}\n"
                + "variable \"region\" {\n  default = \"eu-central-1\"\n}\n")
                .Select(s => s.Name));
    }

    [Fact]
    public void UnknownFormats_AndEmptyContent_YieldNothing()
    {
        Assert.False(StructureExtractor.Supports("src/app.ts"));
        Assert.False(StructureExtractor.Supports("notes.bin"));
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
