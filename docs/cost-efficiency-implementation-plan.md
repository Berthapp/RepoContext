# RepoContext Cost-Efficiency Implementation Plan

Status: implementation handoff

Prepared from repository audit: 2026-07-23

Scope: local, deterministic improvements that reduce agent tokens, tool calls,
full-file reads, and repeated repository analysis without meaningful quality
loss

## How to use this plan

This is an ordered implementation backlog, not a request to land every item in
one change.

The coding agent should:

1. Treat **Release 1** as the authorized implementation target unless a later
   release is named explicitly.
2. Implement one work package at a time, keeping each package independently
   reviewable.
3. Start each package with a failing test or baseline measurement, then make
   the smallest product change that satisfies its acceptance criteria.
4. Stop at every release gate and report changed files, test results, before /
   after measurements, unresolved risks, and any contract changes.
5. Build and test the CLI from this source tree. Do not assume a globally
   installed `repoctx` executable has the same behavior as the checked-in code.
6. Preserve existing user configuration and unrelated working-tree changes.

Release 1 is deliberately limited to the evaluation harness and the five P0
correctness / efficiency packages. Larger indexing, graph, and experimental
work must not be mixed into it.

## Non-negotiable constraints

Every implementation in this plan must satisfy all of the following:

- No LLM calls or model-hosting dependencies.
- No embeddings, vector databases, approximate-nearest-neighbor indexes, or
  semantic APIs backed by a model.
- No runtime network access, telemetry, update checks, or remote services.
- Source code, repository metadata, query history, and usage data never leave
  the machine.
- All decisions are deterministic and explainable from local inputs.
- Repeating a command against the same content, configuration, tool version,
  and options produces byte-stable machine output, except for explicitly
  documented local usage timestamps.
- Sensitive-file exclusions and `.gitignore` handling remain fail-closed.
- Token savings may not be bought by a meaningful loss of relevant files,
  relevant symbols, necessary source spans, impact edges, or task success.
- New optional heuristics must expose their reasons and support disabling.
- Query commands should not mutate semantically relevant repository state.

Add or retain CI tests that reject prohibited network APIs and dependencies.
Any new parser or static-analysis dependency must work entirely from packaged
local assets.

## Current baseline and audited failure modes

The existing system already has a strong base: SQLite FTS5, SHA-256 incremental
file indexing, real `o200k_base` token counts, tree-sitter symbols, outlines,
source slices, an import/test graph, explained ranking, change detection,
architecture output, CLI/MCP surfaces, and a local usage ledger.

Audit baseline:

- Source commit:
  `10c04481d1b3e08478662aafe9774a11a3ebe02e`
  (`claude/token-savings-dashboard`).
- Environment: Windows, .NET SDK 10.0.301, `REPOCTX_NO_STATS=1` for measured
  RepoContext queries.
- 131 core tests passed.
- 52 non-MCP integration tests passed.
- 12 MCP integration tests passed.
- 195 tests passed in total.
- The full integration suite is slow because MCP tests repeatedly start
  separate server processes; bounded batches passed.
- The working tree was clean before this plan was added.

The implementation must first turn these audit findings into regression tests:

1. **Unsafe known-context reuse.** A slice of `ContextEngine.Budget` returns the
   full-file hash. Echoing that hash on a later query for `EnvelopeTokens`
   produces an `unchanged` marker even though those lines were never delivered.
2. **Known markers consume result slots.** If all eight top candidates are
   echoed through `--known`, the response can contain eight markers and no new
   files even though additional relevant candidates exist.
3. **Outlines are query-blind.** A query may match a symbol below the first
   source-ordered outline symbols, while the returned outline omits that symbol.
4. **A single range can be the wrong range.** A query for changing `Budget` can
   select the surrounding class or a different FTS chunk rather than the
   `Budget` method itself.
5. **A tight budget is not hard.** The first item is always admitted. A
   100-token paths request can therefore imply roughly 3,532 tokens, and slices
   can exceed the requested limit too.
6. **Index no-ops still do global work.** Unchanged files skip parsing, but the
   graph is cleared, all indexed source files are reread, and all edges are
   rebuilt on every index command.
7. **Change detection repeats work and has shallow impact.** It scans and hashes
   the tree again, reports one reverse hop, omits useful impact for added files,
   and does not retain the newly computed worktree hashes.
8. **Repository state is underspecified.** `state_hash` covers path and content
   hashes but not configuration, index schema, parser behavior, graph behavior,
   ranking behavior, or output representation.
9. **Default roots can omit tests.** The default include roots are `src`, `app`,
   `lib`, and `docs`; consequently `includeTests: true` does not include a
   top-level `tests` directory.
10. **MCP has avoidable overhead.** Context defaults to unbudgeted `paths`;
    results are JSON serialized inside a text block; tool descriptions and
    schemas cost session tokens; tools are marked non-read-only and
    non-idempotent because local stats are appended.
11. **Usage accounting is optimistic.** Any nonempty outline or slice is
    credited as replacing an entire full-file read. The ledger does not capture
    result identity, query identity, latency, follow-up reads, repeated calls,
    or objective task outcomes.
12. **Generated instructions encourage extra calls.** They prescribe
    architecture, context, outline, related, changed, re-index, and known-state
    steps too broadly instead of making each step conditional.
13. **Documentation has drift.** `CLAUDE.md` names schema version 1 while the
    implementation uses version 2. `init --agents` on an initialized repository
    requires `--force`, which also overwrites the user's configuration.
14. **The published benchmark is not a quality gate.** It is a manual blank
    template plus one measured task, and its reproduction example uses a
    character-count approximation despite describing exact tokenizer counts.

Output-compaction prototypes from the audit:

| Representation change | Before | After | Reduction | Evidence status |
| --- | ---: | ---: | ---: | --- |
| Relaxed JSON escaping, slice result | 2,112 | 1,757 | 16.8% | Audit prototype |
| Relaxed JSON escaping, outline result | 573 | 543 | 5.2% | Audit prototype |
| Grouped `related` JSON | 569 | 295 | 48% | Audit prototype |
| Compact `search` JSON | 531 | 421 | 21% | Audit prototype |
| Columnar outline JSON | 573 | 423 | 26% | Audit prototype |
| Columnar context outline JSON | 2,121 | 1,635 | 23% | Audit prototype |
| Columnar context slices JSON | 2,112 | 1,701 | 19% | Audit prototype |

These are `o200k_base` counts, but the ad-hoc audit did not preserve a complete
command/query/options/output manifest. Therefore they are hypotheses, not
reproducible acceptance baselines. Q0 must rerun and classify every cost number
as:

- **reproduced** — raw input/output artifacts, commit, command, options,
  tokenizer package/version, OS, runtime, and count are preserved;
- **derived** — calculated from reproduced measurements with the formula
  preserved; or
- **hypothesis** — a target that still needs a baseline.

Quality and protocol correctness gates below take precedence over all savings
targets.

## Objective quality and cost gates

Create a deterministic local evaluation suite before changing ranking or output
behavior. It should contain frozen TypeScript/TSX/JavaScript and C# fixtures,
plus focused repository-shaped fixtures for multi-project and top-level-test
layouts.

For each task, store:

- query and options;
- must-find files and acceptable rank bands;
- must-find symbols and source ranges;
- relevant dependency/test edges;
- forbidden sensitive/vendor results;
- maximum useful response tokens;
- expected explanation categories;
- whether a full-file read should still be necessary.

Calculate at least:

- Recall@1, Recall@3, Recall@8, and nDCG@8 for files, at fixed declared token
  budgets.
- Must-find symbol recall.
- Relevant-span recall, required-scaffolding recall, and relevant-line density.
- Dependency/test edge precision and recall.
- Exact serialized response tokens.
- Total simulated workflow tokens, with model-visible and transport accounting
  reported separately.
- Number of calls, repeated calls, repeated bytes/tokens, and full-file reads.
- Cold index time, no-op index time, changed-file index time, bytes read, files
  parsed, and edges recomputed.
- Determinism: repeated outputs and result identifiers must match byte-for-byte.

Use a frozen workflow simulator so savings cannot be manufactured by changing
the simulated agent. Provide at least `locate`, `explain`, `fix`, and `impact`
policies. Each policy:

1. starts with one declared context request;
2. calls symbol search only when a must-find file/symbol is absent;
3. calls focused context/outline only when the file is known but required
   evidence is absent;
