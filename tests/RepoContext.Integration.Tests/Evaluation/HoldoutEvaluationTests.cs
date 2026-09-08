using System.Text;
using System.Text.Json;
using RepoContext.Cli.Output;
using RepoContext.Core.Context;
using RepoContext.Core.Graph;
using RepoContext.Core.Indexing;

namespace RepoContext.Integration.Tests.Evaluation;

/// <summary>
/// Retrieval diagnostics on pinned real code. Oracle-assisted evidence completion
/// is deliberately separate from coding-task success or a model cost comparison.
/// </summary>
public sealed class HoldoutEvaluationTests
{
    [Fact]
    public void FrozenManifestAndEverySourceLabel_MatchTheirChecksums() =>
        HoldoutManifest.Load().Validate();

    [Fact]
    public void HoldoutRetrieval_DoesNotRegressFromTheRecordedBaseline()
    {
        HoldoutManifest manifest = HoldoutManifest.Load();
        manifest.Validate();
        var reports = new List<HoldoutTaskReport>();
        foreach (HoldoutSource source in manifest.Repositories)
        {
            using var repo = new HoldoutRepo(source);
            foreach (HoldoutTask task in manifest.Tasks.Where(t => t.Repository == source.Id))
                reports.Add(Measure(repo, source, task));
        }

        var report = new HoldoutReport(
            HoldoutManifest.FrozenSha256,
            HoldoutManifest.Hash(File.ReadAllBytes(typeof(ContextEngine).Assembly.Location)),
            HoldoutManifest.Hash(File.ReadAllBytes(typeof(ContextCostModel).Assembly.Location)),
            reports);
        string path = Path.Combine(HoldoutManifest.DirectoryPath, "baseline.json");
        if (Environment.GetEnvironmentVariable("REPOCTX_UPDATE_HOLDOUT_BASELINE") == "1")
            File.WriteAllText(path, JsonSerializer.Serialize(report, HoldoutManifest.JsonOptions) + "\n");

        Assert.True(File.Exists(path), "Create the first reviewed baseline with REPOCTX_UPDATE_HOLDOUT_BASELINE=1.");
        HoldoutReport expected = JsonSerializer.Deserialize<HoldoutReport>(File.ReadAllText(path), HoldoutManifest.JsonOptions)!;
        Assert.Equal(HoldoutManifest.FrozenSha256, expected.ManifestSha256);
        Assert.Equal(expected.Tasks.Select(t => t.TaskId), reports.Select(t => t.TaskId));
        foreach (HoldoutTaskReport actual in reports)
        {
            HoldoutTaskReport baseline = expected.Tasks.Single(t => t.TaskId == actual.TaskId);
            HoldoutTask label = manifest.Tasks.Single(t => t.Id == actual.TaskId);
            HashSet<(string Path, int Line)> requiredLines = Lines(label.RequiredSpans.Select(s => new HoldoutRange(s.Path, s.StartLine, s.EndLine)));
            Assert.True(actual.EligibleRequiredFiles >= baseline.EligibleRequiredFiles, $"{actual.TaskId}: eligible candidate recall regressed");
            foreach (HoldoutRelationshipDiagnostic edge in baseline.Relationships.Where(r => r.Indexed))
                Assert.Contains(actual.Relationships, r => r.From == edge.From && r.To == edge.To && r.Kind == edge.Kind && r.Indexed);
            foreach (HoldoutArm arm in actual.Arms)
            {
                Assert.False(arm.Shortfall, $"{actual.TaskId}/{arm.Name}: no successful context response fit");
                HoldoutArm before = baseline.Arms.Single(a => a.Name == arm.Name);
                Assert.Empty(before.Items.Where(i => i.Required).Select(i => i.Path)
                    .Except(arm.Items.Select(i => i.Path), StringComparer.Ordinal));
                Assert.Empty(Lines(before.Items.SelectMany(i => i.Spans)).Intersect(requiredLines)
                    .Except(Lines(arm.Items.SelectMany(i => i.Spans))));
                Assert.True(arm.RequiredFilesDelivered >= before.RequiredFilesDelivered,
                    $"{actual.TaskId}/{arm.Name}: file recall regressed {before.RequiredFilesDelivered} -> {arm.RequiredFilesDelivered}");
                Assert.True(arm.RelevantLinesDelivered >= before.RelevantLinesDelivered,
                    $"{actual.TaskId}/{arm.Name}: relevant line coverage regressed {before.RelevantLinesDelivered} -> {arm.RelevantLinesDelivered}");
                if (arm.ResponseBudgetTokens is { } budget)
                    Assert.InRange(arm.ResponseTokens, 0, budget);
            }
        }
    }

