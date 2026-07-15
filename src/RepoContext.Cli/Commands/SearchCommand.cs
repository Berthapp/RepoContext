using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Query;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx search</c> command (spec F5): BM25 full-text search.</summary>
public static class SearchCommand
{
    public static Command Build()
    {
        var query = new Argument<string>("query")
        {
            Description = "The text to search for.",
        };
        var top = new Option<int>("--top")
        {
            Description = "Maximum number of results.",
            DefaultValueFactory = _ => 10,
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");
        var symbolsOnly = new Option<bool>("--symbols")
        {
            Description = "Restrict the search to symbols (classes, functions, ...).",
        };

        var command = new Command("search", "Full-text search across the indexed repository.")
        {
            query,
            top,
            format,
            symbolsOnly,
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

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            string queryText = parseResult.GetValue(query) ?? string.Empty;
            string? match = FtsQuery.Build(queryText);
            if (match is null)
            {
                Console.Error.WriteLine("Query has no searchable terms.");
                return ExitCode.InvalidArguments;
            }

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureSchemaCurrent(store))
            {
                return ExitCode.NoIndex;
            }

            IReadOnlyList<SearchHit> hits = store.Search(match, topN, parseResult.GetValue(symbolsOnly));
            string rendered = SearchOutput.Render(queryText, hits, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "search", UsageSources.Cli, rendered,
                scale: CommandSupport.ScaleFor(layout));
            return ExitCode.Success;
        });

        return command;
    }
}
