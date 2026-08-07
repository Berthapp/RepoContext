using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Scanning;

namespace RepoContext.Cli.Commands;

/// <summary>The <c>repoctx init</c> command (spec F1).</summary>
public static class InitCommand
{
    public static Command Build()
    {
        var force = new Option<bool>("--force")
        {
            Description = "Overwrite an existing configuration.",
        };
        var agents = new Option<bool>("--agents")
        {
            Description = "Add RepoContext usage instructions to CLAUDE.md and AGENTS.md.",
        };
        var noAgents = new Option<bool>("--no-agents")
        {
            Description = "Skip the CLAUDE.md / AGENTS.md prompt and leave those files untouched.",
        };

        var command = new Command("init", "Initialize a RepoContext index in the current repository.")
        {
            force,
            agents,
            noAgents,
        };

        command.SetAction(parseResult =>
        {
            bool wantAgents = parseResult.GetValue(agents);
            bool skipAgents = parseResult.GetValue(noAgents);
            if (wantAgents && skipAgents)
            {
                Console.Error.WriteLine("Use either --agents or --no-agents, not both.");
                return ExitCode.InvalidArguments;
            }

            // --agents / --no-agents win; otherwise ask on a real terminal, and
            // stay non-interactive (skip) for pipes, CI and tests.
            bool writeAgents = wantAgents || (!skipAgents && PromptForAgents());

            RepoLayout layout = RepoLayout.For(Directory.GetCurrentDirectory());
            try
            {
                InitResult result = Initializer.Initialize(layout, parseResult.GetValue(force), writeAgents);
                string what = result.ConfigOverwritten ? "Reinitialized" : "Initialized";
                Console.WriteLine($"{what} RepoContext in {layout.Root}");
                Console.WriteLine($"  wrote {RepoContextInfo.ConfigFileName}");
                if (result.GitignoreUpdated)
                {
                    Console.WriteLine("  updated .gitignore (.repoctx/)");
                }

                Console.WriteLine(
                    $"  scope: the whole repository, {result.ScannedFiles} file(s) selected");
                WriteProjects(result.Projects);

                foreach (AgentFileResult agentFile in result.AgentFiles)
                {
                    string verb = agentFile.Change switch
                    {
                        AgentFileChange.Created => "created",
                        AgentFileChange.Updated => "updated",
                        _ => "unchanged",
                    };
                    Console.WriteLine($"  {verb} {agentFile.FileName}");
                }

                if (result.AgentFiles.Count == 0 && !skipAgents)
                {
                    Console.WriteLine(
                        "Tip: pass --agents to add RepoContext usage to CLAUDE.md and AGENTS.md.");
                }
                else if (result.AgentFiles.Count > 0)
                {
                    Console.WriteLine(
                        "Tip: run 'repoctx integrate' to wire up the coding agents this repository "
                        + "uses (skills, project rules, MCP registration).");
                }

                Console.WriteLine("Next: run 'repoctx index'.");
                return ExitCode.Success;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }
        });

        return command;
    }

    /// <summary>
    /// Lists the detected project roots. Long lists are truncated because the
    /// message only has to confirm that nested projects are covered.
    /// </summary>
    private static void WriteProjects(IReadOnlyList<DetectedProject> projects)
    {
        if (projects.Count == 0)
        {
            return;
        }

        const int limit = 10;
        Console.WriteLine($"  projects: {projects.Count} detected");
        foreach (DetectedProject project in projects.Take(limit))
        {
            Console.WriteLine($"    {project.Path} ({project.Ecosystem}, {project.Marker})");
        }

        if (projects.Count > limit)
        {
            Console.WriteLine($"    ... and {projects.Count - limit} more");
        }
    }

    /// <summary>
    /// Asks whether to write the agent-instruction files. Only prompts on an
    /// interactive terminal; returns false when stdin/stdout are redirected so
    /// scripted and CI runs stay non-interactive.
    /// </summary>
    private static bool PromptForAgents()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        Console.Write("Add RepoContext usage instructions to CLAUDE.md and AGENTS.md? [y/N] ");
        string? answer = Console.ReadLine()?.Trim();
        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
