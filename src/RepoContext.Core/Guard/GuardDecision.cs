namespace RepoContext.Core.Guard;

/// <summary>What the guard decided about one inspected tool call.</summary>
public enum GuardOutcome
{
    /// <summary>
    /// The call is not a read the guard understands. The client handles it
    /// normally; the guard only counts that it saw something it cannot judge.
    /// </summary>
    Unsupported,

    /// <summary>A read the guard understands and considers cheap enough.</summary>
    Allowed,

    /// <summary>
    /// A supported expensive read. In enforce mode it is denied together with a
    /// concrete cheaper call; in observe mode it is only counted.
    /// </summary>
    Redirected,

    /// <summary>
    /// An expensive read that had already been redirected up to the bound. It is
    /// allowed through: the agent asked again, and cost policy never outranks
    /// getting the work done.
    /// </summary>
    Escalated,

    /// <summary>
    /// The guard could not evaluate the call (malformed payload, unreadable
    /// index, I/O failure). Always permissive, always counted.
    /// </summary>
    Error,
}

/// <summary>
/// One evidence-bearing read the guard evaluated inside a single tool call.
/// </summary>
/// <param name="Path">Repository-relative path with <c>/</c> separators, or the raw path when it could not be resolved.</param>
/// <param name="EstimatedTokens">Estimated cost of the requested portion in the active token profile.</param>
/// <param name="Expensive">Whether <see cref="EstimatedTokens"/> exceeds the threshold.</param>
/// <param name="Indexed">Whether file metadata was available; an unindexed file is never judged expensive.</param>
/// <param name="Partial">Whether the request asked for a line range rather than the whole file.</param>
public sealed record GuardTarget(
    string Path, int EstimatedTokens, bool Expensive, bool Indexed, bool Partial);

/// <summary>
/// The guard's verdict on one tool call: what it saw, what it costs and — only
/// ever as a denial — what to call instead.
/// </summary>
public sealed record GuardDecision
{
    /// <summary>The outcome the counters record.</summary>
    public required GuardOutcome Outcome { get; init; }

    /// <summary>One short sentence, safe to show to a model and to a human.</summary>
    public required string Reason { get; init; }

    /// <summary>The reads inspected inside this call, in request order.</summary>
    public IReadOnlyList<GuardTarget> Targets { get; init; } = [];

    /// <summary>
    /// Concrete, safely quoted commands to run instead. Empty unless the
    /// outcome is <see cref="GuardOutcome.Redirected"/>.
    /// </summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>
    /// Whether a client should be told to deny the call. True only for
    /// <see cref="GuardOutcome.Redirected"/> in <see cref="GuardMode.Enforce"/>;
    /// the guard never emits an allow decision, because granting a read is the
    /// client's business, not a cost policy's.
    /// </summary>
    public bool Deny { get; init; }

    /// <summary>Estimated tokens of the most expensive inspected read.</summary>
    public int EstimatedTokens => Targets.Count == 0 ? 0 : Targets.Max(t => t.EstimatedTokens);

    /// <summary>A permissive decision that records what was seen.</summary>
    public static GuardDecision Permit(GuardOutcome outcome, string reason,
        IReadOnlyList<GuardTarget>? targets = null) =>
        new() { Outcome = outcome, Reason = reason, Targets = targets ?? [] };
}
