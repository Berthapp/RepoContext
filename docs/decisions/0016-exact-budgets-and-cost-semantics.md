# ADR 0016 — Exact hard budgets and explicit cost semantics

- **Status:** accepted
- **Date:** 2026-07-23
- **Release:** 1 (work package Q3; JSON `schema_version` 3)

## Context

`--budget-tokens` did not bound anything reliably.

- **The first item was always admitted.** A 100-token request could return a
  single item implying ~3,532 tokens, because the packer skipped the budget
  check for the first candidate so that a tight budget "never yields an empty
  bundle".
- **Item costs were an estimate.** Embedded content was charged a flat
  40-token framing allowance plus path and reason tokens — close, but not the
  bytes actually emitted.
- **The document wrapper was outside the packer.** Top-level fields, the
  `results` array framing, the trailing newline and (over MCP) the content block
  were never counted at all.
- **One number meant two things.** For `--detail paths` the budget charged
  *projected downstream reads*; for embedded detail it charged *response
  content*. The same flag therefore bounded two incomparable quantities.

An agent cannot plan against a budget that behaves this way, and "context window
overflow" is not a recoverable error for most callers.

## Decisions

### 1. One shared, format-aware cost oracle

`IResponseCostModel` (core) is implemented by `ContextCostModel` (CLI). The
packer does not estimate: for each tentative item set it asks the **real
renderer** to serialize the result and tokenizes the exact surface boundary.
The final set is rendered once more and measured again.

This terminates and has no self-referential fixed point, because the measured
document never contains its own token count (exact totals are recorded
out-of-band after rendering) and `representation_id` is hashed with its own
field omitted (ADR 0015). Computing an actionable retry value is a separate
bounded increasing calculation because the echoed budget participates in result
identity.

The CLI and the MCP server both construct a model backed by the same renderer.
They share selection code, while each measures its own normative surface. Near
a tight boundary the CLI newline and surface-specific identity may therefore
produce different best-fit evidence sets; each still obeys the same semantics
and its own exact ceiling.

### 2. Normative counted boundaries

| Boundary | What is tokenized |
| --- | --- |
| `core_document_tokens` | the rendered core result, no CLI newline, no transport wrapper — an out-of-band metric, never a budget |
| CLI response budget | exact stdout **including its single trailing newline** |
| MCP response budget | the model-visible text content block |
| `transport_tokens` | JSON-RPC escaping and envelope — reported by the Q0 harness, **not** part of the per-call budget |
| workflow overhead | server instructions, tool definitions and call arguments — session cost, not per-result cost |

Release 1 covers the existing MCP text-JSON representation. Structured MCP
accounting arrives with Q7.

### 3. Three budgets with distinct, explicit meanings

- **`--budget-tokens`** keeps its v2 cost basis for the compatibility window:
  pointers charge projected full reads, embedded detail charges its legacy
  content basis. It is a *"charged work"* cap and is documented as such. It is
  explicitly **not** a promise about response size.
- **`--response-budget-tokens`** is a hard ceiling on the exact model-visible
  response.
- **`--projected-read-budget-tokens`** is a hard ceiling on the full-file reads
  implied by delivered pointers.

Silently reinterpreting `--budget-tokens` would have broken existing scripts in
the most dangerous way possible — quietly, and only under load. When several are
supplied, **every active constraint must pass**; no option overrides another.

The **first-item bypass is removed from all three.** Best-fit packing survives:
an oversized high-ranked item is skipped, not treated as a stop signal, so
smaller relevant items behind it still land.

### 4. A too-small budget is an error, not a truncation

If a hard response budget cannot fit a useful successful payload:

- the CLI writes a concise message to stderr including
  `retry_budget_tokens=<n>` and exits `ExitCode.InvalidArguments` (3);
- MCP returns `IsError=true` with the same `retry_budget_tokens`;
- **no partial success result is emitted on either channel**, and the requested
  success-payload budget does not constrain this error message.

The retry value is deterministically computed against a compact useful payload
and is guaranteed to fit. It may exceed the mathematical minimum: exhaustively
rendering every lower integer for every candidate made a tiny invalid request an
unbounded CPU amplifier. A request with genuinely no eligible candidates is an
ordinary empty success when its empty document fits; even empty/reuse-only
documents remain subject to the hard response ceiling.

### 5. Explicit cost fields, and honest accounting for reuse

`content_tokens` (embedded evidence) and `projected_read_tokens` (implied full
reads) are reported separately, per item and per document, replacing the blended
`estimated_tokens` for new consumers. `omitted_by` names why candidates were
dropped: `top`, `response_budget`, `projected_read_budget`, `budget_tokens`.

The plan enumerates four omission reasons; `budget_tokens` is reported as a
fifth rather than folded into `response_budget`, because the two have different
cost bases during the compatibility window and merging them would mislabel the
cause.

Reuse markers are charged at their real serialized cost. They are never labelled
zero-cost. The evaluation reports measured reductions rather than assuming every
marker is always smaller than every evidence unit.

`detail=auto` is explicitly **deferred to Release 2** (I3). It needs an
evaluated, versioned rule table and explanation codes.

## Consequences

- Packing costs more CPU: candidate variants and bounded reuse-prefix trimming
  can require several render/tokenization passes per candidate. The finite
  candidate/variant lists bound normal admission; retry sizing uses at most 80
  increasing probes and never scans every integer below the result.
- A tight `--budget-tokens` can now legitimately return zero items where it
  previously returned one oversized one. That is the correction, not a
  regression.
- Core selection is coupled to an output profile by construction. The mitigation
  is that there is exactly one renderer, and the oracle is injected rather than
  reimplemented.

## Verification

`AuditRegressionTests` tokenizes accepted JSON, text and Markdown CLI surfaces
independently of the packer's bookkeeping, covers empty and reuse-only results,
retries the returned sizing value, and uses a high-base fake cost model to bound
error-path probes. CLI/MCP integration tests cover both accepted and shortfall
channels. A best-fit regression also verifies that a higher-ranked response
budget rejection is not mislabeled as a `top` omission.

## Alternatives rejected

- **Redefining `--budget-tokens` as a hard response cap.** Simplest surface, but
  it changes the meaning of a flag existing scripts already pass, and the
  failure mode is a silently truncated context bundle.
- **An exact total-token field inside the document.** Self-referential: writing
  the count changes the count.
- **Estimating the wrapper with a constant.** That is the defect being fixed;
  any constant is wrong for some result.
- **Rejecting oversized items by ending the pass.** Would forfeit smaller
  relevant items and make budgets behave like truncation.
