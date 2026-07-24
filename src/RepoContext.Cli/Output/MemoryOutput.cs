using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Memory;

namespace RepoContext.Cli.Output;

/// <summary>
/// Renders <c>repoctx memory</c> documents (ADR 0013). JSON uses the current
/// repository-wide output schema; its item shape is shared with the
/// <c>context</c> bundle's memory section.
/// </summary>
public static class MemoryOutput
{
    /// <summary>Renders the result of <c>memory add</c>.</summary>
    public static string RenderAdd(MemoryEntry entry, bool updated, int totalEntries, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                return JsonSerializer.Serialize(new AddDocument
                {
                    SchemaVersion = RepoContextInfo.SchemaVersion,
                    Command = "memory",
                    Action = "add",
                    Entry = MemoryDto.From(entry),
                    Updated = updated ? true : null,
                    TotalEntries = totalEntries,
                }, OutputJson.Options);

            case OutputFormat.Md:
                var md = new StringBuilder();
                md.Append(updated ? "Updated" : "Stored").Append(" memory `").Append(entry.Id)
                  .Append("` (").Append(entry.Kind).Append(')');
                if (entry.Files.Count > 0)
                {
                    md.Append(" — files: ").Append(string.Join(", ",
                        entry.Files.Keys.Select(p => $"`{p}`")));
                }

                md.Append(". ").Append(totalEntries).Append('/')
                  .Append(MemoryStore.MaxEntries).Append(" entries.\n");
                return md.ToString();

            default:
                var sb = new StringBuilder();
                sb.Append(updated ? "Updated" : "Stored").Append(" memory ").Append(entry.Id)
                  .Append(" (").Append(entry.Kind).Append(')');
                if (entry.Files.Count > 0)
                {
                    sb.Append(" · files: ").Append(string.Join(", ", entry.Files.Keys));
                }

                if (entry.Session is { } session)
                {
                    sb.Append(" · session: ").Append(session);
                }

                sb.Append(" · ").Append(totalEntries).Append('/')
                  .Append(MemoryStore.MaxEntries).Append(" entries\n");
                return sb.ToString();
        }
    }

    /// <summary>Renders the result of <c>memory search</c> (or the list mode).</summary>
    public static string RenderSearch(MemoryQueryResult result, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                return JsonSerializer.Serialize(new SearchDocument
                {
                    SchemaVersion = RepoContextInfo.SchemaVersion,
                    Command = "memory",
                    Action = "search",
                    Query = result.Query,
                    Terms = result.Terms.Count > 0 ? result.Terms : null,
                    Count = result.Hits.Count,
                    TotalEntries = result.TotalEntries,
                    EstimatedTokens = result.EstimatedTokens,
                    Results = result.Hits.Select(MemoryDto.From).ToList(),
                }, OutputJson.Options);

            case OutputFormat.Md:
                return RenderSearchMd(result);

            default:
                return RenderSearchText(result);
        }
    }

    /// <summary>Renders the result of <c>memory rm</c>.</summary>
    public static string RenderRemove(string id, OutputFormat format) => format switch
    {
        OutputFormat.Json => JsonSerializer.Serialize(new RemoveDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "memory",
            Action = "rm",
            Id = id,
            Removed = true,
        }, OutputJson.Options),
        OutputFormat.Md => $"Removed memory `{id}`.\n",
        _ => $"Removed memory {id}.\n",
    };

    private static string RenderSearchText(MemoryQueryResult result)
    {
        var sb = new StringBuilder();
        if (result.Query is { Length: > 0 } query)
        {
            sb.Append("Memory matches for \"").Append(query).Append("\" (")
              .Append(result.Terms.Count).Append(" term(s), ")
              .Append(result.TotalEntries).Append(" entries):\n");
        }
        else
        {
            sb.Append("Memory (").Append(result.Hits.Count).Append(" of ")
              .Append(result.TotalEntries).Append(" entries):\n");
        }

        int i = 1;
        foreach (MemoryHit hit in result.Hits)
        {
            sb.Append($"{i,3}. {hit.Entry.Id}  {hit.Entry.Kind,-10}");
            if (result.Query is { Length: > 0 })
            {
                sb.Append($"  {hit.Score,6:F4}");
            }

            sb.Append($"  ~{hit.EstimatedTokens} tokens  {hit.Entry.Created}");
            if (hit.Stale)
            {
                sb.Append("  stale");
            }

            sb.Append('\n');
            foreach (string line in hit.Entry.Text.Split('\n'))
            {
                sb.Append("      ").Append(line).Append('\n');
            }

            AppendDetailsText(sb, hit);
            i++;
        }

        sb.Append('\n').Append(result.Hits.Count).Append(" hit(s) · ~")
          .Append(result.EstimatedTokens).Append(" estimated tokens\n");
        return sb.ToString();
    }

    private static void AppendDetailsText(StringBuilder sb, MemoryHit hit)
    {
        var details = new List<string>();
        if (hit.Entry.Files.Count > 0)
        {
            details.Add("files: " + string.Join(", ", hit.Entry.Files.Keys));
        }

        if (hit.Entry.Tags.Count > 0)
        {
            details.Add("tags: " + string.Join(", ", hit.Entry.Tags));
        }

        if (hit.Entry.Session is { } session)
        {
            details.Add("session: " + session);
        }

        if (hit.Reasons.Count > 0)
        {
            details.Add("reasons: " + string.Join(", ", hit.Reasons));
        }

        if (hit.StaleFiles.Count > 0)
        {
            details.Add("stale: " + string.Join(", ", hit.StaleFiles));
        }

        if (details.Count > 0)
        {
            sb.Append("      ").Append(string.Join(" · ", details)).Append('\n');
        }
    }

    private static string RenderSearchMd(MemoryQueryResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Memory");
        if (result.Query is { Length: > 0 } query)
        {
            sb.Append(": ").Append(query);
        }

        sb.Append("\n\n");
        foreach (MemoryHit hit in result.Hits)
        {
            sb.Append("- `").Append(hit.Entry.Id).Append("` **").Append(hit.Entry.Kind).Append("**");
            if (result.Query is { Length: > 0 })
            {
                sb.Append($" {hit.Score:F4}");
            }

            sb.Append(" · ~").Append(hit.EstimatedTokens).Append(" tokens · ").Append(hit.Entry.Created);
            if (hit.Stale)
            {
                sb.Append(" · **stale**");
            }

            sb.Append("  \n  ").Append(hit.Entry.Text.Replace("\n", "\n  ")).Append('\n');

            var details = new List<string>();
            if (hit.Entry.Files.Count > 0)
            {
                details.Add("files: " + string.Join(", ", hit.Entry.Files.Keys.Select(p => $"`{p}`")));
            }

            if (hit.Entry.Tags.Count > 0)
            {
                details.Add("tags: " + string.Join(", ", hit.Entry.Tags));
            }

            if (hit.Reasons.Count > 0)
            {
                details.Add("reasons: " + string.Join(", ", hit.Reasons));
            }

            if (details.Count > 0)
            {
                sb.Append("  ").Append(string.Join(" · ", details)).Append('\n');
            }
        }

        sb.Append('\n').Append(result.Hits.Count).Append(" hit(s) of ")
          .Append(result.TotalEntries).Append(" entries · ~")
          .Append(result.EstimatedTokens).Append(" estimated tokens\n");
        return sb.ToString();
    }

    private sealed record AddDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Action { get; init; }

        public required MemoryDto Entry { get; init; }

        /// <summary>Present (true) when an existing entry with the same id was refreshed.</summary>
        public bool? Updated { get; init; }

        public int TotalEntries { get; init; }
    }

    private sealed record SearchDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Action { get; init; }

        public string? Query { get; init; }

        public IReadOnlyList<string>? Terms { get; init; }

        public int Count { get; init; }

        public int TotalEntries { get; init; }

        public int EstimatedTokens { get; init; }

        public required IReadOnlyList<MemoryDto> Results { get; init; }
    }

    private sealed record RemoveDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Action { get; init; }

        public required string Id { get; init; }

        public bool Removed { get; init; }
    }
}

