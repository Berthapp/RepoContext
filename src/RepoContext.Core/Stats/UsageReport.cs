using System.Globalization;

namespace RepoContext.Core.Stats;

/// <summary>Aggregated token figures for one grouping (totals, a command, a day).</summary>
public sealed record UsageBucket
{
    public required int Calls { get; init; }

    /// <summary>
    /// Tokens all responses in this bucket cost, using the per-call calibration
    /// recorded in the ledger (raw o200k for uncalibrated calls).
    /// </summary>
    public required long ServedTokens { get; init; }

    /// <summary>Full-read tokens these responses are credited as replacing.</summary>
    public required long ReplacedTokens { get; init; }

    /// <summary>
    /// Estimated net tokens saved: credited reads minus response cost. This can
    /// be negative while usage is discovery-heavy and follows the documented
    /// replacement assumptions in ADR 0011.
    /// </summary>
    public long SavedTokens => ReplacedTokens - ServedTokens;
}

/// <summary>Aggregates for one command.</summary>
public sealed record UsageByCommand(string Command, UsageBucket Bucket);

/// <summary>Aggregates for one UTC day (<c>yyyy-MM-dd</c>).</summary>
public sealed record UsageByDay(string Day, UsageBucket Bucket);

/// <summary>
/// One point on the cumulative call timeline: the running totals after the
/// call it is anchored on. Long logs are bucketed (see
/// <see cref="UsageReport.TimelinePointCount"/>), so a point can stand for
/// more than one call; the last point always carries the report totals.
/// </summary>
public sealed record UsagePoint
{
    /// <summary>1-based ordinal of the last recorded call this point includes.</summary>
    public required int Call { get; init; }

    /// <summary>UTC day (<c>yyyy-MM-dd</c>) of that call.</summary>
    public required string Day { get; init; }

    /// <summary>Response tokens of every call up to and including <see cref="Call"/>.</summary>
    public required long ServedTokens { get; init; }

    /// <summary>Replaced full-read tokens up to and including <see cref="Call"/>.</summary>
    public required long ReplacedTokens { get; init; }

    /// <summary>Cumulative net saving at this point.</summary>
    public long SavedTokens => ReplacedTokens - ServedTokens;
}

/// <summary>
/// The aggregated usage report behind <c>repoctx stats</c> (ADR 0011). A pure
/// function of the usage log: the recent-days window anchors on the newest
/// record, never on the wall clock, so identical log ⇒ byte-identical output.
/// </summary>
public sealed record UsageReport
{
    /// <summary>Days shown in the recent-days breakdown.</summary>
    public const int RecentDayCount = 14;

    /// <summary>
    /// Upper bound on <see cref="Timeline"/> points. A longer log is bucketed
    /// into this many points so the rendered curve stays small and readable
    /// without dropping any call from the running totals.
    /// </summary>
    public const int TimelinePointCount = 120;

    public required UsageBucket Totals { get; init; }

    /// <summary>Per-command aggregates, ordered by command name (Ordinal).</summary>
    public required IReadOnlyList<UsageByCommand> Commands { get; init; }

    /// <summary>The most recent recorded days (up to <see cref="RecentDayCount"/>), ascending.</summary>
    public required IReadOnlyList<UsageByDay> Days { get; init; }

    /// <summary>
    /// The cumulative timeline over all recorded calls in chronological order
    /// (at most <see cref="TimelinePointCount"/> points), ascending. Empty for
    /// an empty log.
    /// </summary>
    public required IReadOnlyList<UsagePoint> Timeline { get; init; }

    /// <summary>UTC day of the first record, null on an empty log.</summary>
    public string? FirstDay { get; init; }

    /// <summary>UTC day of the last record, null on an empty log.</summary>
    public string? LastDay { get; init; }

    /// <summary>Builds the report from raw records.</summary>
    public static UsageReport Build(IReadOnlyList<UsageRecord> records)
    {
        List<UsageByCommand> commands = records
            .GroupBy(r => r.Command, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new UsageByCommand(g.Key, Aggregate(g)))
            .ToList();

        List<UsageByDay> allDays = records
            .GroupBy(DayOf, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new UsageByDay(g.Key, Aggregate(g)))
            .ToList();

        return new UsageReport
        {
            Totals = Aggregate(records),
            Commands = commands,
            Days = allDays.Count <= RecentDayCount
                ? allDays
                : allDays[^RecentDayCount..],
            Timeline = BuildTimeline(records),
            FirstDay = allDays.Count > 0 ? allDays[0].Day : null,
            LastDay = allDays.Count > 0 ? allDays[^1].Day : null,
        };
    }

    /// <summary>The UTC day a record belongs to.</summary>
    public static string DayOf(UsageRecord record) =>
        record.Ts.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Accumulates the log into the cumulative timeline. Records are ordered by
    /// timestamp (a stable sort, so a clock that stands still keeps log order)
    /// and bucketed into at most <see cref="TimelinePointCount"/> points: every
    /// call contributes to the running totals, and the closing call of each
    /// bucket carries them. Pure, like the rest of the report.
    /// </summary>
    private static List<UsagePoint> BuildTimeline(IReadOnlyList<UsageRecord> records)
    {
        var points = new List<UsagePoint>(Math.Min(records.Count, TimelinePointCount));
        int target = Math.Min(records.Count, TimelinePointCount);
        long served = 0;
        long replaced = 0;
        int emitted = 0;
        int call = 0;
        foreach (UsageRecord record in records.OrderBy(r => r.Ts))
        {
            call++;
            served += record.Served;
            replaced += record.Replaced;

            // The bucket this call closes, 1..target; monotonic, and the final
            // call always closes the last bucket.
            int bucket = (int)((long)call * target / records.Count);
            if (bucket <= emitted)
            {
                continue;
            }

            emitted = bucket;
            points.Add(new UsagePoint
            {
                Call = call,
                Day = DayOf(record),
                ServedTokens = served,
                ReplacedTokens = replaced,
            });
        }

        return points;
    }

    private static UsageBucket Aggregate(IEnumerable<UsageRecord> records)
    {
        int calls = 0;
        long served = 0;
        long replaced = 0;
        foreach (UsageRecord record in records)
        {
            calls++;
            served += record.Served;
            replaced += record.Replaced;
        }

        return new UsageBucket { Calls = calls, ServedTokens = served, ReplacedTokens = replaced };
    }
}