4. calls `related` only when a required dependency/test edge is absent;
5. performs a deterministic full-file read only when a labeled required range
   has not been delivered;
6. retains receipts only within that scenario; and
7. stops when all labeled evidence is present or a fixed maximum step count is
   reached.

Count client instructions and MCP tool definitions once per simulated session,
then every call argument, model-visible tool result, and deterministic
follow-up read. Record these immutable accounting layers:

- core result document;
- CLI stdout including its trailing newline;
- MCP model-visible content block;
- MCP JSON-RPC transport envelope;
- amortized server instructions/tool definitions; and
- projected downstream full-file reads.

Q0 must store the workflow policies and accounting rules beside the fixtures.

Global release gates:

- No must-find file, symbol, or source span may disappear.
- There may be zero critical-item misses in any language or task stratum.
- Recall and nDCG may not decline by more than one percentage point separately
  for TypeScript/JavaScript, C#, and each task class; report macro and
  micro-averages so one class cannot hide another.
- Sensitive-file leakage is always zero. Vendor/generated ranking uses
  task-specific relevance labels because vendor code can be intentionally
  queried.
- Relevant-line density is never accepted without unchanged relevant-span and
  required-scaffolding recall.
- Output-only changes must preserve the canonical information decoded from the
  `(request, response)` pair exactly.
- Each cost-oriented package must improve its primary metric by at least 10%,
  unless the package fixes correctness or unlocks later savings.
- Exact operation counters are the primary performance gate. Timing is a
  secondary trend measured in Release mode on an isolated fixture copy with
  three warm-ups and ten measured runs; report median and p95. “Cold” means a
  fresh process and index directory, not an unverifiable empty OS filesystem
  cache.
- Machine-readable outputs must remain deterministic on Windows, Linux, and
  macOS, verified by normalized golden-output CI jobs.

Freeze fixture labels and baselines in a reviewable change before the product
change they evaluate. A product change may not modify its own labels to pass.
Only the plan owner/maintainer may approve a non-critical one-point waiver, with
the affected language/task/fixture and rationale recorded in the relevant ADR.
Critical-item and sensitive-file gates have no waiver.

These static evidence metrics are deterministic proxies for downstream task
success; they do not by themselves prove that an arbitrary model will produce a
correct patch. Behavioral fixtures should additionally identify the expected
changed symbols and local tests/acceptance checks. The evaluation runner itself
must not call an LLM or use a network. Optional manual agent comparisons may
supplement these gates but cannot replace them or run in CI.

## Release map

| Release | Goal | Work packages | Gate |
| --- | --- | --- | --- |
| 1 | Make context reuse, evidence selection, and budgets safe | Q0-Q4 | All P0 red tests pass; exact-token and relevance gates pass |
| 2 | Reduce protocol/tool overhead and improve adoption | Q5-Q8, I1-I3 | Wire tokens and call counts improve with decoded parity |
| 3 | Avoid repeated repository analysis | A1-A2 | No-op and one-file-change benchmarks pass |
| 4 | Make smaller agents sufficient | A3-A6 | Capsule, impact, test, and diagnosis fixtures pass |
| 5 | Evaluate opt-in heuristics | E1-E4 | Each experiment beats baseline and can be disabled |

Rough cumulative effort, excluding review, release, and unknown dependency
work:

| Release | Estimated engineer-days | Confidence |
| --- | ---: | --- |
| 1 | 21-34 | Medium |
| 2 | 21-36 | Low-medium |
| 3 | 20-32 | Low |
| 4 | 40-68 | Low |
| 5 | 17-30 | Low |

These are planning ranges, not delivery promises. Re-estimate after each gate
using the completed package's actuals.

## Quick wins

### Q0 — Deterministic evaluation and full-workflow cost harness

Priority: P0; Release 1, first package.

1. **Current problem:** Existing tests validate contracts and selected ranking
   scenarios, while `docs/benchmark.md` is manual and `docs/token-savings.md`
   covers one task. There is no automated guard against saving tokens by
   dropping useful evidence.
2. **Proposed solution:** Add a local evaluation project or test fixture layer
   with labeled tasks, exact tokenization of complete CLI and MCP payloads,
   simulated agent workflows, ranking/span/edge metrics, and stored baseline
   snapshots. Add a `repoctx benchmark --fixtures` command only if it can reuse
   the same library without complicating the core test runner.
3. **Expected savings:** No direct production savings. It enables safe
   optimization and prevents regressions that would trigger extra reads or LLM
   retries.
4. **Quality and behavior impact:** Makes “no meaningful quality loss”
   enforceable. Agents should see no product behavior change from this package.
5. **Risks and trade-offs:** Frozen labels can overfit; self-repository fixtures
   can drift. Prefer small, purpose-built frozen fixtures and add tasks when a
   real failure is found.
6. **Affected areas:** `tests/RepoContext.Core.Tests/Context`,
   `tests/RepoContext.Integration.Tests`, new evaluation fixtures, test support,
   `docs/benchmark.md`, `docs/token-savings.md`, and CI workflow files.
7. **Effort:** Large, 8-12 engineer-days for the Release 1 corpus, simulator,
   accounting layers, and CI; later languages/tasks extend it incrementally.
8. **Objective measurement:** CI publishes the metrics above; two identical
   runs produce identical results; all 14 audited failure modes have a test or
   an explicitly linked later-package test.

Acceptance criteria:

- Add red reproductions for unsafe `known`, known-slot starvation, missing
  query-matched outline symbols, wrong single slice, and hard-budget overflow.
- Count actual tokenized output, not `wc -c` or bytes/4.
- Include MCP initialization/tool-schema overhead in at least one workflow
  scenario.
- Preserve a baseline manifest containing commit, dirty status, OS/runtime,
  build configuration, exact commands/options, tokenizer version, stats state,
  raw outputs, and metric formulas.
- Record elapsed time and I/O counters separately from deterministic golden
  results so timing variance does not break correctness tests.

### Q1 — Representation-aware reuse receipts

Priority: P0; Release 1.

1. **Current problem:** A file hash proves which file version was indexed; it
   does not prove that the caller received the whole file or a particular
   range/outline. Current instructions encourage agents to echo hashes returned
   with partial content, causing false `unchanged` results.
2. **Proposed solution:** Separate full-file possession from representation
   possession. Add a stateless opaque deterministic receipt for every
   independently reusable evidence unit (slice span, outline symbol, or
   pointer): full SHA-256 encoded as fixed-length base64url over receipt
   version, path, full content hash, detail kind, exact ranges or symbol
   identities, canonical delivered evidence, and the relevant
   parser/producer/transform versions. Do **not** include global analysis or
   ranking state: an unrelated file change must not invalidate evidence the
   caller still possesses. Add repeatable CLI
   `--seen <receipt>` and MCP `seen`. The engine recomputes receipts from current
   evidence; there is no receipt database, prefix expansion, or
   history-dependent lookup. Keep
   `--known <path>@<hash>` temporarily as an explicit assertion that the caller
   independently possesses the complete file; stop generating instructions
   that derive it from slices/outlines. Document an eventual `--known-file`
   rename.
3. **Expected savings:** Repeated identical units should fall to a compact
   receipt marker, commonly saving 80-95% of repeat payload tokens, without
   suppressing unseen lines. New candidates fill the freed space.
4. **Quality and behavior impact:** Removes a serious false-cache hit and makes
   repeated context safe. Agents receive new evidence when a query targets a
   different representation of the same unchanged file.
5. **Risks and trade-offs:** Adds protocol and migration complexity. Receipts
   must be invalidated by parser/evidence changes, not just source edits.
   Full digests cost more tokens than prefixes but avoid persistent state,
   ambiguous expansion, and collision-dependent behavior.
6. **Affected areas:** `ContextEngine`, `ContextResult`, `ContextOptions`,
   `ContextCommand`, `ContextOutput`, `McpTools`, `Hashes`, schema/version
   constants, usage accounting, agent instructions, README, ADR 0010, core and
   integration tests.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** The `Budget`/`EnvelopeTokens` reproduction must
   return the unseen range; an identical second request returns a reuse marker;
   changed delivered evidence, file content, detail/range/lossy options, or
   producer version invalidates the receipt; unrelated repository/ranking
   changes preserve it; repeated-workflow tokens drop at least 50%.

Acceptance criteria:

