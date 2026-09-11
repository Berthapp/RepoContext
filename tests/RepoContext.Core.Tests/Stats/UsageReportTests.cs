using RepoContext.Core.Stats;

namespace RepoContext.Core.Tests.Stats;

public class UsageReportTests
{
    [Fact]
    public void Build_EmptyLog_HasZeroTotalsAndNoBreakdowns()
    {
        UsageReport report = UsageReport.Build([]);

        Assert.Equal(0, report.Totals.Calls);
        Assert.Equal(0, report.Totals.SavedTokens);
        Assert.Empty(report.Commands);
        Assert.Empty(report.Days);
        Assert.Empty(report.Timeline);
        Assert.Null(report.FirstDay);
        Assert.Null(report.LastDay);
    }

    [Fact]
    public void Build_AggregatesTotalsPerCommandAndPerDay()
    {
        UsageReport report = UsageReport.Build(
        [
            Record("search", day: 10, served: 200, replaced: 0),
            Record("context", day: 10, served: 2000, replaced: 5000),
            Record("context", day: 11, served: 1000, replaced: 4000),
            Record("outline", day: 11, served: 300, replaced: 900),
        ]);

        Assert.Equal(4, report.Totals.Calls);
        Assert.Equal(3500, report.Totals.ServedTokens);
        Assert.Equal(9900, report.Totals.ReplacedTokens);
        Assert.Equal(6400, report.Totals.SavedTokens);

        // Commands are ordered by name (Ordinal) for deterministic output.
        Assert.Equal(["context", "outline", "search"], report.Commands.Select(c => c.Command));
        UsageByCommand context = report.Commands[0];
        Assert.Equal(2, context.Bucket.Calls);
        Assert.Equal(6000, context.Bucket.SavedTokens);

        Assert.Equal(["2026-07-10", "2026-07-11"], report.Days.Select(d => d.Day));
        Assert.Equal("2026-07-10", report.FirstDay);
        Assert.Equal("2026-07-11", report.LastDay);
        Assert.Equal(2, report.Days[1].Bucket.Calls);
    }

    [Fact]
    public void Build_KeepsOnlyTheMostRecentRecordedDays()
    {
        var records = new List<UsageRecord>();
        for (int day = 1; day <= 20; day++)
        {
            records.Add(Record("search", day, served: 10, replaced: 0));
        }

        UsageReport report = UsageReport.Build(records);

        Assert.Equal(UsageReport.RecentDayCount, report.Days.Count);
        Assert.Equal("2026-07-07", report.Days[0].Day);
        Assert.Equal("2026-07-20", report.Days[^1].Day);
        // Totals and first/last still cover the whole log.
        Assert.Equal(20, report.Totals.Calls);
        Assert.Equal("2026-07-01", report.FirstDay);
    }

    [Fact]
    public void Build_AccumulatesEveryCallIntoTheTimeline()
    {
        UsageReport report = UsageReport.Build(
        [
            Record("search", day: 10, served: 200, replaced: 0),
            Record("context", day: 10, served: 2000, replaced: 5000),
            Record("context", day: 11, served: 1000, replaced: 4000),
        ]);

        Assert.Equal(3, report.Timeline.Count);
        Assert.Equal([1, 2, 3], report.Timeline.Select(p => p.Call));
        Assert.Equal(["2026-07-10", "2026-07-10", "2026-07-11"], report.Timeline.Select(p => p.Day));

        // Running totals, call by call.
        Assert.Equal(200, report.Timeline[0].ServedTokens);
        Assert.Equal(0, report.Timeline[0].ReplacedTokens);
        Assert.Equal(-200, report.Timeline[0].SavedTokens);
        Assert.Equal(2200, report.Timeline[1].ServedTokens);
        Assert.Equal(5000, report.Timeline[1].ReplacedTokens);

        // The last point is the report total, by construction.
        Assert.Equal(report.Totals.ServedTokens, report.Timeline[^1].ServedTokens);
        Assert.Equal(report.Totals.ReplacedTokens, report.Timeline[^1].ReplacedTokens);
        Assert.Equal(report.Totals.SavedTokens, report.Timeline[^1].SavedTokens);
    }

    [Fact]
    public void Build_BucketsALongLogWithoutLosingACall()
    {
        var records = new List<UsageRecord>();
        for (int i = 0; i < 500; i++)
        {
            records.Add(Record("context", day: 5, served: 10, replaced: 100));
        }

        UsageReport report = UsageReport.Build(records);

        Assert.Equal(UsageReport.TimelinePointCount, report.Timeline.Count);
        // Bucketing thins the curve; it never drops a call from the running sum.
        Assert.Equal(500, report.Timeline[^1].Call);
        Assert.Equal(5_000, report.Timeline[^1].ServedTokens);
        Assert.Equal(50_000, report.Timeline[^1].ReplacedTokens);
        Assert.Equal(report.Totals.SavedTokens, report.Timeline[^1].SavedTokens);

        for (int i = 1; i < report.Timeline.Count; i++)
        {
            Assert.True(report.Timeline[i].Call > report.Timeline[i - 1].Call);
            Assert.True(report.Timeline[i].ReplacedTokens >= report.Timeline[i - 1].ReplacedTokens);
        }
    }

    [Fact]
    public void SavedTokens_IsNegativeForDiscoveryOnlyUsage()
    {
        UsageReport report = UsageReport.Build([Record("search", day: 10, served: 500, replaced: 0)]);

        Assert.Equal(-500, report.Totals.SavedTokens);
    }

    private static UsageRecord Record(string command, int day, int served, int replaced) => new()
    {
        Ts = new DateTimeOffset(2026, 7, day, 12, 0, 0, TimeSpan.Zero),
        Command = command,
        Source = UsageSources.Mcp,
        Served = served,
        Replaced = replaced,
    };
}
