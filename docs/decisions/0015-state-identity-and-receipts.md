# ADR 0015 — State identity, evidence units and reuse receipts

- **Status:** accepted
- **Date:** 2026-07-23
- **Release:** 1 (work packages Q0, Q1, Q2, Q4; JSON `schema_version` 3)

## Context

A pre-Release-1 audit found a correctness bug at the heart of the token-saving
story. `context --detail slices` returned a *range* of a file but labelled it
with the *file's* content hash, and the generated agent instructions told agents
to echo that hash back through `--known`. Doing so produced an `unchanged`
marker for the whole file — suppressing lines the caller had never been sent.
The saving was real; the evidence was missing.

Three related problems shared a root cause: the system had exactly one notion of
identity (a path/content `state_hash`) and one notion of possession (a file
hash).

1. **Possession was file-shaped, evidence was range-shaped.** There was no way
   to say "I have lines 115-139" as distinct from "I have this file".
2. **Reuse competed with new context.** `--known` markers occupied entries in
   `results`, so echoing the eight top candidates returned eight markers and no
   new evidence.
3. **`state_hash` covered content only.** A parser, chunker, ranking or output
   change altered the answer without moving the fingerprint, so a stale answer
   looked fresh.

A fourth, separate defect: a candidate retained only one best range and the
first N symbols in source order, so a query could match a method and receive the
enclosing class — or miss the method entirely when it sat below the outline cap.

## Decisions

### 1. Five fingerprints instead of one

`Fingerprints` (in `RepoContext.Core.Identity`) defines a distinct value for
each question, each a SHA-256 over a canonical byte layout produced by
`Canonical.Hash`. Every UTF-8 field and repeated record is prefixed with its
decimal UTF-8 byte length plus `:`. No delimiter is reserved, so arbitrary
query text and legal Unix filenames cannot forge a field or record boundary;
`Hash("ab","c") != Hash("a","bc")`.

| Fingerprint | Canonical inputs |
| --- | --- |
| `content_state` | version tag plus sorted, length-prefixed `(path, content_hash)` records (new v3 layout) |
| `analysis_state` | `content_state`, `analysis_producer_version`, index schema version, ranking version, evidence version, effective config hash |
| `worktree_state` | `content_state` plus sorted, length-prefixed `(status, path, current_content_hash)` records; deletions use `-` |
| `evidence_id` | `analysis_state`, canonical analysed query, canonical options, ordered evidence-unit records with reasons |
| `representation_id` | `evidence_id`, output schema version, representation version, format, profile, encoding, surface, hash of the canonical body |

`representation_id` is computed over the rendered body **with its own field
omitted**, which is what keeps it free of self-reference. A benchmark-only
`transport_profile_id` additionally covers the MCP protocol and tool-schema
versions and deliberately excludes the volatile JSON-RPC request ID.

Paths are slash-normalised before hashing and entries are ordinally sorted, so
two checkouts of the same tree at different absolute paths agree on every value.
No timestamp, absolute path, process ID or hash-map iteration order is ever an
input.

### 2. A persisted producer fingerprint, checked fail-closed

`ProducerVersions.AnalysisProducerVersion` is a catch-all string covering every
index-time producer whose output is stored on disk: scanner/classification,
byte decoding, parser, chunker, tokenizer, graph construction and the persisted
content-state layout. `index` persists it in `meta`; a mismatch forces a rebuild
(the incremental content-hash diff would otherwise report everything as
unchanged).

Query commands and MCP tools reject an index whose stored value is stale, the
same way an outdated on-disk schema is rejected. This matters beyond freshness:
it is what stops a receipt derived from different analysis from being honoured.

Ranking and evidence-selection versions are applied *live* and are inputs to
`analysis_state`; representation version belongs to `representation_id`. None
invalidates the stored index. Indexing-only configuration is persisted
separately from live ranking/synonym configuration, so ranking changes move
`analysis_state` without reparsing files.

### 3. Stateless per-unit receipts

Every independently reusable evidence unit — a source span, an outline symbol,
or a pointer — carries a `receipt`: SHA-256 as unpadded base64url (43 chars)
over the receipt version, path, **full** content hash, detail kind, unit kind,
exact line range, symbol identity, a hash of the canonical source evidence, and
only the producers capable of changing that unit. Pointers use a pointer-layout
version; spans use decoder + evidence versions; symbols use decoder + parser +
evidence versions.

Deliberately **not** included: ranking, `analysis_state`, or any other global
analysis state. An unrelated file changing elsewhere must not invalidate
evidence the caller still legitimately holds.

