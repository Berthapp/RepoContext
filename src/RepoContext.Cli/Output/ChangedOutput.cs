using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Indexing;

namespace RepoContext.Cli.Output;

/// <summary>Renders <c>changed</c> results as text, JSON or Markdown.</summary>
public static class ChangedOutput
{
    public static string Render(ChangedResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderText(ChangedResult r)
    {
        var sb = new StringBuilder();
        if (!r.Stale)
        {
            sb.Append($"Index is current (state {r.State}). No changes.\n");
            return sb.ToString();
        }

        sb.Append($"Index is stale (state {r.State}). Run 'repoctx index'.\n");
        sb.Append("Changed:\n");
        foreach (ChangedFile file in r.Changed)
        {
            sb.Append($"  {file.Status,-8}  {file.Path}\n");
        }

        if (r.Impacted.Count > 0)
        {
            sb.Append("Impacted (link to a change):\n");
            foreach (ImpactedFile file in r.Impacted)
            {
                sb.Append($"  {file.Path}  ({string.Join(", ", file.Reasons)})\n");
            }
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(ChangedResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Changed\n\n");
        sb.Append(r.Stale
            ? $"_Index **stale** (state `{r.State}`) — run `repoctx index`._\n\n"
            : $"_Index current (state `{r.State}`). No changes._\n");
        foreach (ChangedFile file in r.Changed)
        {
            sb.Append($"- **{file.Status}** `{file.Path}`\n");
        }

        if (r.Impacted.Count > 0)
        {
            sb.Append("\n## Impacted\n\n");
            foreach (ImpactedFile file in r.Impacted)
            {
                sb.Append($"- `{file.Path}` — {string.Join(", ", file.Reasons)}\n");
            }
        }

        return sb.ToString();
    }

    private static string RenderJson(ChangedResult r)
    {
        var doc = new ChangedDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "changed",
            State = r.State,
            Stale = r.Stale,
            Count = r.Changed.Count,
            Changed = r.Changed.Select(c => new ChangedFileDto { Path = c.Path, Status = c.Status }).ToList(),
            Impacted = r.Impacted.Select(i => new ImpactedFileDto { Path = i.Path, Reasons = i.Reasons }).ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private sealed record ChangedDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        /// <summary>Short state hash of the index the tree was compared against.</summary>
        public required string State { get; init; }

        /// <summary>True when the working tree differs from the index.</summary>
        public bool Stale { get; init; }

        public int Count { get; init; }

        public required IReadOnlyList<ChangedFileDto> Changed { get; init; }

        public required IReadOnlyList<ImpactedFileDto> Impacted { get; init; }
    }

    private sealed record ChangedFileDto
    {
        public required string Path { get; init; }

        public required string Status { get; init; }
    }

    private sealed record ImpactedFileDto
    {
        public required string Path { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }
    }
}
