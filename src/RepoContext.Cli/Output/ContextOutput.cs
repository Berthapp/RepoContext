using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Context;
using RepoContext.Core.Outline;

namespace RepoContext.Cli.Output;

/// <summary>Renders a context bundle as text or JSON. Reasons are always included.</summary>
public static class ContextOutput
{
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
        sb.Append("_Terms: ").Append(string.Join(", ", result.Terms))
          .Append(" · state `").Append(result.State).Append("`_\n\n");
        foreach (ContextItem item in result.Items)
        {
            sb.Append("### `").Append(item.Path).Append("`  (").Append(item.Kind).Append(")\n");
            sb.Append($"- score: {item.Score:F4} · lines L{item.StartLine}-{item.EndLine} · ~{item.EstimatedTokens} tokens");
            if (item.Unchanged)
            {
                sb.Append(" · **unchanged**");
            }

            if (item.DuplicateOf is { } dupMd)
            {
                sb.Append(" · **duplicate of** `").Append(dupMd).Append('`');
            }

            if (item.Stripped)
            {
                sb.Append(" · comments stripped");
            }

            sb.Append(" · hash `").Append(item.Hash).Append("`\n");
            sb.Append("- reasons: ").Append(string.Join(", ", item.Reasons)).Append('\n');
            AppendSymbolsMd(sb, item);
            if (item.Snippet is { Length: > 0 } snippet)
            {
                sb.Append("\n```\n").Append(snippet).Append("\n```\n");
            }

            sb.Append('\n');
        }

        sb.Append($"_Budget: {result.Items.Count} file(s) · ~{result.EstimatedTokens} estimated tokens");
        if (result.TokenProfile is { } mdProfile)
        {
            sb.Append(" (").Append(mdProfile).Append("-calibrated)");
        }

        if (result.Omitted > 0)
        {
            sb.Append($" · {result.Omitted} more candidate(s) omitted");
        }

        sb.Append("._\n");
        return sb.ToString();
    }

    private static void AppendSymbolsMd(StringBuilder sb, ContextItem item)
    {
        if (item.Symbols is not { Count: > 0 } symbols)
        {
            return;
        }

        foreach (OutlineSymbol s in symbols)
        {
            sb.Append($"- L{s.StartLine}-{s.EndLine} `{s.Signature}`");
            if (s.Doc is { Length: > 0 })
            {
                sb.Append(" — ").Append(s.Doc);
            }

            sb.Append('\n');
        }

        if (item.SymbolsOmitted is { } cut)
        {
            sb.Append($"- _+{cut} more symbol(s)_\n");
        }
    }

    private static string RenderText(ContextResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Context for \"").Append(result.Query).Append("\" (")
          .Append(result.Terms.Count).Append(" term(s), state ").Append(result.State).Append("):\n");

        int i = 1;
        foreach (ContextItem item in result.Items)
        {
            string marker = item.Unchanged ? "  unchanged"
                : item.DuplicateOf is { } dup ? "  duplicate-of:" + dup
                : item.Stripped ? "  stripped"
                : string.Empty;
            sb.Append($"{i,3}. {item.Path,-46} {item.Score,7:F4}  {item.Kind,-6}  " +
                      $"[L{item.StartLine}-{item.EndLine}]  ~{item.EstimatedTokens} tokens  {item.Hash}{marker}\n");
            sb.Append("      reasons: ").Append(string.Join(", ", item.Reasons)).Append('\n');
            if (item.Symbols is { Count: > 0 } symbols)
            {
                foreach (OutlineSymbol s in symbols)
                {
                    sb.Append($"      L{s.StartLine}-{s.EndLine}  {s.Kind,-9}  {s.Signature}\n");
                }

                if (item.SymbolsOmitted is { } cut)
                {
                    sb.Append($"      (+{cut} more symbols)\n");
                }
            }

            if (item.Snippet is { Length: > 0 } snippet)
            {
                foreach (string line in snippet.Split('\n'))
                {
                    sb.Append("      | ").Append(line).Append('\n');
                }
            }

            i++;
        }

        sb.Append($"\nBudget: {result.Items.Count} file(s) · ~{result.EstimatedTokens} estimated tokens");
        if (result.TokenProfile is { } profile)
        {
            sb.Append(" (").Append(profile).Append("-calibrated)");
        }

        if (result.Omitted > 0)
        {
            sb.Append($" · {result.Omitted} more candidate(s) omitted");
        }

        sb.Append('\n');
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
            State = result.State,
            Detail = result.Detail.ToString().ToLowerInvariant(),
            TokenProfile = result.TokenProfile,
            Count = result.Items.Count,
            Omitted = result.Omitted > 0 ? result.Omitted : null,
            EstimatedTokens = result.EstimatedTokens,
            Results = result.Items.Select(item => new ContextItemDto
            {
                Path = item.Path,
                Kind = item.Kind,
                Score = item.Score,
                StartLine = item.StartLine,
                EndLine = item.EndLine,
                EstimatedTokens = item.EstimatedTokens,
                FileTokens = item.FileTokens,
                Hash = item.Hash,
                Unchanged = item.Unchanged ? true : null,
                DuplicateOf = item.DuplicateOf,
                Stripped = item.Stripped ? true : null,
                Reasons = item.Reasons,
                Symbols = item.Symbols?.Select(s => new ContextSymbolDto
                {
                    Name = s.Name,
                    Kind = s.Kind,
                    StartLine = s.StartLine,
                    EndLine = s.EndLine,
                    Signature = s.Signature,
                    Doc = s.Doc,
                }).ToList(),
                SymbolsOmitted = item.SymbolsOmitted,
                Snippet = item.Snippet,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private sealed record ContextDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Query { get; init; }

        public required IReadOnlyList<string> Terms { get; init; }

        /// <summary>Short index state hash; changes whenever the index content changes.</summary>
        public required string State { get; init; }

        public required string Detail { get; init; }

        /// <summary>Active token-calibration label (absent for raw o200k counts).</summary>
        public string? TokenProfile { get; init; }

        public int Count { get; init; }

        /// <summary>Scored candidates beyond the top/budget limits (absent when zero).</summary>
        public int? Omitted { get; init; }

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

        /// <summary>Full-file read cost when the item carries content (absent otherwise).</summary>
        public int? FileTokens { get; init; }

        public required string Hash { get; init; }

        /// <summary>Present (true) only when the caller's known hash still matches.</summary>
        public bool? Unchanged { get; init; }

        /// <summary>Another bundle item with byte-identical content; read that one.</summary>
        public string? DuplicateOf { get; init; }

        /// <summary>Present (true) when the slice was comment-stripped (lines approximate).</summary>
        public bool? Stripped { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }

        public IReadOnlyList<ContextSymbolDto>? Symbols { get; init; }

        public int? SymbolsOmitted { get; init; }

        public string? Snippet { get; init; }
    }

    private sealed record ContextSymbolDto
    {
        public required string Name { get; init; }

        public required string Kind { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string Signature { get; init; }

        public string? Doc { get; init; }
    }
}
