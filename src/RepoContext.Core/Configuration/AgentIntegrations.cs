namespace RepoContext.Core.Configuration;

/// <summary>How a managed file is maintained.</summary>
public enum ManagedFileKind
{
    /// <summary>
    /// Markdown carrying a marker-delimited RepoContext block. Created if absent,
    /// the block replaced in place if present; content outside the markers is
    /// never touched.
    /// </summary>
    Instructions,

    /// <summary>
    /// A client configuration file RepoContext does not own (MCP registrations).
    /// Written only when absent; an existing file is left untouched, because it
    /// may register other servers whose shape RepoContext must not guess at.
    /// </summary>
    ClientConfig,
}

/// <summary>One file a client integration maintains.</summary>
/// <param name="RelativePath">Repository-relative, always with <c>/</c> separators.</param>
/// <param name="Kind">How the file is maintained.</param>
/// <param name="Content">The managed block, or the whole file for a client config.</param>
/// <param name="Preamble">Written above the block when the file is created (front matter).</param>
public sealed record ManagedFile(
    string RelativePath, ManagedFileKind Kind, string Content, string? Preamble = null);

/// <summary>A coding environment RepoContext can wire itself into.</summary>
public sealed record AgentClientDefinition
{
    /// <summary>Stable identifier used by <c>--client</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for command output.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The files this integration maintains, in write order.</summary>
    public required IReadOnlyList<ManagedFile> Files { get; init; }

    /// <summary>
    /// Repository-relative paths whose presence means this environment is in use.
    /// Any one of them is enough.
    /// </summary>
    public required IReadOnlyList<string> DetectionPaths { get; init; }

    /// <summary>One line explaining what the integration does, for <c>--list</c>.</summary>
    public required string Summary { get; init; }
}

/// <summary>What happened to one managed file.</summary>
public sealed record IntegrationFileResult(string ClientId, string RelativePath, AgentFileChange Change);

/// <summary>How much instruction text goes into the always-loaded files.</summary>
public enum InstructionStyle
{
    /// <summary>
    /// The short pointer; the full protocol arrives on demand (a skill or rule
    /// file, or <c>repoctx guide</c>). The default, and the cheap one.
    /// </summary>
    Pointer,

    /// <summary>The full protocol inline, for people who prefer it in the file.</summary>
    Inline,
}

/// <summary>
/// The catalog of coding environments RepoContext integrates with, and the
/// idempotent apply/check/remove operations over their managed files.
/// </summary>
/// <remarks>
/// <para>
/// The integration exists to make RepoContext cheap to use, so it is shaped by
/// where each client loads instructions from. Claude Code, Cursor and Windsurf
/// load a skill/rule file only when its description matches the task; those
/// clients get the short pointer in their always-loaded memory file and the full
/// protocol in the on-demand file, which is where nearly all of the text lives.
/// Copilot's repository instructions and a plain <c>AGENTS.md</c> have no such
/// mechanism, so they get the pointer plus a documented way to fetch the rest
/// (<c>repoctx guide</c>) rather than several hundred tokens on every prompt.
/// </para>
/// <para>
/// Nothing here writes outside the repository root: a machine-global client
/// configuration is the user's, not a repository's, so integrations that would
/// need one (Windsurf's MCP registration) simply do not register MCP.
/// </para>
/// </remarks>
public static class AgentIntegrations
{
    /// <summary>The MCP server name registered in generated client configuration.</summary>
    public const string McpServerName = "repoctx";

    /// <summary>
    /// The client used when nothing is detected: every agent that reads
    /// <c>AGENTS.md</c>, which is the broadest convention available.
    /// </summary>
    public const string FallbackClientId = "agents";

    /// <summary>The catalog for an instruction style, ordered by id.</summary>
    public static IReadOnlyList<AgentClientDefinition> All(InstructionStyle style) =>
    [
        Agents(style),
        ClaudeCode(style),
        Copilot(style),
        Cursor(),
        Windsurf(),
    ];

    /// <summary>Every client id, ordered.</summary>
    public static IReadOnlyList<string> Ids { get; } =
        [.. All(InstructionStyle.Pointer).Select(c => c.Id)];

