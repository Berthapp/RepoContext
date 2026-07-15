using System.Globalization;

namespace RepoContext.Core.Stats;

/// <summary>Aggregated token figures for one grouping (totals, a command, a day).</summary>
public sealed record UsageBucket
{
    public required int Calls { get; init; }

    /// <summary>Tokens all responses in this bucket cost (real o200k counts).</summary>
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
/// The aggregated usage report behind <c>repoctx stats</c> (ADR 0011). A pure
/// function of the usage log: the recent-days window anchors on the newest
/// record, never on the wall clock, so identical log ⇒ byte-identical output.
/// </summary>
public sealed record UsageReport
{
    /// <summary>Days shown in the recent-days breakdown.</summary>
    public const int RecentDayCount = 14;

    public required UsageBucket Totals { get; init; }

    /// <summary>Per-command aggregates, ordered by command name (Ordinal).</summary>
    public required IReadOnlyList<UsageByCommand> Commands { get; init; }

    /// <summary>The most recent recorded days (up to <see cref="RecentDayCount"/>), ascending.</summary>
    public required IReadOnlyList<UsageByDay> Days { get; init; }

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
            FirstDay = allDays.Count > 0 ? allDays[0].Day : null,
            LastDay = allDays.Count > 0 ? allDays[^1].Day : null,
        };
    }

    /// <summary>The UTC day a record belongs to.</summary>
    public static string DayOf(UsageRecord record) =>
        record.Ts.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