    private static HoldoutTaskReport Measure(HoldoutRepo repo, HoldoutSource source, HoldoutTask task)
    {
        var engine = new ContextEngine(repo.Store, repo.Config);
        int fileCount = repo.Store.GetFiles().Count;
        ContextResult candidates = engine.Run(task.Query, new ContextOptions
        {
            Top = fileCount,
            Detail = ContextDetail.Paths,
        });
        Assert.Equal(0, candidates.Omissions.Top);
        Assert.Equal(0, candidates.Omissions.ResponseBudget);
        Assert.Equal(0, candidates.Omissions.BudgetTokens);
        Assert.Equal(0, candidates.Omissions.ProjectedReadBudget);
        var options = new ContextOptions { Top = 8, Detail = ContextDetail.Slices };
        if (task.HistoricalDiagnostic)
            Assert.Equal(ContextDetail.Slices, DetailPolicy.Resolve(QueryAnalyzer.Analyze(task.Query, repo.Config).Terms).Detail);
        ContextResult unbudgeted = engine.Run(task.Query, options);
        ContextCostModel jsonCost = ContextCostModel.ForCli(OutputFormat.Json);
        ContextCostModel compactCost = ContextCostModel.ForCli(OutputFormat.Json, compact: true);
        ContextCostModel markdownCost = ContextCostModel.ForCli(OutputFormat.Md);
        var arms = new List<HoldoutArm>
        {
            Arm("top8_unbudgeted", unbudgeted, jsonCost, null),
            Arm("json_2000", engine.Run(task.Query, options with { ResponseBudgetTokens = 2000 }, jsonCost), jsonCost, 2000),
            Arm("compact_json_2000", engine.Run(task.Query, options with { ResponseBudgetTokens = 2000 }, compactCost), compactCost, 2000),
            Arm("markdown_2000", engine.Run(task.Query, options with { ResponseBudgetTokens = 2000, SerializedCharging = false }, markdownCost), markdownCost, 2000),
        };

        List<HoldoutFileDiagnostic> fileDiagnostics = task.RequiredPaths.Select(path => new HoldoutFileDiagnostic(
            path,
            Rank(candidates, path),
            Rank(unbudgeted, path),
            arms.ToDictionary(a => a.Name, a => a.Items.FindIndex(i => i.Path == path) is var index && index >= 0 ? index + 1 : (int?)null)))
            .ToList();
        var relationships = task.RequiredRelationships.Select(r => new HoldoutRelationshipDiagnostic(
            r.From, r.To, r.Kind,
            Related.Query(repo.Store, r.From)?.Entries.Any(e => e.Path == r.To && e.Relation == Relation.Imports) == true))
            .ToList();
        return new HoldoutTaskReport(
            task.Id, task.Repository, task.Language, task.HistoricalDiagnostic,
            fileCount, candidates.TotalCandidates, candidates.Omissions.NonpositiveScore,
            candidates.Items.Count, task.RequiredPaths.Count,
            fileDiagnostics.Count(f => f.EligibleRank is not null),
            Lines(task.RequiredSpans.Select(s => new HoldoutRange(s.Path, s.StartLine, s.EndLine))).Count,
            new Dictionary<string, int>
            {
                ["json"] = jsonCost.Measure(unbudgeted),
                ["compact_json"] = compactCost.Measure(unbudgeted),
                ["markdown"] = markdownCost.Measure(unbudgeted),
            },
            fileDiagnostics, relationships, arms);

        HoldoutArm Arm(string name, ContextResult result, ContextCostModel cost, int? budget)
        {
            List<HoldoutRange> delivered = result.Items.SelectMany(item =>
                (item.Spans ?? []).Select(span => new HoldoutRange(item.Path, span.StartLine, span.EndLine)))
                .ToList();
            HashSet<(string Path, int Line)> deliveredLines = Lines(delivered);
            HashSet<(string Path, int Line)> requiredLines = Lines(task.RequiredSpans.Select(s =>
                new HoldoutRange(s.Path, s.StartLine, s.EndLine)));
            var followupReads = new List<HoldoutRead>();
            foreach (string missingPath in requiredLines.Except(deliveredLines)
                .Select(l => l.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                // Deliberately oracle-assisted: labels decide which full files to
                // read. This measures actual body bytes/tokens, not search effort,
                // a real agent's follow-ups, or savings over another workflow.
                byte[] bytes = File.ReadAllBytes(Path.Combine(repo.Root, missingPath));
                using var reader = new StreamReader(new MemoryStream(bytes));
                string body = reader.ReadToEnd();
                followupReads.Add(new(missingPath, bytes.Length, Tokens.Count(body)));
            }

            string surface = cost.SurfaceText(result);
            List<HoldoutItem> items = result.Items.Select(item => new HoldoutItem(
                item.Path, source.Files.Single(f => f.Path == item.Path).Role,
                task.RequiredPaths.Contains(item.Path),
                (item.Spans ?? []).Select(s => new HoldoutRange(item.Path, s.StartLine, s.EndLine)).ToList()))
                .ToList();
            return new HoldoutArm(name, budget, result.Shortfall is not null,
                Tokens.Count(surface), Encoding.UTF8.GetByteCount(surface),
                HoldoutManifest.Hash(Encoding.UTF8.GetBytes(surface)),
                items.Count(i => i.Required),
                requiredLines.Intersect(deliveredLines).Count(), deliveredLines.Count,
                followupReads, items,
                task.RequiredPaths.Where(path => !items.Any(i => i.Path == path)).ToList(),
                task.RequiredPaths.Where(path => Rank(unbudgeted, path) is not null && !items.Any(i => i.Path == path)).ToList(),
                task.RequiredPaths.Where(path => Rank(unbudgeted, path) is null && items.Any(i => i.Path == path)).ToList());
        }
    }

    private static int? Rank(ContextResult result, string path)
    {
        int index = result.Items.ToList().FindIndex(i => i.Path == path);
        return index < 0 ? null : index + 1;
    }

    internal static HashSet<(string Path, int Line)> Lines(IEnumerable<HoldoutRange> ranges) =>
        ranges.SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1)
            .Select(line => (range.Path, line))).ToHashSet();

    [Fact]
    public void RelevantLineAccounting_UnionsOverlaps_AndKeepsPathsDistinct()
    {
        HashSet<(string Path, int Line)> required = Lines(
            [new("a.cs", 2, 5), new("a.cs", 4, 7), new("b.ts", 4, 5)]);
        HashSet<(string Path, int Line)> delivered = Lines(
            [new("a.cs", 1, 4), new("a.cs", 3, 5), new("b.ts", 1, 4)]);
        Assert.Equal(8, required.Count);
        Assert.Equal(9, delivered.Count);
        Assert.Equal(5, required.Intersect(delivered).Count());
        Assert.Equal(3, required.Except(delivered).Count());
    }
}