- Reuse markers do not consume `Top` slots intended for new context.
- Charge marker and response-envelope tokens at their actual serialized cost;
  never label them zero-cost.
- Return reused units in a separate compact `reused` collection or otherwise
  prove that `Top = N` can still deliver up to N new useful items.
- Bound and budget the `reused` collection. If more valid receipts match than
  fit, report aggregate `reused_count` plus a deterministic bounded list; reuse
  metadata can never make the response unbounded.
- Invalid, nonmatching, or stale-producer receipts fail closed and never hide
  content. An identical path/evidence unit copied to another repository may
  safely produce the same stateless receipt; “foreign repository” is not a
  separate failure class.
- If both options name the same file, a valid explicit full-file `--known`
  assertion takes precedence over `--seen`; invalid entries suppress nothing.
- Update `AgentInstructions`, checked-in `AGENTS.md`/`CLAUDE.md`, README, MCP
  server instructions, and drift snapshots in Release 1. Provider-specific
  templates remain in Release 2.
- Test mixed seen/unseen units from one file and many valid seen receipts under
  a tight response budget.
- Every span and independently reusable outline symbol carries its own receipt.
  A single-unit item may repeat that value at item level for convenience. A
  multi-unit item omits the item-level receipt; supplying one span's receipt
  suppresses only that span, and unseen spans from the same file remain
  deliverable.
- Bump the output schema if existing JSON field semantics change.

### Q2 — Query-aware outlines and multi-span evidence

Priority: P0; Release 1.

1. **Current problem:** Candidate state retains one best symbol/chunk per file.
   Outlines take the first symbols in source order, and slices choose one range.
   This can omit the matching symbol or return a broad class/preamble instead
   of the method that answers the query.
2. **Proposed solution:** Add an internal bounded `EvidenceHit`/`EvidenceUnit`
   API used by context generation; do not expose duplicate per-file rows from
   public `search`. Give each hit a stable identity/channel derived from
   file-content hash, hit kind, exact range, and heading/symbol identity; never
   rely on a SQLite row ID. Use starvation-safe per-file and per-channel caps so
   one repetitive file cannot consume the global hit limit. Normalize
   overlapping hits into deterministic evidence units aligned to symbol
   boundaries. For context outlines, pin query-matched symbols first, then their
   range-contained parent/container and high-value structural siblings.
   Standalone `outline <file>` remains source ordered in
   Release 1; a later `--focus <terms>` can opt into the same selector. For
   slices, allow multiple non-overlapping symbol-aligned spans with configurable
   context lines. Pack spans by marginal relevance per exact token cost.
3. **Expected savings:** Fewer corrective `search`, `outline`, and full-file
   reads; expected 20-50% lower follow-up tokens on tasks that touch a method in
   a large file.
4. **Quality and behavior impact:** Higher relevant-symbol and relevant-line
   recall. Smaller agents get explicit, localized evidence rather than needing
   to infer which part of a class matters.
5. **Risks and trade-offs:** Multi-span payloads complicate the wire contract.
   Too many disconnected snippets can remove necessary local context. Cap span
   count, include line numbers/container identity, and use fixtures for
   cross-symbol tasks.
6. **Affected areas:** `ContextEngine.Candidate`, FTS candidate generation,
   `IndexStore.Search`, `SearchHit`, `Outline`, `ContextItem`, JSON/Markdown/text
   renderers, MCP output, parser symbol identities, and context tests.
7. **Effort:** Medium-large, 5-8 engineer-days.
8. **Objective measurement:** 100% must-find symbol recall on fixtures; the
   query-matched symbol appears even when it is below the old outline cap;
   relevant-line density improves by at least 20%; corrective follow-up calls
   decline in simulated workflows.

Acceptance criteria:

- Evidence ordering is deterministic under equal scores.
- Exact duplicate FTS/symbol evidence cannot double-weight a candidate.
- Release 1 scaffolding is limited to containers proven by source-range
  containment and already indexed signatures/docs. Import/type-use scaffolding
  requires explicit source-range facts and is deferred to A3 if those facts are
  unavailable.
- Every scaffolding unit has a declared explanation such as `container`.
- A single-span result remains simple; multi-span output has an explicit
  `spans` representation rather than concatenated ambiguous text.
- In schema v3, legacy `start_line`, `end_line`, and `snippet` remain populated
  only for exactly one span. Multi-span consumers use `spans`; do not invent a
  synthetic enclosing range.
- Tests cover duplicate general/symbol hits, a repetitive file reaching
  per-file caps, mixed seen/unseen spans in one file, and a query match below
  the outline limit.

### Q3 — Exact hard budgets and explicit cost semantics

Priority: P0; Release 1.

1. **Current problem:** The first result bypasses budget enforcement, item costs
   use a framing allowance, and the fixed document/MCP wrapper is outside the
   packer. `paths` also conflates response tokens with projected downstream
   full-file-read tokens.
2. **Proposed solution:** First introduce one shared format-aware cost oracle
   used by packing and rendering. Do not put an exact total-token counter inside
   the document it measures. For each tentative item set, render the canonical
   successful payload, tokenize the exact surface boundary, and accept or skip
   the candidate. Rerender the final set once and assert the same count. This
   terminates after the finite ranked candidate list and has no
   self-referential fixed point. Preserve the current
   `--budget-tokens` cost basis during the compatibility window: paths charge
   projected full reads, while embedded detail charges its legacy content
   basis, but remove the first-item bypass. Add explicit
   `--response-budget-tokens` for a hard model-visible response ceiling and
   `--projected-read-budget-tokens` for downstream pointer reads. Report
   `content_tokens` and `projected_read_tokens`; record exact total response
   tokens out-of-band in the local usage meter/evaluation result after
   rendering. Do not silently reinterpret existing scripts.
3. **Expected savings:** Prevents accidental context-window overflow and lets
   agents choose a cheaper detail mode without a trial call. Savings are task
   dependent; the direct target is zero budget overruns.
4. **Quality and behavior impact:** Predictable behavior improves agent
   planning. A too-small request returns actionable sizing data instead of one
   oversized arbitrary item.
5. **Risks and trade-offs:** Exact serialization inside packing costs CPU and
   can couple core selection to output profiles. Cache invariant envelopes, pass
   an explicit format cost profile into core, and keep one renderer as the
   source of truth. Multiple explicit budget dimensions add CLI complexity but
   avoid a dangerous legacy semantic change.
6. **Affected areas:** `ContextEngine.Budget`, `ContextResult`, all context
   output renderers, `Tokens`, CLI/MCP validation, usage metrics, README, ADR
   0010, token-frugal tests.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** For every accepted
   `--response-budget-tokens` value in a boundary-value matrix, tokenizing the
   defined model-visible output yields a count at or below budget; out-of-band
   evaluation/usage counts equal independently tokenized counts; no first-item
   exception remains.

Acceptance criteria:

- Define the Release 1 counted boundaries normatively:
  - `core_document_tokens` is an out-of-band metric over the rendered core
    result without a CLI newline or transport wrapper;
  - the CLI response budget tokenizes exact stdout, including its trailing
    newline;
  - the MCP response budget tokenizes the model-visible text content block;
  - MCP JSON-RPC escaping/envelope is reported as `transport_tokens` by Q0 but
    is not part of the per-call response budget;
  - server instructions, tool definitions, and call arguments are workflow
    overhead, not part of the per-result budget; and
  - Release 1 covers the existing MCP text-JSON representation. Structured MCP
    accounting is added with Q7.
- Preserve useful best-fit packing: an oversized high-ranked item does not
  block smaller relevant items.
- If legacy and explicit budgets are supplied together, every active constraint
  must pass; no option silently overrides another. Echo active budget values and
  their bases in self-describing output.
- Expose why candidates were omitted: `top`, `response_budget`,
  `projected_read_budget`, or `nonpositive_score`.
- If a response budget cannot fit the smallest useful successful payload, CLI
  returns `ExitCode.InvalidArguments` with concise stderr and MCP returns
  `IsError=true` with `minimum_budget_tokens`. The requested success-payload
  budget does not apply to this error channel, and no partial success result is
  emitted.
- Defer `detail=auto` to Release 2. It needs an evaluated, documented rule table
  and explanation codes; it is not part of Q3.
- Update README/ADR wording: `--budget-tokens` is a compatibility “charged work”
  cap, while only `--response-budget-tokens` promises a hard model-visible
  result ceiling.