/// <summary>
/// The JSON shape of one memory entry, shared between the <c>memory</c>
/// documents and the <c>context</c> bundle's memory section so consumers
/// parse a single contract.
/// </summary>
public sealed record MemoryDto
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Text { get; init; }

    /// <summary>Relevance (absent in list mode and in <c>add</c> documents).</summary>
    public double? Score { get; init; }

    /// <summary>Linked paths → short content hash recorded at write time.</summary>
    public IReadOnlyDictionary<string, string>? Files { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string? Session { get; init; }

    public string? Created { get; init; }

    /// <summary>Present (true) when a linked file drifted since the memory was written.</summary>
    public bool? Stale { get; init; }

    /// <summary>The drifted paths (present only when stale).</summary>
    public IReadOnlyList<string>? StaleFiles { get; init; }

    public int? EstimatedTokens { get; init; }

    public IReadOnlyList<string>? Reasons { get; init; }

    /// <summary>Maps a bare entry (no scoring context, e.g. <c>add</c>).</summary>
    public static MemoryDto From(MemoryEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Text = entry.Text,
        Files = entry.Files.Count > 0 ? entry.Files : null,
        Tags = entry.Tags.Count > 0 ? entry.Tags : null,
        Session = entry.Session,
        Created = entry.Created is { Length: > 0 } ? entry.Created : null,
    };

    /// <summary>Maps a recalled hit with score, reasons, staleness and cost.</summary>
    public static MemoryDto From(MemoryHit hit) => From(hit.Entry) with
    {
        Score = hit.Score > 0 ? hit.Score : null,
        Stale = hit.Stale ? true : null,
        StaleFiles = hit.StaleFiles.Count > 0 ? hit.StaleFiles : null,
        EstimatedTokens = hit.EstimatedTokens,
        Reasons = hit.Reasons.Count > 0 ? hit.Reasons : null,
    };
}
