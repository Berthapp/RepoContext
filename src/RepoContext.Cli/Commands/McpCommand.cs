using System.CommandLine;
using RepoContext.Cli.Mcp;

namespace RepoContext.Cli.Commands;

/// <summary>
/// The <c>repoctx mcp</c> command (M5): serves the query engine to AI agents as
/// an MCP server over stdio. Exposes <c>repoctx.search</c>,
/// <c>repoctx.get_context</c> and <c>repoctx.get_related_files</c>.
/// </summary>
public static class McpCommand
{
    public static Command Build()
    {
        var command = new Command("mcp",
            "Run the MCP server over stdio (tools: repoctx.search, repoctx.get_context, "
            + "repoctx.get_related_files). The server reads/writes JSON-RPC on stdin/stdout.");

        command.SetAction(parseResult =>
        {
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                // Stop the server gracefully instead of terminating the process.
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                return McpServerRunner.RunAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return ExitCode.Success;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        });

        return command;
    }
}
