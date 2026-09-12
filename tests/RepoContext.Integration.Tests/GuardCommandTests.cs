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
    public void Enforce_DeniesAnExpensiveRead_AndNamesTheCheaperCall()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

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

        CliResult denied = ws.RunWithInput(payload, "guard", "hook", "--mode", "enforce");
        CliResult repeated = ws.RunWithInput(payload, "guard", "hook", "--mode", "enforce");

        Assert.NotEmpty(denied.StdOut);
        Assert.Equal(0, repeated.ExitCode);
        Assert.Empty(repeated.StdOut.Trim());
    }

    [Fact]
    public void Observe_NeverBlocksAnything()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "observe");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
        Assert.Contains("redirected=1", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
        Assert.Contains("denied=0", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserveIsTheDefaultMode()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook");

        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void SmallReadsAndNarrowWindowsAreAllowed()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult small = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf("src/middleware.ts")), "guard", "hook", "--mode", "enforce");

        Assert.Empty(small.StdOut.Trim());
        Assert.Contains(
            "allowed",
            ws.Run("guard", "check", BigFile, "--mode", "enforce", "--limit", "40").StdOut,
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
            CliResult result = ws.RunWithInput(
                BashEvent(ws.Root, command), "guard", "hook", "--mode", "enforce");

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StdOut.Trim());
        }
    }

    [Fact]
    public void SupportedShellReadsAreJudged_UnsupportedOnesAreNot()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult judged = ws.RunWithInput(
            BashEvent(ws.Root, $"cat {BigFile}"), "guard", "hook", "--mode", "enforce");
        CliResult notJudged = ws.RunWithInput(
            BashEvent(ws.Root, $"cat {BigFile} | head -n 20"), "guard", "hook", "--mode", "enforce");

        Assert.Contains("repoctx outline", DenialReason(judged.StdOut), StringComparison.Ordinal);
        Assert.Empty(notJudged.StdOut.Trim());
        Assert.Contains(
            "unsupported=1", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
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

        CliResult result = ws.RunWithInput(payload, "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void WithoutAnIndex_TheGuardStandsAside()
    {
        using var ws = new FixtureWorkspace("sample-ts");
        ws.Run("init");

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf("src/middleware.ts")), "guard", "hook", "--mode", "enforce");

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

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
    }

    [Fact]
    public void ExcludedAndSensitiveFilesAreNeverJudgedOrNamed()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult result = ws.RunWithInput(
            ReadEvent(ws.Root, ws.PathOf(".env")), "guard", "hook", "--mode", "enforce");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut.Trim());
        Assert.DoesNotContain(".env", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStart_AnnouncesASessionName_ThatCompactionReplaces()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult started = ws.RunWithInput(
            LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook");
        string first = Announced(started.StdOut);

        ws.RunWithInput(LifecycleEvent(ws.Root, "PreCompact", "auto", "conv-1"), "guard", "hook");
        CliResult resumed = ws.RunWithInput(
            LifecycleEvent(ws.Root, "SessionStart", "compact", "conv-1"), "guard", "hook");
        string second = Announced(resumed.StdOut);

        Assert.NotEqual(first, second);
        Assert.StartsWith("claude-code-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterCompaction_EvidenceIsDeliveredAgainInsteadOfSuppressed()
    {
        // The whole point of milestone 3: a local session file must not keep
        // asserting possession for a conversation whose context was replaced.
        using FixtureWorkspace ws = IndexedWorkspace();
        string session = Announced(ws.RunWithInput(
            LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook").StdOut);

        string[] query = ["context", "login", "--detail", "slices", "--top", "2",
            "--session", session, "--format", "json"];
        ws.Run(query);
        CliResult repeated = ws.Run(query);
        Assert.True(Reused(repeated.StdOut) > 0);

        ws.RunWithInput(LifecycleEvent(ws.Root, "PreCompact", "auto", "conv-1"), "guard", "hook");

        CliResult afterCompaction = ws.Run(query);
        Assert.Equal(0, afterCompaction.ExitCode);
        Assert.Equal(0, Reused(afterCompaction.StdOut));
    }

    [Fact]
    public void TwoAgentsNeverShareAPossessionClaim()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        string first = Announced(ws.RunWithInput(
            LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-1"), "guard", "hook").StdOut);
        string second = Announced(ws.RunWithInput(
            LifecycleEvent(ws.Root, "SessionStart", "startup", "conv-2"), "guard", "hook").StdOut);

        Assert.NotEqual(first, second);
        ws.Run("context", "login", "--detail", "slices", "--session", first, "--format", "json");
        CliResult other = ws.Run(
            "context", "login", "--detail", "slices", "--session", second, "--format", "json");

        Assert.Equal(0, Reused(other.StdOut));
    }

    [Fact]
    public void Status_ReportsCountersWithoutLeakingPaths()
    {
        using FixtureWorkspace ws = IndexedWorkspace();
        ws.RunWithInput(ReadEvent(ws.Root, ws.PathOf(BigFile)), "guard", "hook", "--mode", "enforce");

        CliResult status = ws.Run("guard", "status");

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
        Assert.Contains("observed=0", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ExplainsACostWithoutWritingState()
    {
        using FixtureWorkspace ws = IndexedWorkspace();

        CliResult check = ws.Run("guard", "check", BigFile, "--mode", "enforce");

        Assert.Equal(0, check.ExitCode);
        Assert.Contains("outcome: redirected", check.StdOut, StringComparison.Ordinal);
        Assert.Contains("estimated tokens", check.StdOut, StringComparison.Ordinal);
        Assert.Contains("observed=0", ws.Run("guard", "status").StdOut, StringComparison.Ordinal);
    }

    private static string Announced(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        string context = document.RootElement.GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext").GetString()!;
        int start = context.IndexOf("claude-code-", StringComparison.Ordinal);
        Assert.True(start >= 0, context);
        int end = context.IndexOf('.', start);
        return context[start..end];
    }

    private static int Reused(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("reused_count").GetInt32();
    }
}
