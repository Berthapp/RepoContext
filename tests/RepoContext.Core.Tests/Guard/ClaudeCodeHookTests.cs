using System.Text.Json;
using RepoContext.Core.Context;
using RepoContext.Core.Guard;

namespace RepoContext.Core.Tests.Guard;

/// <summary>
/// The Claude Code hook contract, as verified against the official reference on
/// 2026-09-12 (ADR 0023).
/// </summary>
public class ClaudeCodeHookTests
{
    private const string ReadPayload = """
        {
          "session_id": "abc123",
          "transcript_path": "/home/u/.claude/projects/x/transcript.jsonl",
          "cwd": "/home/u/project",
          "permission_mode": "default",
          "hook_event_name": "PreToolUse",
          "tool_name": "Read",
          "tool_input": { "file_path": "/home/u/project/src/app.ts", "offset": 100, "limit": 40 }
        }
        """;

    [Fact]
    public void Parse_ReadsTheDocumentedFields()
    {
        Assert.True(ClaudeCodeHook.TryParse(ReadPayload, out HookRequest request, out _));

        Assert.Equal(HookEventKind.PreToolUse, request.Kind);
        Assert.Equal("Read", request.ToolName);
        Assert.Equal("abc123", request.SessionId);
        Assert.Equal("/home/u/project", request.Cwd);
    }

    [Fact]
    public void Parse_UsesTheRequestedWindow_NotTheWholeFile()
    {
        ClaudeCodeHook.TryParse(ReadPayload, out HookRequest request, out _);

        GuardReadRequest read = Assert.Single(ClaudeCodeHook.ReadsFrom(request, out _));
        Assert.Equal("/home/u/project/src/app.ts", read.Path);
        Assert.Equal(100, read.StartLine);
        Assert.Equal(40, read.LineLimit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"hook_event_name\":")]
    public void Parse_FailsSoftlyOnMalformedPayloads(string payload)
    {
        Assert.False(ClaudeCodeHook.TryParse(payload, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Parse_ToleratesAMissingToolInput()
    {
        Assert.True(ClaudeCodeHook.TryParse(
            """{"hook_event_name":"PreToolUse","tool_name":"Read"}""",
            out HookRequest request, out _));

        Assert.Empty(ClaudeCodeHook.ReadsFrom(request, out _));
    }

    [Fact]
    public void ReadsFrom_IgnoresToolsOutsideTheGuardsScope()
    {
        ClaudeCodeHook.TryParse(
            """{"hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":"a.ts"}}""",
            out HookRequest request, out _);

        Assert.Empty(ClaudeCodeHook.ReadsFrom(request, out string note));
        Assert.NotEmpty(note);
    }

    [Fact]
    public void Render_NeverEmitsAnAllowDecision()
    {
        // An allow decision would skip the client's own permission prompts. A
        // cost policy must never be able to widen what an agent may read.
        foreach (GuardOutcome outcome in Enum.GetValues<GuardOutcome>())
        {
            string? response = ClaudeCodeHook.RenderPreToolUse(
                GuardDecision.Permit(outcome, "whatever"));

            Assert.Null(response);
        }
    }

    [Fact]
    public void Render_EmitsTheDocumentedDenyShape()
    {
        var decision = new GuardDecision
        {
            Outcome = GuardOutcome.Redirected,
            Reason = "too expensive",
            Deny = true,
            Suggestions = ["repoctx outline src/app.ts"],
            Targets = [new GuardTarget("src/app.ts", 9_000, true, true, false)],
        };

        string response = Assert.IsType<string>(ClaudeCodeHook.RenderPreToolUse(decision));
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement specific = document.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PreToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        Assert.Contains(
            "repoctx outline src/app.ts",
            specific.GetProperty("permissionDecisionReason").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesUntrustedPathsIntoValidJson()
    {
        var decision = new GuardDecision
        {
            Outcome = GuardOutcome.Redirected,
            Reason = "too expensive",
            Deny = true,
            Suggestions = ["repoctx outline 'src/\"weird\"\n.ts'"],
        };

        string response = Assert.IsType<string>(ClaudeCodeHook.RenderPreToolUse(decision));

        // The point is that it parses at all: a hand-built string would not.
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.Contains(
            "weird",
            document.RootElement.GetProperty("hookSpecificOutput")
                .GetProperty("permissionDecisionReason").GetString()!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SessionStart", "startup", EpochReasons.Startup)]
    [InlineData("SessionStart", "resume", EpochReasons.Resume)]
    [InlineData("SessionStart", "compact", EpochReasons.Compact)]
    [InlineData("SessionStart", "fork", EpochReasons.Fork)]
    [InlineData("SessionStart", "clear", EpochReasons.Clear)]
    [InlineData("PreCompact", "auto", EpochReasons.Compact)]
    [InlineData("PreCompact", null, EpochReasons.Compact)]
    [InlineData("SessionStart", "something-new", EpochReasons.Unknown)]
    [InlineData("SessionStart", null, EpochReasons.Unknown)]
    public void EpochReason_RecordsWhy_ButEveryEventStillStartsOne(
        string eventName, string? source, string expected)
    {
        string payload = source is null
            ? $$"""{"hook_event_name":"{{eventName}}","session_id":"s"}"""
            : $$"""{"hook_event_name":"{{eventName}}","session_id":"s","source":"{{source}}"}""";
        ClaudeCodeHook.TryParse(payload, out HookRequest request, out _);

        Assert.Equal(expected, ClaudeCodeHook.EpochReason(request));
    }

    [Fact]
    public void SessionStart_AnnouncesTheSessionNameAndItsExpiry()
    {
        string response = ClaudeCodeHook.RenderSessionStart("claude-code-abc123-e2", GuardMode.Observe);

        using JsonDocument document = JsonDocument.Parse(response);
        string context = document.RootElement.GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext").GetString()!;

        Assert.Contains("claude-code-abc123-e2", context, StringComparison.Ordinal);
        Assert.Contains("--session", context, StringComparison.Ordinal);
        Assert.Contains("compaction", context, StringComparison.Ordinal);
    }

    [Fact]
    public void IsUnsupportedShell_SeparatesUnparsableFromHarmless()
    {
        ClaudeCodeHook.TryParse(
            """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"cat a | wc -l"}}""",
            out HookRequest pipeline, out _);
        ClaudeCodeHook.TryParse(
            """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"git status"}}""",
            out HookRequest known, out _);

        Assert.True(ClaudeCodeHook.IsUnsupportedShell(pipeline));
        Assert.False(ClaudeCodeHook.IsUnsupportedShell(known));
    }
}
