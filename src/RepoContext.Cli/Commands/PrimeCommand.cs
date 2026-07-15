using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Architecture;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx prime</c> command (ADR 0012): a cache-stable repository
/// primer for the top of an agent's prompt. Unlike every other command it
/// defaults to <c>md</c> output — the primer's purpose is to be pasted into
/// a prompt block behind a cache breakpoint, where cached input tokens cost
/// a fraction of fresh ones.
/// </summary>
public static class PrimeCommand
{
    public static Command Build()
    {
        var files = new Option<int>("--files")
        {
            Description = "Number of key (most-imported) files to outline.",
            DefaultValueFactory = _ => PrimeEngine.DefaultFiles,
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: md (default - the primer is a prompt block), text or json.",
            DefaultValueFactory = _ => "md",
        };
        format.Aliases.Add("-f");

        var command = new Command("prime",
            "Emit a cache-stable repository primer: byte-identical output until indexed content changes.")
        {
            files,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'md', 'text' or 'json'.");
                return ExitCode.InvalidArguments;
            }

            int fileCount = parseResult.GetValue(files);
            if (fileCount <= 0)
            {
                Console.Error.WriteLine("--files must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureSchemaCurrent(store))
            {
                return ExitCode.NoIndex;
            }

            Core.Indexing.TokenScale scale = CommandSupport.ScaleFor(layout);
            var engine = new PrimeEngine(store, scale);
            PrimeResult result = engine.Build(fileCount);
            string rendered = PrimeOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "prime", UsageSources.Cli, rendered, scale: scale);
            return ExitCode.Success;
        });

        return command;
    }
}
