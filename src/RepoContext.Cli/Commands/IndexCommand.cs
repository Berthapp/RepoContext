using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx index</c> command (spec F3).</summary>
public static class IndexCommand
{
    public static Command Build()
    {
        var full = new Option<bool>("--full")
        {
            Description = "Force a full rebuild instead of an incremental update.",
        };

        var command = new Command("index", "Build or incrementally update the index.")
        {
            full,
        };

        command.SetAction(parseResult =>
        {
            RepoLayout? layout = RepoLayout.Discover(Directory.GetCurrentDirectory());
            if (layout is null)
            {
                Console.Error.WriteLine("Not initialized. Run 'repoctx init' first.");
                return ExitCode.NoIndex;
            }

            RepoctxConfig config = ConfigStore.Load(layout.ConfigPath);
            var indexer = new Indexer(layout, config, CliInfo.Version);
            IndexStats stats = indexer.Run(parseResult.GetValue(full));

            string mode = stats.FullRebuild ? "full" : "incremental";
            Console.WriteLine($"Indexed {layout.Root} ({mode})");
            Console.WriteLine(
                $"  files: {stats.TotalFiles} (+{stats.Added} ~{stats.Changed} -{stats.Deleted} ={stats.Unchanged})");
            Console.WriteLine(
                $"  chunks: {stats.TotalChunks}  symbols: {stats.TotalSymbols}  edges: {stats.TotalEdges}");
            return ExitCode.Success;
        });

        return command;
    }
}
