using RepoContext.Core.Configuration;
using RepoContext.Core.Scanning;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Scanning;

/// <summary>
/// Coverage of a repository root that holds several projects. The default
/// configuration must reach every project and every subfolder below the root;
/// before 0.8.0 the default roots (<c>src/app/lib/docs</c>) silently indexed
/// nothing here.
/// </summary>
public class MultiProjectScanTests
{
    private static IReadOnlyList<string> ScanDefault(FixtureRepo repo) =>
        [.. new FileScanner(repo.Root, RepoctxConfig.CreateDefault())
            .Scan()
            .Select(f => f.RelativePath)];

    [Fact]
    public void DefaultConfig_IndexesEveryProjectAndSubfolder()
    {
        using var repo = new FixtureRepo("multi-project");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.Contains("apps/web/src/auth/login.ts", paths);
        Assert.Contains("apps/web/src/lib/session.ts", paths);
        Assert.Contains("apps/web/package.json", paths);
        Assert.Contains("services/api/Controllers/HealthController.cs", paths);
        Assert.Contains("services/api/Services/TokenService.cs", paths);
        Assert.Contains("services/api/SampleApi.csproj", paths);

        // A directory that belongs to no project at all is still covered.
        Assert.Contains("tools/scripts/deploy.js", paths);
    }

    [Fact]
    public void DefaultExcludes_ApplyInsideNestedProjects()
    {
        using var repo = new FixtureRepo("multi-project");

        Assert.DoesNotContain("apps/web/dist/bundle.js", ScanDefault(repo));
    }

    [Fact]
    public void NestedIgnoreFile_AppliesToItsOwnSubtreeOnly()
    {
        using var repo = new FixtureRepo("multi-project");
        // Same directory name under the other project, with no ignore file.
        repo.Write("services/api/generated/Dto.cs", "namespace SampleApi; public sealed class Dto;\n");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.DoesNotContain("apps/web/generated/schema.ts", paths);
        Assert.Contains("services/api/generated/Dto.cs", paths);
    }

    [Fact]
    public void NestedGitignore_IsHonoured()
    {
        using var repo = new FixtureRepo("multi-project");
        repo.Write("apps/web/.gitignore", "local/\n");
        repo.Write("apps/web/local/draft.ts", "export const draft = 1;\n");
        repo.Write("services/api/local/Draft.cs", "namespace SampleApi; public sealed class Draft;\n");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.DoesNotContain("apps/web/local/draft.ts", paths);
        Assert.Contains("services/api/local/Draft.cs", paths);
    }

    [Fact]
    public void NestedGitignore_IsIgnoredWhenRespectGitignoreIsOff()
    {
        using var repo = new FixtureRepo("multi-project");
        repo.Write("apps/web/.gitignore", "local/\n");
        repo.Write("apps/web/local/draft.ts", "export const draft = 1;\n");

        RepoctxConfig config = RepoctxConfig.CreateDefault() with { RespectGitignore = false };
        IReadOnlyList<string> paths =
            [.. new FileScanner(repo.Root, config).Scan().Select(f => f.RelativePath)];

        Assert.Contains("apps/web/local/draft.ts", paths);
    }

    [Fact]
    public void NestedIgnoreFile_CanReincludeWhatAParentIgnored()
    {
        using var repo = new FixtureRepo("multi-project");
        repo.Write(".repoctxignore", "*.draft.ts\n");
        repo.Write("apps/web/src/a.draft.ts", "export const a = 1;\n");
        repo.Write("apps/web/.repoctxignore", "generated/\n!*.draft.ts\n");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.Contains("apps/web/src/a.draft.ts", paths);
    }

    [Fact]
    public void RootIgnoreFile_StillAppliesToNestedProjects()
    {
        using var repo = new FixtureRepo("multi-project");
        repo.Write(".repoctxignore", "*.draft.ts\n");
        repo.Write("apps/web/src/a.draft.ts", "export const a = 1;\n");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.DoesNotContain("apps/web/src/a.draft.ts", paths);
        Assert.Contains("apps/web/src/auth/login.ts", paths);
    }

