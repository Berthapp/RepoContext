namespace RepoContext.Core.Configuration;

/// <summary>Whether a managed file was created, updated, left unchanged or skipped.</summary>
public enum AgentFileChange
{
    /// <summary>The file did not exist and was created with the managed block.</summary>
    Created,

    /// <summary>The file existed; the managed block was inserted or replaced in place.</summary>
    Updated,

    /// <summary>The managed block was already present and identical; nothing was written.</summary>
    Unchanged,

    /// <summary>
    /// The file exists and RepoContext does not merge into it (client configuration
    /// files it does not own). Nothing was written.
    /// </summary>
    Skipped,

    /// <summary>The managed block was removed; the rest of the file was preserved.</summary>
    Removed,

    /// <summary>Nothing to remove: no managed block was present.</summary>
    Absent,
}

/// <summary>The outcome of ensuring the RepoContext block in one agent-instructions file.</summary>
public sealed record AgentFileResult(string FileName, AgentFileChange Change);

/// <summary>
/// The RepoContext usage instructions written into agent-instruction files, in
/// the two sizes an agent actually pays for.
/// </summary>
/// <remarks>
/// <para>
/// Instruction text is not free. A block in <c>CLAUDE.md</c>, <c>AGENTS.md</c> or
/// <c>.github/copilot-instructions.md</c> is loaded into <b>every</b> prompt of
/// every session, whether or not the task needs RepoContext at all — so a full
/// protocol description there is a fixed tax on all work in the repository.
/// Clients that support on-demand instructions (Claude Code skills, Cursor rules
/// with <c>alwaysApply: false</c>, Windsurf rules) load a file only when its
/// description matches the task.
/// </para>
/// <para>
/// Hence two texts. <see cref="PointerBlock"/> is the always-loaded minimum: what
/// the tool is, the one call to start with, and where the rest lives. It is
/// budgeted by a test, like every other token figure in this tool.
/// <see cref="Playbook"/> is the full protocol, delivered on demand — as a skill
/// or rule file, or by running <c>repoctx guide</c>. Clients with no on-demand
/// mechanism get <see cref="Block"/>, the full protocol inline, because for them
/// the alternative is not a cheaper prompt but a worse one.
/// </para>
/// <para>
/// All three are constants (no timestamps, no repository state), so writes are
/// idempotent and the agent's prompt-prefix cache survives every re-index — the
/// byte-stability invariant of ADR 0012 §4.
/// </para>
/// </remarks>
public static class AgentInstructions
{
    /// <summary>Agent-instruction files managed by <c>repoctx init --agents</c>.</summary>
    public static IReadOnlyList<string> DefaultFileNames { get; } = ["CLAUDE.md", "AGENTS.md"];

    // The marker text is part of the on-disk contract: it is how a block written
    // by an older version is found and replaced instead of duplicated. It names
    // `init` for that reason alone — `integrate` maintains the same region.
    internal const string BeginMarker = "<!-- BEGIN RepoContext (managed by `repoctx init`) -->";
    internal const string EndMarker = "<!-- END RepoContext -->";

    /// <summary>The full protocol, markers included. For clients without on-demand loading.</summary>
    public static string Block { get; } = Wrap(BuildPlaybook());

    /// <summary>The always-loaded pointer, markers included.</summary>
    public static string PointerBlock { get; } = Wrap(BuildPointer());

    /// <summary>
    /// The full protocol without markers — the body of a skill/rule file and the
    /// output of <c>repoctx guide</c>.
    /// </summary>
    public static string Playbook { get; } = BuildPlaybook();

    /// <summary>
    /// Ensures the always-loaded pointer is present in <paramref name="fileName"/>
    /// under <paramref name="root"/>. This is what <c>init --agents</c> writes, and
    /// it is byte-identical to what <c>integrate</c> writes into the same files, so
    /// the two commands never fight over the block.
    /// </summary>
    public static AgentFileResult Ensure(string root, string fileName) =>
        Ensure(root, fileName, PointerBlock, preamble: null);

