namespace RepoContext.Core.Context;

/// <summary>Why <c>--detail auto</c> picked the detail level it picked.</summary>
public enum DetailChoiceReason
{
    /// <summary>The query names a change to make; source spans are what answers it.</summary>
    Action,

    /// <summary>The query asks where something is or how it fits together.</summary>
    Survey,

    /// <summary>No rule matched; the general-purpose choice applies.</summary>
    Default,
}

/// <summary>A resolved detail level and the rule that produced it.</summary>
/// <param name="Detail">The concrete level to run with.</param>
/// <param name="Reason">Which rule fired.</param>
public sealed record DetailChoice(ContextDetail Detail, DetailChoiceReason Reason)
{
    /// <summary>The stable label for the rule, e.g. <c>auto:action</c>.</summary>
    public string Label => "auto:" + Reason.ToString().ToLowerInvariant();
}

/// <summary>
/// Picks a detail level from the shape of the task when the caller asks for
/// <c>--detail auto</c> (ADR 0018, plan item I3).
/// </summary>
/// <remarks>
/// <para>
/// The point is to remove a whole round trip. An agent that guesses wrong —
/// <c>paths</c> for "fix the login redirect", <c>slices</c> for "where do we
/// handle sessions" — pays for one useless response and then asks again. The
/// rule table below is small, versioned and deterministic, so the same task text
/// always resolves the same way and the choice can be reviewed rather than
/// guessed at.
/// </para>
/// <para>
/// Resolution happens before the engine runs: the response reports the concrete
/// level it was run with, exactly as if the caller had named it. Nothing about
/// the wire contract, the cost oracle or the budget accounting changes — this is
/// input-side sugar, and deliberately nothing more.
/// </para>
/// <para>
/// Action beats survey when a query contains both ("find where we validate the
/// token and fix it"): the survey half is answerable from the slices, the change
/// half is not answerable from an outline.
/// </para>
/// </remarks>
public static class DetailPolicy
{
    /// <summary>
    /// Version of the rule table. Bump when the term sets or precedence change,
    /// so a recorded evaluation can be attributed to the rules that produced it.
    /// </summary>
    public const int RuleTableVersion = 1;

    /// <summary>
    /// Verbs and nouns that mean "I am about to change code". German entries are
    /// included because <see cref="QueryAnalyzer"/> already treats German as a
    /// first-class query language.
    /// </summary>
    private static readonly HashSet<string> ActionTerms = new(StringComparer.Ordinal)
    {
        // English
        "fix", "change", "implement", "refactor", "debug", "test", "add", "remove",
        "rename", "migrate", "update", "bug", "broken", "failing", "patch", "write",
        // German
        "beheben", "ändern", "aendern", "implementieren", "umbauen", "testen",
        "hinzufügen", "hinzufuegen", "entfernen", "umbenennen", "fehler", "kaputt",
    };

    /// <summary>
    /// Terms that mean "show me the lay of the land". These are answered by more
    /// files at less depth, which is what an outline is.
    /// </summary>
    private static readonly HashSet<string> SurveyTerms = new(StringComparer.Ordinal)
    {
        // English
        "where", "find", "locate", "which", "list", "overview", "architecture",
        "structure", "impact", "impacts", "dependency", "dependencies", "uses",
        // German
        "wo", "finde", "finden", "welche", "welcher", "überblick", "ueberblick",
        "architektur", "struktur", "auswirkung", "auswirkungen", "abhängigkeiten",
        "abhaengigkeiten",
    };

    /// <summary>
    /// Resolves a detail level from analyzed query terms. Terms are the analyzer's
    /// output, so synonyms configured for the repository participate too.
    /// </summary>
    public static DetailChoice Resolve(IReadOnlyList<string> terms)
    {
        foreach (string term in terms)
        {
            if (ActionTerms.Contains(term))
            {
                return new DetailChoice(ContextDetail.Slices, DetailChoiceReason.Action);
            }
        }

        foreach (string term in terms)
        {
            if (SurveyTerms.Contains(term))
            {
                return new DetailChoice(ContextDetail.Outline, DetailChoiceReason.Survey);
            }
        }

        // Slices, not paths: a bare pointer almost never ends the conversation,
        // and the response budget bounds the cost of being generous here.
        return new DetailChoice(ContextDetail.Slices, DetailChoiceReason.Default);
    }
}
