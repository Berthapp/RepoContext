using System.CommandLine;
using RepoContext.Core.Configuration;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx guide</c> command: prints the full RepoContext usage protocol
/// (ADR 0018).
/// </summary>
/// <remarks>
/// This is the on-demand half of progressive disclosure. Agent-instruction files
/// carry a short pointer that is loaded into every prompt; the protocol itself is
/// fetched only by an agent that has decided to use the tool. Clients with a
/// native skill/rule mechanism get the same text as a file and never need this
/// command; clients without one pay for the protocol once per task instead of
/// once per prompt.
///
/// The output is a constant — no repository state, no timestamps — so it is
/// byte-identical across runs and safe to keep behind a prompt-cache breakpoint.
/// </remarks>
public static class GuideCommand
{
    public static Command Build()
    {
        var command = new Command("guide",
            "Print the full RepoContext usage protocol for AI coding agents.");

        command.SetAction(_ =>
        {
            CommandSupport.WriteRendered(AgentInstructions.Playbook);
            return ExitCode.Success;
        });

        return command;
    }
}
