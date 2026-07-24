using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Architecture;
using RepoContext.Core.Outline;

namespace RepoContext.Cli.Output;

/// <summary>
/// Renders the cache-stable repository primer (ADR 0012). Every format keeps
/// the engine's stability guarantees: no volatile fields, path/name ordering,
/// quantized aggregates — same index content and token calibration imply
/// byte-identical output.
/// </summary>
public static class PrimeOutput
{
    public static string Render(PrimeResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Text => RenderText(result),
        _ => RenderMarkdown(result),
    };

    private static string RenderMarkdown(PrimeResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Repository primer\n\n");
        sb.Append("_Cache-stable for unchanged indexed content and token calibration — ")
          .Append("safe to place behind a prompt-cache breakpoint._\n\n");
        sb.Append($"~{r.ApproxLoc} LOC in ~{r.ApproxFiles} files. Languages: ")
          .Append(string.Join(" · ", r.Languages.Select(l => $"{l.Language} ~{l.ApproxLoc}")))
          .Append(".\n");
        if (r.TokenProfile is { } profile)
        {
            sb.Append("Token counts: ").Append(profile).Append("-calibrated.\n");
        }

        if (r.Dirs.Count > 0)
        {
            sb.Append("\n## Layout\n\n");
            foreach (PrimeDir dir in r.Dirs)
            {
                sb.Append($"- `{dir.Path}/` — ~{dir.ApproxLoc} LOC, {dir.Files} file(s)\n");
            }
        }

        if (r.Entrypoints.Count > 0)
        {
            sb.Append("\n## Entrypoints\n\n");
            foreach (string entry in r.Entrypoints)
            {
                sb.Append("- `").Append(entry).Append("`\n");
            }
        }

        if (r.Files.Count > 0)
        {
            sb.Append("\n## Key files\n");
            foreach (PrimeFile file in r.Files)
            {
                sb.Append($"\n### `{file.Path}` — hash `{file.Hash}`, full read ~{file.Tokens} tokens\n\n");
                foreach (OutlineSymbol s in file.Symbols)
                {
                    sb.Append($"- L{s.StartLine}-{s.EndLine} `{s.Signature}`");
                    if (s.Doc is { Length: > 0 })
                    {
                        sb.Append(" — ").Append(s.Doc);
                    }

                    sb.Append('\n');
                }

                if (file.SymbolsOmitted > 0)
                {
                    sb.Append($"- _+{file.SymbolsOmitted} more symbol(s)_\n");
                }
            }
        }

        return sb.ToString();
    }

    private static string RenderText(PrimeResult r)
    {
        var sb = new StringBuilder();
        sb.Append("Repository primer (cache-stable for unchanged content and token calibration):\n");
        sb.Append($"  ~{r.ApproxLoc} LOC in ~{r.ApproxFiles} files\n");
        sb.Append("  languages: ")
          .Append(string.Join(", ", r.Languages.Select(l => $"{l.Language} ~{l.ApproxLoc}")))
          .Append('\n');
        if (r.TokenProfile is { } profile)
        {
            sb.Append("  token profile: ").Append(profile).Append('\n');
        }

        foreach (PrimeDir dir in r.Dirs)
        {
            sb.Append($"  {dir.Path + "/",-28} ~{dir.ApproxLoc} LOC  {dir.Files} file(s)\n");
        }

        if (r.Entrypoints.Count > 0)
        {
            sb.Append("  entrypoints: ").Append(string.Join(", ", r.Entrypoints)).Append('\n');
        }

        foreach (PrimeFile file in r.Files)
        {
            sb.Append($"\n  {file.Path}  {file.Hash}  ~{file.Tokens} tokens\n");
            foreach (OutlineSymbol s in file.Symbols)
            {
                sb.Append($"    L{s.StartLine}-{s.EndLine}  {s.Kind,-9}  {s.Signature}\n");
            }

            if (file.SymbolsOmitted > 0)
            {
                sb.Append($"    (+{file.SymbolsOmitted} more symbols)\n");
            }
        }

        return sb.ToString();
    }

    private static string RenderJson(PrimeResult r)
    {
        var doc = new PrimeDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "prime",
            TokenProfile = r.TokenProfile,
            ApproxFiles = r.ApproxFiles,
            ApproxLoc = r.ApproxLoc,
            Languages = r.Languages
                .Select(l => new PrimeLanguageDto { Language = l.Language, ApproxLoc = l.ApproxLoc })
                .ToList(),
            Dirs = r.Dirs
                .Select(d => new PrimeDirDto { Path = d.Path, ApproxLoc = d.ApproxLoc, Files = d.Files })
                .ToList(),
            Entrypoints = r.Entrypoints,
            Results = r.Files.Select(f => new PrimeFileDto
            {
                Path = f.Path,
                Hash = f.Hash,
                EstimatedTokens = f.Tokens,
                Symbols = f.Symbols.Select(s => new PrimeSymbolDto
                {
                    Name = s.Name,
                    Kind = s.Kind,
                    StartLine = s.StartLine,
                    EndLine = s.EndLine,
                    Signature = s.Signature,
                    Doc = s.Doc,
                }).ToList(),
                SymbolsOmitted = f.SymbolsOmitted > 0 ? f.SymbolsOmitted : null,
            }).ToList(),
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private sealed record PrimeDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public string? TokenProfile { get; init; }

        public int ApproxFiles { get; init; }

        public int ApproxLoc { get; init; }

        public required IReadOnlyList<PrimeLanguageDto> Languages { get; init; }

        public required IReadOnlyList<PrimeDirDto> Dirs { get; init; }

        public required IReadOnlyList<string> Entrypoints { get; init; }

        public required IReadOnlyList<PrimeFileDto> Results { get; init; }
    }

    private sealed record PrimeLanguageDto
    {
        public required string Language { get; init; }

        public int ApproxLoc { get; init; }
    }

    private sealed record PrimeDirDto
    {
        public required string Path { get; init; }

        public int ApproxLoc { get; init; }

        public int Files { get; init; }
    }

    private sealed record PrimeFileDto
    {
        public required string Path { get; init; }

        public required string Hash { get; init; }

        public int EstimatedTokens { get; init; }

        public required IReadOnlyList<PrimeSymbolDto> Symbols { get; init; }

        public int? SymbolsOmitted { get; init; }
    }

    private sealed record PrimeSymbolDto
    {
        public required string Name { get; init; }

        public required string Kind { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string Signature { get; init; }

        public string? Doc { get; init; }
    }
}
