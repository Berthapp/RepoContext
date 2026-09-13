using System.Text.Json;
using System.Text.Json.Nodes;
using RepoContext.Core.Context;

namespace RepoContext.Core.Guard;

/// <summary>The lifecycle events the adapter acts on.</summary>
public enum HookEventKind
{
    /// <summary>Anything the adapter does not act on.</summary>
    Other,

    /// <summary>A tool call is about to run.</summary>
    PreToolUse,

    /// <summary>A conversation started, resumed, was cleared, compacted or forked.</summary>
    SessionStart,

    /// <summary>A conversation ended.</summary>
    SessionEnd,

    /// <summary>The context is about to be compacted.</summary>
    PreCompact,

    /// <summary>A child context may inherit instructions but not possession.</summary>
    SubagentStart,
}

/// <summary>One parsed hook payload.</summary>
/// <param name="Kind">The event the adapter recognized.</param>
/// <param name="EventName">The raw <c>hook_event_name</c>, echoed back in the response.</param>
/// <param name="ToolName">The tool of a <see cref="HookEventKind.PreToolUse"/> event.</param>
/// <param name="SessionId">The client's session id; never persisted raw.</param>
/// <param name="Cwd">The working directory the tool call runs in.</param>
/// <param name="Trigger">The client's matcher value (<c>source</c>/<c>trigger</c>) when present.</param>
/// <param name="ToolInput">The tool arguments, or null.</param>
public sealed record HookRequest(
    HookEventKind Kind,
    string EventName,
    string? ToolName,
    string? SessionId,
    string? Cwd,
    string? Trigger,
    JsonElement? ToolInput);

/// <summary>
/// The Claude Code side of the read-cost guard: the payload contract, the
/// response contract, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Verified against the official hook reference (code.claude.com/docs/en/hooks)
/// on 2026-09-12. Input fields used: <c>hook_event_name</c>, <c>tool_name</c>,
/// <c>tool_input</c>, <c>session_id</c>, <c>cwd</c>. Output: exit code 0 plus
/// <c>hookSpecificOutput</c> carrying <c>hookEventName</c>,
/// <c>permissionDecision</c> and <c>permissionDecisionReason</c> for
/// <c>PreToolUse</c>, and <c>additionalContext</c> for <c>SessionStart</c>.
/// </para>
/// <para>
/// Two deliberate restrictions. First, the adapter <b>never emits
/// <c>"allow"</c></b>: an allow decision would skip the client's own permission
/// prompts, and a cost policy has no business widening what an agent may read.
/// Only <c>"deny"</c> or no decision at all is ever produced. Second, it does not
/// assume a pre-tool hook can substitute a tool result — the portable contract
/// is a denial plus a concrete next call, which the documented
/// <c>permissionDecision</c> field does support.
/// </para>
/// <para>
/// Field names that the reference documents only as matcher values
/// (<c>source</c> for <c>SessionStart</c>, <c>trigger</c> for <c>PreCompact</c>)
/// are read when present but never relied on: every lifecycle event starts a new
/// context epoch regardless, so an unrecognized or renamed field can only cost a
/// repeated read, never a false possession claim.
/// </para>
/// </remarks>
public static class ClaudeCodeHook
{
    /// <summary>The client id used for epoch keys and for <c>--client</c>.</summary>
    public const string ClientId = "claude-code";

    /// <summary>Parses a hook payload. Malformed input is a soft failure.</summary>
    public static bool TryParse(string? json, out HookRequest request, out string error)
    {
        request = new HookRequest(HookEventKind.Other, string.Empty, null, null, null, null, null);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty hook payload";
            return false;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            error = "malformed hook payload: " + e.Message;
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "hook payload is not an object";
            return false;
        }

