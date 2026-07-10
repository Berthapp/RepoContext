using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

namespace RepoContext.Core.Tests.TestSupport;

/// <summary>Builds an index for a fixture and opens a store for assertions.</summary>
public static class IndexHelper
{
    public static IndexStore BuildIndex(FixtureRepo repo, RepoctxConfig? config = null)
    {
        RepoLayout layout = RepoLayout.For(repo.Root);
        Directory.CreateDirectory(layout.IndexDirectory);
        config ??= RepoctxConfig.CreateDefault() with { Include = ["."] };
        new Indexer(layout, config, "test").Run(full: true);
        return IndexStore.Open(layout.DatabasePath);
    }
}
