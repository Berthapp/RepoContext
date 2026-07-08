using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoContext.Core;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Output;

/// <summary>Renders search results as deterministic text or JSON.</summary>
public static class SearchOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        NewLine = "\n",
    };

    public static string Render(string query, IReadOnlyList<SearchHit> hits, OutputFormat format) =>
        format == OutputFormat.Json ? RenderJson(query, hits) : RenderText(query, hits);

    private static string RenderText(string query, IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();
        sb.Append("Found ").Append(hits.Count).Append(" result(s) for \"").Append(query).Append("\":\n");
        int i = 1;
        foreach (SearchHit hit in hits)
        {
            sb.Append($"{i,3}. {hit.Path,-48} {Round(hit.Score),7:F4}  {hit.Kind,-6}  " +
                      $"[L{hit.StartLine}-{hit.EndLine}]  {string.Join(",", hit.Reasons)}\n");
            i++;
        }

        return sb.ToString();
    }

    private static string RenderJson(string query, IReadOnlyList<SearchHit> hits)
    {
        var doc = new SearchDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "search",
            Query = query,
            Count = hits.Count,
            Results = hits.Select(h => new SearchResult
            {
                Path = h.Path,
                Kind = h.Kind,
                Score = Round(h.Score),
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                ChunkKind = h.ChunkKind,
                Heading = h.Heading,
                Reasons = h.Reasons,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private sealed record SearchDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Query { get; init; }

        public int Count { get; init; }

        public required IReadOnlyList<SearchResult> Results { get; init; }
    }

    private sealed record SearchResult
    {
        public required string Path { get; init; }

        public required string Kind { get; init; }

        public double Score { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string ChunkKind { get; init; }

        public string? Heading { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }
    }
}
