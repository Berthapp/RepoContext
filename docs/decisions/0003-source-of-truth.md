# ADR 0003 — Source of truth in the absence of a separate MVP specification

- **Status:** accepted
- **Date:** 2026-07-08
- **Milestone:** M-Skeleton

## Context

The build prompt repeatedly names `docs/repocontext-mvp-spezifikation.md` as the
binding specification (features F1–F9, relevance pipeline, data model "chapter
7", NFRs "chapter 10", config defaults "chapter 9", exit codes F7, acceptance
criteria "chapter 12", etc.). That file does not exist in this repository, its
history, or the environment. What is present is:

- `repocontext-produktdoku.md` — the v2 product documentation (German), at the
  repository root.
- The build prompt itself, saved as `docs/build-prompt.md`.

The repository owner confirmed: **use the product document at the repository
root as the source of truth.**

## Decision

Treat the combination of `repocontext-produktdoku.md` (product principles,
scope, example outputs, example configuration) and `docs/build-prompt.md`
(milestones, constraints, per-feature behaviour) as the binding guidance.

Where the build prompt references a "spec chapter", the concrete decision is
derived from these two documents and recorded as an ADR here. The conflict rule
becomes: **product doc + build prompt > ADRs > implementation preference.**

Because there is no external specification to contradict, the "ask first for
changes to the data model or the CLI/JSON contracts" rule is honoured by
proposing those contracts explicitly (as ADRs and, for M1+, in a status report)
before they harden, rather than by diffing against a missing document.

## Consequences

- The data model (M1), config schema (M1) and JSON output contracts (M1+) are
  first defined in ADRs and surfaced in the milestone status reports for
  approval, since they cannot be validated against a pre-existing spec.
- The German product document is not edited (per the constraints); only ADRs and
  `docs/benchmark.md` are added under `docs/`.
