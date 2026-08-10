using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx integrate</c> command: wires RepoContext into the coding
/// environments a repository actually uses (ADR 0018).
/// </summary>
/// <remarks>
/// Separate from <c>init</c> on purpose. <c>init --agents</c> on an already
/// initialized repository needs <c>--force</c>, which also overwrites
/// <c>repoctx.config.json</c> — so refreshing instructions used to risk the
/// user's configuration. <c>integrate</c> never touches configuration at all:
/// it maintains marker-delimited instruction blocks and creates client MCP
/// registrations that do not exist yet.
/// </remarks>
public static class IntegrateCommand
{
    public static Command Build()
    {
        var client = new Option<string[]>("--client")
        {
            Description = "Integrate this client (repeatable): "
                          + string.Join(", ", AgentIntegrations.Ids)
                          + ". Default: every client detected in the repository.",
        };
        var check = new Option<bool>("--check")
        {
            Description = "Report what would change and write nothing. Exits 4 on drift.",
        };
        var remove = new Option<bool>("--remove")
        {
            Description = "Remove the managed blocks again, preserving surrounding content.",
        };
        var inline = new Option<bool>("--inline-playbook")
        {
            Description = "Put the full protocol in the always-loaded instruction files instead "
                          + "of the short pointer. Costs tokens on every prompt.",
        };
        var list = new Option<bool>("--list")
        {
            Description = "List the supported clients and the files each one maintains.",
        };

        var command = new Command("integrate",
            "Wire RepoContext into the coding agents this repository uses. Never touches "
            + RepoContextInfo.ConfigFileName + ".")
        {
            client,
            check,
            remove,
            inline,
            list,
        };

        command.SetAction(parseResult =>
        {
            InstructionStyle style = parseResult.GetValue(inline)
                ? InstructionStyle.Inline
                : InstructionStyle.Pointer;

            if (parseResult.GetValue(list))
            {
                WriteCatalog(style);
                return ExitCode.Success;
            }

            bool wantCheck = parseResult.GetValue(check);
            bool wantRemove = parseResult.GetValue(remove);
            if (wantCheck && wantRemove)
            {
                Console.Error.WriteLine("Use either --check or --remove, not both.");
                return ExitCode.InvalidArguments;
            }

            string current = Directory.GetCurrentDirectory();
            RepoLayout layout = RepoLayout.Discover(current) ?? RepoLayout.For(current);

            if (!TryResolveClients(parseResult.GetValue(client), style, layout.Root,
                    out IReadOnlyList<AgentClientDefinition> clients))
            {
                return ExitCode.InvalidArguments;
            }

            var results = new List<IntegrationFileResult>();
            foreach (AgentClientDefinition definition in clients)
            {
                results.AddRange(wantCheck
                    ? AgentIntegrations.Check(layout.Root, definition)
                    : wantRemove
                        ? AgentIntegrations.Remove(layout.Root, definition)
                        : AgentIntegrations.Apply(layout.Root, definition));
            }

            WriteResults(layout.Root, clients, results, wantCheck);

            if (wantCheck && AgentIntegrations.HasDrift(results))
            {
                Console.Error.WriteLine(
                    "Integration is out of date. Run 'repoctx integrate' to update it.");
                return ExitCode.Drift;
            }

            if (!wantCheck && !wantRemove && !layout.IsInitialized)
            {
                Console.WriteLine(
                    $"Note: this repository has no {RepoContextInfo.ConfigFileName} yet. "
                    + "Run 'repoctx init' and 'repoctx index' so the instructions have an index to query.");
            }

            return ExitCode.Success;
        });

        return command;
    }

    /// <summary>
    /// Resolves the requested clients, or detects them. Unknown ids fail the
    /// command rather than being skipped: a typo must not silently integrate
    /// nothing.
    /// </summary>
    private static bool TryResolveClients(
        string[]? requested,
        InstructionStyle style,
        string root,
        out IReadOnlyList<AgentClientDefinition> clients)
    {
        if (requested is not { Length: > 0 })
        {
            clients = AgentIntegrations.Detect(root, style);
            return true;
        }

        // An explicit --client must produce the same files detection would, so the
        // MCP launch shape is resolved from the repository here too.
        McpLaunch launch = AgentIntegrations.DetectMcpLaunch(root);
        var resolved = new List<AgentClientDefinition>();
        foreach (string id in requested)
        {
            AgentClientDefinition? definition = AgentIntegrations.Find(id, style, launch);
            if (definition is null)
            {
                Console.Error.WriteLine(
                    $"Unknown --client '{id}'. Known clients: {string.Join(", ", AgentIntegrations.Ids)}.");
                clients = [];
                return false;
            }

            if (!resolved.Any(c => c.Id == definition.Id))
            {
                resolved.Add(definition);
            }
        }

        clients = resolved;
        return true;
    }

    private static void WriteCatalog(InstructionStyle style)
    {
        Console.WriteLine("Supported clients:");
        foreach (AgentClientDefinition definition in AgentIntegrations.All(style))
        {
            Console.WriteLine($"  {definition.Id} — {definition.DisplayName}");
            Console.WriteLine($"    {definition.Summary}");
            Console.WriteLine($"    detected by: {string.Join(", ", definition.DetectionPaths)}");
            foreach (ManagedFile file in definition.Files)
            {
                string how = file.Kind == ManagedFileKind.Instructions
                    ? "managed block"
                    : "created only when absent";
                Console.WriteLine($"    {file.RelativePath} ({how})");
            }
        }
    }

    private static void WriteResults(
        string root,
        IReadOnlyList<AgentClientDefinition> clients,
        IReadOnlyList<IntegrationFileResult> results,
        bool checkOnly)
    {
        string verbPrefix = checkOnly ? "would be " : string.Empty;
        Console.WriteLine(
            $"{(checkOnly ? "Checked" : "Integrated")} {clients.Count} client(s) in {root}");

        foreach (IntegrationFileResult result in results)
        {
            string verb = result.Change switch
            {
                AgentFileChange.Created => verbPrefix + "created",
                AgentFileChange.Updated => verbPrefix + "updated",
                AgentFileChange.Unchanged => "unchanged",
                AgentFileChange.Removed => "removed",
                AgentFileChange.Absent => "absent",
                _ => "left untouched (not managed by repoctx)",
            };

            Console.WriteLine($"  [{result.ClientId}] {verb} {result.RelativePath}");
        }
    }
}