### Q4 — Correct state fingerprints and result identities

Priority: P0; Release 1.

1. **Current problem:** One path/content `state_hash` is treated as sufficient
   for results even when configuration, schema, parser, graph, ranking, or
   representation logic changes.
2. **Proposed solution:** Introduce versioned fingerprints:
   `content_state` for path/content pairs, `analysis_state` for content plus
   config/index-schema/parser/graph/ranking versions, `worktree_state` for a
   detected local delta, `evidence_id` for analysis state plus canonical
   analyzed query, explicit/defaulted core options, ordered evidence units and
   reasons, and `representation_id` for evidence identity plus output schema,
   format/profile, encoding, surface boundary (CLI, MCP text, or later MCP
   structured), and representation options. `representation_id` hashes the
   canonical representation body with its own identity field omitted, avoiding
   self-reference. A benchmark-only `transport_profile_id` additionally covers
   MCP protocol/JSON-RPC serialization and tool-schema versions while excluding
   the volatile request ID. Centralize canonical hashing and ordering. Result
   identities use command-specific state: index-backed queries do not scan the
   worktree, while `changed` includes `worktree_state`.
3. **Expected savings:** Enables safe result reuse, delta queries, and cache
   invalidation without repeated broad requests. Direct savings appear in Q1
   and A1.
4. **Quality and behavior impact:** Prevents stale results after a non-content
   semantic change and makes agent decisions auditable.
5. **Risks and trade-offs:** More version constants require discipline. Do not
   hash timestamps, absolute paths, machine-specific separators, or unordered
   serialization.
6. **Affected areas:** `MetaKeys`, `Indexer.ComputeStateHash`, `ChangeDetector`,
   `ConfigStore`, `RepoContextInfo`, `IndexSchema`, scanner/parser/chunker/
   tokenizer/graph/ranking version declarations, every result DTO, output
   schema, and deterministic tests.
7. **Effort:** Medium, 2-4 engineer-days.
8. **Objective measurement:** Each relevant input mutation changes only the
   expected fingerprint; identical repositories in different absolute
   directories produce identical fingerprints; stale receipts are rejected.

Acceptance criteria:

- Publish a canonical byte layout for every fingerprint in an ADR.
- Persist a catch-all index-time `analysis_producer_version` covering scanner/
  file classification, byte decoding/newline normalization, parser, chunker,
  synthetic symbol chunks, tokenizer, and graph construction; optional
  component fingerprints may support narrower rebuilds. Query commands reject
  an index whose stored producer fingerprint is stale; index rebuilds the
  affected data. Split
  indexing-affecting configuration from live ranking/output configuration so
  only necessary changes rebuild stored analysis.
- Add a test matrix for content, config, schema, parser-version, ranking,
  output-option, and query changes.
- Define query identity as the canonical analyzed term sequence plus the exact
  original query where it affects output; normalize omitted and explicit
  default options to the same canonical value.
- Define `worktree_state` over the indexed base `content_state` plus sorted
  status/path/current-content-hash entries, with an explicit deletion marker.
  Added and modified files are content-hashed. Only worktree-sensitive commands
  compute it. Test two different contents added at the same path.
- Do not use the short display hash as the sole internal identity.

### Release 1 protocol migration (schema v3)

Release 1 intentionally bumps the global machine-output
`RepoContextInfo.SchemaVersion` from 2 to 3. CLI options remain compatible;
`--seen`, `--response-budget-tokens`, and
`--projected-read-budget-tokens` are additive. Existing `--budget-tokens` and
full-file `--known` keep their documented v2 cost/possession meanings during the
compatibility window.

The current MCP tool continues returning the same context JSON in its existing
text content block during Release 1. Native structured MCP is a separate Q7
change.

Representative v2 single-slice result:

```json
{"schema_version":2,"command":"context","query":"change budget packing","terms":["change","budget","packing"],"state":"aaaa1111bbbb","detail":"slices","count":1,"omitted":0,"estimated_tokens":120,"results":[{"path":"src/A.cs","kind":"source","score":0.9,"start_line":20,"end_line":28,"estimated_tokens":120,"file_tokens":500,"hash":"cccc2222dddd","reasons":["fts"],"snippet":"void Pack() {}"}]}
```

Normative v3 shape for the equivalent new evidence:

```json
{"schema_version":3,"command":"context","query":"change budget packing","terms":["change","budget","packing"],"state":"aaaa1111bbbb","content_state":"aaaa1111bbbb","analysis_state":"eeee3333ffff","evidence_id":"gggg4444hhhh","representation_id":"iiii5555jjjj","detail":"slices","top":8,"count":1,"reused_count":0,"omitted":0,"content_tokens":9,"projected_read_tokens":0,"estimated_tokens":120,"results":[{"path":"src/A.cs","kind":"source","score":0.9,"start_line":20,"end_line":28,"estimated_tokens":120,"file_tokens":500,"hash":"cccc2222dddd","reasons":["fts"],"snippet":"void Pack() {}","content_tokens":9,"projected_read_tokens":0,"receipt":"V93aWJQj2fYkYnC_X7A0w4Hk-OQWxnN7j5Kj6mvQhU0","spans":[{"start_line":20,"end_line":28,"text":"void Pack() {}","receipt":"V93aWJQj2fYkYnC_X7A0w4Hk-OQWxnN7j5Kj6mvQhU0"}]}],"reused":[]}
```

Normative v3 shape when the exact evidence receipt is supplied through
`--seen`:

```json
{"schema_version":3,"command":"context","query":"change budget packing","terms":["change","budget","packing"],"state":"aaaa1111bbbb","content_state":"aaaa1111bbbb","analysis_state":"eeee3333ffff","evidence_id":"kkkk6666llll","representation_id":"mmmm7777nnnn","detail":"slices","top":8,"count":0,"reused_count":1,"omitted":0,"content_tokens":0,"projected_read_tokens":0,"estimated_tokens":0,"results":[],"reused":[{"path":"src/A.cs","start_line":20,"end_line":28,"receipt":"V93aWJQj2fYkYnC_X7A0w4Hk-OQWxnN7j5Kj6mvQhU0"}]}
```

Field semantics:

- `top` caps new entries in `results`; `reused` entries never consume it.
- `count` equals `results.length`.
- `reused_count` is the total number of valid matched receipts.
- `reused` is a deterministic budgeted prefix; optional `reused_omitted` equals
  `reused_count - reused.length` when positive.
- `omitted` counts positive-scoring candidate files that are neither delivered
  nor acknowledged as reused because of top or active budget constraints.
- `content_tokens` is embedded evidence only.
- `projected_read_tokens` is the sum of full-file reads implied by delivered
  pointers; it is zero for embedded/reused evidence.
- Exact total response/core-document counts are deliberately absent from the
  measured payload. Q3 records them out-of-band after rendering.
- Top-level/item `estimated_tokens`, top-level `state`, item `file_tokens`, and
  single-span `start_line`/`end_line`/`snippet` remain for one schema version
  with their v2 meanings. Mark them deprecated in code/docs. New consumers use
  the explicit fields.
- When there are multiple spans, omit the legacy single-span fields. Never map
  them to an artificial enclosing range.
- Each independently reusable span/symbol/pointer has a receipt. The item-level
  receipt shown above is a convenience alias because the item has one span.
- `receipt` identifies semantic evidence and its producer versions, independent
  of unrelated global analysis changes or JSON/Markdown/MCP encoding.
  `representation_id` identifies the canonical encoded body under Q4's
  self-field-omission rule.

Golden tests must use real computed identifiers/counts; the illustrative values
above only define shape and relationships.

### Q5 — Token-lean agent output profile

Priority: P1; Release 2.

1. **Current problem:** JSON is single-line and omits nulls, but repeats long
   field names, paths, relationship labels, and reasons. Default JSON escaping
   is unnecessarily expensive for source code.
2. **Proposed solution:** Add explicit stable
   `--format json --profile agent` syntax (and MCP equivalent) with a
   `profile_version`, rather than silently making human JSON cryptic. Use
   relaxed safe JSON escaping, document-level reason dictionaries, grouped
   relationships,
   compact ranges, and columnar rows where measured. Omit echoed query terms
   only when canonical `(request, response)` decoding reconstructs the same
   information. Retain the current self-describing profile for humans and
   compatibility.
3. **Expected savings:** Target 15-25% for context/search/outline, at least 35%
   for relationship-heavy output, based on audit prototypes.
