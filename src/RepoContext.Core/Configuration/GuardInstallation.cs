using System.Text.Json;
using System.Text.Json.Nodes;
using RepoContext.Core.Guard;

namespace RepoContext.Core.Configuration;

/// <summary>The outcome of one guard installation operation.</summary>
/// <param name="Change">What happened, or would happen, to the settings file.</param>
/// <param name="RelativePath">The settings file, repository-relative.</param>
/// <param name="Error">Set when the file was left untouched because it could not be understood.</param>
public sealed record GuardInstallResult(
    AgentFileChange Change, string RelativePath, string? Error = null);

/// <summary>
/// Installs, inspects and removes the read-cost guard's hook entries in a
/// client's own settings file (milestone 4 of the same-quality, lower-cost plan).
/// </summary>
/// <remarks>
/// <para>
/// The settings file belongs to the user, not to RepoContext. Everything here is
/// surgical: only hook entries whose command is recognizably this tool's are
/// added, updated or removed, and every other hook, permission, environment
/// variable and MCP server in the file is written back exactly as it was found.
/// A file that cannot be parsed is reported and left alone — rewriting a
/// malformed settings file would be the fastest way to destroy a configuration
/// nobody asked us to touch.
/// </para>
/// <para>
/// Installation is opt-in and idempotent: running it twice changes nothing the
/// second time, and removing what was never installed is not an error.
/// </para>
/// <para>
/// The entries are written in shell form (no <c>args</c>), which Claude Code runs
/// through <c>sh</c> on Unix and PowerShell on Windows. That is deliberate: on a
/// Windows npm install <c>repoctx</c> is a <c>.cmd</c> shim, which a shell can
/// launch and a bare process spawn cannot.
/// </para>
/// </remarks>
public static class GuardInstallation
{
    /// <summary>The client settings file the guard installs into.</summary>
    public const string SettingsPath = ".claude/settings.json";

    /// <summary>Seconds the client waits for one guard evaluation.</summary>
    private const int HookTimeoutSeconds = 5;

    private const string NpmLauncherPath = "node_modules/repocontext-tool/bin/repoctx.js";

    /// <summary>The hook events the guard subscribes to, in write order.</summary>
    private static readonly (string Event, string? Matcher)[] Subscriptions =
    [
        // Tool events match on tool name; only these two can carry a file read.
        ("PreToolUse", "Read|Bash"),
        // Lifecycle events have their own matcher vocabulary; the guard reacts to
        // every value the same way, so it registers without one.
        ("SessionStart", null),
        ("PreCompact", null),
        ("SessionEnd", null),
        ("SubagentStart", null),
    ];

    /// <summary>The command a guard hook entry runs.</summary>
    public static string CommandFor(GuardMode mode, McpLaunch launch)
    {
        string launcher = launch == McpLaunch.LocalNpmPackage
            ? "node \"${CLAUDE_PROJECT_DIR}/" + NpmLauncherPath + "\""
            : "repoctx";
        return $"{launcher} guard hook --client {ClaudeCodeHook.ClientId} "
               + $"--mode {GuardModes.Name(mode)}";
    }

    /// <summary>Whether a command string is one of ours.</summary>
    /// <remarks>
    /// Ownership is decided by the command text because that is the only field a
    /// hook entry is guaranteed to carry. An extra marker key would be neater and
    /// would also be an unknown field in someone else's schema.
    /// </remarks>
    public static bool IsOwned(string? command) =>
        command is not null && Enum.GetValues<GuardMode>().Any(mode =>
            Enum.GetValues<McpLaunch>().Any(launch =>
                string.Equals(command, CommandFor(mode, launch), StringComparison.Ordinal))
            || string.Equals(command, $"repoctx guard hook --mode {GuardModes.Name(mode)}",
                StringComparison.Ordinal));

    /// <summary>Whether a repository has a guard hook installed, and in which mode.</summary>
    public static bool IsInstalled(string root, out string? mode)
    {
        mode = null;
        JsonObject? settings = TryRead(FullPath(root), out _);
        if (settings is null)
        {
            return false;
        }

        foreach (JsonObject entry in OwnedEntries(settings))
        {
            string command = CommandText(entry) ?? string.Empty;
            mode = ModeIn(command);
            return true;
        }

        return false;
    }

    /// <summary>Reports what <see cref="Apply"/> would do, writing nothing.</summary>
    public static GuardInstallResult Check(string root, GuardMode mode, McpLaunch launch) =>
        Apply(root, mode, launch, write: false);

    /// <summary>Installs or refreshes the guard hooks.</summary>
    public static GuardInstallResult Apply(
        string root, GuardMode mode, McpLaunch launch, bool write = true)
    {
        string path = FullPath(root);
        bool existed = File.Exists(path);
        JsonObject? settings = TryRead(path, out string? error);
        if (error is not null)
        {
            return new GuardInstallResult(AgentFileChange.Skipped, SettingsPath, error);
        }

        settings ??= [];
        string before = settings.ToJsonString(Writer);
        string command = CommandFor(mode, launch);

        JsonObject hooks;
        switch (settings["hooks"])
        {
            case JsonObject existing:
                hooks = existing;
                break;
            case null:
                hooks = [];
                settings["hooks"] = hooks;
                break;
            default:
                return new GuardInstallResult(
                    AgentFileChange.Skipped, SettingsPath,
                    "'hooks' in .claude/settings.json is not an object; left untouched.");
        }

        foreach ((string eventName, string? matcher) in Subscriptions)
        {
            JsonArray groups;
            switch (hooks[eventName])
            {
                case JsonArray existing:
                    groups = existing;
                    break;
                case null:
                    groups = [];
                    hooks[eventName] = groups;
                    break;
                default:
                    return new GuardInstallResult(
                        AgentFileChange.Skipped, SettingsPath,
                        $"'hooks.{eventName}' in .claude/settings.json is not an array; "
                        + "left untouched.");
            }

            Upsert(groups, matcher, command);
        }

        string after = settings.ToJsonString(Writer);
        if (existed && string.Equals(before, after, StringComparison.Ordinal))
        {
            return new GuardInstallResult(AgentFileChange.Unchanged, SettingsPath);
        }

        if (write)
        {
            Write(root, path, settings);
        }

        return new GuardInstallResult(
            existed ? AgentFileChange.Updated : AgentFileChange.Created, SettingsPath);
    }

