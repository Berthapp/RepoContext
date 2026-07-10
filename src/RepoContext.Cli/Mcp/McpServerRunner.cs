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
                + "repository index. Every result carries machine-readable reasons and the same "
                + "schema_version as the CLI's JSON output. Run 'repoctx index' to build or update "
                + "the index. All tools are read-only and never leave the machine.",
            ToolCollection = McpTools.Build(),
        };

        await using var transport = new StdioServerTransport("repoctx");
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCode.Success;
    }
}
