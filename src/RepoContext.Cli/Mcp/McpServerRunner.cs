using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RepoContext.Cli.Mcp;

/// <summary>
/// Hosts the RepoContext MCP server over stdio (M5). Uses the official MCP C#
/// SDK's low-level API (no generic host / DI container) to keep the footprint
/// minimal. The stdio transport communicates over stdin/stdout only, so the
/// "no network at runtime" constraint holds.
/// </summary>
public static class McpServerRunner
{
    /// <summary>
    /// Runs the server until the client closes stdin or cancellation is
    /// requested. Nothing is written to stdout except MCP protocol messages.
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "repoctx", Version = CliInfo.Version },
            ServerInstructions =
                "RepoContext serves compact, explainable, deterministic context from a local "
                + "repository index; every token figure is a real BPE count. The economical loop: "
                + "(1) repoctx.get_context with a budgetTokens and detail='slices' for working "
                + "context, or detail='outline' to survey; pass a stable 'session' name and "
                + "delivered slices are remembered, so later calls return unchanged files as "
                + "zero-cost markers without you echoing hashes; set stripComments=true to drop "
                + "comment banners from slices (lossy); (2) repoctx.get_outline before reading "
                + "any file - a skeleton costs a fraction of the file; (3) repoctx.get_related_files "
                + "instead of searching for dependencies; (4) after editing, repoctx.get_changes "
                + "with patch=true returns just the changed hunks instead of a full re-read - when "
                + "stale, run 'repoctx index' (fast, incremental) and re-query; (5) never pay "
                + "twice: a session (above) or known=['path@hash'] makes unchanged files "
                + "zero-cost markers; (6) never re-derive: repoctx.memory_search before "
                + "exploring a topic (get_context folds matching memories in automatically), "
                + "and after completing a task repoctx.memory_add stores 1-2 distilled "
                + "sentences - kind='note' for knowledge, 'decision' for a why, 'constraint' "
                + "for a warning - linked to the files they describe, so they are stale-flagged "
                + "when the code drifts. Results are deterministic and carry machine-readable "
                + "reasons. Nothing ever leaves the machine.",
            ToolCollection = McpTools.Build(),
        };

        await using var transport = new StdioServerTransport("repoctx");
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCode.Success;
    }
}
