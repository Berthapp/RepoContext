using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the M5 MCP server. Each test spawns the real
/// <c>repoctx mcp</c> binary and drives it with the official MCP client over
/// stdio, exactly as an AI agent would.
/// </summary>
public class McpServerTests
{
    private static FixtureWorkspace Indexed()
    {
        var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    private static Task<McpClient> ConnectAsync(FixtureWorkspace ws)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "repoctx",
            Command = "dotnet",
            Arguments = [CliHarness.CliDllPath, "mcp"],
            WorkingDirectory = ws.Root,
        });

        return McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ListTools_ExposesTheThreeReadOnlyTools()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        IList<McpClientTool> tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(3, tools.Count);
        Assert.Contains("repoctx.search", names);
        Assert.Contains("repoctx.get_context", names);
        Assert.Contains("repoctx.get_related_files", names);
    }

    [Fact]
    public async Task Search_ReturnsSchemaVersionedJsonWithReasons()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.search",
            new Dictionary<string, object?> { ["query"] = "login", ["top"] = 3 });

        Assert.True(result.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(result));
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("search", doc.RootElement.GetProperty("command").GetString());
        JsonElement results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);
        Assert.True(results[0].GetProperty("reasons").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetContext_RanksLoginFirst_WithReasons()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?> { ["task"] = "change the login logic", ["top"] = 20 });

        Assert.True(result.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(result));
        JsonElement results = doc.RootElement.GetProperty("results");
        Assert.Equal("src/auth/login.ts", results[0].GetProperty("path").GetString());
        Assert.True(results[0].GetProperty("reasons").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetRelatedFiles_ListsImportsAndTests()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_related_files",
            new Dictionary<string, object?> { ["file"] = "src/auth/login.ts" });

        Assert.True(result.IsError is not true);
        string json = TextOf(result);
        Assert.Contains("src/auth/session.ts", json);
        Assert.Contains("src/auth/__tests__/login.test.ts", json);
    }

    [Fact]
    public async Task ToolOutput_IsIdenticalToCliJson()
    {
        using FixtureWorkspace ws = Indexed();

        // The CLI harness rebuilds stdout line-by-line, so only line endings can
        // differ from the MCP text; the JSON payload must otherwise be identical.
        string cli = Normalize(ws.Run("search", "login", "--top", "3", "--format", "json").StdOut);

        await using McpClient client = await ConnectAsync(ws);
        CallToolResult result = await client.CallToolAsync(
            "repoctx.search",
            new Dictionary<string, object?> { ["query"] = "login", ["top"] = 3 });

        Assert.Equal(cli, Normalize(TextOf(result)));
    }

    [Fact]
    public async Task WithoutIndex_ToolReturnsError()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init"); // initialized but not indexed

        await using McpClient client = await ConnectAsync(ws);
        CallToolResult result = await client.CallToolAsync(
            "repoctx.search",
            new Dictionary<string, object?> { ["query"] = "login" });

        Assert.True(result.IsError);
        Assert.Contains("index", TextOf(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_WithNonPositiveTop_ReturnsError()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.search",
            new Dictionary<string, object?> { ["query"] = "login", ["top"] = 0 });

        Assert.True(result.IsError);
        Assert.Contains("top", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelatedFiles_OutsideRepository_ReturnsError()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_related_files",
            new Dictionary<string, object?> { ["file"] = "../outside.ts" });

        Assert.True(result.IsError);
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim('\n');
}
