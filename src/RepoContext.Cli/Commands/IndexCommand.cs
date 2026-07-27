using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;
using RepoContext.Core.Scanning;

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

            WarnAboutMissingIncludeRoots(layout, config, stats);

            string mode = stats.FullRebuild ? "full" : "incremental";
            Console.WriteLine($"Indexed {layout.Root} ({mode})");
            Console.WriteLine(
                $"  files: {stats.TotalFiles} (+{stats.Added} ~{stats.Changed} -{stats.Deleted} ={stats.Unchanged})");
            Console.WriteLine(
                $"  chunks: {stats.TotalChunks}  symbols: {stats.TotalSymbols}  edges: {stats.TotalEdges}");
            Console.WriteLine(
                $"  work: {stats.BytesRead} bytes read  {stats.FilesParsed} files parsed  "
                + $"{stats.GraphFilesAnalyzed} graph files analyzed  "
                + $"{stats.EdgesRecomputed} edges recomputed  {stats.ElapsedMilliseconds} ms");
            return ExitCode.Success;
        });

        return command;
    }

    /// <summary>
    /// Warns when an explicit <c>include</c> list leaves the repository partly
    /// or wholly unindexed. Configurations written before 0.8.0 pin the roots
    /// to <c>src/app/lib/docs</c>, which indexes nothing in a repository whose
    /// projects live elsewhere - and does so silently, because a scan that
    /// finds no root is not an error.
    /// </summary>
    private static void WarnAboutMissingIncludeRoots(RepoLayout layout, RepoctxConfig config, IndexStats stats)
    {
        if (config.Include.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> missing = FileScanner.MissingIncludeRoots(layout.Root, config);
        if (missing.Count == 0 && stats.TotalFiles > 0)
        {
            return;
        }

        Console.Error.WriteLine(
            missing.Count > 0
                ? $"Warning: configured include root(s) do not exist: {string.Join(", ", missing)}"
                : "Warning: the configured include roots selected no files.");
        Console.Error.WriteLine(
            $"  Only {string.Join(", ", config.Include)} is scanned, so code elsewhere in this "
            + "repository (including nested projects) is not indexed.");
        Console.Error.WriteLine(
            $"  Remove \"include\" from {RepoContextInfo.ConfigFileName} (or set it to []) to index "
            + "the whole repository, then re-run 'repoctx index'.");
    }
}
