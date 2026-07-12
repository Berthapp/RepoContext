using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx outline</c> command (M6, ADR 0010): a file's skeleton —
/// symbols, signatures, doc summaries, token cost — for a fraction of the
/// tokens a full read would spend.
/// </summary>
public static class OutlineCommand
{
    public static Command Build()
    {
        var file = new Argument<string>("file")
        {
            Description = "The file to outline.",
        };
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");

        var command = new Command("outline",
            "Show a file's skeleton (symbols, signatures, docs) instead of its content.")
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

            Core.Outline.OutlineResult? result = Core.Outline.Outline.Query(store, relative);
            if (result is null)
            {
                Console.Error.WriteLine($"File not found in index: {relative}");
                return ExitCode.Error;
            }

            CommandSupport.WriteRendered(OutlineOutput.Render(result, outputFormat));
            return ExitCode.Success;
        });

        return command;
    }
}
