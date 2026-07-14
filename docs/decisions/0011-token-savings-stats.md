# ADR 0011 — token-savings dashboard (`repoctx stats`)

- **Status:** accepted
- **Date:** 2026-07-14
- **Milestone:** M7 (JSON `schema_version` stays 2)

## Context

RepoContext's value proposition is saved tokens, but the proof lived only in
hand-run benchmarks (`docs/token-savings.md`). Users should see what repoctx
saved *them*, on *their* repository, from *their* real usage. That requires
(a) recording per-query token figures locally and (b) a dashboard that
aggregates them.

## Decisions

1. **A conservative, explainable savings ledger.** Every served query records
   two real (o200k) figures:
   - `served` — what the rendered response itself costs.
   - `replaced` — full-read tokens the response made unnecessary: embedded
     slices and outline skeletons count the file's stored full-read cost
     (`files.token_count`), and `unchanged` markers count the re-read they
     avoided. Pointers (`context --detail paths`) and discovery answers
     (`search`, `related`, `changed`, `architecture`) replace nothing and
     record `replaced = 0`, whatever their guidance value over grep.

   **Net saved = Σ replaced − Σ served**, over all recorded calls. The number
   can be negative for discovery-heavy usage — the dashboard reports
   measurements, not marketing. Discovery value is deliberately not credited;
   the reported figure is a floor.
2. **Local JSONL log, never part of the index.** Records append to
   `.repoctx/stats.jsonl` (one compact JSON line each: `v`, `ts` UTC,
   `command`, `source` cli|mcp, `served`, `replaced`, `files`, `unchanged`).
   The scanner already skips `.repoctx/`, and the directory is git-ignored, so
   recording affects neither the index state hash nor `changed`. Reading is
   tolerant: malformed lines are skipped.
3. **Recording is an invisible side effect.** It happens after the response is
   rendered, never modifies output, never throws (best-effort append), and is
   skipped entirely when `REPOCTX_NO_STATS` is set (non-empty). Determinism
   (identical index + identical query ⇒ byte-identical output) is untouched:
   the timestamp lives only in the log, which is usage statistics — the same
   carve-out the constraint grants index statistics. Both CLI commands and MCP
   tools record, under the same command names, distinguished by `source`.
4. **`repoctx stats [--format text|json|md]`** aggregates the log into totals,
   a per-command breakdown (ordered by name) and the most recent 14 *recorded*
   days. The recent-day window anchors on the newest record, not the wall
   clock, and number formatting is culture-invariant — so identical log ⇒
   byte-identical output, in line with the determinism constraint. The JSON
   document keeps `schema_version` 2 (a new additive document type:
   `calls`, `served_tokens`, `replaced_tokens`, `saved_tokens`, `first_day`,
   `last_day`, `commands[]`, `days[]`). A `stats` call is never recorded
   itself. Exit codes: `2` outside an initialized repository, `3` on a bad
   `--format`; an empty log is a successful, empty dashboard.
5. **Visual dashboard: static HTML, never a localhost server.** `stats
   --format html` renders a fully self-contained page (inline CSS/SVG/JS, no
   external resources — CI asserts no `http(s)://` in the output), and
   `stats --open` writes it to `.repoctx/stats.html` and launches the default
   browser (`REPOCTX_NO_LAUNCH` suppresses the launch for headless runs; a
   failed launch is non-fatal). A `--serve` localhost endpoint was rejected:
   the no-network constraint bans socket APIs at compile time, and a browser
   renders local files fine — a server adds attack surface for zero benefit.
   Charts follow the dataviz spec (validated two-slot palette, grouped bars
   per command and per day, tooltips + table view + aria-labels so no value
   is color- or hover-gated, light/dark via tokens). Like every renderer it
   is a pure projection of the log: identical log ⇒ byte-identical HTML.
6. **Free for everyone; funding is sponsorship, not a gate.** The dashboard
   was evaluated as a paid "pro" feature and deliberately shipped free: the
   project stays fully free and open, funded by sponsoring (GitHub Sponsors,
   `.github/FUNDING.yml`). The dashboard doubles as the honest sponsorship
   pitch — it shows each user their own measured savings. No license
   infrastructure exists or is planned. No MCP `get_stats` tool for now — the
   dashboard is human-facing; agents already see per-response costs.

## Measured effect (this repository, 88 files)

Four dogfood queries: `architecture --depth 1` (served 314), `search` (526),
`outline` of a 853-token file (served 787, replaced 853, **+66**),
`context --detail slices --budget-tokens 1500` (served 1,611, replaced 2,169,
**+558**). Net over the four calls: −216 — correctly showing that savings come
from slices/outline/known usage, not from discovery calls.

## Consequences

- Query commands gain a hidden side effect (an appended log line). Failure to
  write is silent by design; a lost record is preferable to a failed query.
- `served` is counted with the same tokenizer per call (~ms, lazy singleton);
  no measurable latency impact against process startup.
- The Privacy story changes slightly: repoctx now *stores* usage data — still
  strictly local, git-ignored, opt-out via `REPOCTX_NO_STATS=1`; nothing is
  ever transmitted. README documents this.
- The ledger undercounts (no credit for avoided grep/exploration) and can
  overcount when an agent would not actually have read a delivered file; both
  biases are documented, the first dominates in practice.
