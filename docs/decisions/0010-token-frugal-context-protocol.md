# ADR 0010 — M6 token-frugal context protocol

- **Status:** accepted
- **Date:** 2026-07-11
- **Milestone:** M6 (JSON `schema_version` 2, index schema v4)

## Context

ADR 0009 made the responses lean; M6 attacks the much larger cost *around*
them. Measured on this repository, an agent that asks `context` for pointers
and then reads the top three files pays ~886 + ~5,336 = **~6,222 tokens** —
the reads, not the responses, dominate. Three further problems: token figures
were a bytes/4 guess (±40 % on indented code), agents re-paid for files they
already held, and after editing they had no cheap way to learn what went
stale.

## Decisions

1. **Real token counts, stored in the index (schema v4).** `index` counts
   every file with a real BPE tokenizer — `o200k_base` via
   `Microsoft.ML.Tokenizers` + its data package, so the vocabulary is embedded
   and counting is **offline and deterministic**. `files.token_count` is the
   exact full-read cost; all `estimated_tokens` fields now carry real counts
   (the name stays: counts still approximate other model families' tokenizers,
   typically within ~10-15 %).
2. **Schema migrations.** The v4 bump requires new columns, so a rebuild now
   drops and recreates the data tables (`IndexStore.Reset`). Query commands
   and MCP tools refuse an index written by another schema version with
   "Index schema is outdated. Run 'repoctx index'." and exit code 2 — the
   index is not usable, which is what F7's "no index" means.
3. **`outline <file>` + `repoctx.get_outline`** — a file's skeleton from the
   symbols table: signatures, line ranges, one-line doc summaries (capped at
   140 chars), content `hash`, and the exact full-read token cost. Measured:
   the `ContextEngine.cs` outline costs 1,111 tokens vs 3,256 for the file —
   an agent decides *whether* to read for a third of the price.
4. **Progressive disclosure: `context --detail paths|outline|slices`.**
   `paths` (default) returns pointers with real full-read costs; `outline`
   embeds capped symbol skeletons (12 per file + `symbols_omitted`); `slices`
   embeds the best-matching source slice — symbol range first, else best FTS
   chunk, else preamble, capped at 80 lines and reconstructed from content
   chunks so `start_line..end_line` matches the text exactly. `--snippets`
   remains as an alias for `--detail slices`.
5. **Budgets charge what the agent actually consumes.** `paths`: the
   full-read cost (the follow-up read dominates). `outline`/`slices`: the
   embedded content **in serialized form** (JSON escaping adds 10-20 % on
   code and is billed) plus a per-item envelope estimate (path + reasons +
   flat field allowance). A too-big candidate is skipped, not a stop signal —
   smaller files further down still fill the budget; at least one item is
   always returned. Measured accuracy: charged 1,986/1,991 vs actual response
   2,110/2,151 (**93-94 %**, remainder is the fixed document header).
6. **Never pay twice: `--known <path>@<hash>` / MCP `known`.** Every item
   carries a short (12-hex) content `hash`; the document carries the index
   `state` hash (SHA-256 over all path+hash pairs, stored as `meta.state_hash`
   at index time). Echoing hashes back marks still-matching files
   `unchanged: true` at zero token charge — and the freed budget pulls in
   files that did not fit before. Determinism is preserved because the known
   set is caller input: identical inputs ⇒ identical output.
7. **`changed` + `repoctx.get_changes`** — the incremental indexer's
   scan-and-hash diff without the write: added/modified/deleted vs the index,
   `stale` flag, current `state`, and impacted dependents (files that import
   or test a changed file, graph-reason capped per ADR 0009). 154 tokens on a
   clean tree.
8. **`architecture --depth N`** (default 3): `--depth 1` is a ~300-token
   orientation an agent can afford at session start.
9. **The instruction layer teaches the loop.** `repoctx init --agents` blocks,
   the README snippet and the MCP `ServerInstructions` all prescribe the same
   economical workflow: orient → budgeted context → outline before reading →
   `related` instead of grep → `changed` after edits → echo `known` hashes.

## Measured effect (this repository, 75 files, o200k counts)

Task: "improve token budget packing in the context engine".

| Workflow | Tokens |
| --- | ---: |
| Pointers + read top-3 files (pre-M6 loop) | 886 + 5,336 = **6,222** |
| `context --detail slices --budget-tokens 2000` (3 best slices embedded) | **2,110** |
| `context --detail outline --budget-tokens 2000` (7 files surveyed) | **2,151** |
| Follow-up with `--known` (3 markers + 5 new files, same budget) | 2,469 |
| `outline` of the 3,256-token main file | 1,111 |
| `changed` on a clean tree / `architecture --depth 1` | 154 / 303 |

## Consequences

- New Core dependencies (`Microsoft.ML.Tokenizers` + o200k data, ~2 MB
  embedded) — offline, deterministic, allowed by the no-network constraint;
  the `--network none` CI job now also proves the tokenizer never downloads.
- Indexing tokenizes changed files only (incremental diff unchanged); a full
  rebuild of this repository stays well under a second per file budget.
- JSON `schema_version` 2: additive fields (`hash`, `state`, `detail`,
  `omitted`, `file_tokens`, `unchanged`, `symbols`, `snippet` semantics) and
  the `estimated_tokens` meaning refined to "real count at the chosen
  detail". MCP `get_context` replaces `snippets` with `detail`/`known`.
- A slice of a tiny file can legitimately cost more than the bare file read
  (envelope); the `file_tokens` field makes that visible to the agent.
- `docs/token-savings.md` documents the workflow benchmark; re-measure there
  when ranking or costing changes.
