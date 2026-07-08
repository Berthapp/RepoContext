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
