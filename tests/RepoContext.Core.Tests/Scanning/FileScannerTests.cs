using RepoContext.Core.Configuration;
using RepoContext.Core.Scanning;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Scanning;

public class FileScannerTests
{
    [Theory]
    [InlineData("customer-data/")]
    [InlineData("/customer-data")]
    public void Scan_PrunesSensitiveDirectories(string pattern)
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("customer-data/records.ts", "export const records = [];\n");
        RepoctxConfig config = RepoctxConfig.CreateDefault() with
        {
            Include = ["."],
            SensitiveFiles = [pattern],
        };

        IReadOnlyList<ScannedFile> files = new FileScanner(repo.Root, config).Scan();

        Assert.DoesNotContain(files,
            f => f.RelativePath.StartsWith("customer-data/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.RelativePath == "src/auth/login.ts");
    }

    [Fact]
    public void Scan_ExcludesSensitiveFiles()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoctxConfig config = RepoctxConfig.CreateDefault() with { Include = ["."] };

        IReadOnlyList<ScannedFile> files = new FileScanner(repo.Root, config).Scan();

        Assert.DoesNotContain(files, f => f.RelativePath == ".env");
    }
}
