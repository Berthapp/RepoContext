# ADR 0022 — Selection diagnostics and explicit task intent

- **Status:** accepted
- **Date:** 2026-09-08
- **Plan:** [context selection and intent](../plans/context-selection-intent.md)

## Decision

`context --explain` and MCP `get_context(explain: true)` add a `selection`
object. It reports the generated candidate count, positive-scoring eligible
count, canonical scope patterns when scoped, omitted count, and at most eight
omitted-file samples. `unlisted` counts omitted files not in that sample.

Each sample contains `path`, `rank`, `score`, `reason`, and
`next_lookup: { "command": "outline", "file": "..." }`. Lookup arguments are
data, not executable shell strings. Rank is the selection order after diversity
and intent, before receipt/full-file reuse. Score is the original adjusted
relevance score; intent can change order without changing that score.

Sample reasons use the same classification as `omitted_by`: `top`,
`response_budget`, `budget_tokens`, or `projected_read_budget`. These describe
constraints encountered during ranked packing, not a claim that a file could
never fit in a different request. Previously held evidence is described by the
existing `reused` fields. Partial-file omissions remain in `spans_omitted` or
`symbols_omitted`. Nonpositive candidates are counted separately and are not
included in omitted-file samples.

The pool is generated from search channels and graph expansion inside the
requested scope. It is not an exhaustive relevance judgment over every indexed
file. Outside files are not ranked, and an absent sample does not prove that a
file was irrelevant, ignored, or outside scope. Search and outline remain the
appropriate follow-ups for a specific file not represented here.

Diagnostics are rendered and tokenized inside the existing hard response
ceiling. Samples can shrink to zero before otherwise fitting source variants are
rejected; the summary and exact unlisted count remain. The summary has a cost,
so enabling diagnostics can reduce delivered source or increase the required
retry budget. Text and Markdown carry the same information. Default responses
omit the new fields entirely.

## Intent policy

`context --intent fix|explain|review` and MCP `get_context(intent: "...")` opt
into a purpose. Intent is not inferred from prose. Promotion requires exactly
one eligible source file explicitly identified by a repository-relative path
in the query, or by a query consisting of one exact symbol name. Ambiguous
symbol names, multiple targets and general questions keep their normal order.
Uniqueness is checked against all scoped source declarations, independently of
FTS hit limits, using ordinal case-insensitive name comparison. Multiple
declarations within one source file count as one file target.
Path separators are normalized; a path prefix is not an exact target. A
sentence-ending period after a path is accepted, including before whitespace
or closing quotation/bracket characters; suffixes such as `.backup` remain
part of a different filename.

The identified implementation is followed by up to two directly linked,
positive-scoring candidates already in the scoped pool:

| Intent | First companion | Second companion |
| --- | --- | --- |
| `fix` | Test | Imported source dependency |
| `explain` | Imported source dependency | Linked document |
| `review` | Importing source dependent | Test |

Within each role the existing relevance order wins. Candidates already
penalized as unrequested fixtures or vendor/generated code are not promoted.
Promoted items carry `intent:<purpose>:<role>` reasons. The top-level `intent`
reports the requested purpose; the per-item reasons show actual promotions.
Relationships retain the index's syntax/reference inference limits. A linked
test is not proof of runtime coverage, and role coverage is not proof of a
complete answer.

The existing best-fit packer still admits complete span variants first and
measures the entire rendered response. There is no forced one-fragment-per-file
pass. When a promoted companion cannot fit, normal budget omission applies.

## Identity and compatibility

Intent adds an `intent.v2` canonical request component. This advances v1 after
the review fixes for truncated symbol hits and sentence punctuation, without
changing exact-unit receipts or requiring an index migration. Default requests retain
their old identity inputs, ranking and wire shape. Diagnostics describe the
selection and affect the representation hash; they do not independently change
evidence identity when the delivered/reused evidence is identical. A different
budgeted selection does change evidence identity. Exact-unit receipts remain
reusable across intents and diagnostic modes.

The new fields are optional additions to both existing JSON schemas (default
v4 and compact v5). The two optional MCP arguments increase the recorded session
schema cost from 1,662 to 1,698 tokens, within the existing 1,700-token gate.

## Evidence and limits

The initial unconditional source-anchor experiment reduced new-task compact
file recall from 29/38 to 28/38 (`fix`), 26/38 (`explain`) and 27/38 (`review`),
with lower relevant-line coverage. Its [raw report](../eval/holdout/intent-unanchored-experiment.json)
is retained as a rejected experiment. This motivated the general constraint
that a purpose alone is insufficient evidence for choosing an implementation
target; no task-specific synonyms, labels or weights were changed.

With explicit target selection, the [comparison report](../eval/holdout/intent-evaluation.json)
preserves all aggregate file/line/read metrics across the corpus: 29/38 required
files and 204/449 relevant lines on the new tasks; 7/11 and 78/191 on the six
historical tasks. These general questions establish fallback behavior, not a
measured improvement in real-agent task success. Focused source/test/dependent/
document fixtures exercise target-specific promotion and retention of source
spans. Further independent tasks are needed to measure its practical benefit.

The CLI test harness now captures stdout verbatim: its former line-event capture
converted LF to CRLF on Windows, changing the text used for token assertions.