public sealed record HoldoutReport(
    string ManifestSha256, string CoreAssemblySha256, string CliAssemblySha256,
    IReadOnlyList<HoldoutTaskReport> Tasks);

public sealed record HoldoutTaskReport(
    string TaskId, string Repository, string Language, bool HistoricalDiagnostic,
    int IndexedFiles, int GeneratedCandidates, int NonpositiveCandidates,
    int EligibleCandidates, int RequiredFiles, int EligibleRequiredFiles, int RequiredLines,
    IReadOnlyDictionary<string, int> SameUnbudgetedEvidenceTokens,
    IReadOnlyList<HoldoutFileDiagnostic> FileDiagnostics,
    IReadOnlyList<HoldoutRelationshipDiagnostic> Relationships,
    IReadOnlyList<HoldoutArm> Arms);

public sealed record HoldoutFileDiagnostic(
    string Path, int? EligibleRank, int? UnbudgetedRank,
    IReadOnlyDictionary<string, int?> DeliveredRanks);

public sealed record HoldoutRelationshipDiagnostic(string From, string To, string Kind, bool Indexed);
public sealed record HoldoutRange(string Path, int StartLine, int EndLine);
public sealed record HoldoutItem(string Path, string Role, bool Required, IReadOnlyList<HoldoutRange> Spans);
public sealed record HoldoutRead(string Path, long Bytes, int BodyTokens);

public sealed record HoldoutArm(
    string Name, int? ResponseBudgetTokens, bool Shortfall, int ResponseTokens,
    int ResponseBytes, string ResponseSha256, int RequiredFilesDelivered,
    int RelevantLinesDelivered, int DeliveredLines, IReadOnlyList<HoldoutRead> OracleFollowupReads,
    List<HoldoutItem> Items, IReadOnlyList<string> MissingRequiredPaths,
    IReadOnlyList<string> LostFromUnbudgetedTop8, IReadOnlyList<string> GainedBeyondUnbudgetedTop8);
