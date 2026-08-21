using RepoContext.Core.Scanning;

namespace RepoContext.Core.Tests.Scanning;

public class FileClassifierTests
{
    [Theory]
    [InlineData("src/auth/login.ts", FileKind.Source)]
    [InlineData("src/auth/__tests__/login.test.ts", FileKind.Test)]
    [InlineData("Tests/UserServiceTests.cs", FileKind.Test)]
    [InlineData("Services/UserService.cs", FileKind.Source)]
    [InlineData("docs/architecture.md", FileKind.Doc)]
    [InlineData("README.md", FileKind.Doc)]
    [InlineData("package.json", FileKind.Config)]
    [InlineData("appsettings.Production.json", FileKind.Config)]
    [InlineData("logo.png", FileKind.Other)]

    // Declarations are extracted from these, so they are code (ADR 0020).
    [InlineData("services/pricing/app.py", FileKind.Source)]
    [InlineData("schema/billing.proto", FileKind.Source)]
    [InlineData("schema/billing.ddl", FileKind.Source)]

    // Documentation formats are documentation, whichever spelling.
    [InlineData("docs/design.rst", FileKind.Doc)]
    [InlineData("docs/design.adoc", FileKind.Doc)]
    [InlineData("docs/design.markdown", FileKind.Doc)]

    // Settings are settings, including the ones named rather than typed.
    [InlineData("infra/Dockerfile", FileKind.Config)]
    [InlineData("services/pricing/app.properties", FileKind.Config)]
    [InlineData("infra/main.tf", FileKind.Config)]

    // Having a language label must never be enough to call a file source:
    // these acquired one in ADR 0020 and would otherwise outrank code for a
    // task about code.
    [InlineData("docs/traceability.csv", FileKind.Other)]
    [InlineData("exports/page.html", FileKind.Other)]
    [InlineData("exports/tickets.jsonl", FileKind.Other)]
    [InlineData("features/refund.feature", FileKind.Other)]
    public void ClassifyKind_MatchesExpected(string path, FileKind expected)
    {
        Assert.Equal(expected, FileClassifier.ClassifyKind(path));
    }

    [Theory]
    [InlineData("a.ts", SourceLanguage.TypeScript)]
    [InlineData("a.tsx", SourceLanguage.Tsx)]
    [InlineData("a.js", SourceLanguage.JavaScript)]
    [InlineData("a.cs", SourceLanguage.CSharp)]
    [InlineData("a.md", SourceLanguage.Markdown)]
    [InlineData("a.json", SourceLanguage.Json)]
    [InlineData("a.txt", SourceLanguage.None)]
    [InlineData("a.py", SourceLanguage.Python)]
    [InlineData("a.rst", SourceLanguage.ReStructuredText)]
    [InlineData("a.adoc", SourceLanguage.AsciiDoc)]
    [InlineData("a.properties", SourceLanguage.Properties)]
    [InlineData("a.tf", SourceLanguage.Hcl)]
    [InlineData("a.feature", SourceLanguage.Gherkin)]

    // Named rather than typed - the reason this takes a path, not an extension.
    [InlineData("infra/Dockerfile", SourceLanguage.Dockerfile)]
    [InlineData("infra/Dockerfile.build", SourceLanguage.Dockerfile)]
    [InlineData("Makefile", SourceLanguage.Makefile)]
    [InlineData("build/rules.mk", SourceLanguage.Makefile)]
    public void DetectLanguage_MatchesExpected(string path, SourceLanguage expected)
    {
        Assert.Equal(expected, FileClassifier.DetectLanguage(path));
    }

    [Theory]
    [InlineData("logo.png", true)]
    [InlineData("app.dll", true)]
    [InlineData("app.ts", false)]
    public void IsBinaryExtension_MatchesExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileClassifier.IsBinaryExtension(path));
    }

    [Fact]
    public void LooksBinary_DetectsNulByte()
    {
        Assert.True(FileClassifier.LooksBinary([0x41, 0x00, 0x42]));
        Assert.False(FileClassifier.LooksBinary("hello"u8));
    }
}
