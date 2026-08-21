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
            WarnAboutOversizedFiles(config, stats);
            WarnAboutUnreadableIgnoreFiles(stats);

            string mode = stats.FullRebuild ? "full" : "incremental";
            Console.WriteLine($"Indexed {layout.Root} ({mode})");
            Console.WriteLine(
                $"  files: {stats.TotalFiles} (+{stats.Added} ~{stats.Changed} -{stats.Deleted} ={stats.Unchanged})");
            Console.WriteLine(
                $"  chunks: {stats.TotalChunks}  symbols: {stats.TotalSymbols}  "
                + $"edges: {stats.TotalEdges} ({stats.ReferenceEdges} cross-artifact)  "
                + $"refs: {stats.TotalRefs}");
            if (stats.SkippedBinary + stats.SkippedTooLarge + stats.Unreadable > 0)
            {
                // Only the exclusions RepoContext chose itself; ignore rules are
                // the user's own decision and are not second-guessed here.
                // Unreadable files are named rather than folded into "unchanged",
                // which means verified: an already-indexed one kept its previous
                // content, and one that was never indexed is simply absent.
                Console.WriteLine(
                    $"  skipped: {stats.SkippedBinary} binary  "
                    + $"{stats.SkippedTooLarge} over {config.Indexing.MaxFileSizeKb} KB  "
                    + $"{stats.Unreadable} unreadable");
            }

            Console.WriteLine(
                $"  work: {stats.BytesRead} bytes read  {stats.FilesParsed} files parsed  "
                + $"{stats.GraphFilesAnalyzed} graph files analyzed  "
                + $"{stats.EdgesRecomputed} edges recomputed  {stats.ElapsedMilliseconds} ms");
            return ExitCode.Success;
        });

        return command;
    }

    /// <summary>
    /// Warns when ignore rules could not be read. This is the one failure in
    /// this family that adds to the index rather than subtracting from it: the
    /// directories those rules would have excluded - build output, dependencies
    /// - were walked and indexed instead, and every later query pays for them.
    /// </summary>
    private static void WarnAboutUnreadableIgnoreFiles(IndexStats stats)
    {
        if (stats.UnreadableIgnoreFiles.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"Warning: {stats.UnreadableIgnoreFiles.Count} ignore file(s) could not be read, "
            + "so their exclusions were not applied and the directories they cover are indexed.");
        foreach (string path in stats.UnreadableIgnoreFiles)
        {
            Console.Error.WriteLine($"  {path}");
        }

        Console.Error.WriteLine("  Fix the file's permissions and re-run 'repoctx index --full'.");
    }

    /// <summary>
    /// Warns about text files the size limit excluded. Coverage is subtractive
    /// (ADR 0017), and a subtractive rule is only safe while it fails visibly:
    /// a 900 KB exported specification that silently never enters the index is
    /// indistinguishable, to an agent, from one that does not exist.
    /// </summary>
    private static void WarnAboutOversizedFiles(RepoctxConfig config, IndexStats stats)
    {
        if (stats.SkippedTooLarge == 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"Warning: {stats.SkippedTooLarge} text file(s) exceed "
            + $"indexing.maxFileSizeKb ({config.Indexing.MaxFileSizeKb} KB) and are not indexed.");
        foreach (string path in stats.SkippedTooLargeSample)
        {
            Console.Error.WriteLine($"  {path}");
        }

        if (stats.SkippedTooLarge > stats.SkippedTooLargeSample.Count)
        {
            Console.Error.WriteLine(
                $"  ... and {stats.SkippedTooLarge - stats.SkippedTooLargeSample.Count} more");
        }

        Console.Error.WriteLine(
            "  Raise indexing.maxFileSizeKb in repoctx.config.json to include them, or add them "
            + "to .repoctxignore to accept the gap deliberately.");
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
