using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Graph;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx related</c> command (spec F4).</summary>
public static class RelatedCommand
{
    public static Command Build()
    {
        var file = new Argument<string>("file")
        {
            Description = "The file to find related files for.",
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("related",
            "Show files related to a given file (imports, tests, dependents).")
        {
            file,
            format,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            string input = parseResult.GetValue(file) ?? string.Empty;
            string? relative = layout.ToRelativePath(input, Directory.GetCurrentDirectory());
            if (relative is null)
            {
                Console.Error.WriteLine("Path is outside the repository.");
                return ExitCode.InvalidArguments;
            }

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureSchemaCurrent(store))
            {
                return ExitCode.NoIndex;
            }

            RelatedResult? result = Related.Query(store, relative);
            if (result is null)
            {
                Console.Error.WriteLine($"File not found in index: {relative}");
                return ExitCode.Error;
            }

            string rendered = RelatedOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "related", UsageSources.Cli, rendered);
            return ExitCode.Success;
        });

        return command;
    }
}
