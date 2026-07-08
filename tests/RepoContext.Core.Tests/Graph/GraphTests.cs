using RepoContext.Core.Graph;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Graph;

public class GraphTests
{
    [Fact]
    public void TypeScript_ResolvesRelativeImports_IncludingIndex()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        long login = store.FindFile("src/auth/login.ts")!.Value.Id;
        IReadOnlyList<string> imports = store.GetNeighbors(login, EdgeKind.Import, outgoing: true);
        Assert.Contains("src/auth/session.ts", imports);
        Assert.Contains("src/auth/permissions.ts", imports);
        Assert.Contains("src/lib/crypto.ts", imports);

        // index.ts barrel resolution: lib/index.ts imports crypto.ts.
        long barrel = store.FindFile("src/lib/index.ts")!.Value.Id;
        Assert.Contains("src/lib/crypto.ts", store.GetNeighbors(barrel, EdgeKind.Import, outgoing: true));
    }

    [Fact]
    public void CSharp_NameBasedEdges_LinkTypesToDefiningFiles()
    {
        using var repo = new FixtureRepo("sample-cs");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        long auth = store.FindFile("Auth/AuthService.cs")!.Value.Id;
        IReadOnlyList<string> imports = store.GetNeighbors(auth, EdgeKind.Import, outgoing: true);
        Assert.Contains("Auth/PasswordHasher.cs", imports);
        Assert.Contains("Interfaces/IUserService.cs", imports);
        Assert.Contains("Models/User.cs", imports);
    }

    [Fact]
    public void TestEdges_LinkTestFilesToSubjects()
    {
        using var repo = new FixtureRepo("sample-ts");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        RelatedResult related = Related.Query(store, "src/auth/login.ts")!;
        Assert.Contains(related.Entries, e =>
            e is { Relation: Relation.TestedBy, Path: "src/auth/__tests__/login.test.ts" });
        Assert.Contains(related.Entries, e =>
            e is { Relation: Relation.ImportedBy, Path: "app/page.tsx" });
    }

    [Fact]
    public void CSharp_TestEdges_UseNameConvention()
    {
        using var repo = new FixtureRepo("sample-cs");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        RelatedResult related = Related.Query(store, "Services/UserService.cs")!;
        Assert.Contains(related.Entries, e =>
            e is { Relation: Relation.TestedBy, Path: "Tests/UserServiceTests.cs" });
    }

    [Theory]
    [InlineData("src/auth/__tests__/login.test.ts", "src/auth/login.ts")]
    [InlineData("Tests/UserServiceTests.cs", "Tests/UserService.cs")]
    public void NameConventionTargets_StripsSuffixes(string testPath, string expected)
    {
        Assert.Contains(expected, GraphBuilder.NameConventionTargets(testPath));
    }
}
