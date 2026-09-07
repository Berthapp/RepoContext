using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RepoContext.Integration.Tests;

public class FreshContextTests
{
    [Fact]
    public void Cli_EnsureFresh_CreatesAndRefreshesIndex()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init", "--no-agents").ExitCode);
        File.WriteAllText(ws.PathOf("src/auth/session.ts"), "export function createSession() { return 'OLD_VALUE'; }");
        string[] args = ["context", "createSession", "--ensure-fresh", "--path", "src/auth/session.ts",
            "--detail", "slices", "--response-budget-tokens", "2000", "--format", "json"];
        CliResult first = ws.Run(args);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("OLD_VALUE", first.StdOut);
        File.WriteAllText(ws.PathOf("src/auth/session.ts"), "export function createSession() { return 'NEW_VALUE'; }");
        CliResult next = ws.Run(args);
        Assert.Equal(0, next.ExitCode);
        Assert.Contains("NEW_VALUE", next.StdOut);
        Assert.DoesNotContain("OLD_VALUE", next.StdOut);
    }

    [Fact]
    public async Task Mcp_EnsureFresh_RefreshesWithoutShell()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        Assert.Equal(0, ws.Run("init", "--no-agents").ExitCode);
        string file = ws.PathOf("src/auth/session.ts");
        File.WriteAllText(file, "export function createSession() { return 'MCP_OLD'; }");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        { Name = "repoctx", Command = "dotnet", Arguments = [CliHarness.CliDllPath, "mcp"], WorkingDirectory = ws.Root });
        await using McpClient client = await McpClient.CreateAsync(transport);
        var args = new Dictionary<string, object?>
        {
            ["task"] = "createSession", ["detail"] = "slices", ["ensureFresh"] = true,
            ["path"] = new[] { "src/auth/session.ts" }, ["responseBudgetTokens"] = 2000,
        };
        CallToolResult first = await client.CallToolAsync("repoctx.get_context", args);
        Assert.True(first.IsError is not true);
        Assert.Contains("MCP_OLD", Text(first));
        File.WriteAllText(file, "export function createSession() { return 'MCP_NEW'; }");
        CallToolResult next = await client.CallToolAsync("repoctx.get_context", args);
        Assert.True(next.IsError is not true);
        Assert.Contains("MCP_NEW", Text(next));
        Assert.DoesNotContain("MCP_OLD", Text(next));
    }
    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(x => x.Text));
}
