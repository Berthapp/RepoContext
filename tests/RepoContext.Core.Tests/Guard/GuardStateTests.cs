using RepoContext.Core;
using RepoContext.Core.Guard;
using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Guard;

/// <summary>The guard's bounded local counters and redirect bookkeeping (ADR 0023).</summary>
public class GuardStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-guard-").FullName;

    private RepoLayout Layout => RepoLayout.For(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static GuardDecision Denial(params string[] paths) => new()
    {
        Outcome = GuardOutcome.Redirected,
        Reason = "expensive",
        Deny = true,
        Targets = [.. paths.Select(p => new GuardTarget(p, 9_000, true, true, false))],
    };

    [Fact]
    public void Record_CountsEveryOutcome()
    {
        GuardState.Record(Layout, "e1", GuardDecision.Permit(GuardOutcome.Allowed, "cheap"), 4);
        GuardState.Record(Layout, "e1", GuardDecision.Permit(GuardOutcome.Unsupported, "shell"), 3);
        GuardState.Record(Layout, "e1", GuardDecision.Permit(GuardOutcome.Error, "boom"), 2);
        GuardState.Record(Layout, "e1", Denial("src/big.ts"), 11);

        GuardCounters counters = GuardState.ReadCounters(Layout);

        Assert.Equal(4, counters.Observed);
        Assert.Equal(1, counters.Allowed);
        Assert.Equal(1, counters.Unsupported);
        Assert.Equal(1, counters.Error);
        Assert.Equal(1, counters.Redirected);
        Assert.Equal(1, counters.Denied);
    }

    [Fact]
    public void Record_TracksRedirectsPerEpochAndPath()
    {
        GuardState.Record(Layout, "e1", Denial("src/big.ts"), 5);

        Assert.Equal(1, GuardState.Redirects(Layout, "e1", "src/big.ts"));
        Assert.Equal(0, GuardState.Redirects(Layout, "e1", "src/other.ts"));
        // A new context epoch starts with a clean slate, exactly like its session.
        Assert.Equal(0, GuardState.Redirects(Layout, "e2", "src/big.ts"));
    }

    [Fact]
    public void Record_DoesNotCountRedirectsForAnObservation()
    {
        GuardState.Record(
            Layout, "e1",
            Denial("src/big.ts") with { Deny = false }, 5);

        Assert.Equal(0, GuardState.Redirects(Layout, "e1", "src/big.ts"));
    }

    [Fact]
    public void ForgetEpoch_DropsOnlyThatEpoch()
    {
        GuardState.Record(Layout, "e1", Denial("a.ts"), 1);
        GuardState.Record(Layout, "e2", Denial("a.ts"), 1);

        GuardState.ForgetEpoch(Layout, "e1");

        Assert.Equal(0, GuardState.Redirects(Layout, "e1", "a.ts"));
        Assert.Equal(1, GuardState.Redirects(Layout, "e2", "a.ts"));
    }

    [Fact]
    public void State_NeverContainsAPath()
    {
        // The ledger must not become a new place repository structure leaks from.
        GuardState.Record(Layout, "e1", Denial("src/secret/customer-list.ts"), 5);

        string text = File.ReadAllText(GuardState.PathFor(Layout));

        Assert.DoesNotContain("customer-list", text, StringComparison.Ordinal);
        Assert.DoesNotContain("src/secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStats_SuppressesCountersButNotTheLoopBound()
    {
        // Statistics are optional; the bound that stops an enforcing guard from
        // denying the same read forever is not.
        string? previous = Environment.GetEnvironmentVariable(UsageRecorder.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, "1");

            GuardState.Record(Layout, "e1", Denial("src/big.ts"), 5);

            Assert.Equal(0, GuardState.ReadCounters(Layout).Observed);
            Assert.Equal(1, GuardState.Redirects(Layout, "e1", "src/big.ts"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, previous);
        }
    }

    [Fact]
    public void NoStats_WritesNothingAtAllForAPermissiveDecision()
    {
        string? previous = Environment.GetEnvironmentVariable(UsageRecorder.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, "1");

            GuardState.Record(Layout, "e1", GuardDecision.Permit(GuardOutcome.Allowed, "cheap"), 1);

            Assert.False(File.Exists(GuardState.PathFor(Layout)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsageRecorder.DisableVariable, previous);
        }
    }

    [Fact]
    public void DamagedState_ReadsAsEmptyInsteadOfFailing()
    {
        Directory.CreateDirectory(Layout.IndexDirectory);
        File.WriteAllText(GuardState.PathFor(Layout), "{ this is not json");

        Assert.Equal(0, GuardState.ReadCounters(Layout).Observed);
        Assert.Equal(0, GuardState.Redirects(Layout, "e1", "a.ts"));
    }

    [Fact]
    public void Latency_IsBoundedAndReportedAsAnUpperBound()
    {
        var latency = new GuardLatency();
        for (int i = 0; i < 100; i++)
        {
            latency = latency.With(i < 95 ? 7 : 450);
        }

        Assert.Equal(100, latency.Count);
        Assert.Equal(450, latency.MaxMs);
        Assert.Equal(10, latency.QuantileUpperBoundMs(0.5));
        Assert.Equal(10, latency.QuantileUpperBoundMs(0.95));
        Assert.Equal(GuardLatency.Bounds.Length + 1, latency.Buckets.Count);
    }

    [Fact]
    public void Describe_IsOneLineOfCountsOnly()
    {
        GuardState.Record(Layout, "e1", Denial("a.ts"), 3);

        string described = GuardState.Describe(GuardState.ReadCounters(Layout));

        Assert.Contains("observed=1", described, StringComparison.Ordinal);
        Assert.DoesNotContain("a.ts", described, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', described);
    }

    [Fact]
    public void ConcurrentRecords_DoNotLoseTheLoopBound()
    {
        // Several hooks can run at once (parallel tool calls, two agents). The
        // bound that stops an enforcing guard from denying forever must survive
        // that, and no writer may corrupt the file for the others.
        Parallel.For(0, 16, _ => GuardState.Record(Layout, "e1", Denial("src/big.ts"), 3));

        GuardCounters counters = GuardState.ReadCounters(Layout);
        Assert.Equal(16, counters.Observed);
        Assert.Equal(16, counters.Denied);
        Assert.Equal(16, GuardState.Redirects(Layout, "e1", "src/big.ts"));
    }
}