        string eventName = String(root, "hook_event_name") ?? string.Empty;
        request = new HookRequest(
            Kind: eventName switch
            {
                "PreToolUse" => HookEventKind.PreToolUse,
                "SessionStart" => HookEventKind.SessionStart,
                "SessionEnd" => HookEventKind.SessionEnd,
                "PreCompact" => HookEventKind.PreCompact,
                "SubagentStart" => HookEventKind.SubagentStart,
                _ => HookEventKind.Other,
            },
            EventName: eventName,
            ToolName: String(root, "tool_name"),
            SessionId: String(root, "session_id"),
            Cwd: String(root, "cwd"),
            Trigger: String(root, "source") ?? String(root, "trigger"),
            ToolInput: root.TryGetProperty("tool_input", out JsonElement input)
                && input.ValueKind == JsonValueKind.Object
                ? input
                : null);
        return true;
    }

    /// <summary>
    /// The file reads a tool call performs, as far as the guard can tell.
    /// </summary>
    /// <param name="request">The parsed payload.</param>
    /// <param name="note">Why nothing was extracted, when the list is empty.</param>
    /// <returns>
    /// An empty list means "not judged": either the call reads nothing, or it is
    /// outside the documented subset. Both are handled normally by the client.
    /// </returns>
    public static IReadOnlyList<GuardReadRequest> ReadsFrom(HookRequest request, out string note)
    {
        note = string.Empty;
        if (request.Kind != HookEventKind.PreToolUse || request.ToolInput is not { } input)
        {
            note = "not a tool call";
            return [];
        }

        switch (request.ToolName)
        {
            case "Read":
            {
                string? path = String(input, "file_path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    note = "read without a file path";
                    return [];
                }

                // The actual requested window, not the whole file: a client that
                // already asks for 40 lines is doing the right thing and must not
                // be charged for the file it did not ask for.
                return [new GuardReadRequest(
                    path, Integer(input, "offset"), Integer(input, "limit"), "read-tool")];
            }

            case "Bash":
            {
                ShellReadParse parse = ShellReadParser.Parse(String(input, "command"));
                note = parse.Note;
                return parse.Kind == ShellReadKind.Read ? parse.Reads : [];
            }

            default:
                note = "tool outside the guard's scope";
                return [];
        }
    }

    /// <summary>
    /// Whether a shell call was outside the documented subset, as opposed to
    /// simply reading nothing. Only the former is counted as unsupported.
    /// </summary>
    public static bool IsUnsupportedShell(HookRequest request) =>
        request is { Kind: HookEventKind.PreToolUse, ToolName: "Bash", ToolInput: { } input }
        && ShellReadParser.Parse(String(input, "command")).Kind == ShellReadKind.Unsupported;

    /// <summary>The epoch reason a lifecycle event implies.</summary>
    /// <remarks>
    /// Every recognized value starts a new epoch, and so does an unrecognized
    /// one. The mapping exists to record <i>why</i>, not to decide <i>whether</i>:
    /// deciding on a field the client may rename is exactly the kind of unproven
    /// retention assumption this guard must not make.
    /// </remarks>
    public static string EpochReason(HookRequest request) => request.Trigger switch
    {
        "startup" => EpochReasons.Startup,
        "resume" => EpochReasons.Resume,
        "clear" => EpochReasons.Clear,
        "compact" => EpochReasons.Compact,
        "fork" => EpochReasons.Fork,
        "manual" or "auto" when request.Kind == HookEventKind.PreCompact => EpochReasons.Compact,
        _ => request.Kind == HookEventKind.PreCompact ? EpochReasons.Compact : EpochReasons.Unknown,
    };

    /// <summary>
    /// The response for a <c>PreToolUse</c> decision, or null when the guard has
    /// nothing to say (which is every case except an enforced denial).
    /// </summary>
    public static string? RenderPreToolUse(GuardDecision decision)
    {
        if (!decision.Deny)
        {
            return null;
        }

        var specific = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = DenialText(decision),
        };

        return new JsonObject { ["hookSpecificOutput"] = specific }.ToJsonString(Compact);
    }

    /// <summary>The denial text handed to the model: what happened and what to call instead.</summary>
    public static string DenialText(GuardDecision decision)
    {
        var lines = new List<string> { decision.Reason };
        if (decision.Suggestions.Count > 0)
        {
            lines.Add("Cheaper exact evidence for the same file:");
            lines.AddRange(decision.Suggestions.Select(s => "  " + s));
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The response for <c>SessionStart</c>: tell the agent which session name
    /// this context owns, so evidence reuse follows the real context lifetime.
    /// </summary>
    public static string RenderSessionStart(string sessionName, GuardMode mode)
    {
        var specific = new JsonObject
        {
            ["hookEventName"] = "SessionStart",
            ["additionalContext"] = SessionAnnouncement(sessionName, mode),
        };

        return new JsonObject { ["hookSpecificOutput"] = specific }.ToJsonString(Compact);
    }

    /// <summary>The text of the session announcement.</summary>
    public static string SessionAnnouncement(string sessionName, GuardMode mode) =>
        $"RepoContext session for this conversation: {sessionName}. Pass it as "
        + $"`repoctx context \"...\" --session {sessionName}` (or as the MCP context tool's "
        + "`session` argument) so evidence you already received is not sent again. "
        + "Use this name only in this conversation; never pass it to subagents. The name "
        + "changes after a compaction, a resume or a fork, and the new one is announced then; "
        + "a name that is no longer current simply delivers the evidence again."
        + (mode == GuardMode.Enforce
            ? " Reads of large files are redirected to `repoctx outline`/`repoctx context`; "
              + "repeating the same read gets you the whole file."
            : string.Empty);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
}
