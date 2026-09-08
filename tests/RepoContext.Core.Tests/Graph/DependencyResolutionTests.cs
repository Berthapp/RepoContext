using RepoContext.Core.Configuration;
using RepoContext.Core.Graph;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Graph;

public class DependencyResolutionTests
{
    [Fact]
    public void CSharp_ProjectReferencesSelectTheReachableDuplicateType()
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "B");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("B/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Equal(["A/Person.cs"], Imports(store, "Consumer/Use.cs"));
        RelatedResult related = Related.Query(store, "Consumer/Use.cs")!;
        Assert.Empty(related.Unresolved);
        Assert.Contains(related.Entries, e => e.Path == "A/Person.cs" && e.Reasons.Contains("csharp-syntax"));
    }

    [Fact]
    public void CSharp_UsesSameProjectAndTransitiveReferences()
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "B");
        Project(repo, "Bridge", "../A/A.csproj");
        Project(repo, "Consumer", "../Bridge/Bridge.csproj");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("A/Use.cs", "namespace Domain; class Use { private Person person; }");
        repo.Write("B/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Equal(["A/Person.cs"], Imports(store, "A/Use.cs"));
        Assert.Equal(["A/Person.cs"], Imports(store, "Consumer/Use.cs"));
    }

    [Theory]
    [InlineData(false, "type-outside-project-scope")]
    [InlineData(true, "ambiguous-type")]
    public void CSharp_UnreachableOrAmbiguousTypesStayUnresolved(bool referenceBoth, string reason)
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "B");
        Project(repo, "Consumer", referenceBoth ? ["../A/A.csproj", "../B/B.csproj"] : []);
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("B/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == reason && r.Line == 1);
    }

    [Theory]
    [InlineData("<ProjectReference Include=\"../A/A.csproj\" Condition=\"'$(Mode)' == 'A'\" />")]
    [InlineData("<ProjectReference Include=\"$(Dependency)/A.csproj\" />")]
    [InlineData("<ProjectReference Include=\"../A/A.csproj\" ReferenceOutputAssembly=\"false\" />")]
    [InlineData("<Compile Include=\"../A/Person.cs\" />")]
    public void CSharp_UnsupportedProjectEvaluationDoesNotGuess(string items)
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        repo.Write("Consumer/Consumer.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == "unsupported-project-configuration");
    }

    [Theory]
    [InlineData("Directory.Build.props", "<ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup>")]
    [InlineData("Directory.Build.targets", "<ItemGroup><Compile Include=\"../A/Person.cs\" /></ItemGroup>")]
    [InlineData("Directory.Build.props", "<PropertyGroup><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup>")]
    [InlineData("Directory.Build.props", "<PropertyGroup><DefaultItemExcludes>Excluded/**</DefaultItemExcludes></PropertyGroup>")]
    public void CSharp_CustomDirectoryBuildSettingsStayUnresolved(string settings, string content)
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write(settings, $"<Project>{content}</Project>");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == "unsupported-project-configuration");
    }

    [Fact]
    public void CSharp_MultipleProjectsInOneDirectoryDoNotGuessOwnership()
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write("Consumer/Alternative.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == "ambiguous-project-ownership");
    }

    [Theory]
    [InlineData("build/shared.props")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))")]
    public void CSharp_ImportedBuildSettingsPreserveUniqueSyntaxLinks(string import)
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write("Directory.Build.props", $"""
            <Project>
              <Import Project="{import}" />
              <ItemGroup>
                <PackageReference Include="Analyzer" Version="1.0.0" />
                <AdditionalFiles Include="BannedSymbols.txt" />
              </ItemGroup>
            </Project>
            """);
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("A/Local.cs", "namespace Domain; class Local { private Person person; }");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Equal(["A/Person.cs"], Imports(store, "A/Local.cs"));
        Assert.Equal(["A/Person.cs"], Imports(store, "Consumer/Use.cs"));
        RelatedResult related = Related.Query(store, "Consumer/Use.cs")!;
        Assert.Empty(related.Unresolved);
        Assert.Contains(related.Entries, e => e.Path == "A/Person.cs" && e.Reasons.Contains("csharp-syntax"));
    }

    [Theory]
    [InlineData(false, "ambiguous-type")]
    [InlineData(true, "ambiguous-project-ownership")]
    public void CSharp_UnknownImportedSettingsNeverDisambiguateTypesOrOwners(bool ambiguousOwner, string reason)
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write("Directory.Build.props", "<Project><Import Project=\"build/shared.props\" /></Project>");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        if (ambiguousOwner)
            repo.Write("A/Alternative.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        else
        {
            Project(repo, "B");
            repo.Write("B/Person.cs", "namespace Domain; public class Person {}");
        }
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == reason);
    }

    [Fact]
    public void CSharp_UnknownImportsDoNotHideExplicitCustomCompileSetsInReferencedProjects()
    {
        using var repo = new FixtureRepo("sample-cs");
        repo.Write("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Remove=\"Person.cs\" /></ItemGroup></Project>");
        Project(repo, "Consumer", "../A/A.csproj");
        repo.Write("Consumer/Directory.Build.props", "<Project><Import Project=\"shared.props\" /></Project>");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Empty(Imports(store, "Consumer/Use.cs"));
        Assert.Contains(Related.Query(store, "Consumer/Use.cs")!.Unresolved,
            r => r.Value == "Domain.Person" && r.Reason == "unsupported-project-configuration");
    }

    [Fact]
    public void CSharp_ProjectReferenceEditsRebuildTheGraphAndDiagnostics()
    {
        using var repo = new FixtureRepo("sample-cs");
        Project(repo, "A");
        Project(repo, "Consumer");
        repo.Write("A/Person.cs", "namespace Domain; public class Person {}");
        repo.Write("Consumer/Use.cs", "class Use { private Domain.Person person; }");
        using (IndexStore before = IndexHelper.BuildIndex(repo))
            Assert.NotEmpty(Related.Query(before, "Consumer/Use.cs")!.Unresolved);

        Project(repo, "Consumer", "../A/A.csproj");
        new Indexer(RepoLayout.For(repo.Root), RepoctxConfig.CreateDefault(), "test").Run(full: false);
        using IndexStore after = IndexStore.Open(RepoLayout.For(repo.Root).DatabasePath);
        Assert.Equal(["A/Person.cs"], Imports(after, "Consumer/Use.cs"));
        Assert.Empty(Related.Query(after, "Consumer/Use.cs")!.Unresolved);
    }

    [Fact]
    public void TypeScript_MissingLocalAliasAndPackageImportsAreVisible()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("tsconfig.json", """{"compilerOptions":{"paths":{"@/*":["src/*"]}}}""");
        repo.Write("src/probe.ts", "import './missing.js'; import '@/absent'; import 'external-package';");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        RelatedResult result = Related.Query(store, "src/probe.ts")!;
        Assert.Empty(Imports(store, "src/probe.ts"));
        Assert.Contains(result.Unresolved, r => r.Value == "./missing.js" && r.Reason == "local-module-not-found");
        Assert.Contains(result.Unresolved, r => r.Value == "@/absent" && r.Reason == "alias-target-not-found");
        Assert.Contains(result.Unresolved, r => r.Value == "external-package" && r.Reason == "unsupported-package-import");
    }

    [Theory]
    [InlineData("{invalid", "invalid-module-configuration")]
    [InlineData("{\"extends\":\"@vendor/config\"}", "unsupported-package-extends")]
    [InlineData("{\"extends\":\"./missing.json\"}", "config-extends-not-found")]
    [InlineData("{\"extends\":\"./tsconfig.json\"}", "cyclic-or-deep-config-extends")]
    [InlineData("{\"compilerOptions\":null}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":[]}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":{\"baseUrl\":42}}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":{\"paths\":null}}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":{\"paths\":{\"@/*\":\"src/*\"}}}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":{\"paths\":{\"@/*\":[42]}}}", "invalid-module-configuration")]
    [InlineData("{\"compilerOptions\":{\"paths\":{\"@/*\":[\"src/*/*\"]}}}", "invalid-module-configuration")]
    public void TypeScript_UnsupportedConfigurationIsVisible(string config, string reason)
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("tsconfig.json", config);
        repo.Write("src/probe.ts", "import '@/session'; import './local.js';");
        repo.Write("src/local.ts", "export const local = true;");
        using IndexStore store = IndexHelper.BuildIndex(repo);

        Assert.Equal(["src/local.ts"], Imports(store, "src/probe.ts"));
        Assert.Contains(Related.Query(store, "src/probe.ts")!.Unresolved,
            r => r.Value == "@/session" && r.Reason == reason);
    }

    [Fact]
    public void TypeScript_DiagnosticsUseTheIndexedConfigAndNotLiveEdits()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("tsconfig.json", """{"compilerOptions":{"paths":{"@/*":["src/*"]}}}""");
        repo.Write("src/probe.ts", "import '@/missing';");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        repo.Write("tsconfig.json", "{invalid");

        Assert.Contains(Related.Query(store, "src/probe.ts")!.Unresolved,
            r => r.Value == "@/missing" && r.Reason == "alias-target-not-found");
    }

    private static void Project(FixtureRepo repo, string directory, params string[] references) =>
        repo.Write($"{directory}/{directory}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + string.Concat(references.Select(path => $"<ProjectReference Include=\"{path}\" />")) + "</ItemGroup></Project>");

    private static IReadOnlyList<string> Imports(IndexStore store, string path) =>
        store.GetNeighbors(store.FindFile(path)!.Value.Id, EdgeKind.Import, outgoing: true);
}
