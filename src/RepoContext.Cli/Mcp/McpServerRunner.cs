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
        "RepoContext serves compact, explainable, deterministic context from a local "
        + "repository index. Start with one repoctx.get_context call using detail='slices' "
        + "and responseBudgetTokens=2000; use 'outline' to survey more files or 'paths' "
        + "when only locations are needed. Escalate only for a concrete gap: search when "
        + "a needed file is missing, outline when a needed symbol was not delivered, and "
        + "related_files for dependency or impact questions. Echo returned evidence "
        + "receipts through seen to suppress exactly those pointers, spans, or symbols. Use known "
        + "path@hash only when you independently hold the entire file; never derive it "
        + "from a slice or outline hash. After edits call get_changes and re-index when "
        + "stale. Stop once no evidence needed for the task is missing. Processing is deterministic, "
        + "offline, and local; successful calls may append aggregate counts to the local "
        + "usage ledger.";

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
