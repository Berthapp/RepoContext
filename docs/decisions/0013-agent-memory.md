# ADR 0013 — M9 agent memory (short-term, long-term, reasoning)

- **Status:** accepted
- **Date:** 2026-07-20
- **Milestone:** M9 (JSON `schema_version` 2 — additive; index schema unchanged)

## Context

RepoContext markets itself as *project memory*, but until M9 it only
remembered **derived** facts — what the code *is* (symbols, edges, tokens).
Everything an agent *learns* while working (how the pieces fit, why a design
is the way it is, what must not be touched) evaporates when its context
window closes. The next session re-pays the discovery: measured on this
repository, re-deriving "how does budget packing work" costs an outline
(~1,100 tokens) to several reads (~5,000+), while a distilled two-sentence
answer costs ~40-80. That is the same "never pay twice" gap that `--known`
closed for file contents (ADR 0010) — still open for knowledge.

The obvious approach (let an LLM summarize the repo) violates two
non-negotiables: no LLM calls, and no generated prose (product doc §3). The
constraint that *is* compatible: the **agent** authors memories, RepoContext
stores, retrieves, explains and stale-flags them deterministically — the same
role the index plays for README quotes today. A third memory flavor —
"reasoning" — cannot honestly be chain-of-thought storage in an offline tool;
its useful, deterministic form is a **decision/constraint journal**: recorded
whys and invariants that resurface when the files they concern are touched.

Prior art placement: Claude Code's CLAUDE.md, Cursor rules etc. are
per-agent, prose-in-prompt silos with no staleness signal. RepoContext memory
is agent-agnostic, queryable, budget-charged and hash-linked to the code it
describes.

## Decisions

1. **Three memory shapes, one store.** `note` (long-term knowledge),
   `decision` (a recorded why), `constraint` (an invariant/warning) — plus a
   `--session` scope that makes an entry **short-term**: visible only to
   calls carrying the same session name (the session known-set of ADR 0012
   remains the other half of short-term memory). One store keeps recall,
   staleness and curation uniform.

2. **Storage is `.repoctx/memory.jsonl`, not the SQLite index.** Append-only
   JSON-lines: the latest line per id wins and a `deleted` line is a
   tombstone; the log self-compacts once dead lines pile up. Keeping memory
   out of `index.db` means a re-index (or schema migration) can never destroy
   learned knowledge, and the store stays human-auditable. It lives inside
   the git-ignored `.repoctx/` because agent notes about code are exactly as
   sensitive as the code excerpts already there. Reads are lenient (corrupt
   lines skipped), writes are strict (a refused write is reported, never
   silently dropped).

3. **Content-addressed ids; explicit curation.** `id` = first 8 hex of
   SHA-256 over (kind, text, session, linked path set): re-adding the same
   insight *updates* (refreshes hashes) instead of duplicating. The store
   caps at 500 live entries and text at 2,000 characters — a memory must stay
   cheaper than the reads it replaces; when full, `memory rm` (curation) is
   required. No automatic eviction: silently forgetting is worse than
   refusing.

4. **Staleness is hash-based, never clock-based.** `--file` links record the
   file's short content hash at write time. At every retrieval the stored
   hash is compared with the index; any drift (or a vanished file) flags the
   entry `stale` with the drifted paths listed. Query output therefore stays
   a pure function of store + index + query — no timestamps, no decay, fully
   deterministic. (`created` is stored and echoed verbatim, but never ranked
   or filtered on.)

5. **Deterministic, explainable recall.** `memory search` scores by matched
   query-term fraction — text and tag hits count 1, a hit only in a linked
   path counts 0.5 — through the same `QueryAnalyzer` (stop words, config
   synonyms) as `context`. Ordering is score DESC, created DESC, id ASC;
   list mode (no query) is created DESC, id ASC. Every hit carries reasons
   (`term:`, `tag:`, `file:`).

6. **`context` folds memories in.** Visible entries (long-term ∪ active
   session's) are scored 70 % term match / 30 % linkage to the bundle's own
   scored candidates (reason `linked:<path>`); at most **3** memories are
   packed into a reserve of at most **a fifth** of `--budget-tokens`,
   charged like slices (serialized form under JSON, raw under text/md, plus
   an envelope) — files get whatever the reserve did not actually consume.
   Too-big memories are skipped, never a stop signal — and when the
   always-admitted first file overshoots the reduced file budget (the one
   sanctioned overshoot, ADR 0010), memories yield: the lowest-ranked are
   dropped until the bundle fits the budget again, so memory can never widen
   the pre-M9 budget exception. `--no-memory` (MCP `includeMemory=false`)
   opts out. A repo without memories renders byte-identically to pre-M9
   output.

7. **CLI ↔ MCP parity, with one deliberate exception.** `memory add/search`
   exist as `repoctx.memory_add` / `repoctx.memory_search` returning the
   exact `--format json` bytes (ADR 0008). `memory rm` stays CLI-only:
   deletion of team knowledge is curation and belongs under human
   supervision.

8. **The ledger stays conservative.** `memory` responses record served
   tokens but claim **no** replaced-read credit (like the discovery
   commands, ADR 0011) — the savings claim of a recalled insight is real but
   not measurable as a specific avoided read.

9. **The instruction layer teaches the habit.** The `init --agents` block,
   README snippet and MCP `ServerInstructions` gain the two missing verbs:
   *recall before exploring* and *remember after finishing* — 1-2 distilled
   sentences, linked files, no secrets, no trivia. Without the write habit
   the store stays empty; this is the same adoption lever that made the
   economical loop work (ADR 0010 §9).

## Consequences

- **JSON `schema_version` stays 2**: the `memory` documents and the
  `context` document's optional `memories` array are additive; consumers
  ignoring unknown fields are unaffected, and existing outputs are
  byte-identical when no memories exist.
- **No index schema bump, no config additions.** Limits (500 entries, 2,000
  chars, 3 per bundle, budget/5 reserve) are constants documented here;
  making them configurable is deferred until real usage argues for it.
- Memory quality is the agent's responsibility — the tool guarantees
  storage, deterministic recall, explanation and staleness, not truth.
  Garbage in is bounded by the caps and made visible by `stale` flags and
  `rm`.
- The store is per-clone (inside git-ignored `.repoctx/`): knowledge does
  not survive a fresh clone and is not shared. A committable export /
  team-server sync is the natural team-tier follow-up (product doc §12) and
  deliberately out of scope here.
- `related` does not yet list memories linking a file; `memory search
  --file <path>` covers the need. Revisit if usage shows the extra hop
  matters.
- Version bumps to 0.5.0 (contract addition while pre-1.0).
