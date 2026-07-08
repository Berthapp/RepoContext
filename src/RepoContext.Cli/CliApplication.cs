using System.CommandLine;
using RepoContext.Cli.Commands;

namespace RepoContext.Cli;

/// <summary>
/// Builds and runs the <c>repoctx</c> command-line application.
/// </summary>
/// <remarks>
/// M1 implements <c>init</c>, <c>index</c> and <c>search</c>. The remaining
/// commands (<c>related</c>, <c>context</c>, <c>architecture</c>) are stubs
/// until M3/M4. The command surface is fixed by the specification (F1-F6).
/// </remarks>
public static class CliApplication
{
    public static int Invoke(string[] args)
    {
        RootCommand root = BuildRootCommand();
        ParseResult parseResult = root.Parse(args);

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

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "RepoContext - local-first, explainable project memory for AI coding agents.");

        root.Subcommands.Add(InitCommand.Build());
        root.Subcommands.Add(IndexCommand.Build());
        root.Subcommands.Add(SearchCommand.Build());
        root.Subcommands.Add(RelatedCommand.Build());
        root.Subcommands.Add(ContextCommand.Build());
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