    /// <summary>Looks a client up by id (case-insensitive).</summary>
    public static AgentClientDefinition? Find(string id, InstructionStyle style) =>
        All(style).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The clients whose environment is present in <paramref name="root"/>. Never
    /// empty: a repository with no recognizable client still gets
    /// <see cref="FallbackClientId"/>, because an unwired agent is the case the
    /// integration exists for.
    /// </summary>
    public static IReadOnlyList<AgentClientDefinition> Detect(string root, InstructionStyle style)
    {
        List<AgentClientDefinition> detected =
            [.. All(style).Where(client => client.DetectionPaths.Any(path => Exists(root, path)))];

        if (detected.Count > 0)
        {
            return detected;
        }

        return [All(style).First(c => c.Id == FallbackClientId)];
    }

    /// <summary>Applies one client's integration, creating or refreshing its files.</summary>
    public static IReadOnlyList<IntegrationFileResult> Apply(string root, AgentClientDefinition client)
    {
        var results = new List<IntegrationFileResult>(client.Files.Count);
        foreach (ManagedFile file in client.Files)
        {
            results.Add(new IntegrationFileResult(client.Id, file.RelativePath, file.Kind switch
            {
                ManagedFileKind.Instructions =>
                    AgentInstructions.Ensure(root, file.RelativePath, file.Content, file.Preamble).Change,
                _ => WriteConfigIfAbsent(root, file),
            }));
        }

        return results;
    }

    /// <summary>Reports what <see cref="Apply"/> would do, writing nothing.</summary>
    public static IReadOnlyList<IntegrationFileResult> Check(string root, AgentClientDefinition client)
    {
        var results = new List<IntegrationFileResult>(client.Files.Count);
        foreach (ManagedFile file in client.Files)
        {
            results.Add(new IntegrationFileResult(client.Id, file.RelativePath, file.Kind switch
            {
                ManagedFileKind.Instructions =>
                    AgentInstructions.Inspect(root, file.RelativePath, file.Content),
                _ => File.Exists(FullPath(root, file.RelativePath))
                    ? AgentFileChange.Skipped
                    : AgentFileChange.Created,
            }));
        }

        return results;
    }

    /// <summary>
    /// Removes one client's managed blocks. Client configuration files are left
    /// alone — RepoContext did not necessarily write the one that is there, and
    /// deleting a file that registers other MCP servers would be destructive.
    /// </summary>
    public static IReadOnlyList<IntegrationFileResult> Remove(string root, AgentClientDefinition client)
    {
        var results = new List<IntegrationFileResult>(client.Files.Count);
        foreach (ManagedFile file in client.Files)
        {
            results.Add(new IntegrationFileResult(client.Id, file.RelativePath,
                file.Kind == ManagedFileKind.Instructions
                    ? AgentInstructions.Remove(root, file.RelativePath).Change
                    : AgentFileChange.Skipped));
        }

        return results;
    }

    /// <summary>Whether a set of results contains a change that has not been applied yet.</summary>
    public static bool HasDrift(IEnumerable<IntegrationFileResult> results) =>
        results.Any(r => r.Change is AgentFileChange.Created or AgentFileChange.Updated);

    private static AgentFileChange WriteConfigIfAbsent(string root, ManagedFile file)
    {
        string path = FullPath(root, file.RelativePath);
        if (File.Exists(path))
        {
            return AgentFileChange.Skipped;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, file.Content);
        return AgentFileChange.Created;
    }

