using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Architecture;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx architecture</c> command (spec F6).</summary>
public static class ArchitectureCommand
{
    public static Command Build()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("architecture",
            "Summarize the repository structure, languages and central files.")
        {
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

            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            ArchitectureResult result = new ArchitectureEngine(store).Build();

            string rendered = ArchitectureOutput.Render(result, outputFormat);
            Console.Out.Write(rendered);
            if (!rendered.EndsWith('\n'))
            {
                Console.Out.Write('\n');
            }

            return ExitCode.Success;
        });

        return command;
    }
}
