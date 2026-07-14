using RepoContext.Core.Indexing;

namespace RepoContext.Core.Stats;

/// <summary>
/// Records one usage-log entry per served query (ADR 0011). Recording is a
/// best-effort side effect: it never touches the rendered output, never
/// throws, and can be disabled with <c>REPOCTX_NO_STATS=1</c> — so the
/// determinism contract (identical index + identical query ⇒ byte-identical
/// output) is untouched.
/// </summary>
public static class UsageRecorder
{
    /// <summary>Set (non-empty) to disable usage recording entirely.</summary>
    public const string DisableVariable = "REPOCTX_NO_STATS";

    /// <summary>Whether recording is enabled in this process.</summary>
    public static bool Enabled =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableVariable));

    /// <summary>
    /// Appends a record for a served response; <paramref name="rendered"/> is
    /// counted with the same tokenizer the index uses.
    /// </summary>
    public static void Record(
        RepoLayout layout, string command, string source, string rendered,
        int replacedTokens = 0, int? files = null, int? unchanged = null)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            UsageLog.Append(UsageLog.PathFor(layout), new UsageRecord
            {
                Ts = DateTimeOffset.UtcNow,
                Command = command,
                Source = source,
                Served = Tokens.Count(rendered),
                Replaced = replacedTokens,
                Files = files,
                Unchanged = unchanged,
            });
        }
        catch (Exception)
        {
            // Stats must never break or slow down a query; a lost record is fine.
        }
    }
}
