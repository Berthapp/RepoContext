using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Architecture;

namespace RepoContext.Cli.Output;

/// <summary>Renders the architecture summary as text, JSON or Markdown.</summary>
public static class ArchitectureOutput
{

    public static string Render(ArchitectureResult result, OutputFormat format) => format switch
    {
        OutputFormat.Json => RenderJson(result),
        OutputFormat.Md => RenderMarkdown(result),
        _ => RenderText(result),
    };

    private static string RenderText(ArchitectureResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"Architecture: {r.TotalFiles} files, {r.TotalLoc} LOC\n\n");

        sb.Append("Structure (depth 3, LOC):\n");
        WriteTree(sb, r.Tree, 0, "  ");

        sb.Append("\nLanguages:\n");
        foreach (LanguageStat l in r.Languages)
        {
            sb.Append($"  {l.Language,-12} {l.Files,3} files  {l.Loc,6} LOC\n");
        }

        sb.Append("\nMost imported (centrality):\n");
        foreach ((string path, int dependents) in r.Central)
        {
            sb.Append($"  {dependents,3}  {path}\n");
        }

        sb.Append("\nEntrypoints:\n");
        foreach (string entry in r.Entrypoints)
        {
            sb.Append($"  - {entry}\n");
        }

        return sb.ToString();
    }

    private static void WriteTree(StringBuilder sb, TreeNode node, int depth, string indent)
    {
        if (depth > 0)
        {
            sb.Append(string.Concat(Enumerable.Repeat(indent, depth)))
              .Append(node.Name).Append('/').Append("  ")
              .Append($"{node.FileCount} files, {node.Loc} LOC\n");
        }

        foreach (TreeNode child in node.Children)
        {
            WriteTree(sb, child, depth + 1, indent);
        }
    }

    private static string RenderMarkdown(ArchitectureResult r)
    {
        var sb = new StringBuilder();
        sb.Append("# Architecture\n\n");
        sb.Append($"**{r.TotalFiles}** files · **{r.TotalLoc}** LOC\n\n");

        sb.Append("## Structure\n\n```\n");
        WriteTree(sb, r.Tree, 0, "  ");
        sb.Append("```\n\n");

        sb.Append("## Languages\n\n| Language | Files | LOC |\n| --- | ---: | ---: |\n");
        foreach (LanguageStat l in r.Languages)
        {
            sb.Append($"| {l.Language} | {l.Files} | {l.Loc} |\n");
        }

        sb.Append("\n## Most imported\n\n| Dependents | File |\n| ---: | --- |\n");
        foreach ((string path, int dependents) in r.Central)
        {
            sb.Append($"| {dependents} | `{path}` |\n");
        }

        sb.Append("\n## Entrypoints\n\n");
        foreach (string entry in r.Entrypoints)
        {
            sb.Append($"- `{entry}`\n");
        }

        return sb.ToString();
    }

    private static string RenderJson(ArchitectureResult r)
    {
        var doc = new ArchitectureDocument
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "architecture",
            TotalFiles = r.TotalFiles,
            TotalLoc = r.TotalLoc,
            Tree = ToDto(r.Tree),
            Languages = r.Languages
                .Select(l => new LanguageDto { Language = l.Language, Files = l.Files, Loc = l.Loc })
                .ToList(),
            MostImported = r.Central
                .Select(c => new CentralDto { Path = c.Path, Dependents = c.Dependents })
                .ToList(),
            Entrypoints = r.Entrypoints,
        };

        return JsonSerializer.Serialize(doc, OutputJson.Options);
    }

    private static TreeDto ToDto(TreeNode node) => new()
    {
        Name = node.Name,
        Path = node.Path,
        Loc = node.Loc,
        FileCount = node.FileCount,
        Children = node.Children.Select(ToDto).ToList(),
    };

    private sealed record ArchitectureDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public int TotalFiles { get; init; }

        public int TotalLoc { get; init; }

        public required TreeDto Tree { get; init; }

        public required IReadOnlyList<LanguageDto> Languages { get; init; }

        public required IReadOnlyList<CentralDto> MostImported { get; init; }

        public required IReadOnlyList<string> Entrypoints { get; init; }
    }

    private sealed record TreeDto
    {
        public required string Name { get; init; }

        public required string Path { get; init; }

        public int Loc { get; init; }

        public int FileCount { get; init; }

        public required IReadOnlyList<TreeDto> Children { get; init; }
    }

    private sealed record LanguageDto
    {
        public required string Language { get; init; }

        public int Files { get; init; }

        public int Loc { get; init; }
    }

    private sealed record CentralDto
    {
        public required string Path { get; init; }

        public int Dependents { get; init; }
    }
}
