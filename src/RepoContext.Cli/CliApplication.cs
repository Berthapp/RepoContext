using System.CommandLine;

namespace RepoContext.Cli;

/// <summary>
/// Builds and runs the <c>repoctx</c> command-line application.
/// </summary>
/// <remarks>
/// In M-Skeleton every subcommand is a stub that reports "not implemented" and
/// returns <see cref="ExitCode.Error"/>. Real behaviour is added milestone by
/// milestone (M1: init/index/search, M2: symbols, M3: related/context, M4:
/// architecture). The command surface is fixed by the specification (F1-F6);
/// no commands beyond it are added.
/// </remarks>
public static class CliApplication
{
    /// <summary>
    /// Parses <paramref name="args"/> and executes the requested command.
    /// </summary>
    /// <returns>A process exit code (see <see cref="ExitCode"/>).</returns>
    public static int Invoke(string[] args)
    {
        RootCommand root = BuildRootCommand();

        ParseResult parseResult = root.Parse(args);

        // Map argument/parse errors to the dedicated exit code (spec F7).
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return ExitCode.InvalidArguments;
        }

        return parseResult.Invoke();
    }

    /// <summary>
    /// Constructs the full command tree. Exposed for tests.
    /// </summary>
    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "RepoContext - local-first, explainable project memory for AI coding agents.");

        root.Subcommands.Add(NotImplemented("init",
            "Initialize a RepoContext index in the current repository."));
        root.Subcommands.Add(NotImplemented("index",
            "Build or incrementally update the index."));
        root.Subcommands.Add(NotImplemented("search",
            "Full-text search across the indexed repository."));
        root.Subcommands.Add(NotImplemented("related",
            "Show files related to a given file (imports, tests, dependents)."));
        root.Subcommands.Add(NotImplemented("context",
            "Return a compact, explained context bundle for a natural-language task."));
        root.Subcommands.Add(NotImplemented("architecture",
            "Summarize the repository structure, languages and central files."));

        return root;
    }

    private static Command NotImplemented(string name, string description)
    {
        var command = new Command(name, description);
        command.SetAction(_ =>
        {
            Console.Error.WriteLine($"repoctx {name}: not implemented");
            return ExitCode.Error;
        });
        return command;
    }
}
