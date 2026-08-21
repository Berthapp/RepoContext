# Token Benchmark Protocol

This is a **protocol template** for measuring RepoContext's core hypothesis:
that giving an agent a compact, explained context bundle instead of letting it
read the repository freely **reduces token usage** without hurting task success.

Execution is manual and is **not** part of CI. Fill in the tables below for a
real repository and a real agent.

## Setup

- **Repository under test:** _(name, commit SHA, approx. file count / LOC)_
- **Agent / model:** _(e.g. Claude Code + model X)_
- **RepoContext version:** `repoctx --version`
- **Index built with:** `repoctx index` on the commit above.

For each task, run it twice with the **same agent and model**:

- **Baseline (A):** the agent works normally (its own file reading/search).
- **RepoContext (B):** the agent is instructed to first call
  `repoctx context "<task>" --format json` (and `related` / `search --symbols`
  as needed) and prefer those files.

Record **input tokens**, **output tokens**, **total tokens**, wall-clock time,
and whether the task **succeeded** (same acceptance bar for both arms).

## Metrics

- **Token delta** = `(A_total − B_total) / A_total` (higher is better).
- **Success parity:** B must not regress task success versus A.
- Report median and range across the 10 tasks; note any task where B failed but
  A succeeded (these are the important cases).

## Task slots (10)

Pick realistic tasks (bug fix, feature, refactor, "where is X handled").

| # | Task description | A tokens (total) | B tokens (total) | Δ tokens | A success | B success | Notes |
| --- | --- | ---: | ---: | ---: | :---: | :---: | --- |
| 1 |  |  |  |  |  |  |  |
| 2 |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |
| 4 |  |  |  |  |  |  |  |
| 5 |  |  |  |  |  |  |  |
| 6 |  |  |  |  |  |  |  |
| 7 |  |  |  |  |  |  |  |
| 8 |  |  |  |  |  |  |  |
| 9 |  |  |  |  |  |  |  |
| 10 |  |  |  |  |  |  |  |

## Summary

- **Median token delta:** _____ %
- **Range:** _____ % … _____ %
- **Success parity held:** yes / no _(explain any B-only failures)_
- **Verdict:** _(does the core hypothesis hold on this repo?)_

## Index scale check (M10, ADR 0019)

A separate, reproducible measurement of indexing cost, independent of any
agent or model. It answers one question: what does an incremental index cost
on a repository large enough for it to matter?

**Corpus.** A generated repository of 14,000 files — 10,000 TypeScript modules
across 40 services (each importing its predecessor), 2,000 C# types across 20
projects, 2,000 Markdown specifications that name a ticket key, a repository
path, a type and a link.

**Method.** `repoctx init && repoctx index` (cold), then `repoctx index` again
with nothing changed (no-op). `bytes read` and `files parsed` come from the
command's own `work:` line, so the figures are the tool's, not the harness's.

| | 0.9.2 | 0.10.0 |
| --- | ---: | ---: |
| cold index | 5.4 s | 5.7 s |
| no-op index | 2.2 s | 1.1 s |
| repository bytes read per run | 5,121,312 | 2,560,775 |
| symbols | 14,000 | 18,022 |
| edges | 11,960 | 15,960 |

The halved byte count is structural rather than incidental: 0.9.2 read every
indexed file twice per run — once to hash, once again in the graph rebuild —
and 0.10.0 rebuilds the graph from stored references, so only the hash pass
touches the working tree. The cold run is marginally slower because it does
strictly more: it extracts references and gives documents and data files an
outline (4,022 more symbols, 4,000 more edges).

Query latency on the same corpus, including ~0.35 s of process startup:
`trace` 0.50 s, `search` 0.49 s, `context` 0.81 s, `context --path` 0.52 s.

Reproduce it on any large repository you have: the interesting number is
`bytes read` on a no-op run, which should equal the size of the indexed
working tree exactly once.
