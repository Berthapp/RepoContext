using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Graph;

namespace RepoContext.Cli.Output;

/// <summary>Renders <c>related</c> results as text or JSON.</summary>
public static class RelatedOutput
{

    public static string Render(RelatedResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderMarkdown(RelatedResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Related: `").Append(result.Path).Append("` (").Append(result.Kind).Append(")\n\n");
        if (result.Entries.Count == 0)
        {
            sb.Append("_No related files._\n");
            return sb.ToString();
        }

        foreach (IGrouping<Relation, RelatedEntry> group in result.Entries
            .GroupBy(e => e.Relation)
            .OrderBy(g => g.Key))
        {
            sb.Append("## ").Append(Label(group.Key)).Append("\n\n");
            foreach (RelatedEntry entry in group.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                sb.Append("- `").Append(entry.Path).Append("`\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string RenderText(RelatedResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Related to ").Append(result.Path).Append(" (").Append(result.Kind).Append("):\n");
        if (result.Entries.Count == 0)
        {
            sb.Append("  (no related files)\n");
            return sb.ToString();
        }

        foreach (IGrouping<Relation, RelatedEntry> group in result.Entries
            .GroupBy(e => e.Relation)
            .OrderBy(g => g.Key))
        {
            sb.Append("  ").Append(Label(group.Key)).Append(":\n");
            foreach (RelatedEntry entry in group.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                sb.Append("    - ").Append(entry.Path).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string RenderJson(RelatedResult result)
    {
        var doc = new RelatedDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "related",
            Path = result.Path,
            Kind = result.Kind,
            Count = result.Entries.Count,
            Results = result.Entries
                .OrderBy(e => e.Relation)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .Select(e => new RelatedItem
                {
                    Path = e.Path,
                    Relation = Label(e.Relation),
                    Reasons = e.Reasons,
                })
                .ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private static string Label(Relation relation) => relation switch
    {
        Relation.Imports => "imports",
        Relation.ImportedBy => "imported_by",
        Relation.Tests => "tests",
        Relation.TestedBy => "tested_by",
        _ => relation.ToString().ToLowerInvariant(),
    };

    private sealed record RelatedDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Path { get; init; }

        public required string Kind { get; init; }

        public int Count { get; init; }

        public required IReadOnlyList<RelatedItem> Results { get; init; }
    }

    private sealed record RelatedItem
    {
        public required string Path { get; init; }

        public required string Relation { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }
    }
}