4. **Quality and behavior impact:** Decoded information remains identical.
   Smaller payloads leave more context for code. The schema/output description
   must make the compact form easy for smaller agents to interpret.
5. **Risks and trade-offs:** Short/columnar schemas are less readable and more
   brittle for ad-hoc consumers. Version the profile, advertise `outputSchema`
   once through MCP tool metadata/version documentation rather than embedding
   it in every result, and maintain decoder golden tests.
6. **Affected areas:** `OutputJson`, `ContextOutput`, `SearchOutput`,
   `RelatedOutput`, `OutlineOutput`, `ChangedOutput`, `ArchitectureOutput`,
   CLI options, MCP tools, docs, and token-lean integration tests.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** Tokenize complete before/after payloads; require
   the targets above and structural decode equivalence for every command.

### Q6 — Repository coverage discovery and `doctor`

Priority: P1; Release 2.

1. **Current problem:** `includeTests: true` cannot include tests outside the
   configured roots. Users can unknowingly index a partial repository, reducing
   relevance and impact quality.
2. **Proposed solution:** During `init`, deterministically detect conventional
   local roots from present project/workspace files and supported files:
   `tests`, `test`, `packages`, `tools`, and language-specific project roots.
   Add read-only `repoctx doctor` to report supported but unindexed files,
   shadowed test/doc settings, stale index/config/schema, missing project
   mappings, instruction drift, and estimated coverage.
3. **Expected savings:** On the missing-root workflow fixture, reduce the
   fallback search/read count from at least one to zero. This is primarily a
   coverage/correctness package outside affected layouts.
4. **Quality and behavior impact:** Better recall and test recommendations.
   Existing initialized repositories are reported, not silently rewritten.
5. **Risks and trade-offs:** Broad auto-includes can add generated/vendor files.
   Detection must honor excludes, sensitive patterns, gitignore, file size, and
   supported extensions before proposing a root.
6. **Affected areas:** `RepoctxConfig.CreateDefault`, `Initializer`,
   `FileScanner`, `FileClassifier`, new doctor command/engine/output, CLI
   registration, fixtures, README, and generated instructions.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** Top-level test fixtures are indexed; the
   missing-root workflow needs no fallback read; doctor reports every
   intentionally omitted supported fixture with a reason; no sensitive files
   enter any index/cache and vendor inclusion follows fixture labels.

### Q7 — MCP defaults, annotations, and structured results

Priority: P1; Release 2.

1. **Current problem:** MCP context defaults to unbudgeted pointers, returns
   JSON inside a text block, advertises verbose tool metadata, and marks
   semantically read-only deterministic tools as mutating/non-idempotent due to
   the usage ledger.
2. **Proposed solution:** Add a server-lifetime startup option with exactly two
   Release 2 values: `repoctx mcp --profile legacy-text` and
   `repoctx mcp --profile agent-structured-v1`. Do not add a per-call response
   mode or infer client capabilities. `legacy-text` remains the default for the
   compatibility release and preserves current tool names/defaults/text JSON.
   `agent-structured-v1` advertises one versioned `outputSchema`, defaults
   context to slices with conservative explicit response/projected-read
   budgets, and returns the full result only in `structuredContent`. If the SDK
   requires a `content` entry, return one constant non-source marker such as
   `structured_result`; never duplicate the result. Client templates opt into
   this profile only after their frozen transcript fixture passes. Tighten tool
   descriptions. Disable ledger writes by default for MCP query tools (or move
   them behind an explicit accounting mode) before declaring the tools
   read-only/idempotent.
3. **Expected savings:** Target at least 30% lower MCP initialization/schema
   tokens and 15% lower context-result wire tokens; also removes a likely
   pointer-plus-read round trip.
4. **Quality and behavior impact:** Clients can consume typed results directly;
   safer annotations improve tool selection. Default detail should satisfy more
   tasks in one call.
5. **Risks and trade-offs:** MCP client support differs. The explicit startup
   profile makes capability selection the integration owner's decision and is
   fixed for a server lifetime. Do not change the global default until a later
   schema/compatibility decision backed by client fixtures. Preserve current
   tool names. Validate with frozen offline client/config/transcript fixtures;
   real-client trials are optional, manual, and version-recorded.
6. **Affected areas:** `McpTools`, `McpServerRunner`, MCP SDK configuration,
   all output DTOs, MCP integration tests, README, generated client templates.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** Capture offline
   initialize/list-tools/call-tool transcripts; compare model-visible and
   transport tokens separately and verify canonical `(request,response)`
   decoded fields. Exactly one representation reaches the model, annotations
   match observable ledger/state behavior, and all current/new MCP tests pass.

### Q8 — Fast no-op commands and non-blocking local accounting

Priority: P1; Release 2.

1. **Current problem:** A no-op index rebuilds the full graph. Change detection
   repeats scan/hash work. Stats lock contention can add seconds to a query even
   though stats are not needed to answer it.
2. **Proposed solution:** Skip graph reconstruction when content/config/schema
   and graph version are unchanged. Reuse a shared scan manifest inside a
   command. Record timing/I/O counters. Make usage logging best-effort and
   bounded without changing deterministic command results; if a record cannot
   be appended safely within a short limit, spool locally or report a dropped
   count on the next stats command.
3. **Expected savings:** Zero source rereads/rebuild work for an unchanged
   graph, plus elimination of multi-second stats-induced query stalls. A full
   no-op index still scans/hashes files until A1, so do not promise an 80%
   wall-time reduction in this package.
4. **Quality and behavior impact:** No result change. Faster tools discourage
   agents from bypassing RepoContext with broader reads.
5. **Risks and trade-offs:** Incorrect invalidation can retain stale edges.
   Logging must not corrupt concurrent JSONL records. Q4 fingerprints are a
   prerequisite.
6. **Affected areas:** `Indexer`, `GraphBuilder`, `ChangeDetector`,
   `UsageLog`, `UsageRecorder`, `UsageRecord`, stats output, performance tests.
7. **Effort:** Medium, 3-5 engineer-days.
8. **Objective measurement:** Instrument files read/parsed and edges rebuilt;
   no-op runs perform zero source parses, zero graph source rereads, and zero
   edge rebuilds; p95 query latency under stats contention stays within 10% of
   stats-disabled latency. Report no-op time without a hard percentage gate
   until A1 removes the remaining scan/hash work.

## Larger architectural improvements

### A1 — Incremental repository manifest and targeted analysis updates

Priority: P1; Release 3.

1. **Current problem:** Indexing, change detection, graph building, and future
   result caches each rediscover repository state separately.
2. **Proposed solution:** Store a versioned manifest with path, kind, size,
   content hash, parser version, symbol/chunk summaries, outgoing-edge digest,
   and last analysis fingerprint. Build deterministic Merkle directory hashes
   for fast subtree comparison. Reuse Git blob identities for tracked clean
   files only when `.gitattributes`, clean/smudge filters, working-tree encoding,
   and line-ending rules prove the blob bytes equal indexed working-tree bytes;
   otherwise hash conservatively. Retain a bounded chain of state snapshots and
   deltas so `changed --since <analysis_state>` is answerable after multiple
   index operations, with an explicit `state_not_retained` fallback.
3. **Expected savings:** For Git-clean repositories, target 80-99% fewer source
   bytes parsed/reread on no-op and small-delta operations. For Git-dirty and
   non-Git repositories, report hashing, parsing, and graph-reread savings
   separately and make no unsupported byte-read claim. Agents also avoid broad
   refreshes by requesting targeted deltas.
4. **Quality and behavior impact:** Equivalent analysis with narrower work.
   Explicit deltas let agents update only invalidated context.
5. **Risks and trade-offs:** Filesystem timestamp/size shortcuts can be wrong.
   They may only avoid hashing when backed by trustworthy Git/object or
   platform file identity; otherwise hash. Manifest recovery must be atomic.
6. **Affected areas:** index schema/store, scanner, indexer, change detector,
   hashes/state, CLI/MCP changed results, migrations, performance fixtures.
7. **Effort:** Large, 8-12 engineer-days.
8. **Objective measurement:** Compare against a forced full rebuild after
   randomized add/modify/delete/rename sequences, including added-file content
   hashing and multiple retained states. Database/query outputs must match
   exactly. Report separate Git-clean, Git-dirty, filtered/line-ending, and
   non-Git bytes hashed/read/parsed plus timing. The 80% overall no-op-time
   target applies only to the reproducible Git-clean fixture.

