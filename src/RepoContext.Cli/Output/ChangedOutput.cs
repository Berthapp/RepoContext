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
            sb.Append(
                $"Index is current (content {r.ContentState}, worktree {r.WorktreeState}). No changes.\n");
            return sb.ToString();
        }

        sb.Append(
            $"Index is stale (content {r.ContentState}, worktree {r.WorktreeState}). "
            + "Run 'repoctx index'.\n");
        sb.Append("Changed:\n");
        foreach (ChangedFile file in r.Changed)
        {
            sb.Append($"  {file.Status,-8}  {file.Path}");
            if (file.Hunks is not null && file.PatchTokens is { } patchTokens && file.FileTokens is { } fileTokens)
            {
                sb.Append($"  (~{patchTokens} patch tokens vs {fileTokens} full read)");
            }

            sb.Append('\n');
            foreach (PatchHunk hunk in file.Hunks ?? [])
            {
                sb.Append($"      @@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@\n");
                foreach (string line in hunk.Text.Split('\n'))
                {
                    sb.Append("      ").Append(line).Append('\n');
                }
            }
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
            ? $"_Index **stale** (content `{r.ContentState}` · worktree `{r.WorktreeState}`) — "
                + "run `repoctx index`._\n\n"
            : $"_Index current (content `{r.ContentState}` · worktree `{r.WorktreeState}`). "
                + "No changes._\n");
        foreach (ChangedFile file in r.Changed)
        {
            sb.Append($"- **{file.Status}** `{file.Path}`");
            if (file.Hunks is not null && file.PatchTokens is { } patchTokens && file.FileTokens is { } fileTokens)
            {
                sb.Append($" — ~{patchTokens} patch tokens vs {fileTokens} full read");
            }

            sb.Append('\n');
            if (file.Hunks is { Count: > 0 } hunks)
            {
                sb.Append("\n```diff\n");
                foreach (PatchHunk hunk in hunks)
                {
                    sb.Append($"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@\n");
                    sb.Append(hunk.Text).Append('\n');
                }

                sb.Append("```\n\n");
            }
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
            ContentState = r.ContentState,
            WorktreeState = r.WorktreeState,
            Stale = r.Stale,
            Count = r.Changed.Count,
            Changed = r.Changed.Select(c => new ChangedFileDto
            {
                Path = c.Path,
                Status = c.Status,
                PatchTokens = c.PatchTokens,
                FileTokens = c.FileTokens,
                Hunks = c.Hunks?.Select(h => new HunkDto
                {
                    OldStart = h.OldStart,
                    OldLines = h.OldLines,
                    NewStart = h.NewStart,
                    NewLines = h.NewLines,
                    Text = h.Text,
                }).ToList(),
            }).ToList(),
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

        /// <summary>Short fingerprint of the indexed base contents.</summary>
        public required string ContentState { get; init; }

        /// <summary>Short fingerprint of the indexed base plus the local delta.</summary>
        public required string WorktreeState { get; init; }

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

        /// <summary>Cost of receiving the hunks (patch mode, modified files).</summary>
        public int? PatchTokens { get; init; }

        /// <summary>The full re-read the patch replaces (patch mode).</summary>
        public int? FileTokens { get; init; }

        /// <summary>Delta hunks vs the indexed content (patch mode).</summary>
        public IReadOnlyList<HunkDto>? Hunks { get; init; }
    }

    private sealed record HunkDto
    {
        public int OldStart { get; init; }

        public int OldLines { get; init; }

        public int NewStart { get; init; }

        public int NewLines { get; init; }

        /// <summary>Body lines prefixed ' ' (context), '-' (removed), '+' (added).</summary>
        public required string Text { get; init; }
    }

    private sealed record ImpactedFileDto
    {
        public required string Path { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }
    }
}
