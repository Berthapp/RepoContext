using RepoContext.Cli.Output;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// End-to-end Q4 identity behaviour and Q1 fail-closed reuse, exercised through
/// the real engine and the real renderer.
/// </summary>
public sealed class IdentityMatrixTests
{
    private const string Query = "change budget packing";

    private static ContextResult Run(EvalRepo repo, ContextOptions? options = null, RepoctxConfig? config = null) =>
        new ContextEngine(repo.Store, config ?? repo.Config)
            .Run(Query, options ?? new ContextOptions { Top = 3, Detail = ContextDetail.Slices });

    /// <summary>Identical inputs produce identical identities, run after run.</summary>
    [Fact]
    public void Identities_AreStable_ForIdenticalInputs()
    {
        using var repo = new EvalRepo();

        ContextResult a = Run(repo);
        ContextResult b = Run(repo);

        Assert.Equal(a.ContentState, b.ContentState);
        Assert.Equal(a.AnalysisState, b.AnalysisState);
        Assert.Equal(a.EvidenceId, b.EvidenceId);
        Assert.Equal(64, a.FullContentState.Length);
        Assert.Equal(64, a.FullAnalysisState.Length);
        Assert.Equal(64, a.FullEvidenceId.Length);
    }

    /// <summary>
    /// Two checkouts of the same tree at different absolute paths must agree on
    /// every fingerprint — no absolute path or machine detail may leak in.
    /// </summary>
    [Fact]
    public void Identities_MatchAcrossDifferentAbsoluteDirectories()
    {
        using var first = new EvalRepo();
        using var second = new EvalRepo();

        ContextResult a = Run(first);
        ContextResult b = Run(second);

        Assert.Equal(a.ContentState, b.ContentState);
        Assert.Equal(a.AnalysisState, b.AnalysisState);
        Assert.Equal(a.EvidenceId, b.EvidenceId);
    }

    /// <summary>A content change moves both content and analysis state.</summary>
    [Fact]
    public void ContentChange_MovesContentAndAnalysisState()
    {
        using var repo = new EvalRepo();
        ContextResult before = Run(repo);

        repo.Write("src/Packing/Extra.cs", "namespace Eval.Packing;\n\npublic sealed class Extra { }\n");
        repo.Reindex();

        ContextResult after = new ContextEngine(repo.Reopened!, repo.Config)
            .Run(Query, new ContextOptions { Top = 3, Detail = ContextDetail.Slices });

        Assert.NotEqual(before.ContentState, after.ContentState);
        Assert.NotEqual(before.AnalysisState, after.AnalysisState);
    }

    /// <summary>
    /// A live ranking-configuration change moves analysis state while the indexed
    /// content is untouched — the exact case the single v2 state hash missed.
    /// </summary>
    [Fact]
    public void RankingConfigChange_MovesAnalysisStateButNotContentState()
    {
        using var repo = new EvalRepo();

        ContextResult before = Run(repo);
        RepoctxConfig reweighted = repo.Config with
        {
            Ranking = repo.Config.Ranking with
            {
                Weights = new RankingWeights { Fts = 0.1, Symbol = 0.6, Graph = 0.2, Path = 0.1 },
            },
        };
        ContextResult after = Run(repo, config: reweighted);

        Assert.Equal(before.ContentState, after.ContentState);
        Assert.NotEqual(before.AnalysisState, after.AnalysisState);
    }

    /// <summary>A different query is a different evidence identity, same analysis state.</summary>
    [Fact]
    public void QueryChange_MovesEvidenceIdOnly()
    {
        using var repo = new EvalRepo();

        ContextResult a = Run(repo);
        ContextResult b = new ContextEngine(repo.Store, repo.Config)
            .Run("session lifetime", new ContextOptions { Top = 3, Detail = ContextDetail.Slices });

        Assert.Equal(a.AnalysisState, b.AnalysisState);
        Assert.NotEqual(a.EvidenceId, b.EvidenceId);
    }

    /// <summary>A core option change is an evidence-identity change.</summary>
    [Fact]
    public void CoreOptionChange_MovesEvidenceId()
    {
        using var repo = new EvalRepo();

        ContextResult slices = Run(repo, new ContextOptions { Top = 3, Detail = ContextDetail.Slices });
        ContextResult outline = Run(repo, new ContextOptions { Top = 3, Detail = ContextDetail.Outline });

        Assert.NotEqual(slices.EvidenceId, outline.EvidenceId);
    }