There is no receipt database, no prefix expansion and no history-dependent
lookup. The engine recomputes candidate receipts from current evidence and
compares for equality. Because the value is a pure function of the unit, the
same unit copied to another repository yields the same receipt — safe by
construction, so "foreign repository" is not a failure class.

Full digests cost more tokens than prefixes, and that is the intended trade:
prefixes would require persistent state, ambiguous expansion and
collision-dependent behaviour.

**Fail-closed everywhere.** A malformed receipt is discarded before comparison,
so a typo can never coincidentally suppress evidence. A well-formed receipt that
matches nothing suppresses nothing.

### 4. Reuse is acknowledged, never delivered as a result

`--seen <receipt>` (CLI, repeatable) and `seen` (MCP) move matched units out of
`results` into a separate `reused` collection.

- `top` caps **new** entries only; reused units never consume a slot.
- Mixed possession works: acknowledging one span of a file still delivers that
  file's other spans. This is the audited bug's direct fix.
- An item with several units omits the item-level `receipt`. Suppressing one
  span must not be expressible as suppressing the whole file.
- `reused` is a deterministic, bounded prefix. `reused_count` reports the true
  total and `reused_omitted` the remainder, so reuse metadata cannot make the
  response unbounded.
- Reused entries are **not** free: they occupy real tokens in the rendered
  document and are charged by the Q3 response budget like everything else.

`--known <path>@<hash>` survives the compatibility window as an explicit
assertion that the caller holds the **entire** file. When both options name the
same file the full-file assertion wins and the file is acknowledged once. A
future rename to `--known-file` is documented but not made here.

Because a matched `--known` file is now acknowledged rather than delivered, the
v2 item field `unchanged` no longer exists in v3.

### 5. Query-aware evidence selection

A candidate now retains several hits per channel instead of one best range, with
a per-file, per-channel cap (8) so one repetitive file cannot consume the global
limit. Evidence identity is the channel plus the exact range and heading — never
a SQLite row ID, which is not stable across rebuilds. Scores combine with `max`,
never `sum`, so exact duplicate evidence cannot double-weight a candidate.

**Outlines** pin query-matched symbols first, then containers proven by
source-range containment, then plain structure in source order — while emitting
in source order so the skeleton still reads like the file. Each selected symbol
declares its `role` (`match` or `container`). This is what keeps a matched
symbol visible past the 12-symbol cap. Role is query-relative explanation
metadata and is excluded from the symbol receipt, so a receipt from standalone
`outline` remains reusable by focused context.

**Slices** return up to `MaxSpans` (default 3) non-overlapping, symbol-aligned
`spans`, each with its own exact range and receipt. A symbol whose range
contains another matched symbol is treated as scaffolding and is *not* embedded
as a source span: embedding a class body to restate what the contained method
already says costs hundreds of tokens for no added evidence. Containers still
appear as signature-level scaffolding in outlines. On the evaluation corpus this
keeps the matched method while dropping a redundant enclosing body. The earlier
audit's 1,235 → 561 prototype is not claimed as a Release 1 measurement because
no raw pre-change artifact was preserved.

Release 1 scaffolding is limited to containment proven by indexed source ranges.
Import and type-use scaffolding needs facts the index does not carry and is
deferred to A3.

## Consequences

- **Breaking (v3):** item `unchanged` is gone; `--known` matches move to
  `reused`; multi-span items omit `start_line`/`end_line`/`snippet` rather than
  inventing a synthetic enclosing range.
- **Deprecated but retained for one window:** top-level `state` and
  `estimated_tokens`, item `estimated_tokens`/`file_tokens`, and the single-span
  trio. New consumers read `content_state`, `content_tokens`,
  `projected_read_tokens` and `spans`.
- Existing indexes are rejected by query commands until `repoctx index` rebuilds
  them with current producer and indexing-configuration metadata.
- More version constants require discipline: a parser or evidence change that
  forgets to bump one will serve stale receipts. The bump is the invalidation
  mechanism, so it belongs to the change that causes it.
- Receipts are longer than a short hash. That is the price of statelessness.

## Alternatives rejected

- **Truncated receipt prefixes.** Cheaper on the wire, but they require a
  receipt store to expand against and make behaviour collision-dependent.
- **Keeping markers inside `results`.** Would have preserved the v2 shape but
  left the starvation bug: `count` would conflate new evidence with
  acknowledgements.
- **Including ranking state in receipts.** Simpler to compute, but an unrelated
  edit anywhere would invalidate evidence the caller still holds, destroying the
  saving the feature exists to deliver.
- **One combined `state` covering everything.** Rejected as the original defect:
  it cannot express which of content, analysis, worktree or encoding moved.
