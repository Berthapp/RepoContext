using System.Globalization;
using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Stats;

namespace RepoContext.Cli.Output;

/// <summary>
/// Renders the token-savings dashboard (<c>repoctx stats</c>, ADR 0011) as
/// text, JSON or Markdown. Purely a projection of the usage log: identical
/// log ⇒ byte-identical output.
/// </summary>
public static class StatsOutput
{
    public static string Render(UsageReport report, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(report),
        OutputFormat.Md => RenderMarkdown(report),
        _ => RenderText(report),
    };

    private static string RenderText(UsageReport report)
    {
        if (report.Totals.Calls == 0)
        {
            return "Token savings: no usage recorded yet.\n"
                 + "Run queries (context, outline, search, ...) and check back; "
                 + "recording is local-only (.repoctx/stats.jsonl).\n";
        }

        var sb = new StringBuilder();
        sb.Append("Token savings (o200k counts, ")
          .Append(report.FirstDay).Append(" to ").Append(report.LastDay).Append("):\n\n");
        sb.Append($"  calls            {N(report.Totals.Calls),12}\n");
        sb.Append($"  response tokens  {N(report.Totals.ServedTokens),12}\n");
        sb.Append($"  reads replaced   {N(report.Totals.ReplacedTokens),12}\n");
        sb.Append($"  net saved        {N(report.Totals.SavedTokens),12}{PercentSuffix(report.Totals)}\n");

        sb.Append("\n  By command:\n");
        sb.Append($"    {"command",-12} {"calls",6} {"served",12} {"replaced",12} {"saved",12}\n");
        foreach (UsageByCommand command in report.Commands)
        {
            AppendRow(sb, command.Command, command.Bucket);
        }

        sb.Append($"\n  Recent days (up to {UsageReport.RecentDayCount}):\n");
        sb.Append($"    {"day",-12} {"calls",6} {"served",12} {"replaced",12} {"saved",12}\n");
        foreach (UsageByDay day in report.Days)
        {
            AppendRow(sb, day.Day, day.Bucket);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, UsageBucket bucket) =>
        sb.Append($"    {label,-12} {N(bucket.Calls),6} {N(bucket.ServedTokens),12} " +
                  $"{N(bucket.ReplacedTokens),12} {N(bucket.SavedTokens),12}\n");

    private static string RenderMarkdown(UsageReport report)
    {
        if (report.Totals.Calls == 0)
        {
            return "# Token savings\n\n_No usage recorded yet. Run queries (context, outline, "
                 + "search, ...) and check back; recording is local-only "
                 + "(`.repoctx/stats.jsonl`)._\n";
        }

        var sb = new StringBuilder();
        sb.Append("# Token savings\n\n");
        sb.Append("_o200k counts, ").Append(report.FirstDay).Append(" to ")
          .Append(report.LastDay).Append("._\n\n");
        sb.Append("- calls: **").Append(N(report.Totals.Calls)).Append("**\n");
        sb.Append("- response tokens: **").Append(N(report.Totals.ServedTokens)).Append("**\n");
        sb.Append("- reads replaced: **").Append(N(report.Totals.ReplacedTokens)).Append("**\n");
        sb.Append("- net saved: **").Append(N(report.Totals.SavedTokens)).Append("**")
          .Append(PercentSuffix(report.Totals)).Append('\n');

        sb.Append("\n## By command\n\n");
        AppendMdTable(sb, "command", report.Commands.Select(c => (c.Command, c.Bucket)));

        sb.Append($"\n## Recent days (up to {UsageReport.RecentDayCount})\n\n");
        AppendMdTable(sb, "day", report.Days.Select(d => (d.Day, d.Bucket)));

        return sb.ToString();
    }

    private static void AppendMdTable(
        StringBuilder sb, string labelHeader, IEnumerable<(string Label, UsageBucket Bucket)> rows)
    {
        sb.Append("| ").Append(labelHeader).Append(" | calls | served | replaced | saved |\n");
        sb.Append("| --- | ---: | ---: | ---: | ---: |\n");
        foreach ((string label, UsageBucket bucket) in rows)
        {
            sb.Append("| ").Append(label)
              .Append(" | ").Append(N(bucket.Calls))
              .Append(" | ").Append(N(bucket.ServedTokens))
              .Append(" | ").Append(N(bucket.ReplacedTokens))
              .Append(" | ").Append(N(bucket.SavedTokens))
              .Append(" |\n");
        }
    }

    private static string RenderJson(UsageReport report)
    {
        var doc = new StatsDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "stats",
            Calls = report.Totals.Calls,
            ServedTokens = report.Totals.ServedTokens,
            ReplacedTokens = report.Totals.ReplacedTokens,
            SavedTokens = report.Totals.SavedTokens,
            FirstDay = report.FirstDay,
            LastDay = report.LastDay,
            Commands = report.Commands.Select(c => new StatsRow
            {
                Command = c.Command,
                Calls = c.Bucket.Calls,
                ServedTokens = c.Bucket.ServedTokens,
                ReplacedTokens = c.Bucket.ReplacedTokens,
                SavedTokens = c.Bucket.SavedTokens,
            }).ToList(),
            Days = report.Days.Select(d => new StatsRow
            {
                Day = d.Day,
                Calls = d.Bucket.Calls,
                ServedTokens = d.Bucket.ServedTokens,
                ReplacedTokens = d.Bucket.ReplacedTokens,
                SavedTokens = d.Bucket.SavedTokens,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    /// <summary>Culture-invariant thousands formatting (determinism).</summary>
    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Net savings as a share of the replaced reads, when measurable.</summary>
    private static string PercentSuffix(UsageBucket totals)
    {
        if (totals.ReplacedTokens <= 0)
        {
            return string.Empty;
        }

        long percent = (long)Math.Round(
            100.0 * totals.SavedTokens / totals.ReplacedTokens, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"  ({percent} % of replaced reads)");
    }

    private sealed record StatsDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public int Calls { get; init; }

        public long ServedTokens { get; init; }

        public long ReplacedTokens { get; init; }

        /// <summary>Replaced reads minus response cost; negative means net overhead so far.</summary>
        public long SavedTokens { get; init; }

        public string? FirstDay { get; init; }

        public string? LastDay { get; init; }

        public required IReadOnlyList<StatsRow> Commands { get; init; }

        public required IReadOnlyList<StatsRow> Days { get; init; }
    }

    private sealed record StatsRow
    {
        /// <summary>Set in the per-command breakdown.</summary>
        public string? Command { get; init; }

        /// <summary>Set in the per-day breakdown (UTC, yyyy-MM-dd).</summary>
        public string? Day { get; init; }

        public int Calls { get; init; }

        public long ServedTokens { get; init; }

        public long ReplacedTokens { get; init; }

        public long SavedTokens { get; init; }
    }
}
