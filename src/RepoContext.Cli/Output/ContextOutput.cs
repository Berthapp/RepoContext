using System.Text;
using System.Text.Json;
using RepoContext.Core;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Outline;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Output;

/// <summary>
/// Renders a context bundle as text, Markdown or JSON. Reasons are always
/// included. This is the single source of truth for the context wire shape:
/// the CLI, the MCP server and the Q3 cost oracle all render through it, so a
/// budget measured during packing is measured against the bytes that are
/// actually emitted (ADR 0016).
/// </summary>
public static class ContextOutput
{
    public static string Render(
        ContextResult result, OutputFormat format, string surface = Surfaces.Core) => format switch
        {
            OutputFormat.Json => RenderJson(result, surface),
            OutputFormat.Md => RenderMarkdown(result, surface),
            _ => RenderText(result, surface),
        };

    private static string RenderMarkdown(ContextResult result, string surface)
    {
        var sb = new StringBuilder();
        sb.Append("# Context: ").Append(result.Query).Append("\n\n");
        sb.Append("_Terms: ").Append(string.Join(", ", result.Terms))
          .Append(" · content `").Append(result.ContentState)
          .Append("` · analysis `").Append(result.AnalysisState)
          .Append("` · evidence `").Append(result.EvidenceId).Append("`_\n\n");
        foreach (ContextItem item in result.Items)
        {
            sb.Append("### `").Append(item.Path).Append("`  (").Append(item.Kind).Append(")\n");
            sb.Append(FormattableString.Invariant(
                $"- score: {item.Score:F4} · ~{item.ContentTokens} content tokens"));
            if (item.ProjectedReadTokens > 0)
            {
                sb.Append($" · ~{item.ProjectedReadTokens} projected read tokens");
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
            if (item.Receipt is { Length: > 0 } receipt)
            {
                sb.Append("- receipt: `").Append(receipt).Append("`\n");
            }

            AppendSymbolsMd(sb, item);
            AppendSpansMd(sb, item);
            sb.Append('\n');
        }

        AppendMemoriesMd(sb, result);
        AppendReusedMd(sb, result);
        sb.Append($"_Budget: {result.Items.Count} file(s)");
        if (result.Memories.Count > 0)
        {
            sb.Append($" + {result.Memories.Count} memory item(s)");
        }

        sb.Append($" · ~{result.EstimatedTokens} estimated tokens")
          .Append($" · ~{result.ContentTokens} content tokens");
        if (result.ProjectedReadTokens > 0)
        {
            sb.Append($" · ~{result.ProjectedReadTokens} projected read tokens");
        }

        if (result.TokenProfile is { } mdProfile)
        {
            sb.Append(" (").Append(mdProfile).Append("-calibrated)");
        }

        if (result.Omitted > 0)
        {
            sb.Append($" · {result.Omitted} more candidate(s) omitted");
        }

        sb.Append("._\n");
        AppendBudgetsMd(sb, result);
        string body = sb.ToString();
        sb.Append("_Representation: `")
          .Append(RepresentationDisplayId(result, "md", surface, body))
          .Append("`._\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the bundle's memory section (ADR 0013) — distilled agent
    /// knowledge recalled for this task, each entry explained and stale-flagged.
    /// Omitted entirely when no memory matched, so bundles without memories
    /// are byte-identical to earlier versions.
    /// </summary>
    private static void AppendMemoriesMd(StringBuilder sb, ContextResult result)
    {
        if (result.Memories.Count == 0)
        {
            return;
        }

        sb.Append("### Memory\n");
        foreach (Core.Memory.MemoryHit hit in result.Memories)
        {
            sb.Append("- `").Append(hit.Entry.Id).Append("` **").Append(hit.Entry.Kind)
              .Append($"** ({hit.Score:F4} · ~{hit.EstimatedTokens} tokens");
            if (hit.Stale)
            {
                sb.Append(" · **stale**");
            }

            sb.Append(")  \n  ").Append(hit.Entry.Text.Replace("\n", "\n  ")).Append('\n');
            var details = new List<string>();
            if (hit.Entry.Files.Count > 0)
            {
                details.Add("files: " + string.Join(", ", hit.Entry.Files.Keys.Select(p => $"`{p}`")));
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

        sb.Append('\n');
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
            if (s.Role is { Length: > 0 } role)
            {
                sb.Append(" _(").Append(role).Append(")_");
            }

            if (s.Doc is { Length: > 0 })
            {
                sb.Append(" — ").Append(s.Doc);
            }

            if (s.Receipt is { Length: > 0 } receipt)
            {
                sb.Append(" · receipt `").Append(receipt).Append('`');
            }

            sb.Append('\n');
        }

        if (item.SymbolsOmitted is { } cut)
        {
            sb.Append($"- _+{cut} more symbol(s)_\n");
        }
    }

    private static void AppendSpansMd(StringBuilder sb, ContextItem item)
    {
        if (item.Spans is not { Count: > 0 } spans)
        {
            return;
        }

        foreach (ContextSpan span in spans)
        {
            sb.Append($"\n**L{span.StartLine}-{span.EndLine}**");
            if (span.Symbol is { Length: > 0 } symbol)
            {
                sb.Append(" `").Append(symbol).Append('`');
            }

            sb.Append(" · receipt `").Append(span.Receipt).Append('`');
            sb.Append("\n```\n").Append(span.Text).Append("\n```\n");
        }

        if (item.SpansOmitted is { } omitted)
        {
            sb.Append("- _+").Append(omitted).Append(" relevant span(s) omitted by budget_\n");
        }
    }

    private static void AppendReusedMd(StringBuilder sb, ContextResult result)
    {
        if (result.ReusedCount == 0)
        {
            return;
        }

        sb.Append($"_Reused (already held by you): {result.ReusedCount}");
        if (result.ReusedOmitted > 0)
        {
            sb.Append($", {result.ReusedOmitted} not listed");
        }

        sb.Append("._\n");
        foreach (ReusedUnit unit in result.Reused)
        {
            string range = unit.StartLine is { } start ? $" L{start}-{unit.EndLine}" : string.Empty;
            sb.Append("- `").Append(unit.Path).Append('`').Append(range)
              .Append(" · receipt `").Append(unit.Receipt).Append("`\n");
        }

        sb.Append('\n');
    }

    private static void AppendBudgetsMd(StringBuilder sb, ContextResult result)
    {
        List<string> budgets = BudgetLabels(result);
        if (budgets.Count > 0)
        {
            sb.Append("_Active budgets: ").Append(string.Join(" · ", budgets)).Append("._\n");
        }
    }

    private static string RenderText(ContextResult result, string surface)
    {
        var sb = new StringBuilder();
        sb.Append("Context for \"").Append(result.Query).Append("\" (")
          .Append(result.Terms.Count).Append(" term(s), content ").Append(result.ContentState)
          .Append(", analysis ").Append(result.AnalysisState)
          .Append(", evidence ").Append(result.EvidenceId).Append("):\n");

        int i = 1;
        foreach (ContextItem item in result.Items)
        {
            string marker = item.DuplicateOf is { } dup ? "  duplicate-of:" + dup
                : item.Stripped ? "  stripped"
                : string.Empty;
            sb.Append(FormattableString.Invariant(
                $"{i,3}. {item.Path,-46} {item.Score,7:F4}  {item.Kind,-6}  ~{item.ContentTokens}c/{item.ProjectedReadTokens}r tokens  {item.Hash}{marker}\n"));
            sb.Append("      reasons: ").Append(string.Join(", ", item.Reasons)).Append('\n');
            if (item.Receipt is { Length: > 0 } receipt)
            {
                sb.Append("      receipt: ").Append(receipt).Append('\n');
            }

            if (item.Symbols is { Count: > 0 } symbols)
            {
                foreach (OutlineSymbol s in symbols)
                {
                    string role = s.Role is { Length: > 0 } r ? $" [{r}]" : string.Empty;
                    sb.Append($"      L{s.StartLine}-{s.EndLine}  {s.Kind,-9}  {s.Signature}{role}\n");
                    if (s.Receipt is { Length: > 0 } symbolReceipt)
                    {
                        sb.Append("        receipt: ").Append(symbolReceipt).Append('\n');
                    }
                }

                if (item.SymbolsOmitted is { } cut)
                {
                    sb.Append($"      (+{cut} more symbols)\n");
                }
            }

            if (item.Spans is { Count: > 0 } spans)
            {
                foreach (ContextSpan span in spans)
                {
                    sb.Append($"      --- L{span.StartLine}-{span.EndLine}");
                    if (span.Symbol is { Length: > 0 } symbol)
                    {
                        sb.Append("  ").Append(symbol);
                    }

                    sb.Append("  receipt: ").Append(span.Receipt).Append('\n');
                    foreach (string line in span.Text.Split('\n'))
                    {
                        sb.Append("      | ").Append(line).Append('\n');
                    }
                }

                if (item.SpansOmitted is { } omitted)
                {
                    sb.Append($"      (+{omitted} relevant spans omitted by budget)\n");
                }
            }

            i++;
        }

        foreach (ReusedUnit unit in result.Reused)
        {
            string range = unit.StartLine is { } start ? $" L{start}-{unit.EndLine}" : string.Empty;
            sb.Append($"     reused: {unit.Path}{range}  receipt: {unit.Receipt}\n");
        }

        if (result.Memories.Count > 0)
        {
            sb.Append("\nMemory:\n");
            int m = 1;
            foreach (Core.Memory.MemoryHit hit in result.Memories)
            {
                string staleMarker = hit.Stale ? "  stale" : string.Empty;
                sb.Append($"  M{m}. {hit.Entry.Id}  {hit.Entry.Kind,-10}  {hit.Score,6:F4}  " +
                          $"~{hit.EstimatedTokens} tokens{staleMarker}\n");
                foreach (string line in hit.Entry.Text.Split('\n'))
                {
                    sb.Append("      ").Append(line).Append('\n');
                }

                var details = new List<string>();
                if (hit.Entry.Files.Count > 0)
                {
                    details.Add("files: " + string.Join(", ", hit.Entry.Files.Keys));
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

                m++;
            }
        }

        sb.Append($"\nBudget: {result.Items.Count} file(s)");
        if (result.Memories.Count > 0)
        {
            sb.Append($" + {result.Memories.Count} memory item(s)");
        }

        sb.Append($" · ~{result.EstimatedTokens} estimated tokens")
          .Append($" · ~{result.ContentTokens} content tokens");
        if (result.ProjectedReadTokens > 0)
        {
            sb.Append($" · ~{result.ProjectedReadTokens} projected read tokens");
        }

        if (result.ReusedCount > 0)
        {
            sb.Append($" · {result.ReusedCount} reused");
        }

        if (result.TokenProfile is { } profile)
        {
            sb.Append(" (").Append(profile).Append("-calibrated)");
        }

        if (result.Omitted > 0)
        {
            sb.Append($" · {result.Omitted} more candidate(s) omitted");
        }

        sb.Append('\n');
        List<string> budgets = BudgetLabels(result);
        if (budgets.Count > 0)
        {
            sb.Append("Active budgets: ").Append(string.Join(" · ", budgets)).Append('\n');
        }

        string body = sb.ToString();
        sb.Append("Representation: ")
          .Append(RepresentationDisplayId(result, "text", surface, body))
          .Append('\n');
        return sb.ToString();
    }

    private static List<string> BudgetLabels(ContextResult result)
    {
        var labels = new List<string>();
        if (result.BudgetTokens is { } charged)
        {
            labels.Add($"charged-work={charged}");
        }

        if (result.ResponseBudgetTokens is { } response)
        {
            labels.Add($"exact-response={response}");
        }

        if (result.ProjectedReadBudgetTokens is { } read)
        {
            labels.Add($"projected-full-read={read}");
        }

        return labels;
    }

    /// <summary>
    /// Serializes the v3 context document. <c>representation_id</c> is computed
    /// over the canonical body with its own field omitted, so the identity never
    /// self-references (Q4).
    /// </summary>
    private static string RenderJson(ContextResult result, string surface)
    {
        ContextDocument body = BuildDocument(result, representationId: null);
        string withoutIdentity = JsonSerializer.Serialize(body, OutputJson.Options);
        string representationId =
            RepresentationDisplayId(result, "json", surface, withoutIdentity);

        return JsonSerializer.Serialize(
            BuildDocument(result, representationId), OutputJson.Options);
    }

    private static string RepresentationDisplayId(
        ContextResult result, string format, string surface, string body) =>
        Hashes.Short(Fingerprints.RepresentationId(
            string.IsNullOrEmpty(result.FullEvidenceId) ? result.EvidenceId : result.FullEvidenceId,
            RepoContextInfo.SchemaVersion,
            format,
            profile: result.TokenProfile ?? "o200k",
            encoding: "utf-8",
            surface,
            canonicalBody: body));

    private static ContextDocument BuildDocument(ContextResult result, string? representationId) =>
        new()
        {
            SchemaVersion = RepoContextInfo.SchemaVersion,
            Command = "context",
            Query = result.Query,
            Terms = result.Terms,
            State = result.State,
            ContentState = result.ContentState,
            AnalysisState = result.AnalysisState,
            EvidenceId = result.EvidenceId,
            RepresentationId = representationId,
            Detail = result.Detail.ToString().ToLowerInvariant(),
            Top = result.Top,
            Budgets = BuildBudgets(result),
            TokenProfile = result.TokenProfile,
            Count = result.Items.Count,
            ReusedCount = result.ReusedCount,
            ReusedOmitted = result.ReusedOmitted > 0 ? result.ReusedOmitted : null,
            Omitted = result.Omitted > 0 ? result.Omitted : null,
            OmittedBy = BuildOmissions(result.Omissions),
            ContentTokens = result.ContentTokens,
            ProjectedReadTokens = result.ProjectedReadTokens,
            EstimatedTokens = result.EstimatedTokens,
            Results = result.Items.Select(BuildItem).ToList(),
            Reused = result.Reused.Select(r => new ReusedUnitDto
            {
                Path = r.Path,
                StartLine = r.StartLine,
                EndLine = r.EndLine,
                Symbol = r.Symbol,
                Receipt = r.Receipt,
            }).ToList(),
            Memories = result.Memories.Count > 0
                ? result.Memories.Select(MemoryDto.From).ToList()
                : null,
        };

    private static OmissionsDto? BuildOmissions(OmissionReasons omissions) =>
        omissions.Top == 0 && omissions.ResponseBudget == 0
        && omissions.ProjectedReadBudget == 0 && omissions.BudgetTokens == 0
        && omissions.NonpositiveScore == 0
            ? null
            : new OmissionsDto
            {
                Top = Positive(omissions.Top),
                ResponseBudget = Positive(omissions.ResponseBudget),
                ProjectedReadBudget = Positive(omissions.ProjectedReadBudget),
                BudgetTokens = Positive(omissions.BudgetTokens),
                NonpositiveScore = Positive(omissions.NonpositiveScore),
            };

    private static BudgetsDto? BuildBudgets(ContextResult result) =>
        result.BudgetTokens is null
        && result.ResponseBudgetTokens is null
        && result.ProjectedReadBudgetTokens is null
            ? null
            : new BudgetsDto
            {
                ChargedWorkTokens = result.BudgetTokens,
                ResponseTokens = result.ResponseBudgetTokens,
                ProjectedReadTokens = result.ProjectedReadBudgetTokens,
            };

    private static int? Positive(int value) => value > 0 ? value : null;

    private static ContextItemDto BuildItem(ContextItem item)
    {
        bool multiSpan = item.Spans is { Count: > 1 };
        return new ContextItemDto
        {
            Path = item.Path,
            Kind = item.Kind,
            Score = item.Score,
            // Deprecated single-span fields: never mapped onto a synthetic
            // enclosing range when several spans were delivered.
            StartLine = multiSpan ? null : item.StartLine,
            EndLine = multiSpan ? null : item.EndLine,
            ContentTokens = item.ContentTokens,
            ProjectedReadTokens = item.ProjectedReadTokens,
            EstimatedTokens = item.EstimatedTokens,
            FileTokens = item.FileTokens,
            Hash = item.Hash,
            Receipt = item.Receipt,
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
                Role = s.Role,
                Receipt = s.Receipt,
            }).ToList(),
            SymbolsOmitted = item.SymbolsOmitted,
            Spans = item.Spans?.Select(s => new ContextSpanDto
            {
                StartLine = s.StartLine,
                EndLine = s.EndLine,
                Symbol = s.Symbol,
                Text = s.Text,
                Receipt = s.Receipt,
            }).ToList(),
            SpansOmitted = item.SpansOmitted,
            Snippet = multiSpan ? null : item.Snippet,
        };
    }

    private sealed record ContextDocument
    {
        public int SchemaVersion { get; init; }

        public required string Command { get; init; }

        public required string Query { get; init; }

        public required IReadOnlyList<string> Terms { get; init; }

        /// <summary>Deprecated (v2): short content state. Use <c>content_state</c>.</summary>
        public required string State { get; init; }

        public required string ContentState { get; init; }

        public required string AnalysisState { get; init; }

        public required string EvidenceId { get; init; }

        /// <summary>Omitted while the body is hashed to produce this very value.</summary>
        public string? RepresentationId { get; init; }

        public required string Detail { get; init; }

        /// <summary>The cap on new entries; reused units never consume it.</summary>
        public int Top { get; init; }

        public BudgetsDto? Budgets { get; init; }

        /// <summary>Active token-calibration label (absent for raw o200k counts).</summary>
        public string? TokenProfile { get; init; }

        public int Count { get; init; }

        public int ReusedCount { get; init; }

        public int? ReusedOmitted { get; init; }

        /// <summary>Scored candidates beyond the top/budget limits (absent when zero).</summary>
        public int? Omitted { get; init; }

        public OmissionsDto? OmittedBy { get; init; }

        public int ContentTokens { get; init; }

        public int ProjectedReadTokens { get; init; }

        /// <summary>Deprecated (v2): the blended legacy cost basis.</summary>
        public int EstimatedTokens { get; init; }

        public required IReadOnlyList<ContextItemDto> Results { get; init; }

        public required IReadOnlyList<ReusedUnitDto> Reused { get; init; }

        /// <summary>Relevant agent memories folded into the bundle (absent when none — ADR 0013).</summary>
        public IReadOnlyList<MemoryDto>? Memories { get; init; }
    }

    private sealed record OmissionsDto
    {
        public int? Top { get; init; }

        public int? ResponseBudget { get; init; }

        public int? ProjectedReadBudget { get; init; }

        public int? BudgetTokens { get; init; }

        public int? NonpositiveScore { get; init; }
    }

    private sealed record BudgetsDto
    {
        /// <summary>Legacy charged-work basis (not serialized response size).</summary>
        public int? ChargedWorkTokens { get; init; }

        /// <summary>Exact model-visible rendered response basis.</summary>
        public int? ResponseTokens { get; init; }

        /// <summary>Projected downstream full-file-read basis.</summary>
        public int? ProjectedReadTokens { get; init; }
    }

    private sealed record ReusedUnitDto
    {
        public required string Path { get; init; }

        public int? StartLine { get; init; }

        public int? EndLine { get; init; }

        public string? Symbol { get; init; }

        public required string Receipt { get; init; }
    }

    private sealed record ContextItemDto
    {
        public required string Path { get; init; }

        public required string Kind { get; init; }

        public double Score { get; init; }

        /// <summary>Deprecated (v2); omitted for multi-span items.</summary>
        public int? StartLine { get; init; }

        /// <summary>Deprecated (v2); omitted for multi-span items.</summary>
        public int? EndLine { get; init; }

        public int ContentTokens { get; init; }

        public int ProjectedReadTokens { get; init; }

        /// <summary>Deprecated (v2): the blended legacy cost basis.</summary>
        public int EstimatedTokens { get; init; }

        /// <summary>Deprecated (v2): full-file read cost when the item carries content.</summary>
        public int? FileTokens { get; init; }

        public required string Hash { get; init; }

        /// <summary>Convenience alias; present only when the item has a single unit.</summary>
        public string? Receipt { get; init; }

        /// <summary>Another bundle item with byte-identical content; read that one.</summary>
        public string? DuplicateOf { get; init; }

        /// <summary>Present (true) when the slice was comment-stripped (lines approximate).</summary>
        public bool? Stripped { get; init; }

        public required IReadOnlyList<string> Reasons { get; init; }

        public IReadOnlyList<ContextSymbolDto>? Symbols { get; init; }

        public int? SymbolsOmitted { get; init; }

        public IReadOnlyList<ContextSpanDto>? Spans { get; init; }

        public int? SpansOmitted { get; init; }

        /// <summary>Deprecated (v2); omitted for multi-span items.</summary>
        public string? Snippet { get; init; }
    }

    private sealed record ContextSpanDto
    {
        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public string? Symbol { get; init; }

        public required string Text { get; init; }

        public required string Receipt { get; init; }
    }

    private sealed record ContextSymbolDto
    {
        public required string Name { get; init; }

        public required string Kind { get; init; }

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public required string Signature { get; init; }

        public string? Doc { get; init; }

        /// <summary>Declared explanation for a query-aware selection (<c>match</c>/<c>container</c>).</summary>
        public string? Role { get; init; }

        public string? Receipt { get; init; }
    }
}
