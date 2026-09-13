using RepoContext.Core.Indexing;

namespace RepoContext.Core.Guard;

/// <summary>What the index knows about a file's size, in the guard's terms.</summary>
/// <param name="TokenCount">Raw <c>o200k_base</c> token count of the whole file.</param>
/// <param name="LineCount">Line count of the whole file.</param>
public readonly record struct GuardFileMetrics(int TokenCount, int LineCount);

/// <summary>
/// The metadata lookup the guard is allowed to perform: one indexed row per
/// path, nothing else.
/// </summary>
/// <remarks>
/// Deliberately this narrow. A hook runs in front of a tool call the user is
/// waiting on, so it must not re-index, parse, rank or open anything large. A
/// file the index does not carry — never indexed, excluded, filtered as
/// sensitive, or added since the last run — simply has no metrics, and a read
/// with no metrics is always allowed.
/// </remarks>
public interface IGuardIndex
{
    /// <summary>Looks up one repository-relative path.</summary>
    bool TryGetMetrics(string relativePath, out GuardFileMetrics metrics);
}

/// <summary>Tunable limits of the read-cost policy.</summary>
public sealed record ReadCostPolicyOptions
{
    /// <summary>
    /// Estimated tokens above which a read counts as expensive, in the active
    /// token profile.
    /// </summary>
    /// <remarks>
    /// A cost, deliberately not a line count: 400 lines of dense TypeScript and
    /// 400 lines of generated JSON do not cost the same, and the caller is
    /// billed for tokens. The default is an engineering starting point — roughly
    /// a file an outline plus one targeted slice can replace — and is not a
    /// value validated by the (not yet executed) real-agent comparison.
    /// </remarks>
    public int MaxReadTokens { get; init; } = 2_000;

    /// <summary>
    /// How often one path may be redirected within a context epoch before the
    /// read is allowed through.
    /// </summary>
    /// <remarks>
    /// The bound is what makes enforcement safe rather than merely cheap: an
    /// agent that asks again gets the file. Without it, a file whose evidence the
    /// index cannot supply would be unreachable and the task would stall.
    /// </remarks>
    public int MaxRedirectsPerPath { get; init; } = 1;

    /// <summary>How many suggested alternatives a denial may list.</summary>
    public int MaxSuggestions { get; init; } = 3;
}

