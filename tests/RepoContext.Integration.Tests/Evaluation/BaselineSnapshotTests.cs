using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Golden baseline for the evaluation corpus (Q0). The snapshot is checked in,
/// so every relevance or cost movement arrives as a reviewable diff instead of a
/// claim.
/// </summary>
/// <remarks>
/// Set <c>REPOCTX_UPDATE_EVAL_BASELINE=1</c> to rewrite the snapshot. Do that only
/// when the change to it is the point of the review — never to make a red gate
/// green.
/// </remarks>
public sealed class BaselineSnapshotTests
{
    private static string SnapshotPath =>
        Path.Combine(RepoRoot(), "docs", "eval", "baseline.md");

    private static string RawDirectory =>
        Path.Combine(RepoRoot(), "docs", "eval", "raw");

    [Fact]
    public void EvaluationBaseline_MatchesTheCheckedInSnapshot()
    {
        using var repo = new EvalRepo();
        string actual = Normalize(EvalReport.Render(repo));
        IReadOnlyDictionary<string, string> artifacts = EvalArtifacts.Render(repo);

        if (Environment.GetEnvironmentVariable("REPOCTX_UPDATE_EVAL_BASELINE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            File.WriteAllText(SnapshotPath, actual);
            Directory.CreateDirectory(RawDirectory);
            foreach (string existing in Directory.GetFiles(RawDirectory))
            {
                if (!artifacts.ContainsKey(Path.GetFileName(existing)))
                {
                    File.Delete(existing);
                }
            }

            foreach ((string name, string content) in artifacts)
            {
                File.WriteAllText(Path.Combine(RawDirectory, name), Normalize(content));
            }
        }

        Assert.True(File.Exists(SnapshotPath), $"missing baseline snapshot at {SnapshotPath}");
        Assert.Equal(Normalize(File.ReadAllText(SnapshotPath)), actual);
        Assert.True(Directory.Exists(RawDirectory), $"missing raw artifact directory at {RawDirectory}");
        Assert.Equal(
            artifacts.Keys,
            Directory.GetFiles(RawDirectory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal));
        foreach ((string name, string expected) in artifacts)
        {
            string path = Path.Combine(RawDirectory, name);
            Assert.True(File.Exists(path), $"missing raw evaluation artifact at {path}");
            Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(expected));
        }
    }

    /// <summary>Two renders of the same corpus are byte-identical.</summary>
    [Fact]
    public void EvaluationBaseline_IsDeterministic()
    {
        using var repo = new EvalRepo();

        Assert.Equal(EvalReport.Render(repo), EvalReport.Render(repo));
    }

    [Fact]
    public void McpSessionSurface_StaysWithinTokenBudget()
    {
        int tokens = Tokens.Count(McpSessionFixture.InstructionsAndToolSchemas);

        Assert.True(tokens <= 1_500, $"MCP instructions and tool schemas cost {tokens} tokens.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "RepoContext.slnx")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
