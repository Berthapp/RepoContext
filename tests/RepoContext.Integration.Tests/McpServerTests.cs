using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RepoContext.Core.Indexing;
using RepoContext.Core.Storage;

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
    public async Task ListTools_ExposesSevenNonDestructiveInstrumentedTools()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        IList<McpClientTool> tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(7, tools.Count);
        Assert.Contains("repoctx.search", names);
        Assert.Contains("repoctx.get_context", names);
        Assert.Contains("repoctx.get_related_files", names);
        Assert.Contains("repoctx.get_outline", names);
        Assert.Contains("repoctx.get_changes", names);
        Assert.Contains("repoctx.memory_add", names);
        Assert.Contains("repoctx.memory_search", names);
        Assert.All(tools, tool =>
        {
            ToolAnnotations annotations = Assert.IsType<ToolAnnotations>(tool.ProtocolTool.Annotations);
            Assert.Equal(false, annotations.ReadOnlyHint);
            Assert.Equal(false, annotations.IdempotentHint);
            Assert.Equal(false, annotations.DestructiveHint);
        });
    }

    [Fact]
    public async Task GetOutline_ReturnsTheFileSkeleton()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_outline",
            new Dictionary<string, object?> { ["file"] = "src/auth/login.ts" });

        Assert.True(result.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(result));
        Assert.Equal("outline", doc.RootElement.GetProperty("command").GetString());
        Assert.Contains(doc.RootElement.GetProperty("symbols").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "loginUser");
    }

    [Fact]
    public async Task GetOutline_WithoutSymbols_RecordsNoReplacement()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_outline",
            new Dictionary<string, object?> { ["file"] = "docs/architecture.md" });

        Assert.True(result.IsError is not true);
        using JsonDocument response = JsonDocument.Parse(TextOf(result));
        Assert.Empty(response.RootElement.GetProperty("symbols").EnumerateArray());

        string line = Assert.Single(File.ReadAllLines(ws.PathOf(".repoctx/stats.jsonl")));
        using JsonDocument record = JsonDocument.Parse(line);
        Assert.Equal("mcp", record.RootElement.GetProperty("source").GetString());
        Assert.Equal(0, record.RootElement.GetProperty("replaced").GetInt32());
    }

    [Fact]
    public async Task GetChanges_ReportsAModifiedFile()
    {
        using FixtureWorkspace ws = Indexed();
        string session = Path.Combine(ws.Root, "src/auth/session.ts");
        File.WriteAllText(session, File.ReadAllText(session) + "\n// edited\n");

        await using McpClient client = await ConnectAsync(ws);
        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_changes", new Dictionary<string, object?>());

        Assert.True(result.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(result));
        Assert.True(doc.RootElement.GetProperty("stale").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("changed").EnumerateArray(),
            c => c.GetProperty("path").GetString() == "src/auth/session.ts");
    }

    [Fact]
    public async Task GetContext_WithKnownHash_IsAcknowledgedAsReused()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult first = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?> { ["task"] = "change the login logic" });
        using JsonDocument firstDoc = JsonDocument.Parse(TextOf(first));
        JsonElement login = firstDoc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        string hash = login.GetProperty("hash").GetString()!;
        Assert.NotEmpty(File.ReadAllText(ws.PathOf("src/auth/login.ts")));

        CallToolResult second = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "change the login logic",
                ["detail"] = "slices",
                ["known"] = new[] { $"src/auth/login.ts@{hash}" },
            });

        Assert.True(second.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(second));

        // v3: the full-file claim is acknowledged in `reused` and no longer
        // occupies a result slot, matching the CLI exactly.
        Assert.DoesNotContain(
            doc.RootElement.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "src/auth/login.ts");
        Assert.Equal(1, doc.RootElement.GetProperty("reused_count").GetInt32());
        Assert.Contains(
            doc.RootElement.GetProperty("reused").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "src/auth/login.ts");
    }

    /// <summary>
    /// The MCP surface honours per-unit receipts exactly as the CLI does — same
    /// core engine, same packing, same reuse semantics.
    /// </summary>
    [Fact]
    public async Task GetContext_WithSeenReceipt_SuppressesOnlyThatUnit()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult first = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "change the login logic",
                ["detail"] = "slices",
            });
        using JsonDocument firstDoc = JsonDocument.Parse(TextOf(first));
        JsonElement span = firstDoc.RootElement.GetProperty("results")[0].GetProperty("spans")[0];
        string receipt = span.GetProperty("receipt").GetString()!;

        CallToolResult second = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "change the login logic",
                ["detail"] = "slices",
                ["seen"] = new[] { receipt },
            });

        Assert.True(second.IsError is not true);
        using JsonDocument doc = JsonDocument.Parse(TextOf(second));
        Assert.Equal(1, doc.RootElement.GetProperty("reused_count").GetInt32());
        Assert.Contains(
            doc.RootElement.GetProperty("reused").EnumerateArray(),
            r => r.GetProperty("receipt").GetString() == receipt);
    }

    /// <summary>
    /// A response budget too small for the smallest useful payload is a tool
    /// error carrying <c>retry_budget_tokens</c>, never a truncated result.
    /// </summary>
    [Fact]
    public async Task GetContext_TooSmallResponseBudget_IsAToolErrorWithAMinimum()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "change the login logic",
                ["detail"] = "slices",
                ["responseBudgetTokens"] = 40,
            });

        Assert.True(result.IsError);
        Assert.Contains("retry_budget_tokens=", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContext_AcceptedResponseBudgetMatchesExactMcpContent()
    {
        using FixtureWorkspace ws = Indexed();
        await using McpClient client = await ConnectAsync(ws);
        const int budget = 900;

        CallToolResult result = await client.CallToolAsync(
            "repoctx.get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "change the login logic",
                ["detail"] = "slices",
                ["responseBudgetTokens"] = budget,
            });

        Assert.True(result.IsError is not true);
        string text = TextOf(result);
        Assert.True(Tokens.Count(text) <= budget);
        using JsonDocument document = JsonDocument.Parse(text);
        Assert.Equal(
            budget,
            document.RootElement.GetProperty("budgets").GetProperty("response_tokens").GetInt32());
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
        Assert.Equal(Core.RepoContextInfo.SchemaVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("search", doc.RootElement.GetProperty("command").GetString());
        JsonElement results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);
        Assert.True(results[0].GetProperty("reasons").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Search_RejectsAStaleAnalysisProducer()
    {
        using FixtureWorkspace ws = Indexed();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={ws.PathOf(".repoctx/index.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE meta SET value = 'stale-producer' WHERE key = $key";
            command.Parameters.AddWithValue("$key", MetaKeys.AnalysisProducerVersion);
            command.ExecuteNonQuery();
        }

        await using McpClient client = await ConnectAsync(ws);
        CallToolResult result = await client.CallToolAsync(
            "repoctx.search",
            new Dictionary<string, object?> { ["query"] = "login" });

        Assert.True(result.IsError);
        Assert.Contains("analysis version", TextOf(result), StringComparison.OrdinalIgnoreCase);
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