    /// <summary>Removes the guard hooks, leaving every other entry in place.</summary>
    public static GuardInstallResult Remove(string root, bool write = true)
    {
        string path = FullPath(root);
        if (!File.Exists(path))
        {
            return new GuardInstallResult(AgentFileChange.Absent, SettingsPath);
        }

        JsonObject? settings = TryRead(path, out string? error);
        if (error is not null || settings is null)
        {
            return new GuardInstallResult(AgentFileChange.Skipped, SettingsPath, error);
        }

        if (settings["hooks"] is not JsonObject hooks)
        {
            return new GuardInstallResult(AgentFileChange.Absent, SettingsPath);
        }

        string before = settings.ToJsonString(Writer);
        foreach (string eventName in hooks.Select(pair => pair.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            bool removedFromEvent = false;
            for (int g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray entries)
                {
                    continue;
                }

                bool removedFromGroup = false;
                for (int h = entries.Count - 1; h >= 0; h--)
                {
                    if (IsOwned(CommandText(entries[h])))
                    {
                        entries.RemoveAt(h);
                        removedFromGroup = true;
                        removedFromEvent = true;
                    }
                }

                // A group we emptied was ours; one the user emptied is theirs.
                if (removedFromGroup && entries.Count == 0 && group.Count <= 2)
                {
                    groups.RemoveAt(g);
                }
            }

            if (removedFromEvent && groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        if (hooks.Count == 0 && !string.Equals(before, settings.ToJsonString(Writer), StringComparison.Ordinal))
        {
            settings.Remove("hooks");
        }

        if (string.Equals(before, settings.ToJsonString(Writer), StringComparison.Ordinal))
        {
            return new GuardInstallResult(AgentFileChange.Absent, SettingsPath);
        }

        if (write)
        {
            Write(root, path, settings);
        }

        return new GuardInstallResult(AgentFileChange.Removed, SettingsPath);
    }

    private static void Upsert(JsonArray groups, string? matcher, string command)
    {
        // A matcher belongs to the entire group. Moving only our command out
        // of a mismatching group must not widen a neighbouring user's hook.
        bool updated = false;
        for (int g = groups.Count - 1; g >= 0; g--)
        {
            if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray entries)
            {
                continue;
            }

            bool removed = false;
            for (int h = entries.Count - 1; h >= 0; h--)
            {
                if (entries[h] is not JsonObject owned || !IsOwned(CommandText(owned)))
                {
                    continue;
                }

                string? existingMatcher = StringValue(group["matcher"]);
                if (!updated && existingMatcher == matcher)
                {
                    owned["command"] = command;
                    owned["type"] = "command";
                    owned["timeout"] = HookTimeoutSeconds;
                    updated = true;
                }
                else
                {
                    entries.RemoveAt(h);
                    removed = true;
                }
            }

            if (removed && entries.Count == 0 && group.Count <= 2)
            {
                groups.RemoveAt(g);
            }
        }

        if (updated)
        {
            return;
        }

        // Written matcher-first so the generated block reads the way the
        // client's own documentation writes it.
        var created = new JsonObject();
        if (matcher is not null)
        {
            created["matcher"] = matcher;
        }

        created["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = HookTimeoutSeconds,
        });
        groups.Add(created);
    }

    private static IEnumerable<JsonObject> OwnedEntries(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            yield break;
        }

        foreach (KeyValuePair<string, JsonNode?> pair in hooks)
        {
            if (pair.Value is not JsonArray groups)
            {
                continue;
            }

            foreach (JsonNode? node in groups)
            {
                if (node is not JsonObject group || group["hooks"] is not JsonArray entries)
                {
                    continue;
                }

                foreach (JsonNode? entry in entries)
                {
                    if (entry is JsonObject owned
                        && IsOwned(CommandText(owned)))
                    {
                        yield return owned;
                    }
                }
            }
        }
    }

    private static string? StringValue(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out string? text) ? text : null;

    private static string? CommandText(JsonNode? entry) =>
        entry is JsonObject obj ? StringValue(obj["command"]) : null;

    private static string? ModeIn(string command)
    {
        const string flag = "--mode ";
        int at = command.IndexOf(flag, StringComparison.Ordinal);
        if (at < 0)
        {
            return GuardModes.DefaultName;
        }

        string rest = command[(at + flag.Length)..];
        int end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    private static JsonObject? TryRead(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            if (node is JsonObject settings)
            {
                return settings;
            }

            error = ".claude/settings.json is not a JSON object; left untouched.";
            return null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ".claude/settings.json could not be read as JSON; left untouched.";
            return null;
        }
    }

    private static void Write(string root, string path, JsonObject settings)
    {
        // A `.claude` committed as a link into the user's home would otherwise
        // install hooks into their global settings.
        SafePaths.EnsureContained(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, settings.ToJsonString(Writer) + "\n");
    }

    private static string FullPath(string root) =>
        Path.Combine(root, SettingsPath.Replace('/', Path.DirectorySeparatorChar));

    private static readonly JsonSerializerOptions Writer = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
    };
}