### A2 — Project-aware incremental dependency and test graph

Priority: P1; Release 3.

1. **Current problem:** TypeScript graph resolution is mostly relative static
   imports; C# uses lexical identifiers and nearest directories. Workspace
   aliases, project references, package exports, inheritance, implementations,
   and many test links are missed. The graph is rebuilt globally.
2. **Proposed solution:** Parse local project metadata (`tsconfig` paths and
   references, `package.json` workspaces/exports, `.sln`/`.slnx`, `.csproj`
   project references) with deterministic local resolvers. Store typed edges
   with origin evidence and confidence. Recompute outgoing edges only for
   changed files/projects, then update affected reverse indexes. Add
   architecture summaries from the typed graph.
3. **Expected savings:** Target at least 20% fewer fallback
   search/related/full-read steps on labeled multi-project workflows and fewer
   unnecessary test targets, subject to zero must-edge misses.
4. **Quality and behavior impact:** Higher dependency/test recall with
   explainable edges such as `tsconfig-alias`, `project-reference`,
   `implements`, and `test-target`.
5. **Risks and trade-offs:** Language resolution is complex and can create
   false edges. Use exact metadata first, confidence tiers, bounded fallback
   heuristics, and never pretend to be a full compiler.
6. **Affected areas:** `GraphBuilder`, parser interfaces, index schema/store,
   related/context/architecture engines, project metadata readers, fixtures
   for monorepos and multi-project C#, graph tests.
7. **Effort:** Large, 12-20 engineer-days.
8. **Objective measurement:** Labeled edge precision/recall by resolver;
   incremental output equals full rebuild; changed-file edge recomputation is
   proportional to affected projects rather than repository size.

### A3 — Deterministic code, document, and configuration capsules

Priority: P2; Release 4.

1. **Current problem:** Outlines expose signatures and short docs, but agents
   still read implementations to discover control flow, state mutation,
   exceptions, calls, configuration keys, routes, and test assertions.
2. **Proposed solution:** Precompute structured capsules from syntax trees and
   local lexical analysis. Per symbol/file, include signature, inputs/outputs,
   reads/writes, calls, branches, thrown/caught exceptions, async behavior,
   route/config keys, test names/assertions, and direct dependencies. For docs,
   preserve heading hierarchy, links, commands, and key-value facts. Every fact
   carries source ranges and a rule identifier.
3. **Expected savings:** Target 40-70% versus full-file reads for orientation,
   impact review, and smaller changes; many tasks should be answerable from a
   capsule plus one precise implementation slice.
4. **Quality and behavior impact:** Makes cheaper/smaller external models more
   reliable by replacing implicit code inference with explicit facts.
5. **Risks and trade-offs:** Rule summaries can omit dynamic behavior or look
   authoritative when incomplete. Label coverage, language support, and
   uncertainty; link every fact to source; never synthesize prose that is not
   derivable.
6. **Affected areas:** parser contracts/tree-sitter queries, new analysis
   models, index schema/store, context detail enum/output, CLI/MCP schemas,
   language fixtures and tests.
7. **Effort:** Large, 12-20 engineer-days for useful TS/C# coverage.
8. **Objective measurement:** Fact precision/recall on labeled fixtures,
   source-link validity, capsule token ratio, and reduction in required
   full-file reads with unchanged must-find evidence.

### A4 — Semantic change classification, transitive impact, and targeted tests

Priority: P2; Release 4.

1. **Current problem:** `changed` compares full-file hashes and reports shallow
   reverse neighbors. It cannot distinguish public API changes from comments,
   local bodies, configuration, or tests, and cannot recommend the minimal
   relevant test set.
2. **Proposed solution:** Diff stored/current symbol and capsule fingerprints to
   classify changes: comments/docs, private body, signature/public API,
   imports/dependencies, config/schema, tests, add/delete/rename. Traverse typed
   reverse edges with bounded, explainable propagation rules. Precompute test
   reachability and return ordered test projects/files/commands with reasons,
   never execute tests unless explicitly requested by the calling agent.
3. **Expected savings:** Avoid broad context refreshes and full test suites.
   Target at least 50% fewer recommended tests for local-body changes while
   retaining all labeled affected tests.
4. **Quality and behavior impact:** Agents receive focused review and
   verification scope. Public-contract changes intentionally widen impact.
5. **Risks and trade-offs:** Dynamic dispatch, reflection, runtime config, and
   generated code limit completeness. Include confidence/coverage warnings and
   a conservative fallback for public/config changes.
6. **Affected areas:** change detector, manifest/capsules, typed graph, changed
   DTO/output, CLI/MCP, test discovery, fixtures for API/body/config changes.
7. **Effort:** Large, 10-18 engineer-days after A1-A3.
8. **Objective measurement:** Labeled impacted-file/test precision and recall;
   no false negatives in high-confidence fixture classes; recommended test
   count and projected execution time versus full suite.

### A5 — Task dossier and deterministic next-action engine

Priority: P2; Release 4.

1. **Current problem:** Agents manually compose architecture, context, search,
   outline, related, changed, and known-state calls, often repeating queries or
   applying a fixed checklist regardless of task.
2. **Proposed solution:** Add a local task dossier keyed by
   `analysis_state + normalized task + options`. It contains ranked evidence
   units, receipts, dependency/test impact, unresolved evidence gaps, and
   deterministic next actions. Rules may recommend: inspect a named unseen
   symbol, request a specific missing span, re-index because state is stale,
   run a named test target, or stop querying because required evidence is
   already present. Explain every rule and estimated cost.
3. **Expected savings:** Target 25-50% fewer RepoContext/tool calls and almost no
   identical repeated queries within a task.
4. **Quality and behavior impact:** Smaller agents get an explicit workflow and
   stopping rule. Recommendations improve consistency without autonomous code
   changes or test execution.
5. **Risks and trade-offs:** Persisted task/query text is usage data. Keep it
   local, opt-in, bounded, user-clearable, gitignored, and disabled by
   environment/config. Never include source text when receipts suffice.
6. **Affected areas:** new dossier store/engine, context/result identities,
   CLI/MCP command, configuration, local storage lifecycle, stats, docs, agent
   integrations, deterministic rule tests.
7. **Effort:** Large, 8-12 engineer-days.
8. **Objective measurement:** Scripted workflows compare call count, repeated
   result tokens, evidence coverage, and stop decisions; deleting or disabling
   the dossier returns baseline behavior.

### A6 — Local diagnostics and failure-localization bundles

Priority: P2; Release 4.

1. **Current problem:** Agents spend model/tool calls interpreting compiler,
   linter, and test failures and then locating the implicated code/dependencies.
2. **Proposed solution:** Parse existing local diagnostic outputs or explicitly
   supplied log files into structured failure records. Resolve file/line,
   symbol, owning project, likely changed causes, relevant dependency spans, and
   targeted tests. Add deterministic analyzers for duplicate declarations,
   unresolved local imports, dead/unreachable exports where reliable, config
   key references, and test-to-source mapping. Do not execute external commands
   without explicit caller authorization.
3. **Expected savings:** One bundled failure-localization call can replace
   several search/read/related calls; target 30-60% fewer tokens on supported
   failure fixtures.
4. **Quality and behavior impact:** Agents get exact evidence and can repair
   common failures with smaller models. Unsupported diagnostics remain raw and
   explicitly labeled.
5. **Risks and trade-offs:** Tool output formats vary, and static heuristics can
   overstate causality. Version parsers, expose matched rules, retain original
   line references, and use confidence categories.
6. **Affected areas:** new diagnostics parsers/engine, graph/capsules, CLI/MCP
   tools and output schemas, fixtures for `dotnet`, TypeScript, common test
   runners, docs.
7. **Effort:** Large, 10-18 engineer-days incrementally by ecosystem.
8. **Objective measurement:** Diagnostic parse accuracy, culprit-file/symbol
   Recall@K, exact range validity, and workflow token/call reductions on frozen
   failure logs.

## Experimental ideas

Experiments are opt-in, disabled by default, and must not change the stable
ranking/output path until they beat the deterministic baseline.

### E1 — Inefficient workflow detector

Priority: P3; Release 5.

