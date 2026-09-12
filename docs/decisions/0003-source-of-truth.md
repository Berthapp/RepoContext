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

## Update — 2026-09-12

The repository owner asked for the public documentation to be reworked and for
all of it to be in English. Accordingly:

- `repocontext-produktdoku.md` was translated and renamed to
  `repocontext-product-documentation.md`. Wherever the text above names the
  German filename, it refers to that file.
- The consequence "the German product document is not edited" no longer holds.
  It recorded a constraint of the original build prompt, which the owner has
  since superseded; the document's status as source of truth is unchanged.
- Its chapter 7 now carries the measured cost-and-quality argument, mirroring
  `docs/cost-and-quality.md`.

Recorded measurement artifacts under `docs/reviews/` still name the old path
because they are frozen observations of an earlier commit; they are evidence of
what happened then and are deliberately not rewritten.