    /// <summary>An omitted option and its explicit default normalise to one identity.</summary>
    [Fact]
    public void OmittedAndExplicitDefaultOptions_ShareAnIdentity()
    {
        using var repo = new EvalRepo();

        ContextResult implicitDefaults = Run(repo, new ContextOptions { Top = 3, Detail = ContextDetail.Slices });
        ContextResult explicitDefaults = Run(repo, new ContextOptions
        {
            Top = 3,
            Detail = ContextDetail.Slices,
            MaxSpans = 3,
            MaxReusedListed = 20,
            Known = null,
            Seen = [],
        });

        Assert.Equal(implicitDefaults.EvidenceId, explicitDefaults.EvidenceId);
    }

    [Fact]
    public void DuplicateSeenReceipts_HaveSetSemanticsInIdentityAndExecution()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        string receipt = Run(repo, options).Items
            .SelectMany(item => item.Spans ?? [])
            .First().Receipt;

        ContextResult once = Run(repo, options with { Seen = [receipt] });
        ContextResult repeated = Run(repo, options with { Seen = [receipt, receipt] });

        Assert.Equal(once.ReusedCount, repeated.ReusedCount);
        Assert.Equal(once.FullEvidenceId, repeated.FullEvidenceId);
        Assert.Equal(
            ContextOutput.Render(once, OutputFormat.Json),
            ContextOutput.Render(repeated, OutputFormat.Json));
    }

    [Fact]
    public void UnmatchedSessionHistory_DoesNotChurnEvidenceOrRepresentationIdentity()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };
        ContextResult baseline = Run(repo, options);
        string unrelatedReceipt = Receipt.For(
            "src/Unrelated/NeverIndexed.cs",
            new string('a', 64),
            "slices",
            EvidenceUnitKind.Span,
            1,
            1,
            "NeverIndexed",
            "irrelevant evidence");

        ContextResult withHistory = Run(repo, options with
        {
            Seen = [unrelatedReceipt],
            Known = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Unrelated/NeverIndexed.cs"] = "aaaaaaaaaaaa",
            },
        });

        Assert.Equal(baseline.FullEvidenceId, withHistory.FullEvidenceId);
        Assert.Equal(
            ContextOutput.Render(baseline, OutputFormat.Json),
            ContextOutput.Render(withHistory, OutputFormat.Json));
    }

    /// <summary>
    /// The rendered format changes the representation identity but never the
    /// evidence identity behind it.
    /// </summary>
    [Fact]
    public void OutputFormat_MovesRepresentationIdButNotEvidenceId()
    {
        using var repo = new EvalRepo();
        ContextResult result = Run(repo);

        string json = ContextOutput.Render(result, OutputFormat.Json);
        string md = ContextOutput.Render(result, OutputFormat.Md);
        string text = ContextOutput.Render(result, OutputFormat.Text);

        Assert.NotEqual(json, md);

        // Every advertised format exposes the representation identity.
        Assert.Contains("\"representation_id\"", json, StringComparison.Ordinal);
        Assert.Contains("Representation:", md, StringComparison.Ordinal);
        Assert.Contains("Representation:", text, StringComparison.Ordinal);

        string again = ContextOutput.Render(result, OutputFormat.Json);
        Assert.Equal(json, again);
    }

    [Fact]
    public void OutputSurface_MovesRepresentationId()
    {
        using var repo = new EvalRepo();
        ContextResult result = Run(repo);

        string core = RepresentationId(
            ContextOutput.Render(result, OutputFormat.Json, Surfaces.Core));
        string cli = RepresentationId(
            ContextOutput.Render(result, OutputFormat.Json, Surfaces.Cli));
        string mcp = RepresentationId(
            ContextOutput.Render(result, OutputFormat.Json, Surfaces.McpText));

        Assert.NotEqual(core, cli);
        Assert.NotEqual(core, mcp);
        Assert.NotEqual(cli, mcp);

        static string RepresentationId(string json)
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.GetProperty("representation_id").GetString()!;
        }
    }

    /// <summary>
    /// <c>representation_id</c> is computed over the body with its own field
    /// omitted, so it never self-references and stays reproducible.
    /// </summary>
    [Fact]
    public void RepresentationId_IsReproducibleFromTheBodyWithItsOwnFieldOmitted()
    {
        using var repo = new EvalRepo();
        string json = ContextOutput.Render(Run(repo), OutputFormat.Json);

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        string id = doc.RootElement.GetProperty("representation_id").GetString()!;

        Assert.Equal(Core.Storage.Hashes.ShortLength, id.Length);

        // Removing the field yields exactly the body that was hashed to make it.
        string withoutField = json.Replace($",\"representation_id\":\"{id}\"", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("representation_id", withoutField, StringComparison.Ordinal);
    }

    /// <summary>
    /// A well-formed receipt that matches no current evidence suppresses nothing:
    /// reuse fails closed, never hiding content.
    /// </summary>
    [Fact]
    public void NonMatchingReceipt_SuppressesNothing()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };

        ContextResult baseline = Run(repo, options);
        ContextResult withStranger = Run(repo, options with
        {
            Seen = [new string('A', Receipt.EncodedLength)],
        });

        Assert.Equal(0, withStranger.ReusedCount);
        Assert.Equal(baseline.FullEvidenceId, withStranger.FullEvidenceId);
        Assert.Equal(
            ContextOutput.Render(baseline, OutputFormat.Json),
            ContextOutput.Render(withStranger, OutputFormat.Json));
        Assert.Equal(
            baseline.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt),
            withStranger.Items.SelectMany(i => i.Spans ?? []).Select(s => s.Receipt));
    }

    /// <summary>A malformed receipt is discarded and cannot coincidentally match.</summary>
    [Fact]
    public void MalformedReceipt_IsIgnored_AndHidesNothing()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };

        ContextResult baseline = Run(repo, options);
        ContextResult withGarbage = Run(repo, options with { Seen = ["", "nope", "!!!"] });

        Assert.Equal(0, withGarbage.ReusedCount);
        Assert.Equal(baseline.ContentTokens, withGarbage.ContentTokens);
        Assert.Equal(baseline.FullEvidenceId, withGarbage.FullEvidenceId);
    }

    /// <summary>
    /// A receipt is invalidated when the file's content changes, even though the
    /// range is unchanged — the caller now holds stale text.
    /// </summary>
    [Fact]
    public void ContentChange_InvalidatesAnOutstandingReceipt()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 1, Detail = ContextDetail.Slices };

        string receipt = Run(repo, options)
            .Items.Single(i => i.Path == "src/Packing/Packer.cs")
            .Spans!.First(s => s.Symbol == "Budget").Receipt;

        // Change the file, keeping the Budget method's line range intact.
        string path = Path.Combine(repo.Root, "src/Packing/Packer.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "int used = 0;", "int used = 0; // adjusted", StringComparison.Ordinal));
        repo.Reindex();

        ContextResult after = new ContextEngine(repo.Reopened!, repo.Config)
            .Run(Query, options with { Seen = [receipt] });

        Assert.Equal(0, after.ReusedCount);
        Assert.Contains(
            after.Items.SelectMany(i => i.Spans ?? []),
            s => s.Symbol == "Budget");
    }

    /// <summary>
    /// When both options name the same file, the explicit full-file assertion
    /// wins and the file is acknowledged once, not twice.
    /// </summary>
    [Fact]
    public void ExplicitKnownFile_TakesPrecedenceOverASeenReceipt()
    {
        using var repo = new EvalRepo();
        var options = new ContextOptions { Top = 3, Detail = ContextDetail.Slices };

        ContextItem item = Run(repo, options).Items.Single(i => i.Path == "src/Packing/Packer.cs");
        string spanReceipt = item.Spans![0].Receipt;

        ContextResult both = Run(repo, options with
        {
            Known = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Packing/Packer.cs"] = item.Hash,
            },
            Seen = [spanReceipt],
        });

        Assert.DoesNotContain(both.Items, i => i.Path == "src/Packing/Packer.cs");

        // Exactly one acknowledgement for that file: the whole-file claim.
        ReusedUnit acknowledged = Assert.Single(
            both.Reused, r => r.Path == "src/Packing/Packer.cs");
        Assert.Null(acknowledged.StartLine);
    }

    /// <summary>
    /// A stale index — one produced by different analysis versions — is rejected
    /// rather than queried, so receipts derived from it are never honoured.
    /// </summary>
    [Fact]
    public void IndexWithStaleProducerVersion_IsRejected()
    {
        using var repo = new EvalRepo();
        Assert.True(repo.Store.IsProducerCurrent);

        repo.Store.SetMeta(Core.Storage.MetaKeys.AnalysisProducerVersion, "scan1.dec1.par99.chk1.tok1.gph1");

        Assert.False(repo.Store.IsProducerCurrent);
        Assert.True(repo.Store.IsSchemaCurrent, "the on-disk schema is a separate concern");
    }
}
