using RepoContext.Core;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;

namespace RepoContext.Core.Tests.Context;

/// <summary>
/// Binding delivered evidence to the context that actually holds it (ADR 0023,
/// milestone 3 of the same-quality, lower-cost plan).
/// </summary>
public class ContextEpochsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-epoch-").FullName;

    private RepoLayout Layout => RepoLayout.For(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AgentKey_SeparatesAgents_AndNeverStoresTheRawSessionId()
    {
        string first = ContextEpochs.AgentKey("claude-code", "session-one");
        string second = ContextEpochs.AgentKey("claude-code", "session-two");
        string otherClient = ContextEpochs.AgentKey("other", "session-one");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, otherClient);
        Assert.DoesNotContain("session-one", first, StringComparison.Ordinal);
        Assert.Equal(first, ContextEpochs.AgentKey("claude-code", "session-one"));
    }

    [Fact]
    public void SessionName_IsAValidSessionName()
    {
        string name = ContextEpochs.SessionName(
            ContextEpochs.AgentKey("claude-code", Guid.NewGuid().ToString()), 12);

        Assert.True(SessionStore.IsValidName(name), name);
    }

    [Fact]
    public void TryParseSessionName_RoundTrips_AndLeavesManualNamesAlone()
    {
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        Assert.True(ContextEpochs.TryParseSessionName(
            ContextEpochs.SessionName(agent, 7), out string parsed, out int epoch));
        Assert.Equal(agent, parsed);
        Assert.Equal(7, epoch);

        Assert.False(ContextEpochs.TryParseSessionName("review", out _, out _));
        Assert.False(ContextEpochs.TryParseSessionName("feature-42", out _, out _));
    }

    [Fact]
    public void Advance_StartsAtOneAndCounts()
    {
        string agent = ContextEpochs.AgentKey("claude-code", "s");

        Assert.Equal(1, ContextEpochs.Advance(Layout, agent, EpochReasons.Startup).Epoch);
        Assert.Equal(2, ContextEpochs.Advance(Layout, agent, EpochReasons.Compact).Epoch);
        Assert.Equal(2, ContextEpochs.Current(Layout, agent)!.Epoch);
        Assert.Equal(EpochReasons.Compact, ContextEpochs.Current(Layout, agent)!.Reason);
    }

    [Fact]
    public void Advance_KeepsAgentsIndependent()
    {
        string first = ContextEpochs.AgentKey("claude-code", "one");
        string second = ContextEpochs.AgentKey("claude-code", "two");

        ContextEpochs.Advance(Layout, first, EpochReasons.Startup);
        ContextEpochs.Advance(Layout, first, EpochReasons.Compact);
        ContextEpochs.Advance(Layout, second, EpochReasons.Startup);

        Assert.Equal(2, ContextEpochs.Current(Layout, first)!.Epoch);
        Assert.Equal(1, ContextEpochs.Current(Layout, second)!.Epoch);
    }

    [Fact]
    public void IsSuperseded_OnlyAppliesToEpochBoundNames()
    {
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        string first = ContextEpochs.Advance(Layout, agent, EpochReasons.Startup).SessionName;

        Assert.False(ContextEpochs.IsSuperseded(Layout, first));
        Assert.False(ContextEpochs.IsSuperseded(Layout, "review"));

        ContextEpochs.Advance(Layout, agent, EpochReasons.Compact);

        Assert.True(ContextEpochs.IsSuperseded(Layout, first));
        Assert.False(ContextEpochs.IsSuperseded(Layout, "review"));
    }

    [Fact]
    public void Retire_ForgetsTheAgent()
    {
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        ContextEpochs.Advance(Layout, agent, EpochReasons.Startup);

        ContextEpochs.Retire(Layout, agent);

        Assert.Null(ContextEpochs.Current(Layout, agent));
    }

    [Fact]
    public void CompactionInvalidatesDeliveredEvidence()
    {
        // The failure this exists to prevent: a local file keeps asserting
        // possession on behalf of a model whose context was replaced.
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        string before = ContextEpochs.Advance(Layout, agent, EpochReasons.Startup).SessionName;
        string receipt = Receipt.For(
            "a.ts", "hash-a", "slices", EvidenceUnitKind.Span, 1, 4, string.Empty, "const a = 1;");
        SessionStore.Save(
            Layout, before, EmptyResult(), new Dictionary<string, string> { ["a.ts"] = "hash-a" },
            [receipt]);

        Assert.NotEmpty(SessionStore.LoadState(Layout, before).Seen);

        string after = ContextEpochs.Advance(Layout, agent, EpochReasons.Compact).SessionName;

        Assert.NotEqual(before, after);
        Assert.Empty(SessionStore.LoadState(Layout, before).Seen);
        Assert.Empty(SessionStore.LoadState(Layout, before).Known);
        Assert.Empty(SessionStore.LoadState(Layout, after).Seen);
    }

    [Fact]
    public void SupersededSessions_AreNotWrittenToEither()
    {
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        string before = ContextEpochs.Advance(Layout, agent, EpochReasons.Startup).SessionName;
        ContextEpochs.Advance(Layout, agent, EpochReasons.Resume);

        SessionStore.Save(
            Layout, before, EmptyResult(),
            new Dictionary<string, string> { ["a.ts"] = "hash-a" });

        Assert.False(File.Exists(SessionStore.PathFor(Layout, before)));
    }

    [Fact]
    public void ManualSessions_KeepWorkingAcrossEpochs()
    {
        // --session review is the user's own bookkeeping; the guard's lifecycle
        // must not quietly take it over.
        string agent = ContextEpochs.AgentKey("claude-code", "s");
        ContextEpochs.Advance(Layout, agent, EpochReasons.Startup);
        SessionStore.Save(
            Layout, "review", EmptyResult(),
            new Dictionary<string, string> { ["a.ts"] = "hash-a" });

        ContextEpochs.Advance(Layout, agent, EpochReasons.Compact);

        Assert.Single(SessionStore.LoadState(Layout, "review").Known);
    }

    private static ContextResult EmptyResult() => new()
    {
        Query = "q",
        Terms = ["q"],
        State = "abc",
        ContentState = "abc",
        AnalysisState = "analysis",
        EvidenceId = "evidence",
        Detail = ContextDetail.Paths,
        Top = 3,
        Items = [],
        Reused = [],
        ReusedCount = 0,
        TotalCandidates = 0,
        Omitted = 0,
        Omissions = new OmissionReasons(),
        EstimatedTokens = 0,
        ContentTokens = 0,
        ProjectedReadTokens = 0,
    };
}
