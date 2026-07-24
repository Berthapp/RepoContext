using System.CommandLine;
using RepoContext.Cli.Commands;

namespace RepoContext.Cli;

/// <summary>
/// Builds and runs the <c>repoctx</c> command-line application.
/// </summary>
/// <remarks>
/// The command surface is fixed by the specification (F1-F6): <c>init</c>,
/// <c>index</c>, <c>search</c>, <c>related</c>, <c>context</c> and
/// <c>architecture</c>, plus <c>mcp</c> (M5) which serves the same query
/// engine to AI agents over the Model Context Protocol, the token-lean
/// M6 additions <c>outline</c> and <c>changed</c> (ADR 0010), the
/// <c>stats</c> token-savings dashboard (ADR 0011), M8 <c>prime</c> and
/// changed-patch optimizations (ADR 0012), and the M9 agent <c>memory</c>
/// store (ADR 0013).
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
        root.Subcommands.Add(OutlineCommand.Build());
        root.Subcommands.Add(ChangedCommand.Build());
        root.Subcommands.Add(ArchitectureCommand.Build());
        root.Subcommands.Add(PrimeCommand.Build());
        root.Subcommands.Add(MemoryCommand.Build());
        root.Subcommands.Add(StatsCommand.Build());
        root.Subcommands.Add(McpCommand.Build());

        return root;
    }
}
