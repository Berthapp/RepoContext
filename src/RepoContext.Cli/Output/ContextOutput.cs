using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoContext.Core;
using RepoContext.Core.Context;

namespace RepoContext.Cli.Output;

/// <summary>Renders a context bundle as text or JSON. Reasons are always included.</summary>
public static class ContextOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
    };

    public static string Render(ContextResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderMarkdown(ContextResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Context: ").Append(result.Query).Append("\n\n");
        sb.Append("_Terms: ").Append(string.Join(", ", result.Terms)).Append("_\n\n");
        foreach (ContextItem item in result.Items)
        {
            sb.Append("### `").Append(item.Path).Append("`  (").Append(item.Kind).Append(")\n");
            sb.Append($"- score: {item.Score:F4} · lines L{item.StartLine}-{item.EndLine} · ~{item.EstimatedTokens} tokens\n");
            sb.Append("- reasons: ").Append(string.Join(", ", item.Reasons)).Append('\n');
            if (item.Snippet is { Length: > 0 } snippet)
            {
                sb.Append("\n```\n").Append(snippet).Append("\n```\n");
            }

            sb.Append('\n');
        }

        sb.Append($"_Budget: {result.Items.Count} file(s) · ~{result.EstimatedTokens} estimated tokens._\n");
        return sb.ToString();
    }

    private static string RenderText(ContextResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Context for \"").Append(result.Query).Append("\" (")
          .Append(result.Terms.Count).Append(" term(s)):\n");

        int i = 1;
        foreach (ContextItem item in result.Items)
        {
            sb.Append($"{i,3}. {item.Path,-46} {item.Score,7:F4}  {item.Kind,-6}  " +
                      $"[L{item.StartLine}-{item.EndLine}]  ~{item.EstimatedTokens} tokens\n");
            sb.Append("      reasons: ").Append(string.Join(", ", item.Reasons)).Append('\n');
            if (item.Snippet is { Length: > 0 } snippet)
            {
                foreach (string line in snippet.Split('\n'))
                {
                    sb.Append("      | ").Append(line).Append('\n');
                }
            }

            i++;
        }

        sb.Append($"\nBudget: {result.Items.Count} file(s) · ~{result.EstimatedTokens} estimated tokens\n");
        return sb.ToString();
    }

    private static string RenderJson(ContextResult result)
    {
        var doc = new ContextDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "context",
            Query = result.Query,
            Terms = result.Terms,
            Count = result.Items.Count,
            EstimatedTokens = result.EstimatedTokens,
            Results = result.Items.Select(item => new ContextItemDto
            {
                Path = item.Path,
                Kind = item.Kind,
                Score = item.Score,
                StartLine = item.StartLine,
                EndLine = item.EndLine,
                EstimatedTokens = item.EstimatedTokens,
                Reasons = item.Reasons,
                Snippet = item.Snippet,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    private sealed record ContextDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Query { get; init; }

        public required IReadOnlyList<string> Terms { get; init; }

        public int Count { get; init; }

        public int EstimatedTokens { get; init; }

        public required IReadOnlyList<ContextItemDto> Results { get; init; }
    }

    private sealed record ContextItemDto
    {
        public required string Path { get; init; }

        public required string Kind { get; init; }

        public double Score { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public int EstimatedTokens { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }

        public string? Snippet { get; init; }
    }
}
