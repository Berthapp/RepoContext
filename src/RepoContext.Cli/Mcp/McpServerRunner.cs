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
    /// Model-visible workflow guidance. Public so the offline evaluation harness
    /// measures the exact production instructions instead of a hand-written
    /// substitute that can drift.
    /// </summary>
    public const string Instructions =
        "Start with repoctx.get_context(detail='slices', responseBudgetTokens=2000); "
        + "use outline for breadth or paths for locations. Escalate only on a gap: search "
        + "for missing files/symbols, get_outline for a missing symbol in a known file, "
        + "get_related_files for dependencies/impact. Reuse receipts via seen or session. "
        + "Set known=path@hash only after a full-file read. stripComments is lossy. "
        + "get_context already recalls matching memories; call memory_search only for a "
        + "concrete prior-knowledge gap, memory_add only for durable findings. After edits "
        + "call get_changes(patch=true), then run repoctx index if stale. Stop when evidence "
        + "is sufficient. All processing is local, offline, deterministic.";

    /// <summary>
    /// Runs the server until the client closes stdin or cancellation is
    /// requested. Nothing is written to stdout except MCP protocol messages.
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "repoctx", Version = CliInfo.Version },
            ServerInstructions = Instructions,
            ToolCollection = McpTools.Build(),
        };

        await using var transport = new StdioServerTransport("repoctx");
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCode.Success;
    }
}
