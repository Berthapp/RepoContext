using RepoContext.Core.Configuration;
using RepoContext.Core.Guard;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Tests.Guard;

/// <summary>The deterministic read-cost policy (ADR 0023).</summary>
public class ReadCostPolicyTests
{
    private sealed class FakeIndex(params (string Path, int Tokens, int Lines)[] files) : IGuardIndex
    {
        public bool TryGetMetrics(string relativePath, out GuardFileMetrics metrics)
        {
            foreach ((string path, int tokens, int lines) in files)
            {
                if (path == relativePath)
                {
                    metrics = new GuardFileMetrics(tokens, lines);
                    return true;
                }
            }

            metrics = default;
            return false;
        }
    }

    private static ReadCostPolicy Policy(
        IGuardIndex index, int maxReadTokens = 2_000, int maxRedirects = 1,
        TokenScale scale = default) =>
        new(index,
            new ReadCostPolicyOptions
            {
                MaxReadTokens = maxReadTokens,
                MaxRedirectsPerPath = maxRedirects,
            },
            scale,
            path => path.Replace('\\', '/').TrimStart('/'));

    private static GuardReadRequest Read(string path, int? start = null, int? limit = null) =>
        new(path, start, limit, "read-tool");

    [Fact]
    public void CheapRead_IsAllowed()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("small.ts", 300, 30)));

        GuardDecision decision = policy.Evaluate([Read("small.ts")], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Allowed, decision.Outcome);
        Assert.False(decision.Deny);
        Assert.Empty(decision.Suggestions);
    }

    [Fact]
    public void ExpensiveRead_IsDeniedOnceWithAConcreteAlternative()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read("src/big.ts")], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Redirected, decision.Outcome);
        Assert.True(decision.Deny);
        Assert.Contains(decision.Suggestions, s => s.StartsWith("repoctx outline", StringComparison.Ordinal));
        Assert.Contains(decision.Suggestions, s => s.Contains("--detail slices", StringComparison.Ordinal));
        Assert.Equal(9_000, decision.EstimatedTokens);
    }

    [Fact]
    public void PartialRead_IsPricedByWhatWasActuallyRequested()
    {
        // A client that already asks for 40 lines is doing the right thing; it
        // must not be charged for the 860 lines it did not ask for.
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate(
            [Read("src/big.ts", start: 100, limit: 40)], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Allowed, decision.Outcome);
        Assert.Equal(400, decision.EstimatedTokens);
    }

    [Fact]
    public void LimitBeyondTheFile_IsNotChargedTwice()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate(
            [Read("src/big.ts", start: 1, limit: 100_000)], GuardMode.Enforce, _ => 0);

        Assert.Equal(9_000, decision.EstimatedTokens);
    }

    [Fact]
    public void CostIsTokens_NotLines()
    {
        // Same line count, very different cost: the threshold is a token budget
        // precisely because 400 lines of JSON and 400 lines of prose are not the
        // same purchase.
        ReadCostPolicy policy = Policy(new FakeIndex(("dense.json", 12_000, 400), ("sparse.ts", 900, 400)));

        Assert.True(policy.Evaluate([Read("dense.json")], GuardMode.Enforce, _ => 0).Deny);
        Assert.False(policy.Evaluate([Read("sparse.ts")], GuardMode.Enforce, _ => 0).Deny);
    }

    [Fact]
    public void TokenProfileCalibrationIsApplied()
    {
        TokenScale claude = TokenScale.From(new RepoctxConfig
        {
            Tokens = new TokenOptions { Profile = "claude" },
        });
        ReadCostPolicy policy = Policy(
            new FakeIndex(("src/mid.ts", 1_800, 200)), maxReadTokens: 2_000, scale: claude);

        GuardDecision decision = policy.Evaluate([Read("src/mid.ts")], GuardMode.Enforce, _ => 0);

        // 1800 raw o200k tokens are ~2160 in the Claude profile, so the same file
        // is expensive for one model family and not for another.
        Assert.Equal(2_160, decision.EstimatedTokens);
        Assert.True(decision.Deny);
    }

    [Fact]
    public void ObserveMode_NeverDenies()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read("src/big.ts")], GuardMode.Observe, _ => 0);

        Assert.Equal(GuardOutcome.Redirected, decision.Outcome);
        Assert.False(decision.Deny);
    }

    [Fact]
    public void OffMode_DoesNotEvenLook()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read("src/big.ts")], GuardMode.Off, _ => 0);

        Assert.Equal(GuardOutcome.Allowed, decision.Outcome);
        Assert.Empty(decision.Targets);
    }

    [Fact]
    public void RepeatedRequest_Escalates_SoNoAgentIsTrapped()
    {
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read("src/big.ts")], GuardMode.Enforce, _ => 1);

        Assert.Equal(GuardOutcome.Escalated, decision.Outcome);
        Assert.False(decision.Deny);
        Assert.Empty(decision.Suggestions);
    }

    [Fact]
    public void UnindexedFile_IsNeverJudged()
    {
        // Sensitive and excluded files are not in the index. They must stay
        // unmentionable: no cost estimate, no suggestion, no new lookup path.
        ReadCostPolicy policy = Policy(new FakeIndex(("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read(".env")], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Allowed, decision.Outcome);
        Assert.False(Assert.Single(decision.Targets).Indexed);
        Assert.Empty(decision.Suggestions);
    }

    [Fact]
    public void PathOutsideTheRepository_IsNotJudged()
    {
        var policy = new ReadCostPolicy(
            new FakeIndex(("src/big.ts", 9_000, 900)),
            new ReadCostPolicyOptions(),
            TokenScale.Identity,
            _ => null);

        GuardDecision decision = policy.Evaluate(
            [Read("/etc/hosts")], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Allowed, decision.Outcome);
        Assert.False(Assert.Single(decision.Targets).Indexed);
    }

    [Fact]
    public void SeveralFiles_AreJudgedTogether_AndOnlyTheExpensiveOnesAreNamed()
    {
        ReadCostPolicy policy = Policy(
            new FakeIndex(("small.ts", 100, 10), ("src/big.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate(
            [Read("small.ts"), Read("src/big.ts")], GuardMode.Enforce, _ => 0);

        Assert.True(decision.Deny);
        Assert.Equal(2, decision.Targets.Count);
        Assert.DoesNotContain(decision.Suggestions, s => s.Contains("small.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void EscalationNeedsEveryExpensiveFileToHaveBeenRedirected()
    {
        ReadCostPolicy policy = Policy(
            new FakeIndex(("a.ts", 9_000, 900), ("b.ts", 9_000, 900)));

        GuardDecision decision = policy.Evaluate(
            [Read("a.ts"), Read("b.ts")], GuardMode.Enforce, path => path == "a.ts" ? 1 : 0);

        Assert.Equal(GuardOutcome.Redirected, decision.Outcome);
        Assert.True(decision.Deny);
    }

    [Fact]
    public void SuggestedCommandsQuoteUntrustedPaths()
    {
        // A path is attacker-controllable input that ends up in text a model may
        // run. The only acceptable outcome is that it stays exactly one argument.
        const string hostile = "src/a'; rm -rf ~;.ts";
        ReadCostPolicy policy = Policy(new FakeIndex((hostile, 9_000, 900)));

        GuardDecision decision = policy.Evaluate([Read(hostile)], GuardMode.Enforce, _ => 0);

        string outline = decision.Suggestions
            .Single(s => s.StartsWith("repoctx outline ", StringComparison.Ordinal));
        Assert.True(ShellReadParser.TrySplit(outline, out List<string> words));
        Assert.Equal<string[]>(["repoctx", "outline", hostile], [.. words]);
    }

    [Fact]
    public void NoReads_IsUnsupportedRatherThanApproved()
    {
        ReadCostPolicy policy = Policy(new FakeIndex());

        GuardDecision decision = policy.Evaluate([], GuardMode.Enforce, _ => 0);

        Assert.Equal(GuardOutcome.Unsupported, decision.Outcome);
    }
}
