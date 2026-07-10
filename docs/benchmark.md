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
