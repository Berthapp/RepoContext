namespace RepoContext.Core.Configuration;

/// <summary>Whether an agent-instructions file was created, updated or left unchanged.</summary>
public enum AgentFileChange
{
    /// <summary>The file did not exist and was created with the managed block.</summary>
    Created,

    /// <summary>The file existed; the managed block was inserted or replaced in place.</summary>
    Updated,

    /// <summary>The managed block was already present and identical; nothing was written.</summary>
    Unchanged,
}

/// <summary>The outcome of ensuring the RepoContext block in one agent-instructions file.</summary>
public sealed record AgentFileResult(string FileName, AgentFileChange Change);

/// <summary>
/// Writes the RepoContext usage instructions into agent-instruction files
/// (<c>CLAUDE.md</c>, <c>AGENTS.md</c>) so coding agents know to query the index
/// before reading files broadly.
/// </summary>
/// <remarks>
/// The block is delimited by stable markers and the operation is idempotent:
/// re-running replaces the managed region in place rather than duplicating it,
/// and content outside the markers is preserved untouched. The block content is
/// constant (no timestamps), so repeated runs are byte-stable.
/// </remarks>
public static class AgentInstructions
{
    /// <summary>Agent-instruction files managed by <c>repoctx init --agents</c>.</summary>
    public static IReadOnlyList<string> DefaultFileNames { get; } = ["CLAUDE.md", "AGENTS.md"];

    internal const string BeginMarker = "<!-- BEGIN RepoContext (managed by `repoctx init`) -->";
    internal const string EndMarker = "<!-- END RepoContext -->";

    /// <summary>The managed block, markers included. Constant, so writes are idempotent.</summary>
    public static string Block { get; } = BuildBlock();

    /// <summary>
    /// Ensures the managed block is present in <paramref name="fileName"/> under
    /// <paramref name="root"/>. Creates the file if missing, replaces an existing
    /// managed block in place, or appends the block to a file that has none.
    /// </summary>
    public static AgentFileResult Ensure(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, Block + "\n");
            return new AgentFileResult(fileName, AgentFileChange.Created);
        }

        // Markers are single-line and contain no line breaks, so they can be
        // located in the original text without normalizing line endings — this
        // keeps the surrounding content (and its endings) byte-for-byte intact.
        string existing = File.ReadAllText(path);
        int begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
        {
            int endStop = end + EndMarker.Length;
            if (existing[begin..endStop] == Block)
            {
                return new AgentFileResult(fileName, AgentFileChange.Unchanged);
            }

            File.WriteAllText(path, existing[..begin] + Block + existing[endStop..]);
            return new AgentFileResult(fileName, AgentFileChange.Updated);
        }

        // No managed block yet: append it, separated by a blank line.
        string separator = existing.Length == 0
            ? string.Empty
            : existing.EndsWith('\n') ? "\n" : "\n\n";
        File.AppendAllText(path, separator + Block + "\n");
        return new AgentFileResult(fileName, AgentFileChange.Updated);
    }

    private static string BuildBlock()
    {
        // Kept in sync with the README "Agent integration" snippet.
        //
        // Every step here is conditional. The pre-Release-1 wording read as an
        // unconditional checklist, which cost more calls than it saved, and it
        // told agents to echo the hash printed next to a *partial* result — a
        // whole-file possession claim the response never justified (ADR 0012).
        return string.Join('\n',
            BeginMarker,
            "## Getting repository context with RepoContext",
            string.Empty,
            "This repository is indexed by RepoContext (`repoctx`), a local-first, offline",
            "context engine built to save you tokens. Prefer it over reading files broadly;",
            "all token figures it reports are real BPE counts.",
            string.Empty,
            "Start with one budgeted context call, then escalate only on a concrete gap:",
            string.Empty,
            "1. `repoctx context \"<task>\" --detail slices --response-budget-tokens 2000 --format json`",
            "   — ranked source spans packed into a hard response ceiling. Each span carries",
            "   exact lines, reasons and a `receipt`. Use `--detail outline` to survey more",
            "   files for fewer tokens, `--detail paths` when you only need locations.",
            "2. Only if a file you need is missing: `repoctx search \"<term>\" --symbols --format json`.",
            "3. Only if a file is relevant but the symbol you need was not delivered:",
            "   `repoctx outline <file> --format json`.",
            "4. Only for dependency or impact questions: `repoctx related <file> --format json`.",
            "5. Only for cross-cutting or unfamiliar-boundary work: `repoctx architecture --depth 1 --format md`.",
            "6. Only after you edited files: `repoctx changed --format json`; if it reports",
            "   `stale`, run `repoctx index` (fast, incremental) and re-query.",
            "7. Stop querying once no evidence needed for the task is missing.",
            string.Empty,
            "Never pay for the same evidence twice — and never over-claim what you hold:",
            string.Empty,
            "- `--seen <receipt>` (repeatable) suppresses **exactly** the pointer, span or symbol that",
            "  receipt came from. Other parts of the same file still arrive. Echo the",
            "  `receipt` values from evidence units you already received.",
            "- `--known <path>@<hash>` asserts you hold the **entire** file. Use it only when",
            "  you actually read the whole file. Never derive it from a slice or outline —",
            "  the `hash` next to a partial result identifies the file version, not what you",
            "  were sent.",
            "- Reused units are acknowledged in `reused` and never consume a `--top` slot, so",
            "  echoing receipts buys new context rather than markers.",
            EndMarker);
    }
}
