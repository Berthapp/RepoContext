# ADR 0012 — M8 token-optimization levers

- **Status:** accepted
- **Date:** 2026-07-15
- **Milestone:** M8 (JSON `schema_version` 2 — additive; index schema unchanged)

## Context

M6 (ADR 0010) made getting working context ~66 % cheaper than reading files,
and M7 (ADR 0011) let each repository measure its own savings. Six levers
remained, all compatible with the non-negotiables (offline, deterministic,
explainable, no LLM/embedding calls, one JSON contract). They target the
costs *around* a response and the costs the current accounting quietly
mismatches — most consequentially for the tool's largest real consumer,
Claude-family agents, whose tokenizer the index does not use.

## Decisions

1. **Query-time token calibration (`tokens.profile` / `tokens.factor`).** The
   index keeps raw `o200k_base` counts (ADR 0010); a `TokenScale` resolved
   from config is applied where budgets are *charged* and counts are
   *reported* — `context`, `outline`, the `stats` ledger, `prime`. Profiles
   carry measured average factors (`o200k`/`openai` = 1.0, `claude` = 1.2;
   o200k undercounts Claude tokenization on typical source by ~15-25 %); an
   explicit `factor` overrides. Applying the scale at query time (not index
   time) means switching model families never requires a re-index, and the
   raw counts stay a stable substrate. Unknown profile names degrade to raw
   counts rather than to silently wrong ones. `context` reports the active
   label as `token_profile`. Scaling rounds **up** so budgets never
   undershoot. Determinism holds: the scale is pure config input.

2. **`--session` server-side known-set.** `--known path@hash` (ADR 0010) is
   echoed by the agent, i.e. model *output* tokens — priced several times
   input tokens on every major model. `context --session <name>` (and MCP
   `get_context` `session`) tracks the delivered-file set in
   `.repoctx/sessions/<name>.json`: after a response, delivered slices and
   confirmed unchanged markers are recorded (an outline skeleton or a bare
   pointer does not put the file in the caller's hands, so it earns no
   entry); later calls load it and return still-matching files as zero-cost
   `unchanged` markers with no hash echoing. Explicit `--known` entries win
   over session state. The session file is caller input exactly like
   `--known`, so identical index + query + session file ⇒ identical output;
   saving is best-effort and never affects the rendered bytes.

3. **`changed --patch` delta hunks.** The other half of never-pay-twice:
   `--known` made *unchanged* files free, but any edit still cost a full
   re-read. `changed --patch` (MCP `get_changes` `patch=true`) diffs the
   working tree against the indexed content and returns unified-style hunks
   (2 context lines) plus `patch_tokens` vs `file_tokens`. The diff is Myers'
   O(ND) over lines with the common prefix/suffix trimmed and a hard
   edit-distance cap; beyond the cap (or on very large inputs) it falls back
   to a single whole-region hunk — still correct, less minimal. Trailing
   newlines are normalized on both sides so representation differences never
   produce phantom hunks. Delivered hunks earn the replaced-read credit in
   the ledger. Deterministic for a given tree and index.

4. **`prime` — a cache-stable repository primer.** Prompt caching is a
   byte-prefix match and cached input tokens cost ~0.1×, so a repo
   orientation block only pays off if it stops changing. `prime` emits
   languages, a depth-1 layout, entrypoints and the most-imported files with
   outlines/hashes/read costs — with **no** timestamps or state hash,
   path/name ordering throughout (never score order), and volatile aggregates
   (LOC, file counts) **quantized to two significant digits**, so an ordinary
   edit leaves the primer byte-identical. Per-file code facts change only when
   that file changes; reported read costs also change when token calibration
   changes. It defaults to `md` (the primer is a prompt block, not
   a parseable document). An integration test pins byte-stability by
   re-indexing after an in-place edit to a non-key file.

   **Invariant (documented so it is not broken accidentally):** the
   `repoctx init --agents` managed block (`AgentInstructions.Block`) and the
   `prime` output are byte-stable across `index` runs with the same tool,
   indexed content and token calibration. Quantization may deliberately absorb
   small edits; changing calibration changes reported read costs and is an
   explicit cache-key change. Any future feature that refreshes
   either on every index (e.g. embedding a live repo map in `CLAUDE.md`)
   would invalidate the agent's whole prompt-prefix cache on every index and
   is therefore disallowed.

5. **Slice output-lean measures.**
   - **Escape-tax avoidance.** ADR 0010 charges embedded slices in serialized
     form because JSON escaping (`\n`, `\"`, `\\`) is billed — but only JSON
     pays it. `ContextOptions.SerializedCharging` charges raw text for
     `text`/`md` and serialized only for `json`; the CLI sets it from the
     chosen format. The agent instructions and MCP now steer slice work to
     `--format md`, a measured double-digit saving on the dominant payload
     with no loss of content.
   - **Duplicate dedupe.** Byte-identical bundle items (copies, generated
     files sharing a content hash) collapse to a zero-cost `duplicate_of`
     marker pointing at the first occurrence in the same bundle.
   - **`--strip-comments`.** An opt-in lossy transform dropping full-line
     comments and blank runs from slices (outline docs already carry the
     summaries). It is line-based and conservative — lines mixing code and a
     trailing comment are kept intact, so code is never lost — and covers the
     four indexed languages, which share C-family comment syntax. Altered
     items are flagged `stripped` so callers know line ranges are approximate.

6. **Money view in `stats`.** `pricing.inputPerMtok` (+ `currency`) turns net
   saved tokens into a currency figure across text/md/json/html. RepoContext
   ships no built-in rates (they change and vary by model), so the estimate
   is absent unless configured. Net saved is an input-side quantity, so the
   input rate is the right multiplier; the sign is derived from the rounded
   value so a near-zero saving never prints `-$0.00`.

## Consequences

- **Config additions** (`tokens`, `pricing`) are optional with raw-count /
  no-money defaults, so existing configs and their output are unchanged.
- **JSON `schema_version` stays 2**: all new fields are additive and optional
  (`token_profile`, `duplicate_of`, `stripped`, `changed[].hunks` /
  `patch_tokens` / `file_tokens`, the `prime` document, `stats` money
  fields). Consumers that ignore unknown fields are unaffected; the
  determinism guarantee is per-version as before.
- **CLI ↔ MCP parity holds** (ADR 0008): the new options exist on both
  surfaces (`session`, `stripComments`, `patch`), and MCP still returns the
  exact `--format json` bytes.
- **`stats` now reads config** (for `pricing` and, via the recorder, the
  active `tokens` scale). Records store already-scaled served/replaced
  counts, so a profile change is not retroactive — the ledger reflects what
  each call actually cost under the profile in force at record time.
- The Myers diff adds no dependency (hand-written, capped). `prime` reuses
  the architecture and outline engines.
- Re-measure `docs/token-savings.md` when ranking or costing changes; the
  workflow numbers there predate M8's md-charging and session levers.
