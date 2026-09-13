using System.Text.Json;

namespace RepoContext.Integration.Tests;

/// <summary>
/// End-to-end tests for the opt-in read-cost guard (ADR 0023): the hook
/// adapter, the bounded escalation, the lifecycle binding and the installation
/// round trip.
/// </summary>
public class GuardCommandTests
{
    private const string BigFile = "src/generated/catalog.ts";

    /// <summary>
    /// These tests assert on the guard counters, so recording must be on
    /// regardless of what the surrounding CI job sets. A test whose expectation
    /// depends on ambient environment is a test that passes on one runner and
    /// fails on the next.
    /// </summary>
    private static readonly Dictionary<string, string> StatsOn = new()
    {
        ["REPOCTX_NO_STATS"] = string.Empty,
    };

    private static CliResult Hook(FixtureWorkspace ws, string payload, params string[] args) =>
        CliHarness.RunIn(ws.Root, StatsOn, payload, args);

    private static CliResult Cli(FixtureWorkspace ws, params string[] args) =>
        CliHarness.RunIn(ws.Root, StatsOn, standardInput: null, args);

    /// <summary>A workspace with an indexed file far above the cost threshold.</summary>
    private static FixtureWorkspace IndexedWorkspace()
    {
        var ws = new FixtureWorkspace("sample-ts");
        Directory.CreateDirectory(ws.PathOf("src/generated"));
        File.WriteAllLines(
            ws.PathOf(BigFile),
            Enumerable.Range(0, 900).Select(i =>
                $"export function entry{i}(input: string): string {{ return input + \"{i}\"; }}"));
        ws.Run("init");
        ws.Run("index");
        return ws;
    }

