using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Architecture;
using RepoContext.Core.Configuration;
using RepoContext.Core.Stats;
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
        var depth = new Option<int>("--depth")
        {
            Description = "Directory-tree depth (1 gives a minimal orientation summary).",
            DefaultValueFactory = _ => ArchitectureEngine.DefaultDepth,
        };

        var command = new Command("architecture",
            "Summarize the repository structure, languages and central files.")
        {
            format,
            depth,
        };

        command.SetAction(parseResult =>
        {
            if (!OutputFormatParser.TryParse(parseResult.GetValue(format), out OutputFormat outputFormat))
            {
                Console.Error.WriteLine("Invalid --format. Use 'text', 'json' or 'md'.");
                return ExitCode.InvalidArguments;
            }

            int treeDepth = parseResult.GetValue(depth);
            if (treeDepth <= 0)
            {
                Console.Error.WriteLine("--depth must be greater than zero.");
                return ExitCode.InvalidArguments;
            }

            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null || !layout.HasIndex)
            {
                Console.Error.WriteLine("No index found. Run 'repoctx index' first.");
                return ExitCode.NoIndex;
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureIndexUsable(store, config))
            {
                return ExitCode.NoIndex;
            }

            ArchitectureResult result = new ArchitectureEngine(store).Build(treeDepth);
            string rendered = ArchitectureOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(
                layout, "architecture", UsageSources.Cli, CommandSupport.CliSurfaceText(rendered),
                scale: Core.Indexing.TokenScale.From(config));
            return ExitCode.Success;
        });

        return command;
    }
}