    [Fact]
    public void SensitiveFiles_AreNeverReincludedByANestedIgnoreFile()
    {
        using var repo = new FixtureRepo("multi-project");
        repo.Write("apps/web/.env", "SECRET=1\n");
        repo.Write("apps/web/.repoctxignore", "generated/\n!.env\n");

        Assert.DoesNotContain("apps/web/.env", ScanDefault(repo));
    }

    [Fact]
    public void NestedRepositoryMetadata_IsNeverIndexed()
    {
        using var repo = new FixtureRepo("multi-project");
        // One project is a checkout of its own, already initialized for
        // RepoContext. Neither its git objects nor its index may be indexed.
        repo.Write("apps/web/.git/config", "[core]\n\trepositoryformatversion = 0\n");
        repo.Write("apps/web/.repoctx/index.db", "not a real database\n");
        repo.Write("apps/web/repoctx.config.json", "{\"include\":[]}\n");

        IReadOnlyList<string> paths = ScanDefault(repo);

        Assert.DoesNotContain(paths, p => p.StartsWith("apps/web/.git/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.StartsWith("apps/web/.repoctx/", StringComparison.Ordinal));

        // Its own config file is an ordinary file and stays indexable.
        Assert.Contains("apps/web/repoctx.config.json", paths);
    }

    [Fact]
    public void Scan_IsDeterministic()
    {
        using var repo = new FixtureRepo("multi-project");

        Assert.Equal(ScanDefault(repo), ScanDefault(repo));
    }

    [Fact]
    public void ExplicitIncludeRoots_StillNarrowTheScan()
    {
        using var repo = new FixtureRepo("multi-project");
        RepoctxConfig config = RepoctxConfig.CreateDefault() with { Include = ["apps"] };

        IReadOnlyList<string> paths =
            [.. new FileScanner(repo.Root, config).Scan().Select(f => f.RelativePath)];

        Assert.Contains("apps/web/src/auth/login.ts", paths);
        Assert.DoesNotContain("services/api/Services/TokenService.cs", paths);
    }

    [Fact]
    public void MissingIncludeRoots_ReportsWhatIsNotThere()
    {
        using var repo = new FixtureRepo("multi-project");
        RepoctxConfig legacy = RepoctxConfig.CreateDefault() with
        {
            Include = ["src", "app", "lib", "docs"],
        };

        Assert.Equal(
            ["src", "app", "lib", "docs"],
            FileScanner.MissingIncludeRoots(repo.Root, legacy));
        Assert.Empty(new FileScanner(repo.Root, legacy).Scan());
        Assert.Empty(FileScanner.MissingIncludeRoots(repo.Root, RepoctxConfig.CreateDefault()));
    }

    [Fact]
    public void ProjectDetector_FindsEveryNestedProject()
    {
        using var repo = new FixtureRepo("multi-project");

        IReadOnlyList<DetectedProject> projects =
            ProjectDetector.Detect(new FileScanner(repo.Root, RepoctxConfig.CreateDefault()).Scan());

        Assert.Equal(["apps/web", "services/api"], projects.Select(p => p.Path));
        Assert.Equal(["node", "dotnet"], projects.Select(p => p.Ecosystem));
        Assert.Equal(["package.json", "SampleApi.csproj"], projects.Select(p => p.Marker));
    }

    [Fact]
    public void ProjectDetector_ReportsOneProjectPerDirectory_ByMarkerPriority()
    {
        using var repo = new FixtureRepo("multi-project");
        // A directory carrying two markers is one project, not two.
        repo.Write("services/api/package.json", "{\"name\":\"api-tooling\"}\n");

        IReadOnlyList<DetectedProject> projects =
            ProjectDetector.Detect(new FileScanner(repo.Root, RepoctxConfig.CreateDefault()).Scan());

        Assert.Equal(["apps/web", "services/api"], projects.Select(p => p.Path));
        Assert.All(projects, p => Assert.Equal("node", p.Ecosystem));
    }
}
