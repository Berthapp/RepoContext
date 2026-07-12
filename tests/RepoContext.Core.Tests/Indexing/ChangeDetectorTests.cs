using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Indexing;

public class ChangeDetectorTests
{
    private static RepoctxConfig Config => RepoctxConfig.CreateDefault() with { Include = ["."] };

    [Fact]
    public void CleanTree_IsNotStale()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        ChangedResult result = ChangeDetector.Run(RepoLayout.For(repo.Root), Config, store);

        Assert.False(result.Stale);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Impacted);
        Assert.Equal(Hashes.ShortLength, result.State.Length);
    }

    [Fact]
    public void ModifiedAddedDeleted_AreReported_WithImpactedDependents()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        repo.Write("src/auth/session.ts",
            File.ReadAllText(repo.PathOf("src/auth/session.ts")) + "\n// edited\n");
        repo.Write("src/auth/totp.ts", "export function totp() { return 1; }\n");
        repo.Delete("src/auth/permissions.ts");

        ChangedResult result = ChangeDetector.Run(RepoLayout.For(repo.Root), Config, store);

        Assert.True(result.Stale);
        Assert.Contains(result.Changed, c => c.Path == "src/auth/session.ts" && c.Status == ChangedFile.Modified);
        Assert.Contains(result.Changed, c => c.Path == "src/auth/totp.ts" && c.Status == ChangedFile.Added);
        Assert.Contains(result.Changed, c => c.Path == "src/auth/permissions.ts" && c.Status == ChangedFile.Deleted);

        // login.ts imports session.ts, so it is impacted — with the reason.
        ImpactedFile login = result.Impacted.Single(i => i.Path == "src/auth/login.ts");
        Assert.Contains(login.Reasons, r => r.StartsWith("imports:src/auth/", StringComparison.Ordinal));

        // A changed file is never double-reported as impacted.
        Assert.DoesNotContain(result.Impacted, i => i.Path == "src/auth/session.ts");
    }

    [Fact]
    public void IsDeterministic_ForTheSameTreeAndIndex()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        repo.Write("src/auth/session.ts",
            File.ReadAllText(repo.PathOf("src/auth/session.ts")) + "\n// edited\n");

        ChangedResult a = ChangeDetector.Run(RepoLayout.For(repo.Root), Config, store);
        ChangedResult b = ChangeDetector.Run(RepoLayout.For(repo.Root), Config, store);

        Assert.Equal(a.Changed, b.Changed);
        Assert.Equal(
            a.Impacted.Select(i => (i.Path, string.Join('|', i.Reasons))),
            b.Impacted.Select(i => (i.Path, string.Join('|', i.Reasons))));
    }
}
