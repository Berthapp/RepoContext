using System.Text.Json;
using RepoContext.Core.Configuration;
using RepoContext.Core.Guard;

namespace RepoContext.Core.Tests.Configuration;

/// <summary>
/// Installing the guard into a settings file RepoContext does not own (ADR 0023).
/// </summary>
public class GuardInstallationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoctx-guard-install-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SettingsFile => Path.Combine(_root, ".claude", "settings.json");

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, json);
    }

    private JsonElement ReadSettings() =>
        JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.Clone();

    [Fact]
    public void Apply_CreatesTheSettingsFileWhenThereIsNone()
    {
        GuardInstallResult result = GuardInstallation.Apply(
            _root, GuardMode.Observe, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Created, result.Change);
        Assert.Null(result.Error);
        JsonElement hooks = ReadSettings().GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("PreToolUse", out _));
        Assert.True(hooks.TryGetProperty("SessionStart", out _));
        Assert.True(hooks.TryGetProperty("PreCompact", out _));
        Assert.True(hooks.TryGetProperty("SessionEnd", out _));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        GuardInstallation.Apply(_root, GuardMode.Observe, McpLaunch.PathCommand);
        string first = File.ReadAllText(SettingsFile);

        GuardInstallResult second = GuardInstallation.Apply(
            _root, GuardMode.Observe, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Unchanged, second.Change);
        Assert.Equal(first, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Apply_UpdatesOnlyTheModeOnReinstall()
    {
        GuardInstallation.Apply(_root, GuardMode.Observe, McpLaunch.PathCommand);

        GuardInstallResult updated = GuardInstallation.Apply(
            _root, GuardMode.Enforce, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Updated, updated.Change);
        Assert.True(GuardInstallation.IsInstalled(_root, out string? mode));
        Assert.Equal("enforce", mode);
        // Still exactly one entry per event, not a second copy.
        Assert.Equal(1, ReadSettings().GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Sum(g => g.GetProperty("hooks").GetArrayLength()));
    }

    [Fact]
    public void Apply_PreservesEverythingItDoesNotOwn()
    {
        WriteSettings("""
            {
              "permissions": { "allow": ["Bash(git status)"], "deny": ["Read(./.env)"] },
              "env": { "MY_VAR": "1" },
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Edit", "hooks": [{ "type": "command", "command": "my-formatter" }] }
                ],
                "PreToolUse": [
                  { "matcher": "Bash", "hooks": [{ "type": "command", "command": "my-auditor" }] }
                ]
              }
            }
            """);

        GuardInstallation.Apply(_root, GuardMode.Enforce, McpLaunch.PathCommand);

        JsonElement settings = ReadSettings();
        Assert.Equal(
            "Bash(git status)",
            settings.GetProperty("permissions").GetProperty("allow")[0].GetString());
        Assert.Equal("Read(./.env)", settings.GetProperty("permissions").GetProperty("deny")[0].GetString());
        Assert.Equal("1", settings.GetProperty("env").GetProperty("MY_VAR").GetString());
        Assert.Equal(
            "my-formatter",
            settings.GetProperty("hooks").GetProperty("PostToolUse")[0]
                .GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Contains(
            settings.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray(),
            g => g.GetProperty("hooks")[0].GetProperty("command").GetString() == "my-auditor");
    }

    [Fact]
    public void Remove_TakesOnlyOurEntries()
    {
        WriteSettings("""
            {
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Edit", "hooks": [{ "type": "command", "command": "my-formatter" }] }
                ]
              }
            }
            """);
        GuardInstallation.Apply(_root, GuardMode.Enforce, McpLaunch.PathCommand);

        GuardInstallResult removed = GuardInstallation.Remove(_root);

        Assert.Equal(AgentFileChange.Removed, removed.Change);
        JsonElement hooks = ReadSettings().GetProperty("hooks");
        Assert.False(hooks.TryGetProperty("PreToolUse", out _));
        Assert.Equal(
            "my-formatter",
            hooks.GetProperty("PostToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.False(GuardInstallation.IsInstalled(_root, out _));
    }

    [Fact]
    public void Remove_IsIdempotentAndNeverCreatesAFile()
    {
        Assert.Equal(AgentFileChange.Absent, GuardInstallation.Remove(_root).Change);
        Assert.False(File.Exists(SettingsFile));

        GuardInstallation.Apply(_root, GuardMode.Observe, McpLaunch.PathCommand);
        GuardInstallation.Remove(_root);

        Assert.Equal(AgentFileChange.Absent, GuardInstallation.Remove(_root).Change);
    }

    [Fact]
    public void MalformedSettings_AreReportedAndLeftExactlyAsTheyWere()
    {
        const string damaged = "{ \"hooks\": [ this is not settings";
        WriteSettings(damaged);

        GuardInstallResult applied = GuardInstallation.Apply(
            _root, GuardMode.Enforce, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Skipped, applied.Change);
        Assert.NotNull(applied.Error);
        Assert.Equal(damaged, File.ReadAllText(SettingsFile));
        Assert.Equal(AgentFileChange.Skipped, GuardInstallation.Remove(_root).Change);
        Assert.Equal(damaged, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void WrongShapedHooks_AreReportedAndLeftAlone()
    {
        const string wrong = """{"hooks": {"PreToolUse": "not an array"}}""";
        WriteSettings(wrong);

        GuardInstallResult applied = GuardInstallation.Apply(
            _root, GuardMode.Observe, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Skipped, applied.Change);
        Assert.Contains("PreToolUse", applied.Error!, StringComparison.Ordinal);
        Assert.Equal(wrong, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Check_WritesNothing()
    {
        GuardInstallResult check = GuardInstallation.Check(
            _root, GuardMode.Observe, McpLaunch.PathCommand);

        Assert.Equal(AgentFileChange.Created, check.Change);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void Check_ReportsNoDriftOnceInstalled()
    {
        GuardInstallation.Apply(_root, GuardMode.Enforce, McpLaunch.PathCommand);

        Assert.Equal(
            AgentFileChange.Unchanged,
            GuardInstallation.Check(_root, GuardMode.Enforce, McpLaunch.PathCommand).Change);
    }

    [Fact]
    public void NpmInstallsAreLaunchedThroughNode()
    {
        // On a Windows npm install, `repoctx` is a .cmd shim; the committed
        // command has to work on every developer's machine, not the author's.
        string command = GuardInstallation.CommandFor(GuardMode.Observe, McpLaunch.LocalNpmPackage);

        Assert.StartsWith("node ", command, StringComparison.Ordinal);
        Assert.Contains("${CLAUDE_PROJECT_DIR}", command, StringComparison.Ordinal);
        Assert.True(GuardInstallation.IsOwned(command));
    }

    [Theory]
    [InlineData("my-formatter", false)]
    [InlineData("repoctx index", false)]
    [InlineData("prettier --write", false)]
    [InlineData("repoctx guard hook --mode observe", true)]
    public void Ownership_IsDecidedByTheCommandText(string command, bool owned) =>
        Assert.Equal(owned, GuardInstallation.IsOwned(command));
}
