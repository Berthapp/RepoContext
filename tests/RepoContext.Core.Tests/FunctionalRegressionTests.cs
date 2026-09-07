using System.Text.Json;
using Microsoft.Data.Sqlite;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Identity;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests;

public class FunctionalRegressionTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"indexing\":{\"includeTests\":false}}")]
    public void SparseConfiguration_PreservesExclusionsThroughIndexing(string json)
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write(".env", "DUMMY_ONLY=excluded");
        repo.Write("node_modules/demo/index.js", "export const noise = 1;");
        repo.Write("obj/generated.cs", "class Generated {}");
        using IndexStore store = IndexHelper.BuildIndex(repo, ConfigStore.Deserialize(json));
        Assert.Null(store.FindFile(".env"));
        Assert.Null(store.FindFile("node_modules/demo/index.js"));
        Assert.Null(store.FindFile("obj/generated.cs"));
        Assert.NotNull(store.FindFile("src/auth/login.ts"));
    }

    [Fact]
    public void ExplicitEmptyExclusions_AreAnOverride()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write(".env", "DUMMY_ONLY=explicit");
        using IndexStore store = IndexHelper.BuildIndex(repo, ConfigStore.Deserialize(
            """{"exclude":[],"sensitiveFiles":[],"respectGitignore":false}"""));
        Assert.NotNull(store.FindFile(".env"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"exclude\":null}")]
    [InlineData("{\"exclude\":[null]}")]
    [InlineData("{\"indexing\":null}")]
    [InlineData("{\"indexing\":{\"maxFileSizeKb\":-1}}")]
    [InlineData("{\"ranking\":{\"weights\":null}}")]
    public void InvalidConfiguration_HasAnActionableError(string json) =>
        Assert.Throws<JsonException>(() => ConfigStore.Deserialize(json));

    [Theory]
    [InlineData(".js", ".ts")]
    [InlineData(".jsx", ".tsx")]
    [InlineData(".mjs", ".mts")]
    [InlineData(".cjs", ".cts")]
    [InlineData(".js", ".d.ts")]
    public void RuntimeExtensions_ResolveToSource(string extension, string source)
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("src/probe.ts", $"import {{ value }} from './module{extension}';");
        repo.Write("src/module" + source, "export const value = 1;");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        Assert.Contains("src/module" + source, Imports(store, "src/probe.ts"));
    }

    [Fact]
    public void Aliases_RespectInheritanceAndNearestProject()
    {
        using var repo = new FixtureRepo("sample-ts");
        repo.Write("tsconfig.base.json", """{"compilerOptions":{"paths":{"@/*":["shared/*"]}}}""");
        repo.Write("tsconfig.json", """{"extends":"./tsconfig.base.json"}""");
        repo.Write("shared/session.ts", "export const session = 1;");
        repo.Write("src/probe.ts", "import { session } from '@/session.js'; import x from 'external';");
        repo.Write("apps/web/tsconfig.json", """
            { // JSONC with an exact alias winning over the wildcard
              "extends":"../../tsconfig.base.json",
              "compilerOptions":{"paths":{"@/*":["src/*"],"@/session":["special/session"]}},
            }
            """);
        repo.Write("apps/web/src/session.ts", "export const session = 2;");
        repo.Write("apps/web/special/session.ts", "export const session = 3;");
        repo.Write("apps/web/src/probe.ts", "import { session } from '@/session';");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        Assert.Equal(["shared/session.ts"], Imports(store, "src/probe.ts"));
        Assert.Equal(["apps/web/special/session.ts"], Imports(store, "apps/web/src/probe.ts"));
    }

    [Fact]
    public void CSharp_SeparatesDeclarationsStringsAndNamespaces()
    {
        using var repo = new FixtureRepo("sample-cs");
        repo.Write("A/Person.cs", "namespace Alpha; public class Person { public string Text => \"Person\"; }");
        repo.Write("B/Person.cs", "namespace Beta; public class Person { /* Person */ }");
        repo.Write("Uses/Qualified.cs", "class Qualified { private Alpha.Person _person; }");
        repo.Write("Uses/Imported.cs", "using Beta; class Imported { private Person _person; }");
        repo.Write("Uses/Ambiguous.cs", "class Ambiguous { private Person _person; }");
        repo.Write("Uses/Noise.cs", "class Noise { string Text = \"Person\"; /* Person */ }");
        using IndexStore store = IndexHelper.BuildIndex(repo);
        Assert.Empty(Imports(store, "A/Person.cs"));
        Assert.Empty(Imports(store, "B/Person.cs"));
        Assert.Equal(["A/Person.cs"], Imports(store, "Uses/Qualified.cs"));
        Assert.Equal(["B/Person.cs"], Imports(store, "Uses/Imported.cs"));
        Assert.Empty(Imports(store, "Uses/Ambiguous.cs"));
        Assert.Empty(Imports(store, "Uses/Noise.cs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPublication_RollsBackFilesGraphAndMetadata(bool full)
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoLayout layout = RepoLayout.For(repo.Root);
        var indexer = new Indexer(layout, RepoctxConfig.CreateDefault(), "test");
        indexer.Run(full: true);
        using IndexStore before = IndexStore.Open(layout.DatabasePath);
        string? state = before.GetMeta(MetaKeys.StateHash);
        int edges = before.CountEdges();
        string original = before.GetSourceSlice("src/auth/login.ts", 1, int.MaxValue)!.Value.Text;
        repo.Write("src/auth/login.ts", "export const interrupted = true;");
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_publish BEFORE UPDATE ON meta "
                + "WHEN NEW.key='state_hash' BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => indexer.Run(full));
        using IndexStore after = IndexStore.Open(layout.DatabasePath);
        Assert.Equal(state, after.GetMeta(MetaKeys.StateHash));
        Assert.Equal(edges, after.CountEdges());
        Assert.Equal(original, after.GetSourceSlice("src/auth/login.ts", 1, int.MaxValue)!.Value.Text);
    }

    [Fact]
    public async Task ConcurrentIndexers_PublishConsistentState()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoLayout layout = RepoLayout.For(repo.Root);
        var config = RepoctxConfig.CreateDefault();
        new Indexer(layout, config, "test").Run(full: true);
        repo.Write("src/parallel.ts", "import { loginUser } from './auth/login.js'; export const x = loginUser;");
        IndexStats[] runs = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => new Indexer(layout, config, "test").Run(full: false))));
        Assert.Equal(1, runs.Sum(r => r.Added));
        using IndexStore store = IndexStore.Open(layout.DatabasePath);
        Assert.Equal(Fingerprints.ContentState(store.GetExistingFiles().Select(x => (x.Key, x.Value.ContentHash))),
            store.GetMeta(MetaKeys.StateHash));
        Assert.Contains("src/auth/login.ts", Imports(store, "src/parallel.ts"));
        IndexStats unchanged = new Indexer(layout, config, "test").Run(full: false);
        Assert.Equal(0, unchanged.EdgesRecomputed);
        Assert.Equal(0, unchanged.GraphFilesAnalyzed);
    }

    [Fact]
    public void Plurals_PreserveLiteralIdentifiers()
    {
        AnalyzedQuery query = QueryAnalyzer.Analyze("receipts dependencies class status analysis", RepoctxConfig.CreateDefault());
        Assert.Contains("receipt", query.Terms);
        Assert.Contains("receipts", query.Terms);
        Assert.Contains("dependency", query.Terms);
        Assert.DoesNotContain("clas", query.Terms);
        Assert.DoesNotContain("statu", query.Terms);
        Assert.DoesNotContain("analysi", query.Terms);
    }

    private static IReadOnlyList<string> Imports(IndexStore store, string path) =>
        store.GetNeighbors(store.FindFile(path)!.Value.Id, EdgeKind.Import, outgoing: true);
}
