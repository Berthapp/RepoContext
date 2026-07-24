using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// A frozen evaluation repository: a temp copy of a fixture, indexed in-process,
/// with the store left open for querying (Q0).
/// </summary>
/// <remarks>
/// In-process rather than through the CLI binary because the evaluation harness
/// measures the <i>core</i> result and the rendered surfaces separately, and it
/// must be fast enough to run every task on every build. The CLI contract itself
/// is covered by the existing end-to-end tests, which do drive the real binary.
/// </remarks>
public sealed class EvalRepo : IDisposable
{
    private readonly FixtureWorkspace _workspace;

    public EvalRepo(string fixtureName = "eval-repo")
    {
        _workspace = new FixtureWorkspace(fixtureName);
        Layout = RepoLayout.For(_workspace.Root);
        Directory.CreateDirectory(Layout.IndexDirectory);

        Config = RepoctxConfig.CreateDefault() with
        {
            // The evaluation corpus deliberately includes a top-level `tests`
            // directory, which the default include roots do not cover (Q6).
            Include = ["src", "tests", "vendor"],
        };

        Stats = new Indexer(Layout, Config, "eval").Run(full: true);
        Store = IndexStore.Open(Layout.DatabasePath);
    }

    public RepoLayout Layout { get; }

    public RepoctxConfig Config { get; }

    public IndexStore Store { get; }

    public IndexStats Stats { get; }

    public string Root => _workspace.Root;

    /// <summary>Re-indexes after a mutation and reopens the store.</summary>
    public IndexStats Reindex(bool full = false)
    {
        Store.Dispose();
        IndexStats stats = new Indexer(Layout, Config, "eval").Run(full);
        Reopened = IndexStore.Open(Layout.DatabasePath);
        return stats;
    }

    /// <summary>The store reopened by <see cref="Reindex"/>, when used.</summary>
    public IndexStore? Reopened { get; private set; }

    public void Write(string relative, string content)
    {
        string path = Path.Combine(_workspace.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        Store.Dispose();
        Reopened?.Dispose();
        _workspace.Dispose();
    }
}
