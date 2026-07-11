using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Outline;

namespace RepoContext.Cli.Output;

/// <summary>Renders <c>outline</c> results as text, JSON or Markdown.</summary>
public static class OutlineOutput
{
    public static string Render(OutlineResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderText(OutlineResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"Outline: {r.Path} ({r.Kind}, {r.Language}, {r.Lines} lines, ~{r.TokenCount} tokens, hash {r.Hash})\n");
        if (r.Symbols.Count == 0)
        {
            sb.Append("  (no symbols)\n");
            return sb.ToString();
        }

        foreach (OutlineSymbol s in r.Symbols)
        {
            sb.Append($"  L{s.StartLine}-{s.EndLine}  {s.Kind,-9}  {s.Signature}\n");
            if (s.Doc is { Length: > 0 } doc)
            {
                sb.Append("             ").Append(doc).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(OutlineResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Outline: `").Append(r.Path).Append("`\n\n");
        sb.Append($"_{r.Kind} · {r.Language} · {r.Lines} lines · ~{r.TokenCount} tokens · hash `{r.Hash}`_\n\n");
        foreach (OutlineSymbol s in r.Symbols)
        {
            sb.Append($"- L{s.StartLine}-{s.EndLine} **{s.Kind}** `{s.Signature}`");
            if (s.Doc is { Length: > 0 } doc)
            {
                sb.Append(" — ").Append(doc);
            }

            sb.Append('\n');
        }

        if (r.Symbols.Count == 0)
        {
            sb.Append("_No symbols._\n");
        }

        return sb.ToString();
    }

    private static string RenderJson(OutlineResult r)
    {
        var doc = new OutlineDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "outline",
            Path = r.Path,
            Kind = r.Kind,
            Language = r.Language,
            Lines = r.Lines,
            EstimatedTokens = r.TokenCount,
            Hash = r.Hash,
            Symbols = r.Symbols.Select(s => new OutlineSymbolDto
            {
                Name = s.Name,
                Kind = s.Kind,
                StartLine = s.StartLine,
                EndLine = s.EndLine,
                Signature = s.Signature,
                Doc = s.Doc,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private sealed record OutlineDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Path { get; init; }

        public required string Kind { get; init; }

        public required string Language { get; init; }

        public int Lines { get; init; }

        /// <summary>Real token cost of reading the whole file.</summary>
        public int EstimatedTokens { get; init; }

        public required string Hash { get; init; }

        public required IReadOnlyList<OutlineSymbolDto> Symbols { get; init; }
    }

    private sealed record OutlineSymbolDto
    {
        public required string Name { get; init; }

        public required string Kind { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string Signature { get; init; }

        public string? Doc { get; init; }
    }
}
