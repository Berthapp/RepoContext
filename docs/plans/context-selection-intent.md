# Context selection diagnostics and task intent

Branch: `feature/context-selection-intent`

## Problem and scope

The frozen retrieval corpus finds all 38 required file occurrences in the new
tasks, but delivers only 29 under a 2,000-token JSON budget. Aggregate omission
counts cannot explain which files lost a ranking slot or failed a budget. A
previous experiment improved file recall while damaging relevant-line coverage
and increasing follow-up reads; file count alone is not an acceptance criterion.

This change implements the first recommended improvement: selection diagnostics
and task-specific context packing. Git review context, SCIP import, repository
health checks and broad latency optimization remain separate projects.

## Implementation plan

1. Add opt-in `context --explain` and equivalent MCP input. Report a bounded,
   deterministic sample of omitted candidates with their selection rank, score,
   limiting constraint and a structured next lookup. Share classification with
   the existing aggregate counts. Describe the candidate pool and active scope
   honestly; absence from the pool is not proof a file is irrelevant.
2. Add `--intent fix|explain|review` (and MCP `intent`). Preserve existing behavior
   when omitted. Favor relevant tests for fixes, supporting definitions and
   documents for explanations, and dependents/tests for reviews. Use indexed
   relationships and explicit reasons, without inventing semantic guarantees.
   After the initial experiment, require an explicit, unambiguous file/symbol
   target for promotion; a general question must not choose an arbitrary anchor.
3. Preserve useful source spans and use the existing exact-rendered budget
   admission for every response, including diagnostics. Do not force one tiny
   fragment per file or claim complete task evidence from role coverage.
4. Keep receipts tied to their exact evidence units; account for active intent
   in request identity. Preserve default contracts and deterministic output.
   Cover CLI JSON/text/Markdown, compact JSON and MCP.
5. Add regression tests for classification, scope, reuse, intent behavior,
   deterministic output, invalid inputs, exact budgets and actionable shortfalls.
6. Run the frozen corpus and legacy evaluation with default behavior unchanged.
   Evaluate explicit intents separately, reporting file and relevant-line
   coverage plus oracle follow-up reads. Record mixed results honestly; never
   rewrite the frozen labels to improve a score.
7. Document supported behavior, proposed-use examples, validation results and
   remaining limits. Refresh the repository index after edits.

## Acceptance criteria

- Every successful budgeted CLI/MCP output respects its complete response ceiling.
- Per-file omission reasons agree with aggregate classification; diagnostics are
  bounded and reveal when the sample is truncated.
- Intent is explicit, deterministic and scoped; irrelevant files are not seeded
  merely to fill a role. Default evidence selection does not regress.
- Focused tests demonstrate useful tests/dependents/documents can be prioritized
  without replacing complete source spans with arbitrary fragments.
- Corpus evidence records relevant lines and follow-up cost, not just filenames.
- Release build and relevant automated checks pass.

## Implementation results

Implemented CLI/MCP intent and diagnostics, all three output formats, compact
JSON, scoped candidate sampling, exact budget accounting and fitting shortfall
retries. Diagnostics share classification with the aggregate omission counts.
The default policy and evidence-unit receipts remain unchanged.

An unconditional promotion experiment regressed line coverage and follow-up
cost, so it was rejected. Explicit target selection preserves the frozen
corpus's default aggregate metrics across all three intents. Focused fixtures
verify the companion behavior and intact implementation spans. This does not
establish a general retrieval or real-agent success improvement.

The CLI test harness was corrected to preserve exact stdout newlines on Windows.
The new MCP arguments fit within the existing 1,700-token schema gate (1,698).
See [ADR 0022](../decisions/0022-context-selection-intent.md) for the contract,
experimental evidence and limitations.

Final validation on 2026-09-08:

- Release compilation succeeds; all **442 core + 256 integration tests (698)**
  pass, including the frozen corpus and legacy golden without snapshot-update
  variables in the final run.
- All **12 npm launcher tests** pass.
- The intent comparison measures **144 responses** (36 tasks, default plus three
  purposes), all within 2,000 tokens. Its recorded assembly hashes match the
  final CLI/core binaries. File/line coverage and oracle read totals are
  unchanged in every arm; no general retrieval improvement is claimed.
- Legacy source-response artifacts are unchanged. Reviewed snapshot changes
  contain only the extra 36 MCP session-schema tokens and derived totals.
- `git diff --check` passes.

To reproduce the intent report, set `REPOCTX_WRITE_INTENT_REPORT=1` only for
`dotnet test RepoContext.slnx -c Release --filter FullyQualifiedName~ExplicitIntents_AreMeasured`.
Without that variable the test measures and validates without writing a report.
Summarize the retained result with
`python docs/eval/holdout/summarize.py intent-evaluation.json`.
