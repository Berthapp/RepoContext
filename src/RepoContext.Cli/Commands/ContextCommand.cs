using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Identity;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx context</c> command (spec F5, pipeline chapter 6).</summary>
public static class ContextCommand
{
    public static Command Build()
    {
        var task = new Argument<string>("task")
        {
            Description = "A natural-language description of what you want to do.",
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of new files. Reused units never consume a slot.",
            DefaultValueFactory = _ => 8,
        };
        var budget = new Option<int?>("--budget-tokens")
        {
            Description = "Compatibility 'charged work' cap (real BPE counts): projected reads for "
                          + "paths, embedded content otherwise. For a hard ceiling on the response "
                          + "itself use --response-budget-tokens.",
        };
        var responseBudget = new Option<int?>("--response-budget-tokens")
        {
            Description = "Hard ceiling on the exact serialized response, including its trailing newline.",
        };
        var readBudget = new Option<int?>("--projected-read-budget-tokens")
        {
            Description = "Hard ceiling on the full-file reads implied by delivered pointers.",
        };
        var detail = new Option<string>("--detail")
        {
            Description = "Per-file detail: paths (pointers), outline (symbol skeletons) or slices (source content).",
            DefaultValueFactory = _ => "paths",
        };
        var snippets = new Option<bool>("--snippets")
        {
            Description = "Alias for --detail slices.",
        };
        var known = new Option<string[]>("--known")
        {
            Description = "A file you hold IN FULL, as <path>@<hash> (repeatable). Never derive this "
                          + "from a slice or outline - use --seen for delivered ranges.",
        };
        var seen = new Option<string[]>("--seen")
        {
            Description = "A receipt from evidence you already received (repeatable). Only that exact "
                          + "span or symbol is suppressed; unseen parts of the same file still arrive.",
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("context",
            "Return a compact, explained context bundle for a natural-language task.")
        {
            task,
            top,
            budget,
            responseBudget,
            readBudget,
            detail,
            snippets,
            known,
            seen,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
                return ExitCode.InvalidArguments;
            }

            int topN = parseResult.GetValue(top);
            if (topN <= 0)
            {
                Console.Error.WriteLine("--top must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            if (!Positive(parseResult.GetValue(budget), "--budget-tokens", out int? budgetTokens)
                || !Positive(parseResult.GetValue(responseBudget), "--response-budget-tokens",
                    out int? responseBudgetTokens)
                || !Positive(parseResult.GetValue(readBudget), "--projected-read-budget-tokens",
                    out int? readBudgetTokens))
            {
                return ExitCode.InvalidArguments;
            }

            if (!TryParseDetail(parseResult.GetValue(detail), out ContextDetail detailLevel))
            {
                Console.Error.WriteLine("Invalid --detail. Use 'paths', 'outline' or 'slices'.");
                return ExitCode.InvalidArguments;
            }

            // --snippets is the pre-M6 spelling of slice detail; an explicit
            // --detail wins.
            if (parseResult.GetValue(snippets) && parseResult.GetResult(detail) is null or { Implicit: true })
            {
                detailLevel = ContextDetail.Slices;
            }

            if (!TryParseKnown(parseResult.GetValue(known), out Dictionary<string, string>? knownMap))
            {
                Console.Error.WriteLine("Invalid --known. Use <path>@<hash> (hash as printed by context/outline).");
                return ExitCode.InvalidArguments;
            }

            string[] seenReceipts = parseResult.GetValue(seen) ?? [];
            if (Array.Find(seenReceipts, r => !Receipt.IsWellFormed(r)) is { } malformed)
            {
                Console.Error.WriteLine(
                    $"Invalid --seen receipt '{malformed}'. Pass a receipt exactly as returned in "
                    + "a previous response.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            string query = parseResult.GetValue(task) ?? string.Empty;
            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            var costModel = ContextCostModel.ForCli(outputFormat);
            var engine = new ContextEngine(store, config);
            ContextResult result = engine.Run(query, new ContextOptions
            {
                Top = topN,
                BudgetTokens = budgetTokens,
                ResponseBudgetTokens = responseBudgetTokens,
                ProjectedReadBudgetTokens = readBudgetTokens,
                Detail = detailLevel,
                Known = knownMap,
                Seen = seenReceipts,
            }, responseBudgetTokens is null ? null : costModel);

            if (result.Shortfall is { } shortfall)
            {
                // No partial success payload is emitted on this channel.
                Console.Error.WriteLine(
                    $"--response-budget-tokens {shortfall.RequestedBudgetTokens} cannot fit the smallest "
                    + $"useful response; retry_budget_tokens={shortfall.RetryBudgetTokens}.");
                return ExitCode.InvalidArguments;
            }

            string rendered = ContextOutput.Render(result, outputFormat, Surfaces.Cli);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "context", UsageSources.Cli, costModel.SurfaceText(result),
                UsageMeter.ReplacedTokens(result, path => store.FindFile(path)?.TokenCount),
                files: result.Items.Count,
                unchanged: result.ReusedFilesCount);
            return ExitCode.Success;
        });

        return command;
    }

    private static bool Positive(int? value, string option, out int? parsed)
    {
        parsed = value;
        if (value is <= 0)
        {
            Console.Error.WriteLine($"{option} must be greater than zero.");
            return false;
        }

        return true;
    }

    private static bool TryParseDetail(string? value, out ContextDetail detail)
    {
        switch (value?.ToLowerInvariant())
        {
            case null or "" or "paths":
                detail = ContextDetail.Paths;
                return true;
            case "outline":
                detail = ContextDetail.Outline;
                return true;
            case "slices":
                detail = ContextDetail.Slices;
                return true;
            default:
                detail = ContextDetail.Paths;
                return false;
        }
    }

    /// <summary>Parses repeated <c>path@hash</c> entries; the last '@' separates.</summary>
    private static bool TryParseKnown(string[]? entries, out Dictionary<string, string>? known)
    {
        known = null;
        if (entries is not { Length: > 0 })
        {
            return true;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            int at = entry.LastIndexOf('@');
            if (at <= 0 || at == entry.Length - 1)
            {
                return false;
            }

            map[entry[..at]] = entry[(at + 1)..];
        }

        known = map;
        return true;
    }
}
