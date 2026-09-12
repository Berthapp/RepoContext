# Agent cost-and-quality comparison (opt-in)

The product objective is **the same task-result quality at a lower total cost**.
Fewer returned tokens is a mechanism, not the acceptance criterion, so this
harness measures whole workflows: discovery, instructions, tool calls, repeated
context, retries, compaction, editing, review and verification.

It is deliberately **not** part of the offline test suite and not part of CI. It
starts a real coding agent, so it needs credentials and spends real money.
Nothing in normal RepoContext operation calls it, and the product stays offline,
deterministic and free of model calls.

| File | Purpose |
| --- | --- |
| [`manifest.json`](manifest.json) | The frozen arms, tasks, protocol, metrics, accounting rules and promotion criteria |
| [`rubric.md`](rubric.md) | The frozen quality rubric and defect-severity definitions |
| [`harness/run_comparison.py`](harness/run_comparison.py) | Executes the paired, counterbalanced plan; writes one JSON record per run |
| [`harness/score_comparison.py`](harness/score_comparison.py) | Scores recorded runs: per task and aggregate, paired bootstrap intervals, cost per accepted completion |
| [`harness/selftest.py`](harness/selftest.py) | Offline self-check of the whole harness; no agent, no credentials, no network |
| [`harness/fixtures/`](harness/fixtures) | Public synthetic run records the self-check scores |

## The three arms

1. `bare` — the agent with no RepoContext at all.
2. `current` — the 0.14.x integration: instructions, on-demand skill, MCP server.
3. `guarded` — arm 2 plus the 0.15.0 read-cost guard and lifecycle-bound sessions.

Comparing all three separates *what RepoContext already saves* from *what this
change adds*. Agent, model, prompts, commits, tools and verification stay fixed
across arms; only the arm's own setup differs.

## Running it

```sh
# 1. What is missing here?
python3 harness/run_comparison.py --check --config harness/agent.example.json

# 2. The plan, without executing anything.
python3 harness/run_comparison.py --plan

# 3. The offline self-check: proves the harness itself, costs nothing.
python3 harness/selftest.py

# 4. The real comparison (needs credentials and a price sheet).
python3 harness/run_comparison.py --config my-agent.json --out runs.jsonl
python3 harness/score_comparison.py runs.jsonl --prices prices.json \
    -o report.json --markdown report.md
```

`--check` prints the concrete list of what is missing — an unset credential
variable is named by name — because "we could not run it" is a result that
belongs in the report, not a reason to publish an unmeasured number.

## What the scorer will and will not say

* Provider-reported usage is primary. Local tokenizer estimates and
  hypothetically avoided reads are separately labelled estimates and are never
  summed with billed figures.
* Billing categories are disjoint; separately billed reasoning tokens are added
  exactly once, and never when the provider already includes them in output.
* Failures, timeouts and retries stay in the denominator and in the cost total.
* Cost per accepted completion divides total spend — including spend on failed
  runs — by accepted completions. With no accepted completion it is **undefined**,
  not zero.
* Cold and warm cache workloads are summarized separately, never averaged.
* Any missing usage field or unscored rubric marks the comparison incomplete,
  and the report's `claims` section then states that no saving and no
  equal-quality claim is supported.
* Every paired comparison reports the largest quality regression still
  compatible with the data. Absence of a significant difference is not evidence
  of equal quality.

## Status in this repository

**No arm has been executed.** This environment has no agent credentials, no
provider price sheet and no billing access, and the plan forbids inventing them:

* `ANTHROPIC_API_KEY` (or the equivalent for another agent client) is unset,
* no pinned agent client binary is installed,
* no provider pricing sheet with a date and currency is available,
* `npm install` for the three JavaScript/TypeScript repositories needs network
  access that the offline product constraint deliberately excludes from CI.

What *is* finished and verified here is the executable setup: the frozen
manifest and rubric, the counterbalanced plan, the run-record contract, the
prerequisite reporter, the scorer and its offline self-check, which
[`AgentEvaluationManifestTests`](../../../tests/RepoContext.Integration.Tests/Evaluation/AgentEvaluationManifestTests.cs)
keeps frozen in CI. Whoever has credentials can execute the arms without
changing a line of this harness, and the report they produce will state its own
completeness.
