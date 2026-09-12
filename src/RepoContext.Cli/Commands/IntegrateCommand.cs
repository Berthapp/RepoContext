using System.CommandLine;
using RepoContext.Core;
using RepoContext.Core.Configuration;
using RepoContext.Core.Guard;

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
        var guard = new Option<bool>("--guard")
        {
            Description = "Also install the opt-in read-cost guard hooks (Claude Code only). "
                          + "Observe mode unless --guard-mode says otherwise. Only RepoContext's "
                          + "own entries in " + GuardInstallation.SettingsPath + " are touched.",
        };
        var guardMode = new Option<string?>("--guard-mode")
        {
            Description = "Mode for --guard: observe (count only, the default) or enforce "
                          + "(deny an expensive read once and name the cheaper call).",
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
            guard,
            guardMode,
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

            string? requestedMode = parseResult.GetValue(guardMode);
            if (!GuardModes.TryParse(requestedMode, out GuardMode mode))
            {
                Console.Error.WriteLine("Invalid --guard-mode. Use 'off', 'observe' or 'enforce'.");
                return ExitCode.InvalidArguments;
            }

            // Naming a mode is asking for the guard; --guard alone means observe.
            bool wantGuard = parseResult.GetValue(guard) || requestedMode is not null;

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

            bool guardDrift = false;
            if (wantGuard || wantRemove)
            {
                guardDrift = ApplyGuard(
                    layout.Root, clients, mode, wantCheck, wantRemove, wantGuard);
            }

            if (wantCheck && (AgentIntegrations.HasDrift(results) || guardDrift))
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
    /// Installs, checks or removes the read-cost guard for the clients that have
    /// a verified hook contract.
    /// </summary>
    /// <remarks>
    /// Separate from the managed instruction files on purpose: those are files
    /// RepoContext owns, while a client settings file is the user's and is only
    /// ever edited entry by entry. A client without a verified hook contract
    /// keeps its existing behaviour and is told so rather than silently skipped.
    /// </remarks>
    private static bool ApplyGuard(
        string root,
        IReadOnlyList<AgentClientDefinition> clients,
        GuardMode mode,
        bool wantCheck,
        bool wantRemove,
        bool wantGuard)
    {
        bool drift = false;
        bool supported = false;
        McpLaunch launch = AgentIntegrations.DetectMcpLaunch(root);

        foreach (AgentClientDefinition definition in clients)
        {
            if (definition.Id != ClaudeCodeHook.ClientId)
            {
                if (wantGuard)
                {
                    Console.WriteLine(
                        $"  [{definition.Id}] no verified hook contract; the read-cost guard is "
                        + "not installed and this client keeps its existing behaviour.");
                }

                continue;
            }

            supported = true;
            GuardInstallResult result = wantRemove
                ? GuardInstallation.Remove(root)
                : wantCheck
                    ? GuardInstallation.Check(root, mode, launch)
                    : GuardInstallation.Apply(root, mode, launch);

            if (result.Error is { } error)
            {
                Console.Error.WriteLine($"  [{definition.Id}] {error}");
                continue;
            }

            drift |= wantCheck && result.Change is AgentFileChange.Created or AgentFileChange.Updated;
            string verb = result.Change switch
            {
                AgentFileChange.Created => wantCheck ? "would be created" : "created",
                AgentFileChange.Updated => wantCheck ? "would be updated" : "updated",
                AgentFileChange.Unchanged => "unchanged",
                AgentFileChange.Removed => "guard hooks removed from",
                AgentFileChange.Absent => "no guard hooks in",
                _ => "left untouched",
            };

            string suffix = wantRemove || result.Change is AgentFileChange.Unchanged
                ? string.Empty
                : $" (mode {GuardModes.Name(mode)})";
            Console.WriteLine($"  [{definition.Id}] {verb} {result.RelativePath}{suffix}");
        }

        if (wantGuard && !supported)
        {
            Console.WriteLine(
                "  no client with a verified hook contract was selected; nothing to guard.");
        }

        return drift;
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
