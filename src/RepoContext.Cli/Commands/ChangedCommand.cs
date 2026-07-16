using System.CommandLine;
using RepoContext.Cli.Output;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Stats;
using RepoContext.Core.Storage;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx changed</c> command (M6, ADR 0010): diff the working tree
/// against the index so an agent re-reads only what its edits touched.
/// </summary>
public static class ChangedCommand
{
    public static Command Build()
    {
        var format = new Option<string>("--format")
        {
            Description = "Output format: text, json or md.",
            DefaultValueFactory = _ => "text",
        };
        format.Aliases.Add("-f");
        var patch = new Option<bool>("--patch")
        {
            Description = "Include delta hunks (working tree vs indexed content) for modified " +
                          "files, so edits cost a patch instead of a full re-read.",
        };

        var command = new Command("changed",
            "Show working-tree changes since the last index and the files they impact.")
        {
            format,
            patch,
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

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            using IndexStore store = IndexStore.Open(layout.DatabasePath);
            if (!CommandSupport.EnsureSchemaCurrent(store))
            {
                return ExitCode.NoIndex;
            }

            TokenScale scale = TokenScale.From(config);
            ChangedResult result = ChangeDetector.Run(
                layout, config, store, parseResult.GetValue(patch), scale);
            string rendered = ChangedOutput.Render(result, outputFormat);
            CommandSupport.WriteRendered(rendered);
            UsageRecorder.Record(layout, "changed", UsageSources.Cli, rendered,
                replacedTokens: UsageMeter.PatchReplacedTokens(result),
                scale: scale);
            return ExitCode.Success;
        });

        return command;
    }
}
