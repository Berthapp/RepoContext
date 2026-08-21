using RepoContext.Core.Configuration;
using RepoContext.Core.Scanning;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Scanning;

public class FileScannerTests
{
    /// <summary>
    /// A file the process cannot open must be counted and skipped, never
    /// thrown: one unreadable file aborting an index run over a repository of
    /// thousands is the worst possible trade. It is also distinguished from a
    /// binary file, because the two have different causes and different fixes.
    /// </summary>
    [Fact]
    public void Scan_CountsAFileItCannotOpen_RatherThanFailingOrCallingItBinary()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("locked/secret.ts", "export const a = 1;\n");
        string locked = System.IO.Path.Combine(repo.Root, "locked", "secret.ts");

        // Hold the file open exclusively; on every platform this makes the
        // scanner's read attempt fail rather than succeed with partial data.
        using (FileStream exclusive = new(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scanner = new FileScanner(repo.Root, RepoctxConfig.CreateDefault());

            IReadOnlyList<ScannedFile> scanned = scanner.Scan();

            Assert.DoesNotContain(scanned, f => f.RelativePath == "locked/secret.ts");
            Assert.Equal(1, scanner.UnreadableCount);
            Assert.DoesNotContain("locked/secret.ts", scanner.OversizedSample);
        }

        // Released again, it is an ordinary file.
        var second = new FileScanner(repo.Root, RepoctxConfig.CreateDefault());
        Assert.Contains(second.Scan(), f => f.RelativePath == "locked/secret.ts");
        Assert.Equal(0, second.UnreadableCount);
    }

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