/// <summary>
/// The deterministic read-cost policy (milestone 2 of the same-quality,
/// lower-cost plan): given what a client is about to read and what the index
/// already knows, decide whether cheaper exact evidence exists.
/// </summary>
/// <remarks>
/// <para>
/// Pure in the sense that matters: the same request, index metrics, mode and
/// redirect history always produce the same decision. It performs no network
/// call, no model call, no re-index and no command execution.
/// </para>
/// <para>
/// Token figures are <b>estimates</b>. Whole-file counts come from the index and
/// are exact <c>o200k_base</c> counts scaled by the configured calibration
/// profile; a partial read is prorated by lines, because per-line counts are not
/// stored. The policy therefore compares an estimate with a threshold, and the
/// counters it feeds never claim to be billed tokens.
/// </para>
/// </remarks>
public sealed class ReadCostPolicy(
    IGuardIndex index,
    ReadCostPolicyOptions options,
    TokenScale scale,
    Func<string, string?> resolvePath,
    Func<string, string>? outlinePath = null)
{
    /// <summary>
    /// The response ceiling the suggested <c>context</c> call carries. The same
    /// figure the agent playbook starts with, so a redirected read lands on the
    /// documented loop rather than on an unbounded one.
    /// </summary>
    private const int SuggestedResponseBudgetTokens = 2_000;

    /// <summary>The limits in force.</summary>
    public ReadCostPolicyOptions Options { get; } = options;

    /// <summary>
    /// Evaluates one tool call.
    /// </summary>
    /// <param name="requests">Every read the call performs.</param>
    /// <param name="mode">The installed mode.</param>
    /// <param name="priorRedirects">
    /// How often this path has already been redirected in the current context
    /// epoch. The caller owns that state; the policy only reads it.
    /// </param>
    public GuardDecision Evaluate(
        IReadOnlyList<GuardReadRequest> requests,
        GuardMode mode,
        Func<string, int> priorRedirects)
    {
        if (mode == GuardMode.Off)
        {
            return GuardDecision.Permit(GuardOutcome.Allowed, "guard is off");
        }

        if (requests.Count == 0)
        {
            return GuardDecision.Permit(GuardOutcome.Unsupported, "no file read in this call");
        }

        var targets = new List<GuardTarget>(requests.Count);
        foreach (GuardReadRequest request in requests)
        {
            targets.Add(Measure(request));
        }

        GuardTarget[] expensive = [.. targets.Where(t => t.Expensive)];
        if (expensive.Length == 0)
        {
            return GuardDecision.Permit(GuardOutcome.Allowed, "read is cheap enough", targets);
        }

        if (mode == GuardMode.Observe)
        {
            // Observe never changes what the client does; it measures how often
            // enforcement *would* act, which is what makes the first release
            // decision an evidence-based one.
            return new GuardDecision
            {
                Outcome = GuardOutcome.Redirected,
                Reason = ObserveReason(expensive),
                Targets = targets,
                Suggestions = Suggestions(expensive),
                Deny = false,
            };
        }

        if (Array.TrueForAll(expensive, t => priorRedirects(t.Path) >= Options.MaxRedirectsPerPath))
        {
            return new GuardDecision
            {
                Outcome = GuardOutcome.Escalated,
                Reason =
                    "already redirected; the repeated request is allowed so the task cannot stall",
                Targets = targets,
                Deny = false,
            };
        }

        return new GuardDecision
        {
            Outcome = GuardOutcome.Redirected,
            Reason = DenyReason(expensive),
            Targets = targets,
            Suggestions = Suggestions(expensive),
            Deny = true,
        };
    }

    private GuardTarget Measure(GuardReadRequest request)
    {
        string? relative = resolvePath(request.Path);
        if (relative is null)
        {
            // Outside the repository, or unresolvable. Not this policy's business.
            return new GuardTarget(request.Path, 0, Expensive: false, Indexed: false, request.IsPartial);
        }

        if (!index.TryGetMetrics(relative, out GuardFileMetrics metrics))
        {
            return new GuardTarget(relative, 0, Expensive: false, Indexed: false, request.IsPartial);
        }

        int lines = Math.Max(metrics.LineCount, 1);
        long first = Math.Max(1L, request.StartLine ?? 1);
        int remaining = (int)Math.Max(0, lines - first + 1);
        int requested = request.LineLimit is { } limit && limit > 0
            ? Math.Min(limit, remaining)
            : remaining;
        long prorated = requested >= lines
            ? metrics.TokenCount
            : (long)Math.Ceiling(metrics.TokenCount * (double)requested / lines);
        int estimated = scale.Apply((int)Math.Min(prorated, int.MaxValue));

        return new GuardTarget(
            relative, estimated, estimated > Options.MaxReadTokens, Indexed: true, request.IsPartial);
    }

    private string ObserveReason(IReadOnlyList<GuardTarget> expensive) =>
        $"observing: {Describe(expensive)} exceeds {Options.MaxReadTokens} estimated tokens; "
        + "nothing is blocked in observe mode";

    private string DenyReason(IReadOnlyList<GuardTarget> expensive) =>
        $"{Describe(expensive)} costs about {expensive.Max(t => t.EstimatedTokens)} estimated tokens. "
        + "Exact evidence for the same file is cheaper. Repeat this read to get the whole file anyway.";

    private static string Describe(IReadOnlyList<GuardTarget> expensive) =>
        expensive.Count == 1
            ? $"reading {expensive[0].Path}"
            : $"reading {expensive.Count} files ({expensive[0].Path}, ...)";

    private IReadOnlyList<string> Suggestions(IReadOnlyList<GuardTarget> expensive)
    {
        var suggestions = new List<string>();
        foreach (GuardTarget target in expensive.Take(Options.MaxSuggestions))
        {
            string quoted = GuardSuggestion.Quote(target.Path);
            string quotedOutline = GuardSuggestion.Quote(outlinePath?.Invoke(target.Path) ?? target.Path);
            suggestions.Add($"repoctx outline {quotedOutline}");
            suggestions.Add(
                $"repoctx context '<the task in your own words>' --path {quoted} "
                + $"--detail slices --response-budget-tokens {SuggestedResponseBudgetTokens}");
        }

        return suggestions;
    }
}