    /// <summary>
    /// Ensures <paramref name="block"/> is present in <paramref name="relativePath"/>
    /// under <paramref name="root"/>. Creates the file if missing (prefixed with
    /// <paramref name="preamble"/>, e.g. YAML front matter a client requires),
    /// replaces an existing managed block in place, or appends the block to a file
    /// that has none. Content outside the markers is never touched.
    /// </summary>
    public static AgentFileResult Ensure(
        string root, string relativePath, string block, string? preamble)
    {
        string path = Combine(root, relativePath);

        if (!File.Exists(path))
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, (preamble ?? string.Empty) + block + "\n");
            return new AgentFileResult(relativePath, AgentFileChange.Created);
        }

        // Markers are single-line and contain no line breaks, so they can be
        // located in the original text without normalizing line endings — this
        // keeps the surrounding content (and its endings) byte-for-byte intact.
        string existing = File.ReadAllText(path);
        if (TryLocateBlock(existing, out int begin, out int endStop))
        {
            if (existing[begin..endStop] == block)
            {
                return new AgentFileResult(relativePath, AgentFileChange.Unchanged);
            }

            File.WriteAllText(path, existing[..begin] + block + existing[endStop..]);
            return new AgentFileResult(relativePath, AgentFileChange.Updated);
        }

        // No managed block yet: append it, separated by a blank line.
        File.AppendAllText(path, Separator(existing) + block + "\n");
        return new AgentFileResult(relativePath, AgentFileChange.Updated);
    }

    /// <summary>
    /// Reports what <see cref="Ensure(string, string, string, string?)"/> would do,
    /// without writing anything.
    /// </summary>
    public static AgentFileChange Inspect(string root, string relativePath, string block)
    {
        string path = Combine(root, relativePath);
        if (!File.Exists(path))
        {
            return AgentFileChange.Created;
        }

        string existing = File.ReadAllText(path);
        return TryLocateBlock(existing, out int begin, out int endStop) && existing[begin..endStop] == block
            ? AgentFileChange.Unchanged
            : AgentFileChange.Updated;
    }

    /// <summary>
    /// Removes the managed block from <paramref name="relativePath"/>, preserving
    /// everything outside the markers. A file left holding nothing but whitespace
    /// is deleted, because RepoContext created it in that case.
    /// </summary>
    public static AgentFileResult Remove(string root, string relativePath)
    {
        string path = Combine(root, relativePath);
        if (!File.Exists(path))
        {
            return new AgentFileResult(relativePath, AgentFileChange.Absent);
        }

        string existing = File.ReadAllText(path);
        if (!TryLocateBlock(existing, out int begin, out int endStop))
        {
            return new AgentFileResult(relativePath, AgentFileChange.Absent);
        }

        // Also drop the blank line that separated the block from what precedes it,
        // so repeated add/remove cycles do not accumulate empty lines.
        int from = begin;
        while (from > 0 && (existing[from - 1] == '\n' || existing[from - 1] == '\r'))
        {
            from--;
        }

        int to = endStop;
        while (to < existing.Length && (existing[to] == '\n' || existing[to] == '\r'))
        {
            to++;
        }

        string remaining = existing[..from] + existing[to..];
        if (remaining.Trim().Length == 0 || IsOnlyFrontMatter(remaining))
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, remaining.EndsWith('\n') ? remaining : remaining + "\n");
        }

        return new AgentFileResult(relativePath, AgentFileChange.Removed);
    }

    /// <summary>
    /// Whether nothing is left but the front matter RepoContext itself wrote when
    /// it created a skill or rule file. Such a file is inert — a description with
    /// no instructions — so removing the block removes the file. The check is
    /// deliberately narrow: the document must open with the delimiter, and the
    /// only thing allowed after the closing one is whitespace.
    /// </summary>
    private static bool IsOnlyFrontMatter(string text)
    {
        const string delimiter = "---";
        if (!text.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        int closing = text.IndexOf("\n---", delimiter.Length, StringComparison.Ordinal);
        if (closing < 0)
        {
            return false;
        }

        return text[(closing + 4)..].Trim().Length == 0;
    }

    private static string Combine(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Separator(string existing) =>
        existing.Length == 0 ? string.Empty : existing.EndsWith('\n') ? "\n" : "\n\n";

    private static bool TryLocateBlock(string text, out int begin, out int endStop)
    {
        begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = begin < 0 ? -1 : text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        endStop = end < 0 ? -1 : end + EndMarker.Length;
        return begin >= 0 && end > begin;
    }

    private static string Wrap(string body) =>
        BeginMarker + "\n" + body + "\n" + EndMarker;

    /// <summary>
    /// The always-loaded text. Every line here is paid for on every prompt in
    /// the repository, so it carries only what an agent needs to decide to use
    /// the tool at all, plus the single call that answers most tasks.
    /// </summary>
    private static string BuildPointer() =>
        string.Join('\n',
            "## RepoContext",
            string.Empty,
            "This repository is indexed by RepoContext (`repoctx`), a local, offline context",
            "engine built to save you tokens. Prefer it over reading files broadly. Start a",
            "task with one budgeted call and escalate only on a concrete gap:",
            string.Empty,
            "```",
            "repoctx context \"<task>\" --detail auto --response-budget-tokens 2000 --format md",
            "```",
            string.Empty,
            "Windows: if PowerShell refuses `repoctx`, call `repoctx.cmd` instead.",
            string.Empty,
            "For the full protocol — evidence reuse, budgets, outlines, impact, memory — run",
            "`repoctx guide`.");

    /// <summary>
    /// The full protocol, delivered on demand.
    /// </summary>
    /// <remarks>
    /// Every step is conditional. The pre-Release-1 wording read as an
    /// unconditional checklist, which cost more calls than it saved, and it told
    /// agents to echo the hash printed next to a <i>partial</i> result — a
    /// whole-file possession claim the response never justified (ADR 0015).
    /// </remarks>
    private static string BuildPlaybook() =>
        string.Join('\n',
            "## Getting repository context with RepoContext",
            string.Empty,
            "This repository is indexed by RepoContext (`repoctx`), a local-first, offline",
            "context engine built to save you tokens. Prefer it over reading files broadly;",
            "all token figures it reports are real BPE counts.",
            string.Empty,
            "### Start here",
            string.Empty,
            "```",
            "repoctx context \"<task>\" --detail auto --response-budget-tokens 2000 --format md",
            "```",
            string.Empty,
            "`--detail auto` picks slices for change tasks and outlines for survey questions,",
            "and reports its choice as `detail_reason`. Override it when you know better:",
            "`--detail slices` for source spans, `--detail outline` to survey more files,",
            "`--detail paths` for locations only. Markdown avoids JSON escaping around",
            "embedded code; use `--format json` when a client must parse the envelope.",
            "`--strip-comments` drops comment banners (lossy; line ranges become approximate).",
            string.Empty,
            "### On Windows: `repoctx.cmd`",
            string.Empty,
            "An npm install writes three shims side by side — `repoctx`, `repoctx.cmd` and",
            "`repoctx.ps1` — and PowerShell picks the `.ps1`, which a restrictive execution",
            "policy refuses to load (`PSSecurityException`, \"is not digitally signed\"). When",
            "you see that, do not fall back to reading files broadly: re-issue the same",
            "command as `repoctx.cmd`. Arguments and output are identical, so every",
            "`repoctx` call below applies unchanged.",
            string.Empty,
            "### Escalate only on a concrete gap",
            string.Empty,
            "1. A file you need is missing: `repoctx search \"<term>\" --symbols --format json`.",
            "2. A relevant symbol was not delivered: `repoctx outline <file> --format json`.",
            "3. Dependency or impact questions: `repoctx related <file> --format json`.",
            "4. One exact thing to look up - a ticket or requirement key (`ABC-123`), a",
            "   link, a symbol name or a path: `repoctx trace <ref> --format json`. It returns",
            "   every file that declares or mentions it, with lines and read cost. Use it",
            "   instead of searching prose for a key; `context` also picks a key out of the",
            "   task itself.",
            "5. Unfamiliar boundaries: `repoctx architecture --depth 1 --format md`.",
            "6. After editing: `repoctx changed --patch --format md`; if it reports `stale`,",
            "   run `repoctx index` (fast, incremental) and re-query. Its `impacted` list",
            "   includes documents that describe what you changed - check them before",
            "   declaring the task done.",
            "7. A knowledge gap `context` left open: `repoctx memory search \"<topic>\"",
            "   --format json`. `context` already recalls matching memories automatically.",
            "   After difficult work, store one distilled, non-secret finding with",
            "   `repoctx memory add \"<insight>\" --kind note|decision|constraint --file <path>`;",
            "   verify entries flagged `stale`.",
            "8. Optional orientation for a new, unfamiliar repository: `repoctx prime`. Use it",
            "   only when your client can keep the primer behind a cache breakpoint; otherwise",
            "   skip it. It is byte-identical for unchanged indexed content and calibration.",
            "9. Stop querying once no evidence needed for the task is missing.",
            string.Empty,
            "### Work in one area: `--path`",
            string.Empty,
            "`search`, `context` and `trace` take a repeatable `--path <dir-or-glob>` that",
            "narrows the query before ranking. If your task is confined to one part of the",
            "repository, pass it: excluded areas then cost you neither noise nor tokens.",
            string.Empty,
            "### Documents are indexed too",
            string.Empty,
            "Specifications, exported tickets, requirements, API contracts and `.feature`",
            "files are indexed with structure. `repoctx outline <file>` works on them, so",
            "check a document's skeleton before reading it, and `repoctx related <file>`",
            "lists the documents that describe a source file.",
            string.Empty,
            "### Never pay for the same evidence twice",
            string.Empty,
            "- `--session <name>` (or the `REPOCTX_SESSION` environment variable, which your",
            "  MCP configuration may already set) records what you were sent under",
            "  `.repoctx/sessions/`. Later calls return still-valid evidence as zero-cost",
            "  markers with nothing to echo. This is the cheapest form of reuse: it costs no",
            "  output tokens at all.",
            "- `--seen <receipt>` (repeatable) suppresses **exactly** the pointer, span or",
            "  symbol that receipt came from. Other parts of the same file still arrive. Echo",
            "  the `receipt` values from evidence units you already received.",
            "- `--known <path>@<hash>` asserts you hold the **entire** file. Use it only when",
            "  you actually read the whole file. Never derive it from a slice or outline — the",
            "  `hash` next to a partial result identifies the file version, not what you were",
            "  sent.",
            "- Reused units are acknowledged in `reused` and never consume a `--top` slot, so",
            "  echoing receipts buys new context rather than markers.",
            string.Empty,
            "### Calibration",
            string.Empty,
            "If you use a non-OpenAI model, set `tokens.profile` in `repoctx.config.json`",
            "(e.g. `\"claude\"`) so budgets and counts match your tokenizer.");
}
