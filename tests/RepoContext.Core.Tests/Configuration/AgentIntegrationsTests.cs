using System.Text.Json;
using RepoContext.Core.Configuration;
using RepoContext.Core.Indexing;

namespace RepoContext.Core.Tests.Configuration;

/// <summary>
/// Tests for the environment-aware client integrations (ADR 0018), including the
/// token budget that makes the always-loaded instruction cost a reviewable
/// number rather than a claim.
/// </summary>
public sealed class AgentIntegrationsTests : IDisposable
{
    /// <summary>
    /// The ceiling for text that is loaded into every prompt in the repository.
    /// It is deliberately tight: anything that does not help an agent decide to
    /// use the tool belongs in the on-demand playbook instead.
    /// </summary>
    private const int PointerTokenBudget = 150;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "repoctx-integrations", Guid.NewGuid().ToString("N"));

    public AgentIntegrationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PointerBlock_StaysWithinItsAlwaysLoadedTokenBudget()
    {
        int pointer = Tokens.Count(AgentInstructions.PointerBlock);

        Assert.True(
            pointer <= PointerTokenBudget,
            $"the always-loaded pointer costs {pointer} tokens, budget is {PointerTokenBudget}");
    }

    [Fact]
    public void PointerBlock_IsAFractionOfTheFullPlaybook()
    {
        int pointer = Tokens.Count(AgentInstructions.PointerBlock);
        int full = Tokens.Count(AgentInstructions.Block);

        // The whole point of progressive disclosure: what every prompt pays for
        // must stay far below what the task-relevant protocol costs.
        Assert.True(
            pointer * 3 < full,
            $"pointer {pointer} tokens vs full block {full} tokens — the saving has eroded");
    }

    [Fact]
    public void PointerBlock_NamesTheEntryCallAndWhereTheRestLives()
    {
        Assert.Contains("repoctx context", AgentInstructions.PointerBlock, StringComparison.Ordinal);
        Assert.Contains("repoctx guide", AgentInstructions.PointerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BothTexts_NameTheWindowsCmdShim()
    {
        // npm writes `repoctx`, `repoctx.cmd` and `repoctx.ps1` side by side;
        // PowerShell picks the `.ps1`, which a restrictive execution policy
        // refuses to load. An agent that hits that and does not know the
        // one-word fix falls back to reading files broadly — precisely the cost
        // this tool exists to remove. The pointer must carry it too: for clients
        // without on-demand loading it is the only text that ever arrives.
        Assert.Contains("repoctx.cmd", AgentInstructions.PointerBlock, StringComparison.Ordinal);
        Assert.Contains("repoctx.cmd", AgentInstructions.Playbook, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryClient_HasAUniqueIdAndAtLeastOneManagedFile()
    {
        IReadOnlyList<AgentClientDefinition> all = AgentIntegrations.All(InstructionStyle.Pointer);

        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, client =>
        {
            Assert.NotEmpty(client.Files);
            Assert.NotEmpty(client.DetectionPaths);
            Assert.All(client.Files, file => Assert.False(file.RelativePath.Contains('\\')));
        });
    }

    [Fact]
    public void McpConfig_SpawnsNodeWithTheLocalLauncher_WhenTheRepositoryPinsTheNpmPackage()
    {
        // The case the npm package is built for: an MCP client spawns the server
        // without a shell, and on Windows an npm install leaves nothing spawnable
        // on the PATH — only `repoctx`, `repoctx.cmd` and `repoctx.ps1`. `node` is
        // a real executable everywhere, so the launcher is invoked through it.
        WritePackageJson("""{ "devDependencies": { "repocontext-tool": "^0.9.1" } }""");

        string config = McpConfigOf(DetectedClient("claude-code"), ".mcp.json");

        Assert.Contains("\"command\": \"node\"", config, StringComparison.Ordinal);
        Assert.Contains(
            "\"node_modules/repocontext-tool/bin/repoctx.js\", \"mcp\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCodeMcpConfig_AnchorsTheLauncherToTheWorkspaceFolder()
    {
        // A bare relative path would depend on the working directory VS Code
        // happens to spawn the server with; ${workspaceFolder} does not.
        WritePackageJson("""{ "dependencies": { "repocontext-tool": "0.9.1" } }""");

        string config = McpConfigOf(DetectedClient("copilot"), ".vscode/mcp.json");

        Assert.Contains(
            "\"${workspaceFolder}/node_modules/repocontext-tool/bin/repoctx.js\"",
            config,
            StringComparison.Ordinal);
    }

    [Fact]
    public void McpConfig_KeepsThePathCommand_WhenNoNpmDependencyIsDeclared()
    {
        // The .NET tool and the self-contained binary are real executables, so the
        // plain command is right there — and shorter to read in a review.
        string config = McpConfigOf(DetectedClient("claude-code"), ".mcp.json");

        Assert.Contains("\"command\": \"repoctx\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", config, StringComparison.Ordinal);
    }

    [Fact]
    public void McpLaunch_FollowsAPopulatedNodeModules_WithoutAManifestEntry()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "repocontext-tool", "bin"));
        File.WriteAllText(
            Path.Combine(_root, "node_modules", "repocontext-tool", "bin", "repoctx.js"), "");

        Assert.Equal(McpLaunch.LocalNpmPackage, AgentIntegrations.DetectMcpLaunch(_root));
    }

    [Fact]
    public void McpLaunch_IgnoresAnUnrelatedOrMalformedManifest()
    {
        WritePackageJson("""{ "devDependencies": { "typescript": "^5" } }""");
        Assert.Equal(McpLaunch.PathCommand, AgentIntegrations.DetectMcpLaunch(_root));

        WritePackageJson("{ not json");
        Assert.Equal(McpLaunch.PathCommand, AgentIntegrations.DetectMcpLaunch(_root));
    }

    [Theory]
    [InlineData(McpLaunch.PathCommand)]
    [InlineData(McpLaunch.LocalNpmPackage)]
    public void EveryGeneratedMcpConfig_IsValidJson(McpLaunch launch)
    {
        // The configs are built as text so they stay reviewable in a diff, which
        // means nothing but a test stops an interpolation from emitting garbage.
        IEnumerable<string> configs = AgentIntegrations.All(InstructionStyle.Pointer, launch)
            .SelectMany(c => c.Files)
            .Where(f => f.Kind == ManagedFileKind.ClientConfig)
            .Select(f => f.Content);

        Assert.NotEmpty(configs);
        foreach (string config in configs)
        {
            using JsonDocument document = JsonDocument.Parse(config);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Detect_FallsBackToAgentsMd_WhenNoClientIsPresent()
    {
        IReadOnlyList<AgentClientDefinition> detected =
            AgentIntegrations.Detect(_root, InstructionStyle.Pointer);

        AgentClientDefinition only = Assert.Single(detected);
        Assert.Equal(AgentIntegrations.FallbackClientId, only.Id);
    }

    [Fact]
    public void Detect_FindsEveryClientWhoseMarkerIsPresent()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        Directory.CreateDirectory(Path.Combine(_root, ".cursor"));

        IReadOnlyList<string> detected = AgentIntegrations
            .Detect(_root, InstructionStyle.Pointer)
            .Select(c => c.Id)
            .ToList();

        Assert.Contains("claude-code", detected);
        Assert.Contains("cursor", detected);
        Assert.DoesNotContain("windsurf", detected);
    }

    [Fact]
    public void Apply_ClaudeCode_WritesPointerMemorySkillAndMcpRegistration()
    {
        AgentClientDefinition client = Client("claude-code");

        IReadOnlyList<IntegrationFileResult> results = AgentIntegrations.Apply(_root, client);

        Assert.All(results, r => Assert.Equal(AgentFileChange.Created, r.Change));

        // The memory file carries the pointer; the skill carries the protocol.
        string memory = Read("CLAUDE.md");
        string skill = Read(".claude/skills/repocontext/SKILL.md");
        Assert.Contains("repoctx guide", memory, StringComparison.Ordinal);
        Assert.DoesNotContain("Never pay for the same evidence twice", memory, StringComparison.Ordinal);
        Assert.Contains("Never pay for the same evidence twice", skill, StringComparison.Ordinal);
        Assert.StartsWith("---\nname: repocontext\n", skill, StringComparison.Ordinal);
        Assert.Contains("\"mcpServers\"", Read(".mcp.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_Cursor_MarksTheRuleAsNotAlwaysApplied()
    {
        AgentIntegrations.Apply(_root, Client("cursor"));

        string rule = Read(".cursor/rules/repocontext.mdc");
        Assert.Contains("alwaysApply: false", rule, StringComparison.Ordinal);
        Assert.Contains("Never pay for the same evidence twice", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        AgentClientDefinition client = Client("claude-code");
        AgentIntegrations.Apply(_root, client);
        string before = Read("CLAUDE.md");

        IReadOnlyList<IntegrationFileResult> second = AgentIntegrations.Apply(_root, client);

        Assert.Equal(before, Read("CLAUDE.md"));
        Assert.All(second, r =>
            Assert.True(r.Change is AgentFileChange.Unchanged or AgentFileChange.Skipped,
                $"{r.RelativePath} changed on a repeated apply: {r.Change}"));
    }

    [Fact]
    public void Apply_NeverModifiesAnExistingClientConfiguration()
    {
        const string mine = "{ \"mcpServers\": { \"something-else\": { \"command\": \"other\" } } }";
        File.WriteAllText(Path.Combine(_root, ".mcp.json"), mine);

        IReadOnlyList<IntegrationFileResult> results =
            AgentIntegrations.Apply(_root, Client("claude-code"));

        Assert.Equal(mine, Read(".mcp.json"));
        Assert.Equal(
            AgentFileChange.Skipped,
            results.Single(r => r.RelativePath == ".mcp.json").Change);
    }

    [Fact]
    public void Apply_PreservesTheUsersOwnInstructions()
    {
        string path = Path.Combine(_root, "CLAUDE.md");
        File.WriteAllText(path, "# House rules\n\nAlways run the linter.\n");

        AgentIntegrations.Apply(_root, Client("claude-code"));

        string content = Read("CLAUDE.md");
        Assert.StartsWith("# House rules\n\nAlways run the linter.\n", content, StringComparison.Ordinal);
        Assert.Contains("repoctx context", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReportsDrift_WithoutWritingAnything()
    {
        AgentClientDefinition client = Client("claude-code");

        IReadOnlyList<IntegrationFileResult> before = AgentIntegrations.Check(_root, client);

        Assert.True(AgentIntegrations.HasDrift(before));
        Assert.False(File.Exists(Path.Combine(_root, "CLAUDE.md")));

        AgentIntegrations.Apply(_root, client);

        Assert.False(AgentIntegrations.HasDrift(AgentIntegrations.Check(_root, client)));
    }

    [Fact]
    public void Remove_TakesTheBlockBackOut_AndKeepsSurroundingContent()
    {
        string path = Path.Combine(_root, "CLAUDE.md");
        File.WriteAllText(path, "# House rules\n\nAlways run the linter.\n");
        AgentClientDefinition client = Client("claude-code");
        AgentIntegrations.Apply(_root, client);

        AgentIntegrations.Remove(_root, client);

        Assert.Equal("# House rules\n\nAlways run the linter.\n", Read("CLAUDE.md"));
        // A file RepoContext created holds nothing else, so it goes away entirely.
        Assert.False(File.Exists(Path.Combine(_root, ".claude", "skills", "repocontext", "SKILL.md")));
    }

    [Fact]
    public void InlineStyle_PutsTheFullProtocolInTheAlwaysLoadedFile()
    {
        AgentClientDefinition inline = AgentIntegrations.Find("agents", InstructionStyle.Inline)
            ?? throw new InvalidOperationException("the agents client must exist");

        AgentIntegrations.Apply(_root, inline);

        Assert.Contains("Never pay for the same evidence twice", Read("AGENTS.md"), StringComparison.Ordinal);
    }

    private static AgentClientDefinition Client(string id) =>
        AgentIntegrations.Find(id, InstructionStyle.Pointer)
        ?? throw new InvalidOperationException($"unknown client '{id}'");

    private AgentClientDefinition DetectedClient(string id) =>
        AgentIntegrations.Find(id, InstructionStyle.Pointer, AgentIntegrations.DetectMcpLaunch(_root))
        ?? throw new InvalidOperationException($"unknown client '{id}'");

    private void WritePackageJson(string content) =>
        File.WriteAllText(Path.Combine(_root, "package.json"), content);

    private static string McpConfigOf(AgentClientDefinition client, string relativePath) =>
        client.Files.Single(f => f.RelativePath == relativePath).Content;

    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
