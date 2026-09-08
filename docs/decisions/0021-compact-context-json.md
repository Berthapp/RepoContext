# ADR 0021 — Opt-in compact context JSON

- **Status:** accepted
- **Date:** 2026-09-07
- **Contract:** context JSON v5 when requested; default JSON stays v4

## Context

The functional review found that a single source span is serialized both in
`spans[].text` and the deprecated `snippet` field. Under a hard response budget,
this compatibility copy takes space that could carry additional evidence.
Removing it from the default response would break clients still using v4.

## Decision

`repoctx context --format json --compact` and MCP
`repoctx.get_context(compact: true)` opt into context schema v5.

The compact document omits these compatibility fields:

| Location | Removed fields | Replacement |
| --- | --- | --- |
| Document | `state` | `content_state` |
| Document and result item | `estimated_tokens` | `content_tokens`, `projected_read_tokens` |
| Result item | `file_tokens` | `projected_read_tokens` for pointers; `outline` reports full-read cost when needed |
| Result item | `start_line`, `end_line`, `snippet` | `spans[]` for source; `symbols[]` for outlines |

Pointer items still carry their path, hash, receipt and projected read cost.
Scores, reasons, omission accounting, budgets, memories and per-unit receipts
are retained. The default CLI and MCP JSON responses keep their existing shape.
The CLI rejects `--compact` with text or Markdown output.

The packer measures the requested representation through the same renderer that
emits it. The hard response ceiling includes the CLI newline or the MCP text
block, as before; configured token calibration also applies. Shortfall retry
budgets are computed for the requested format.

An identical evidence selection has the same evidence identity and receipts in
both formats. Its representation identity changes, including schema version in
the hash. With a response ceiling, the cheaper representation may admit more
evidence, which then changes the evidence identity as well.

## Verification

Integration tests compare complete normalized documents, check reuse of receipts
across both formats, assert deterministic representation identities, recount
actual CLI/MCP output under response ceilings, exercise calibrated counts, and
execute the advertised retry after a shortfall. Retrieval and performance
measurements compare both formats without changing ranking weights.
