using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Graph;

namespace RepoContext.Cli.Output;

/// <summary>Renders <c>trace</c> results as text, JSON or Markdown.</summary>
public static class TraceOutput
{
    public static string Render(TraceResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderText(TraceResult r)
    {
        var sb = new StringBuilder();
        sb.Append("Trace \"").Append(r.Query).Append("\": ")
            .Append(r.Definitions.Count).Append(" definition(s), ")
            .Append(r.TotalMentions).Append(" file(s) mentioning it\n");

        if (r.Definitions.Count > 0)
        {
            sb.Append("  defined in:\n");
            foreach (TraceDefinition d in r.Definitions)
            {
                sb.Append("    - ").Append(d.Path).Append("  L").Append(d.StartLine)
                    .Append('-').Append(d.EndLine).Append("  ").Append(d.SymbolKind)
                    .Append("  ").Append(d.Signature).Append('\n');
            }
        }

        if (r.Mentions.Count > 0)
        {
            sb.Append("  mentioned in:\n");
            foreach (TraceMention m in r.Mentions)
            {
                sb.Append("    - ").Append(m.Path).Append(" (").Append(m.FileKind).Append(')');
                if (m.Lines.Count > 0)
                {
                    sb.Append("  L").Append(string.Join(",L", m.Lines));
                }

                sb.Append("  ~").Append(m.FileTokens).Append(" tokens if read")
                    .Append("  [").Append(string.Join(',', m.Reasons)).Append("]\n");
            }
        }

        if (r.OmittedMentions > 0)
        {
            sb.Append("  (+").Append(r.OmittedMentions).Append(" more file(s); raise --top)\n");
        }

        if (r.Definitions.Count == 0 && r.Mentions.Count == 0)
        {
            sb.Append("  (nothing references it)\n");
            if (r.Suggestions.Count > 0)
            {
                sb.Append("  known keys with the same prefix: ")
                    .Append(string.Join(", ", r.Suggestions)).Append('\n');
            }
        }
        else
        {
            sb.Append("Reading every file above would cost ~")
                .Append(r.ProjectedReadTokens).Append(" tokens.\n");
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(TraceResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Trace: `").Append(r.Query).Append("`\n\n");
        if (r.Definitions.Count == 0 && r.Mentions.Count == 0)
        {
            sb.Append("_Nothing in the index references it._\n");
            if (r.Suggestions.Count > 0)
            {
                sb.Append("\nKnown keys with the same prefix: ")
                    .Append(string.Join(", ", r.Suggestions.Select(s => "`" + s + "`")))
                    .Append(".\n");
            }

            return sb.ToString();
        }

        if (r.Definitions.Count > 0)
        {
            sb.Append("## Defined in\n\n");
            foreach (TraceDefinition d in r.Definitions)
            {
                sb.Append("- `").Append(d.Path).Append("` L").Append(d.StartLine).Append('-')
                    .Append(d.EndLine).Append(" **").Append(d.SymbolKind).Append("** `")
                    .Append(d.Signature).Append("`\n");
            }

            sb.Append('\n');
        }

        if (r.Mentions.Count > 0)
        {
            sb.Append("## Mentioned in\n\n");
            foreach (TraceMention m in r.Mentions)
            {
                sb.Append("- `").Append(m.Path).Append("` (").Append(m.FileKind).Append(')');
                if (m.Lines.Count > 0)
                {
                    sb.Append(" L").Append(string.Join(",L", m.Lines));
                }

                sb.Append(" · ~").Append(m.FileTokens).Append(" tokens if read\n");
            }

            if (r.OmittedMentions > 0)
            {
                sb.Append("- _+").Append(r.OmittedMentions).Append(" more file(s)_\n");
            }

            sb.Append('\n');
        }

        sb.Append("_Reading every file above would cost ~").Append(r.ProjectedReadTokens)
            .Append(" tokens._\n");
        return sb.ToString();
    }

    private static string RenderJson(TraceResult r)
    {
        var doc = new TraceDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "trace",
            Query = r.Query,
            Resolved = r.Resolved,
            DefinitionCount = r.Definitions.Count,
            MentionCount = r.TotalMentions,
            OmittedMentions = r.OmittedMentions,
            ProjectedReadTokens = r.ProjectedReadTokens,
            Definitions = r.Definitions.Select(d => new TraceDefinitionDto
            {
                Path = d.Path,
                FileKind = d.FileKind,
                Name = d.Name,
                Kind = d.SymbolKind,
                StartLine = d.StartLine,
                EndLine = d.EndLine,
                Signature = d.Signature,
            }).ToList(),
            Mentions = r.Mentions.Select(m => new TraceMentionDto
            {
                Path = m.Path,
                FileKind = m.FileKind,
                Lines = m.Lines,
                FileTokens = m.FileTokens,
                Reasons = m.Reasons,
            }).ToList(),
            Suggestions = r.Suggestions.Count == 0 ? null : r.Suggestions,
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private sealed record TraceDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Query { get; init; }

        /// <summary>The normalized values that actually matched the reference index.</summary>
        public required IReadOnlyList<string> Resolved { get; init; }

        public int DefinitionCount { get; init; }

        public int MentionCount { get; init; }

        public int OmittedMentions { get; init; }

        /// <summary>What reading every returned file in full would cost.</summary>
        public int ProjectedReadTokens { get; init; }

        public required IReadOnlyList<TraceDefinitionDto> Definitions { get; init; }

        public required IReadOnlyList<TraceMentionDto> Mentions { get; init; }

        public IReadOnlyList<string>? Suggestions { get; init; }
    }

    private sealed record TraceDefinitionDto
    {
        public required string Path { get; init; }

        public required string FileKind { get; init; }

        public required string Name { get; init; }

        public required string Kind { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string Signature { get; init; }
    }

    private sealed record TraceMentionDto
    {
        public required string Path { get; init; }

        public required string FileKind { get; init; }

        public required IReadOnlyList<int> Lines { get; init; }

        public int FileTokens { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }
    }
}
