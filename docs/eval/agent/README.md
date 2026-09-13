# Agent cost-and-quality comparison (opt-in)

The product objective is **the same task-result quality at a lower total cost**.
Fewer returned tokens is a mechanism, not the acceptance criterion, so this
harness measures whole workflows: discovery, instructions, tool calls, repeated
context, retries, compaction, editing, review and verification.

The real comparison is deliberately **not** part of offline CI. Its runner
would start a real coding agent and spend money. The offline harness self-check
and accounting regression tests do run in CI.
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

# 4. Future real comparison (currently blocked: see status below).
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

**The full real-agent comparison is not executable yet.** Review on 2026-09-13
found implementation gaps in addition to missing credentials and prices:

- Review tasks refer to supplied patches and hidden defect lists that are absent.
- The compaction/stale-index scenarios have labels but no scenario controllers.
- Repetition order cannot establish cold/warm provider cache state.
- A raw Claude CLI invocation does not write the custom REPOCTX_EVAL_USAGE file.
  It needs an explicit usage adapter; the example configuration now says so.

The preflight now reports these gaps and prevents a paid run from being mistaken
for the frozen comparison. Completing the fixtures/controllers needs a new
versioned manifest; the original tasks and frozen hashes have not been changed.
The 0.15.0 enforcement mode therefore remains experimental.

The runner explicitly requests enforce mode for the guarded arm. It supports a
separate pinned baseline executable, records setup failures, catches agent launch
failures, and retains stdout, stderr and complete workspaces next to the output
in a .artifacts directory for human adjudication. It refuses to overwrite an
existing run set. Repository dependency setup, isolated agent configuration and
verified model/version/cache control still require the real-run adapter work.

The scorer requires the complete frozen run matrix, provenance, billing and
adjudication before allowing comparative claims. Missing or duplicate runs,
failed setup, unverified cache modes, missing repair/severity scores and mixed
currencies cannot pass as a complete comparison. Subscription/API-equivalent
figures are excluded from actual-money totals; incomplete totals are null with
known subtotals labelled separately. Empirical zero-width intervals do not prove
quality parity. No real saving or equal-quality result is established here.
