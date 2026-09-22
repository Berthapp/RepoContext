using RepoContext.Core.Configuration;
using RepoContext.Core.Scanning;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Scanning;

public class FileScannerTests
{
    /// <summary>
    /// The reporting surface never names a path the configuration removed from
    /// view. The sensitive prune runs before any read attempt, so holding the
    /// file open changes nothing here - which is exactly the property being
    /// pinned: there is no order of failures that puts a sensitive name on the
    /// report, because the report is only ever reached by paths the scan was
    /// allowed to look at.
    /// </summary>
    [Fact]
    public void UnreadablePaths_NeverNameASensitiveFile()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("secrets/token.txt", "s3cret\n");
        RepoctxConfig config = RepoctxConfig.CreateDefault() with { SensitiveFiles = ["secrets/"] };
        string secret = System.IO.Path.Combine(repo.Root, "secrets", "token.txt");

        using (new FileStream(secret, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scanner = new FileScanner(repo.Root, config);

            IReadOnlyList<ScannedFile> scanned = scanner.Scan();

            Assert.DoesNotContain(scanned, f => f.RelativePath.Contains("secrets", StringComparison.Ordinal));
            Assert.DoesNotContain(
                scanner.UnreadablePaths, p => p.Contains("secrets", StringComparison.Ordinal));
            Assert.DoesNotContain(string.Empty, scanner.UnreadablePaths);

            // Pruned rather than skipped: a sensitive path is not something the
            // scan failed to look at, so it must not hold an index row open
            // either.
            Assert.False(scanner.WasSkipped("secrets/token.txt"));
        }
    }

    /// <summary>
    /// Retention is a separate question from reporting. A file the scan could
    /// not read keeps its index row - a moment's lock must not be read as a
    /// deletion - and is named, because nothing excluded it.
    /// </summary>
    /// <remarks>
    /// The third case, an entry that cannot even be classified as file or
    /// directory, is retained but deliberately unreported when a directory-only
    /// pattern matches it. It is not covered here: provoking it needs an entry
    /// whose attributes cannot be read, which a test process running as root
    /// cannot create.
    /// </remarks>
    [Fact]
    public void AnUnreadableFile_IsRetainedAndNamed()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("vault/token.txt", "s3cret\n");
        string locked = System.IO.Path.Combine(repo.Root, "vault", "token.txt");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scanner = new FileScanner(repo.Root, RepoctxConfig.CreateDefault());

            scanner.Scan();

            Assert.True(scanner.WasSkipped("vault/token.txt"));
            Assert.Contains("vault/token.txt", scanner.UnreadablePaths);
        }

        // Readable again, it is neither retained nor reported: retention lasts
        // exactly as long as the failure that caused it.
        var second = new FileScanner(repo.Root, RepoctxConfig.CreateDefault());
        second.Scan();
        Assert.False(second.WasSkipped("vault/token.txt"));
        Assert.Empty(second.UnreadablePaths);
    }

    /// <summary>
    /// An unreadable ignore file is the one failure here that makes the index
    /// bigger rather than smaller, so it is reported on its own channel: the
    /// directories its rules would have excluded were indexed instead.
    /// </summary>
    [Fact]
    public void AnUnreadableIgnoreFile_IsReportedApartFromUnreadableContent()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write(".repoctxignore", "app/\n");
        string ignoreFile = System.IO.Path.Combine(repo.Root, ".repoctxignore");

        using (new FileStream(ignoreFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scanner = new FileScanner(repo.Root, RepoctxConfig.CreateDefault());

            IReadOnlyList<ScannedFile> scanned = scanner.Scan();

            // Both are true of the same file and have different consequences:
            // its content is missing from the index, and its rules were not
            // applied - so it appears on both channels, deliberately.
            Assert.Equal([".repoctxignore"], scanner.UnreadableIgnoreFiles);
            Assert.Contains(".repoctxignore", scanner.UnreadablePaths);

            // The rules going unapplied is what the separate warning is for.
            Assert.Contains(scanned, f => f.RelativePath.StartsWith("app/", StringComparison.Ordinal));
        }
    }

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

    [Fact]
    public void Scan_NeverWalksAnIncludeRootOutsideTheRepository()
    {
        // Configs are normally validated on load; the scanner holds the line on
        // its own for configurations built in code.
        using var repo = new FixtureRepo("sample-ts");
        string outside = Directory.CreateTempSubdirectory("repoctx-outside-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(outside, "credentials.txt"), "hunter2\n");
            string escape = System.IO.Path.GetRelativePath(repo.Root, outside);
            RepoctxConfig config = RepoctxConfig.CreateDefault() with { Include = [".", escape, outside] };

            IReadOnlyList<ScannedFile> files = new FileScanner(repo.Root, config).Scan();

            Assert.DoesNotContain(files, f => f.RelativePath.Contains("credentials", StringComparison.Ordinal));
            Assert.Contains(files, f => f.RelativePath == "src/auth/login.ts");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Scan_NeverFollowsAnIncludeRootThatIsALinkOutOfTheRepository()
    {
        using var repo = new FixtureRepo("sample-ts");
        string outside = Directory.CreateTempSubdirectory("repoctx-outside-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(outside, "credentials.txt"), "hunter2\n");
            try
            {
                Directory.CreateSymbolicLink(repo.PathOf("linked"), outside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return; // Windows without developer mode: not exercisable here.
            }

            RepoctxConfig config = RepoctxConfig.CreateDefault() with { Include = ["linked", "."] };

            IReadOnlyList<ScannedFile> files = new FileScanner(repo.Root, config).Scan();

            Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("linked/", StringComparison.Ordinal));
            Assert.Contains(files, f => f.RelativePath == "src/auth/login.ts");
        }
        finally
        {
            if (new DirectoryInfo(repo.PathOf("linked")).LinkTarget is not null)
            {
                Directory.Delete(repo.PathOf("linked"));
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("deploy/server.pem")]
    [InlineData("certs/client.key")]
    [InlineData("ops/id_ed25519")]
    [InlineData(".npmrc")]
    [InlineData("infra/terraform.tfstate")]
    [InlineData("infra/terraform.tfstate.backup")]
    public void Scan_NeverIndexesCredentialFiles_EvenWithoutConfiguredPatterns(string path)
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write(path, "-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n");
        // A hostile configuration may empty the list and try to re-include.
        RepoctxConfig config = RepoctxConfig.CreateDefault() with
        {
            SensitiveFiles = ["!" + System.IO.Path.GetFileName(path)],
        };

        var scanner = new FileScanner(repo.Root, config);
        IReadOnlyList<ScannedFile> files = scanner.Scan();

        Assert.DoesNotContain(files, f => f.RelativePath == path);
        Assert.True(scanner.IsSensitive(path));
        Assert.Contains(files, f => f.RelativePath == "src/auth/login.ts");
    }

    [Fact]
    public void Scan_KeepsPublicKeysAndLookalikeSourceFiles()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("ops/id_ed25519.pub", "ssh-ed25519 AAAA user@host\n");
        repo.Write("src/keys/key.ts", "export const key = 1;\n");

        IReadOnlyList<ScannedFile> files = new FileScanner(repo.Root, RepoctxConfig.CreateDefault()).Scan();

        Assert.Contains(files, f => f.RelativePath == "ops/id_ed25519.pub");
        Assert.Contains(files, f => f.RelativePath == "src/keys/key.ts");
    }
}
