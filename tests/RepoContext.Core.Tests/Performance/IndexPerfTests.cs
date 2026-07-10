using System.Diagnostics;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Tests.TestSupport;

namespace RepoContext.Core.Tests.Performance;

/// <summary>
/// A generous perf smoke test guarding against pathological regressions. The
/// NFR targets in the product doc are a measurement protocol, not a hard CI
/// gate, so the threshold here is deliberately loose.
/// </summary>
public class IndexPerfTests
{
    [Fact]
    public void FullIndex_OfFixture_CompletesWellUnderTenSeconds()
    {
        using var repo = new FixtureRepo("sample-ts");
        RepoLayout layout = RepoLayout.For(repo.Root);
        Directory.CreateDirectory(layout.IndexDirectory);
        RepoctxConfig config = RepoctxConfig.CreateDefault() with { Include = ["."] };

        var sw = Stopwatch.StartNew();
        new Indexer(layout, config, "test").Run(full: true);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 10,
            $"Full index took {sw.Elapsed.TotalSeconds:F2}s (threshold 10s).");
    }
}