    private static string ReadEvent(string root, string path, string session = "s-1") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = session,
            ["cwd"] = root,
            ["permission_mode"] = "default",
            ["tool_name"] = "Read",
            ["tool_input"] = new Dictionary<string, object> { ["file_path"] = path },
        });

    private static string BashEvent(string root, string command, string session = "s-1") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = session,
            ["cwd"] = root,
            ["tool_name"] = "Bash",
            ["tool_input"] = new Dictionary<string, object> { ["command"] = command },
        });

    private static string LifecycleEvent(string root, string name, string source, string session) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = name,
            ["session_id"] = session,
            ["cwd"] = root,
            ["source"] = source,
        });

    private static string DenialReason(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement specific = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        return specific.GetProperty("permissionDecisionReason").GetString()!;
    }

    [Fact]
    public void ChildStart_InvalidatesInheritedParentSession()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "startup", "parent"), "guard", "hook");
        var layout = RepoContext.Core.RepoLayout.For(ws.Root);
        string key = RepoContext.Core.Context.ContextEpochs.AgentKey("claude-code", "parent");
        string before = RepoContext.Core.Context.ContextEpochs.Current(layout, key)!.SessionName;
        Hook(ws, LifecycleEvent(ws.Root, "SubagentStart", "", "parent"), "guard", "hook");
        Assert.True(RepoContext.Core.Context.ContextEpochs.IsSuperseded(layout, before));
    }

    [Fact]
    public void UnwritableRedirectState_AllowsEveryRetry()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        Directory.CreateDirectory(ws.PathOf(".repoctx/guard.json.tmp"));
        string payload = ReadEvent(ws.Root, ws.PathOf(BigFile));
        for (int i = 0; i < 2; i++)
        {
            CliResult result = Hook(ws, payload, "guard", "hook", "--mode", "enforce");
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StdOut.Trim());
        }
    }

    [Theory]
    [InlineData("config")]
    [InlineData("database")]
    public void BrokenIndexOrConfig_FailsOpen(string broken)
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        File.WriteAllText(ws.PathOf(broken == "config" ? "repoctx.config.json" : ".repoctx/index.db"), "broken");
        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)),
            "guard", "hook", "--mode", "enforce");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void EqualLengthEdit_IsNotJudgedUsingOldMetrics()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        string path = ws.PathOf(BigFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("export", "import", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        CliResult result = Hook(ws, ReadEvent(ws.Root, path), "guard", "hook", "--mode", "enforce");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void SuggestedOutline_ResolvesFromTheToolsSubdirectory()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        string subdirectory = ws.PathOf("src");
        CliResult result = Hook(ws, ReadEvent(subdirectory, ws.PathOf(BigFile)),
            "guard", "hook", "--mode", "enforce");
        string suggestion = DenialReason(result.StdOut).Split('\n')
            .Select(line => line.Trim()).First(line => line.StartsWith("repoctx outline", StringComparison.Ordinal));
        Assert.True(RepoContext.Core.Guard.ShellReadParser.TrySplit(suggestion, out var words));
        CliResult outline = CliHarness.RunIn(subdirectory, StatsOn, standardInput: null, [.. words.Skip(1)]);
        Assert.Equal(0, outline.ExitCode);
        Assert.Contains("entry0", outline.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadNearEnd_UsesOnlyRemainingLines()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        CliResult result = Cli(ws, "guard", "check", BigFile, "--mode", "enforce", "--offset", "895");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("deny: no", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGuard_ReportsMalformedSettingsAsFailure()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        Directory.CreateDirectory(ws.PathOf(".claude"));
        File.WriteAllText(ws.PathOf(".claude/settings.json"), "{");
        CliResult result = Cli(ws, "integrate", "--client", "claude-code", "--guard", "--check");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("{", File.ReadAllText(ws.PathOf(".claude/settings.json")));
    }

    [Fact]
    public void Enforce_DeniesAnExpensiveRead_AndNamesTheCheaperCall()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        string reason = DenialReason(result.StdOut);
        Assert.Contains("repoctx outline", reason, StringComparison.Ordinal);
        Assert.Contains(BigFile, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_AllowsTheRepeatedRequest_SoNoTaskCanStall()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        string payload = ReadEvent(ws.Root, ws.PathOf(BigFile));

        CliResult denied = Hook(ws, payload, "guard", "hook", "--mode", "enforce");
        CliResult repeated = Hook(ws, payload, "guard", "hook", "--mode", "enforce");

        Assert.NotEmpty(denied.StdOut);
        Assert.Equal(0, repeated.ExitCode);
        Assert.Empty(repeated.StdOut.Trim());
    }

    [Fact]
    public void Observe_NeverBlocksAnything()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "observe");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
        Assert.Contains("redirected=1", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
        Assert.Contains("denied=0", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserveIsTheDefaultMode()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook");

        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void SmallReadsAndNarrowWindowsAreAllowed()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult small = Hook(ws, ReadEvent(ws.Root, ws.PathOf("src/middleware.ts")), "guard", "hook", "--mode", "enforce");

        Assert.Empty(small.StdOut.Trim());
        Assert.Contains(
            "allowed",
            Cli(ws, "guard", "check", BigFile, "--mode", "enforce", "--limit", "40").StdOut,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheAlternativeCallIsNeverBlocked()
    {
        // If the guard could block the command it suggests, an agent would have
        // no way forward at all.
        using FixtureWorkspace ws = IndexedWorkspace();

        foreach (string command in new[]
        {
            $"repoctx outline {BigFile}",
            "repoctx context 'explain the catalog' --detail slices",
            "git status",
        })
        {
            CliResult result = Hook(ws, BashEvent(ws.Root, command), "guard", "hook", "--mode", "enforce");

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StdOut.Trim());
        }
    }

    [Fact]
    public void SupportedShellReadsAreJudged_UnsupportedOnesAreNot()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult judged = Hook(ws, BashEvent(ws.Root, $"cat {BigFile}"), "guard", "hook", "--mode", "enforce");
        CliResult notJudged = Hook(ws, BashEvent(ws.Root, $"cat {BigFile} | head -n 20"), "guard", "hook", "--mode", "enforce");

        Assert.Contains("repoctx outline", DenialReason(judged.StdOut), StringComparison.Ordinal);
        Assert.Empty(notJudged.StdOut.Trim());
        Assert.Contains(
            "unsupported=1", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"hook_event_name\":\"PreToolUse\"")]
    [InlineData("{\"hook_event_name\":\"SomethingNew\",\"session_id\":\"x\"}")]
    [InlineData("[]")]
    public void MalformedOrUnknownEvents_ExitCleanlyAndBlockNothing(string payload)
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, payload, "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void WithoutAnIndex_TheGuardStandsAside()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf("src/middleware.ts")), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void AStaleIndexIsNotUsedToJudgeARead()
    {
        // The stored token count describes the file as indexed. Once the file on
        // disk differs, judging the read on that number would be a cost claim
        // about content the guard has never seen.
        using FixtureWorkspace ws = IndexedWorkspace();
        File.AppendAllText(ws.PathOf(BigFile), "\nexport const added = true;\n");

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void ExcludedAndSensitiveFilesAreNeverJudgedOrNamed()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(".env")), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
        Assert.DoesNotContain(".env", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_AnnouncesASessionName_ThatCompactionReplaces()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult started = Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook");
        string first = Announced(started.StdOut);

        Hook(ws, LifecycleEvent(ws.Root, "PreCompact", "auto", "conv-1"), "guard", "hook");
        CliResult resumed = Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "compact", "conv-1"), "guard", "hook");
        string second = Announced(resumed.StdOut);

        Assert.NotEqual(first, second);
        Assert.StartsWith("rcx-claude-code-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterCompaction_EvidenceIsDeliveredAgainInsteadOfSuppressed()
    {
        // The whole point of milestone 3: a local session file must not keep
        // asserting possession for a conversation whose context was replaced.
        using FixtureWorkspace ws = IndexedWorkspace();
        string session = Announced(Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook").StdOut);

        string[] query = ["context", "login", "--detail", "slices", "--top", "2",
            "--session", session, "--format", "json"];
        Cli(ws, query);
        CliResult repeated = Cli(ws, query);
        Assert.True(Reused(repeated.StdOut) > 0);

        Hook(ws, LifecycleEvent(ws.Root, "PreCompact", "auto", "conv-1"), "guard", "hook");

        CliResult afterCompaction = Cli(ws, query);
        Assert.Equal(0, afterCompaction.ExitCode);
        Assert.Equal(0, Reused(afterCompaction.StdOut));
    }

    [Fact]
    public void TwoAgentsNeverShareAPossessionClaim()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        string first = Announced(Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook").StdOut);
        string second = Announced(Hook(ws, LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-2"), "guard", "hook").StdOut);

        Assert.NotEqual(first, second);
        Cli(ws, "context", "login", "--detail", "slices", "--session", first, "--format", "json");
        CliResult other = ws.Run(
            "context", "login", "--detail", "slices", "--session", second, "--format", "json");

        Assert.Equal(0, Reused(other.StdOut));
    }

    [Fact]
    public void Status_ReportsCountersWithoutLeakingPaths()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        CliResult status = Cli(ws, "guard", "status");

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("observed=1", status.StdOut, StringComparison.Ordinal);
        Assert.Contains("denied=1", status.StdOut, StringComparison.Ordinal);
        Assert.Contains("not installed", status.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog.ts", status.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStats_KeepsTheGuardWorkingWithoutCounters()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        var environment = new Dictionary<string, string> { ["REPOCTX_NO_STATS"] = "1" };

        CliResult denied = CliHarness.RunIn(
            ws.Root, environment, ReadEvent(ws.Root, ws.PathOf(BigFile)),
            "guard", "hook", "--mode", "enforce");

        Assert.Contains("repoctx outline", DenialReason(denied.StdOut), StringComparison.Ordinal);
        Assert.Contains("observed=0", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ExplainsACostWithoutWritingState()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult check = Cli(ws, "guard", "check", BigFile, "--mode", "enforce");

        Assert.Equal(0, check.ExitCode);
        Assert.Contains("outcome: redirected", check.StdOut, StringComparison.Ordinal);
        Assert.Contains("estimated tokens", check.StdOut, StringComparison.Ordinal);
        Assert.Contains("observed=0", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    private static string Announced(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        string context = document.RootElement.GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext").GetString()!;
        int start = context.IndexOf("rcx-claude-code-", StringComparison.Ordinal);
        Assert.True(start >= 0, context);
        int end = context.IndexOf('.', start);
        string name = context[start..end];
        Assert.True(RepoContext.Core.Context.ContextEpochs.TryParseSessionName(name, out _, out _));
        return name;
    }

    private static int Reused(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("reused_count").GetInt32();
    }

    [Fact]
    public void AnExhaustedBudget_AllowsTheReadInsteadOfMakingAnyoneWait()
    {
        // The client is waiting on a tool call. A guard that overruns its budget
        // stands aside; it does not hold up the work to finish being clever.
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)),
            "guard", "hook", "--mode", "enforce", "--timeout-ms", "0");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
        Assert.Contains("error=1", Cli(ws, "guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDenialPointsAtTheDocumentedBudgetedLoop()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = Hook(ws, ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        string reason = DenialReason(result.StdOut);
        Assert.Contains("--response-budget-tokens 2000", reason, StringComparison.Ordinal);
        Assert.Contains("--detail slices", reason, StringComparison.Ordinal);
    }
}
