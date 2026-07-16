using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Context;
using RepoContext.Core.Indexing;
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
            Description = "Maximum number of files.",
            DefaultValueFactory = _ => 8,
        };
        var budget = new Option<int?>("--budget-tokens")
        {
            Description = "Token budget the bundle is packed into (real BPE counts).",
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
            Description = "A file you already have, as <path>@<hash> (repeatable). " +
                          "Unchanged files come back as zero-cost markers.",
        };
        var session = new Option<string?>("--session")
        {
            Description = "Track the known-file set server-side under this session name " +
                          "(.repoctx/sessions/), so hashes need not be echoed back. " +
                          "Delivered slices are remembered; explicit --known entries win.",
        };
        var stripComments = new Option<bool>("--strip-comments")
        {
            Description = "Lossy: drop full-line comments and blank runs from embedded slices " +
                          "(outline docs already summarize them). Line ranges become approximate.",
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
            detail,
            snippets,
            known,
            session,
            stripComments,
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

            int? budgetTokens = parseResult.GetValue(budget);
            if (budgetTokens is <= 0)
            {
                Console.Error.WriteLine("--budget-tokens must be greater than zero.");
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

            string? sessionName = parseResult.GetValue(session);
            if (sessionName is not null && !SessionStore.IsValidName(sessionName))
            {
                Console.Error.WriteLine(
                    "Invalid --session. Use 1-64 characters from A-Z, a-z, 0-9, '.', '_', '-'.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            if (sessionName is not null)
            {
                knownMap = MergeKnown(SessionStore.Load(layout, sessionName), knownMap);
            }

            string query = parseResult.GetValue(task) ?? string.Empty;
            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureSchemaCurrent(store))
            {
                return ExitCode.NoIndex;
            }

            var engine = new ContextEngine(store, config);
            ContextResult result = engine.Run(query, new ContextOptions
            {
                Top = topN,
                BudgetTokens = budgetTokens,
                Detail = detailLevel,
                Known = knownMap,
                StripComments = parseResult.GetValue(stripComments),
                // Text/md deliver raw slice text; only JSON pays the escape tax,
                // so charge in serialized form only when JSON is the output (ADR 0012).
                SerializedCharging = outputFormat == OutputFormat.Json,
            });

            TokenScale scale = TokenScale.From(config);
            string rendered = ContextOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "context", UsageSources.Cli, rendered,
                UsageMeter.ReplacedTokens(result,
                    path => store.FindFile(path) is { } f ? scale.Apply(f.TokenCount) : null),
                files: result.Items.Count,
                unchanged: result.Items.Count(i => i.Unchanged),
                scale: scale);
            if (sessionName is not null)
            {
                SessionStore.Save(layout, sessionName, result);
            }

            return ExitCode.Success;
        });

        return command;
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

    /// <summary>Session entries seed the map; explicit <c>--known</c> entries win.</summary>
    private static Dictionary<string, string> MergeKnown(
        IReadOnlyDictionary<string, string> sessionKnown, Dictionary<string, string>? explicitKnown)
    {
        var merged = new Dictionary<string, string>(sessionKnown, StringComparer.Ordinal);
        foreach ((string path, string hash) in explicitKnown ?? new Dictionary<string, string>())
        {
            merged[path] = hash;
        }

        return merged;
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
