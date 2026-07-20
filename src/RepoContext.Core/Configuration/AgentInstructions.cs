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
        return string.Join('\n',
            BeginMarker,
            "## Getting repository context with RepoContext",
            string.Empty,
            "This repository is indexed by RepoContext (`repoctx`), a local-first, offline",
            "context engine built to save you tokens. Prefer it over reading files broadly;",
            "all token figures it reports are real BPE counts. The economical loop:",
            string.Empty,
            "1. Orient once: `repoctx prime` — a cache-stable primer (languages, layout,",
            "   entrypoints, key files). It is byte-identical until code changes, so place it",
            "   at the top of the prompt behind a cache breakpoint and it costs cached-rate",
            "   tokens on every later turn.",
            "2. Working context: `repoctx context \"<task>\" --detail slices --budget-tokens 2000 --format md`",
            "   — ranked source slices packed into the budget, each with reasons and a `hash`.",
            "   Prefer `--format md` for slices: JSON escaping of embedded code is billed and",
            "   adds 10-20 %. Use `--format json` when you need to parse the envelope; add",
            "   `--detail outline` to survey more files for fewer tokens, or `--strip-comments`",
            "   to drop comment banners from slices (lossy; line ranges become approximate).",
            "3. Track a session instead of echoing hashes: `repoctx context \"<task>\" --session <name>`",
            "   remembers the slices it delivered, so unchanged files come back as zero-cost",
            "   markers on later calls without you re-typing `--known` lists (that text is",
            "   output tokens, which cost more than input).",
            "4. Before reading any file: `repoctx outline <file> --format json` — symbols,",
            "   signatures and the exact full-read token cost, at a fraction of that cost.",
            "5. Dependencies and tests: `repoctx related <file> --format json` instead of grep.",
            "6. Find a symbol: `repoctx search \"<term>\" --symbols --format json`.",
            "7. After editing: `repoctx changed --patch --format md` returns just the changed",
            "   hunks (far cheaper than a re-read); when it reports `stale`, run `repoctx index`",
            "   (fast, incremental) and re-query.",
            "8. Never re-derive: `repoctx memory search \"<topic>\" --format json` before exploring",
            "   — a prior session may have distilled the answer already (`context` folds matching",
            "   memories into its bundle automatically). Entries linked to files that changed",
            "   since are flagged `stale`: verify those before trusting them.",
            "9. Remember what you learned: after completing a task, store 1-2 sentence insights",
            "   with `repoctx memory add \"<insight>\" --kind note --file <path>` (kinds: `note`",
            "   = knowledge, `decision` = a why, `constraint` = a warning). Link the files the",
            "   insight is about so staleness stays detectable. Distill hard-won findings only —",
            "   never store what a search could trivially re-answer, and never store secrets.",
            string.Empty,
            "Tip: if you use a non-OpenAI model, set `tokens.profile` in `repoctx.config.json`",
            "(e.g. `\"claude\"`) so budgets and counts match your tokenizer.",
            EndMarker);
    }
}