1. **Current problem:** Agents can repeatedly issue equivalent searches, read a
   whole file already covered by received spans, request architecture
   repeatedly, re-index unchanged state, or run a broad test command despite a
   high-confidence target.
2. **Proposed solution:** Analyze observable local RepoContext
   ledger/dossier events with explicit rules for duplicate query/result IDs,
   supersets, stale-state requests, and redundant RepoContext results. External
   full-file reads or test commands are considered only when an opt-in local
   client hook reports them; never infer unobserved actions. Return suggestions
   only and never block an agent action.
3. **Expected savings:** Potential 10-30% fewer calls in inefficient sessions.
4. **Quality and behavior impact:** Makes waste visible and teaches economical
   usage. Suggestions include the evidence and estimated avoidable tokens.
5. **Risks and trade-offs:** A superficially repeated action may be intentional.
   Require exact or provable-superset matches and support dismissal/disable.
6. **Affected areas:** usage schema/report, dossier, new analysis rules, stats
   CLI/HTML, privacy/lifecycle docs, rule tests.
7. **Effort:** Medium, 4-7 engineer-days.
8. **Objective measurement:** Precision on labeled efficient/inefficient traces,
   suggestion acceptance in manual trials, and avoidable-token estimate error.

### E2 — Deterministic lexical fallback for weak/natural-language queries

Priority: P3; Release 5.

1. **Current problem:** Exact FTS/symbol/path terms can miss synonyms,
   abbreviations, compound identifiers, and terminology used only in docs or
   nearby tests.
2. **Proposed solution:** Evaluate local-only query expansion from identifier
   splitting, repository term frequencies, acronym definitions, config-defined
   synonyms, doc heading links, and graph-neighbor vocabulary. Every expansion
   reports its source/rule and has a strict weight/cap.
3. **Expected savings:** Target a 25% reduction in zero-hit/reformulation steps
   on the weak-query fixture set with no relevance-gate regression.
4. **Quality and behavior impact:** May allow smaller models to use ordinary
   task language while retaining lexical explainability.
5. **Risks and trade-offs:** Expansion can reduce precision. Apply only below a
   hit/score threshold and require evaluation gains before default enablement.
6. **Affected areas:** `QueryAnalyzer`, `Identifiers`, FTS query building,
   ranking reasons, config synonyms, evaluation corpus.
7. **Effort:** Medium, 4-7 engineer-days.
8. **Objective measurement:** Zero-hit rate, Recall@K, nDCG@K, false-positive
   rate, and reformulation-call count against the unexpanded baseline.

### E3 — Local Git co-change signal

Priority: P3; Release 5.

1. **Current problem:** Static imports do not capture every architectural
   relationship, especially config, migrations, UI/server pairs, and
   convention-linked files.
2. **Proposed solution:** Optionally derive bounded co-change counts from the
   existing local Git object database, excluding merge/vendor/generated/bulk
   commits and applying time/count thresholds. Store only aggregate local edges
   with commit-count evidence; never inspect remotes or transmit history.
3. **Expected savings:** Quality-first experiment: require at least a 10%
   reduction in fallback related/search steps on held-out history fixtures
   before claiming savings.
4. **Quality and behavior impact:** Adds a weak, explicitly historical signal
   below static edges.
5. **Risks and trade-offs:** Co-change encodes accidents and can be expensive on
   large histories. Keep opt-in, bounded by commit count/time, low-weight, and
   explainable.
6. **Affected areas:** optional Git history reader, graph schema/build,
   configuration, ranking reasons, privacy docs, synthetic history tests.
7. **Effort:** Medium, 5-8 engineer-days.
8. **Objective measurement:** Incremental Recall@K/nDCG gain, edge precision on
   labeled histories, build time/memory, and ablation versus static graph only.

### E4 — Normalized representations and offline tokenizer profiles

Priority: P3; Release 5.

1. **Current problem:** Different external agents/models tokenize the same
   payload differently, while aggressive source normalization can damage exact
   code semantics or line mapping.
2. **Proposed solution:** Evaluate fully local packaged tokenizer profiles for
   supported clients and safe lossless normalization modes such as newline
   normalization, relaxed escaping, path/reason dictionaries, and whitespace
   compaction outside embedded source. Lossy comment stripping remains explicit
   and never the default.
3. **Expected savings:** 5-20% depending on protocol and tokenizer.
4. **Quality and behavior impact:** Better budget prediction per client without
   changing evidence. Exact source text/ranges remain available.
5. **Risks and trade-offs:** Bundled vocabularies increase package size and can
   lag models; normalized code may confuse copy/edit operations. Version and
   test every profile; never fetch tokenizer data at runtime.
6. **Affected areas:** `Tokens`, output profiles, packaging, CLI/MCP client
   options, range mapping, token tests.
7. **Effort:** Medium, 4-8 engineer-days per validated profile.
8. **Objective measurement:** Predicted versus actual locally tokenized payload
   counts, package-size increase, token reduction, and byte/range round-trip
   tests.

## Existing features that mainly need better adoption or integration

### I1 — Non-destructive `repoctx integrate`

Priority: P1; Release 2.

1. **Current problem:** `init --agents` on an initialized repository requires
   `--force`, and `--force` can overwrite user configuration. Existing managed
   instructions are also stale or overly broad.
2. **Proposed solution:** Add an idempotent `repoctx integrate` command that
   updates only managed instruction/configuration blocks for selected clients.
   It must never rewrite `repoctx.config.json` unless a separate explicit
   option is supplied. Add `--check`, `--diff`, `--client`, and
   `--remove-managed-block` modes.
3. **Expected savings:** Enablement package rather than a direct runtime
   optimization. On generated-instruction workflow fixtures, require fewer
   pointer-plus-full-read sequences after the updated block is installed.
4. **Quality and behavior impact:** Users can safely refresh integrations
   without losing custom instructions or config.
5. **Risks and trade-offs:** Marker parsing across varied Markdown files can be
   fragile. Use exact managed markers, atomic writes, backups on malformed
   blocks, and no change on ambiguity.
6. **Affected areas:** `Initializer`, `AgentInstructions`, new integrate
   command, CLI help, tests, README, `AGENTS.md`, `CLAUDE.md`.
7. **Effort:** Small-medium, 2-4 engineer-days.
8. **Objective measurement:** Golden tests for create/update/no-op/check/remove,
   custom content preservation, and byte-identical user config before/after.

### I2 — Client-specific Claude Code, Copilot, Cursor, and generic agent files

Priority: P1; Release 2.

1. **Current problem:** One generic instruction block cannot exploit each
   client's MCP configuration, project rules, hooks, custom agents, and
   read-before-edit behavior. It can also advertise unsupported/stale commands.
2. **Proposed solution:** Generate versioned, minimal templates per client:
   Claude Code project memory/rules and optional hooks; GitHub Copilot
   repository instructions, MCP config, hooks/custom-agent guidance; Cursor
   project rules and MCP config; generic `AGENTS.md`; and a standalone MCP
   snippet. Hooks should only detect stale state or suggest RepoContext—they
   must not transmit data or silently run destructive/expensive actions.
3. **Expected savings:** Fewer unnecessary initialization and full-file-read
   calls; target 20% lower call count in client-specific scripted workflows.
4. **Quality and behavior impact:** Each agent receives concise instructions
   matching its capabilities. Smaller agents benefit from clear tool choice and
   stopping rules.
5. **Risks and trade-offs:** Client formats evolve. Put templates behind
   explicit client/version identifiers, test syntax, and keep generic output as
   fallback.
6. **Affected areas:** integration templates/assets, `AgentInstructions`,
   integrate command, MCP docs, client-specific integration tests and README.
7. **Effort:** Medium, 4-7 engineer-days initially; ongoing maintenance.
8. **Objective measurement:** Validate generated syntax, measure instruction +
   MCP schema tokens, and replay identical tasks through client-shaped tool
   traces for call/read reductions.

### I3 — Conditional economical workflow presets

Priority: P1; Release 2.

1. **Current problem:** Current instructions resemble an unconditional
   checklist: orient, get context, outline, related, changed, index, requery.
   Following every step can cost more than it saves.