    private static string FullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool Exists(string root, string relativePath)
    {
        string path = FullPath(root, relativePath);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static string Instructions(InstructionStyle style) =>
        style == InstructionStyle.Inline ? AgentInstructions.Block : AgentInstructions.PointerBlock;

    // ---- client definitions -------------------------------------------------

    private static AgentClientDefinition ClaudeCode(InstructionStyle style) => new()
    {
        Id = "claude-code",
        DisplayName = "Claude Code",
        Summary = "CLAUDE.md pointer + on-demand skill + project MCP server",
        DetectionPaths = [".claude", "CLAUDE.md"],
        Files =
        [
            new ManagedFile("CLAUDE.md", ManagedFileKind.Instructions, Instructions(style)),
            new ManagedFile(
                ".claude/skills/repocontext/SKILL.md",
                ManagedFileKind.Instructions,
                AgentInstructions.Block,
                Preamble: FrontMatter(
                    "name: repocontext",
                    "description: " + SkillDescription)),
            new ManagedFile(".mcp.json", ManagedFileKind.ClientConfig, McpServersJson()),
        ],
    };

    private static AgentClientDefinition Cursor() => new()
    {
        Id = "cursor",
        DisplayName = "Cursor",
        Summary = "on-demand project rule + project MCP server",
        DetectionPaths = [".cursor", ".cursorrules"],
        Files =
        [
            new ManagedFile(
                ".cursor/rules/repocontext.mdc",
                ManagedFileKind.Instructions,
                AgentInstructions.Block,
                // alwaysApply: false is the whole point — the rule is fetched when
                // the description matches, not prepended to every request.
                Preamble: FrontMatter(
                    "description: " + SkillDescription,
                    "alwaysApply: false")),
            new ManagedFile(".cursor/mcp.json", ManagedFileKind.ClientConfig, McpServersJson()),
        ],
    };

    private static AgentClientDefinition Copilot(InstructionStyle style) => new()
    {
        Id = "copilot",
        DisplayName = "GitHub Copilot",
        Summary = "repository instructions + VS Code MCP server",
        DetectionPaths = [".github/copilot-instructions.md", ".vscode"],
        Files =
        [
            new ManagedFile(
                ".github/copilot-instructions.md", ManagedFileKind.Instructions, Instructions(style)),
            new ManagedFile(".vscode/mcp.json", ManagedFileKind.ClientConfig, VsCodeMcpJson()),
        ],
    };

    private static AgentClientDefinition Windsurf() => new()
    {
        Id = "windsurf",
        DisplayName = "Windsurf",
        Summary = "on-demand workspace rule (MCP is configured globally, outside the repository)",
        DetectionPaths = [".windsurf"],
        Files =
        [
            new ManagedFile(
                ".windsurf/rules/repocontext.md",
                ManagedFileKind.Instructions,
                AgentInstructions.Block,
                Preamble: FrontMatter(
                    "trigger: model_decision",
                    "description: " + SkillDescription)),
        ],
    };

    private static AgentClientDefinition Agents(InstructionStyle style) => new()
    {
        Id = "agents",
        DisplayName = "AGENTS.md (Codex, Jules, Amp, and other agents)",
        Summary = "AGENTS.md instructions, the broadest convention",
        DetectionPaths = ["AGENTS.md"],
        Files = [new ManagedFile("AGENTS.md", ManagedFileKind.Instructions, Instructions(style))],
    };

    /// <summary>
    /// Shared by every on-demand file. A skill or rule is loaded when its
    /// description matches the task, so this sentence is what decides whether the
    /// protocol arrives at all — it names the situations, not the tool.
    /// </summary>
    private const string SkillDescription =
        "Get repository context - relevant files, symbols, source spans, dependencies, tests - "
        + "from the local RepoContext index instead of reading files broadly. Use when locating "
        + "code, explaining or changing existing behaviour, checking what an edit impacts, or "
        + "finding the tests for a file.";

    private static string FrontMatter(params string[] lines) =>
        "---\n" + string.Join('\n', lines) + "\n---\n\n";

    /// <summary>
    /// The <c>mcpServers</c> shape used by Claude Code and Cursor. Written as text
    /// rather than serialized so the generated file is stable, commentable and
    /// reviewable in a diff.
    /// </summary>
    private static string McpServersJson() =>
        """
        {
          "mcpServers": {
            "repoctx": {
              "command": "repoctx",
              "args": ["mcp"]
            }
          }
        }

        """;

    /// <summary>VS Code (and Visual Studio) read <c>servers</c> with an explicit transport.</summary>
    private static string VsCodeMcpJson() =>
        """
        {
          "servers": {
            "repoctx": {
              "type": "stdio",
              "command": "repoctx",
              "args": ["mcp"]
            }
          }
        }

        """;
}