2. **Proposed solution:** Replace the checklist with task-shaped rules and add
   the deferred `detail=auto` policy. Explicit detail always wins. The versioned
   MVP rule table is:
   - queries containing change/action terms (`fix`, `change`, `implement`,
     `refactor`, `debug`, `test`) prefer slices;
   - pure location/survey terms (`where`, `find`, `locate`, `which`,
     `architecture`, `impact`, `dependency`) prefer outlines;
   - other queries prefer slices;
   - if the preferred representation cannot fit both explicit response and
     projected-read limits, try outline, then paths, only when each candidate
     fits its applicable limits; otherwise return the Q3 minimum-budget error.
   Emit `auto:action`, `auto:survey`, `auto:default`, or
   `auto:budget-fallback`. Evaluate and version the exact term sets. Instructions
   start with one such budgeted context call; call architecture only for
   cross-cutting/unknown-boundary tasks; call outline only when a file is
   relevant but the needed symbol was not delivered; call related only for
   dependency/impact questions; call changed only after edits or stale-state
   evidence; re-index only when analysis state is stale; stop once evidence gaps
   are empty. Include receipts in follow-ups.
3. **Expected savings:** 20-50% fewer RepoContext calls in simple tasks and no
   loss for complex tasks that satisfy the conditions.
4. **Quality and behavior impact:** Agents spend deliberately and know when to
   stop querying. Rules remain transparent and can later be emitted by A5.
5. **Risks and trade-offs:** Overly terse instructions may reduce adoption.
   Include one short example per task shape, not a long universal sequence.
6. **Affected areas:** `QueryAnalyzer`, context options/engine, CLI/MCP detail
   validation, generated instructions, MCP server instructions, README, client
   templates, CLI examples, and scripted workflow evaluations.
7. **Effort:** Medium, 3-5 engineer-days after Q1-Q3 contracts settle.
8. **Objective measurement:** Compare scripted traces for locate/explain/fix/
   refactor/impact tasks; must-find evidence remains present while median calls
   and total tokens decline at least 20%.

## Recommended implementation sequence

### Release 1 — correctness before optimization

1. Q0: add evaluation harness and red audit tests.
2. Q4 foundation: store producer versions and define content/analysis/evidence/
   representation identities.
3. Q3 foundation: add the format-aware cost oracle and accounting boundaries,
   without changing packing behavior yet.
4. Q2: retain/select query-aware multi-evidence units.
5. Q1: derive stateless receipts from the finalized evidence-unit contract and
   correct packing of reused items.
6. Q3 completion: enforce legacy and new explicit hard budgets with the shared
   oracle.
7. Update schema v3, ADRs, docs, checked-in/generated instructions, and MCP
   guidance together.
8. Run the release gate and stop for review.

Suggested reviewable changes:

- Change 1: evaluation framework and red tests only.
- Change 2: producer/state/result fingerprints plus the cost-oracle foundation.
- Change 3: internal evidence units and query-aware outlines/multi-span slices.
- Change 4: stateless receipts, reuse packing, CLI/MCP behavior, and immediate
  safe-instruction updates.
- Change 5: hard-budget enforcement, schema v3 migration, ADRs, and complete
  compatibility docs.

Do not include compact output, incremental graph work, or new client templates
in Release 1.

### Release 2 — wire efficiency and adoption

1. Q5 compact agent profile.
2. Q7 structured MCP surface and economical defaults.
3. Q6 coverage detection and doctor.
4. I1 non-destructive integration command.
5. I2 client templates.
6. I3 evaluated `detail=auto` and conditional workflow guidance.
7. Q8 no-op fast path and bounded usage logging.

### Release 3 — incremental repository intelligence

1. A1 shared manifest, state deltas, and incremental analysis.
2. A2 typed project-aware dependency/test graph.

### Release 4 — make smaller agents sufficient

1. A3 structured capsules.
2. A4 semantic impact and targeted test recommendations.
3. A5 task dossier and next-action rules.
4. A6 diagnostic/failure-localization bundles.

### Release 5 — experiments and ablation

Evaluate E1-E4 independently. Land an experiment in the default path only when:

- it improves its declared metric on held-out fixtures;
- global relevance gates remain green;
- it remains explainable and deterministic;
- it has bounded CPU/disk cost;
- it has a kill switch and an ablation test.

## Required engineering practices

### Protocol and compatibility

- Add an ADR for receipt/state identity and another for exact budget semantics.
- Bump `RepoContextInfo.SchemaVersion` for breaking output changes.
- Keep old fields/options for a documented compatibility window when safe.
- Golden-test every output profile and error shape.
- Treat CLI and MCP as two encodings of the same core result, not separate
  ranking/budget implementations.

### Determinism

- Sort paths ordinally after slash normalization.
- Sort equal-scored items with a documented stable tie-breaker.
- Canonicalize project metadata before hashing.
- Never include absolute paths, timestamps, process IDs, random IDs, or hash-map
  iteration order in identities/golden output.
- Test equivalent repositories rooted at different absolute paths.

### Privacy and local storage

- Store manifests, dossiers (which may reference caller-supplied receipts),
  aggregates, and usage records under `.repoctx/` or an explicit user-local data
  directory. Q1 receipts themselves are stateless and require no receipt store.
- Gitignore all generated local state.
- Provide inspect, clear, disable, and retention controls for query/usage data.
- Never persist source snippets in a dossier when a range/hash receipt is
  sufficient.
- Add tests that prohibited files never enter indexes, caches, logs, or
  diagnostics.

### Performance instrumentation

Add internal counters that tests/benchmarks can read without polluting normal
output:

- scanned files and directories;
- bytes hashed/read;
- files parsed/chunked;
- graph nodes/edges examined and replaced;
- SQLite reads/writes/transactions where practical;
- cache/receipt hits and misses by reason;
- packer candidates/units evaluated;
- exact output tokens by envelope/content/metadata;
- command elapsed time.

Counters must be local and opt-in for normal users.

### Documentation

- Correct stale schema and command examples.
- Remove future-embedding ideas from product direction; embeddings violate this
  plan's permanent constraints.
- Replace character/token approximations in exact-token reproductions.
- Explain the difference between full-file possession, received evidence
  receipts, repository content state, analysis state, worktree state, and
  result identity.
- Document limitations for dynamic imports, reflection, generated code, and
  incomplete static-analysis coverage.

## Release 1 definition of done

Release 1 is complete only when all of the following are true:

- The evaluation harness and audited red tests are checked in.
- A partial slice receipt can never suppress a different unseen range.
- Full-file `known` semantics are explicit and no generated instruction
  misrepresents a partial response as full-file possession.
- Reuse markers do not starve new context or claim zero wire cost.
- Query-matched symbols appear in outlines even beyond the source-order cap.
- Multiple relevant spans can be returned with exact, unambiguous ranges.
- Complete serialized context output stays within every accepted hard budget.
- Too-small budgets return a deterministic actionable response.
- Content, analysis, worktree, and result identities have documented canonical
  inputs and invalidation tests.
- CLI and MCP use the same core packing/reuse behavior.
- Output schema changes are versioned and documented.
- All existing and new tests pass.
- The final implementation report includes exact before/after relevance,
  output-token, call-count, and timing numbers.

## Commands for the implementation agent

Use the currently installed/indexed tool for repository-targeted discovery as
required by `AGENTS.md`, but never use it as evidence of the checked-in CLI's
post-change behavior:

```powershell
$env:REPOCTX_NO_STATS = '1'
repoctx context "<current work package>" --detail slices --budget-tokens 4000 --format json
repoctx related "<target file>" --format json
repoctx search "<symbol>" --symbols --format json
```

Build a local source-tree CLI and use its DLL for all before/after contract and
token measurements:

```powershell
dotnet build src/RepoContext.Cli/RepoContext.Cli.csproj -c Release
$repoctxDll = (Resolve-Path 'src/RepoContext.Cli/bin/Release/net10.0/repoctx.dll').Path
dotnet $repoctxDll context "<fixture task>" --detail slices --budget-tokens 2000 --format json
```

Validate the source tree:

```powershell
dotnet test RepoContext.slnx
git diff --check
git status --short
```

For MCP tests, use bounded subsets if the full serial suite exceeds the
environment's command timeout; report every subset and total count. Do not
silence a failing or hanging test.

## Implementation report template

At each release gate, provide:

```text
Release / packages:
Changed files:
Protocol/schema changes:
Tests added:
Tests passed:
Relevance metrics before -> after:
Wire/workflow tokens before -> after:
Calls/full-file reads before -> after:
Cold/no-op/delta index metrics before -> after:
Known limitations:
Deferred work:
Working-tree status:
```

Do not start the next release until this report has been reviewed.
