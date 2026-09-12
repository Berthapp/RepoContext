# Same-quality, lower-cost agent workflows

Status: planned; no hook, session-lifecycle adapter or agent benchmark is implemented by this document.
Baseline: main at `f422371a9362f4b8ed19e032ff2e2ff3c4da41d3` (0.14.1).
Planning update: 0.14.2. Target feature release: 0.15.0.
Date: 2026-09-12.

## Product objective

**RepoContext must deliver the same task-result quality at a lower total cost.**

Fewer returned tokens are a mechanism, not the acceptance criterion. Count the
entire workflow: discovery, instructions, tool calls, repeated context, retries,
compaction, editing, review and verification. A cheaper failed task, an incomplete
answer, or a change needing more human repair is not a product improvement.

The supplied whiteboard suggests enforcing read discipline with hooks and
delegating bulk reading to a cheaper model. Its claimed 90% saving and attribution
are unverified inspiration, not a RepoContext benchmark or release target.

## What exists and what is missing

| Capability | Current implementation | Work to add |
| --- | --- | --- |
| Compact exact-source evidence | Outlines, symbol-aligned slices, scoped context, intent and omission diagnostics | Route eligible broad reads toward existing evidence commands |
| Response limits | Exact rendered budgets in configured accounting units | Read-cost policy with explicit tokenizer-estimate uncertainty |
| Evidence reuse | Exact-unit receipts, whole-file assertions, local sessions and REPOCTX_SESSION | Bind reuse to actual agent context lifetime |
| Delta reads | changed --patch | Prefer useful deltas after edits without treating a diff as a full file |
| Agent integration | Instructions, on-demand skills/rules, MCP registrations | Opt-in client-specific hook installation and removal |
| Measurement | Local response ledger and frozen retrieval evaluations | Paired real-agent quality and total-cost evaluation |

Reuse these components. Do not rebuild the context engine, introduce an LLM into
Core, or replace exact source evidence with generated summaries.

Implementation entry points:

- `src/RepoContext.Core/Configuration/AgentIntegrations.cs`
- `src/RepoContext.Core/Configuration/AgentInstructions.cs`
- `src/RepoContext.Cli/Commands/IntegrateCommand.cs`
- `src/RepoContext.Core/Context/AmbientSession.cs`
- `src/RepoContext.Core/Context/SessionStore.cs`
- `src/RepoContext.Core/Identity/Receipt.cs`
- `src/RepoContext.Core/Stats/`
- `tests/RepoContext.Integration.Tests/Evaluation/`

## Design boundaries

Keep the .NET 10/C# core deterministic, offline, explainable and free of network,
telemetry, model and embedding calls. The real-agent evaluation runs separately
from normal offline CI. It does not add an automatic model call to product use.

Start with Claude Code. Before implementing, verify the current official hook
contract and pin the tested client version: event payloads, permission decisions,
exit codes, output visibility, lifecycle events and supported installation scope.
Do not infer that MCP or AGENTS.md can intercept another client's native reads.
Publish a client capability matrix; unsupported clients retain existing behavior.

A read-cost hook is an optimization, not an access-control boundary. It must
never bypass client permissions, approval rules or repository file exclusions.

Proposed command/option names in this plan are design candidates, not commands
available in 0.14.2. Record the finalized contracts in an ADR before shipping.

## Milestone 1 — Establish the cost and quality baseline

Deliver a separate opt-in evaluation harness and versioned task manifest.

1. Compare three arms: agent without RepoContext; current RepoContext integration;
   current integration plus the candidate hooks/lifecycle adapter. Keep the agent,
   model, prompts, repository commits, tools and verification requirements fixed.
   This separates existing savings from incremental improvement.
2. Include fixes, feature changes, refactoring, explanations and reviews. Include
   TypeScript/JavaScript and C#, a larger multi-project repository, long files,
   cross-file behavior, stale indexes and context-compaction scenarios.
3. Freeze tasks, acceptance tests and review rubrics before tuning. Retain an
   untouched holdout; do not rewrite labels or goldens to hide regressions.
4. Run paired repetitions, initially at least five per task/arm, with isolated
   worktrees, fresh agent contexts and receipts. Counterbalance execution order.
   Expand repetitions only when uncertainty prevents a release decision.
5. Measure both cold and warm-cache workloads separately. Reproduce realistic
   cache behavior instead of charging every repeated token as fresh input.
6. Retain failures, timeouts and retries in the denominator and cost totals.
   No dropping unsuccessful or expensive runs.

Record for every run:

| Dimension | Required measurement |
| --- | --- |
| Quality | Task acceptance tests; explanation/review rubric; regression failures; severity of defects; required human correction |
| Spend | Actual provider-reported uncached input, cached input/cache writes and output/reasoning billing categories where exposed; all agent/model calls |
| Workflow | Follow-up reads, tool calls, retry/compaction counts, fallback use, review/test effort |
| Time | End-to-end latency, hook latency, indexing cost and timeout rate |
| Provenance | Repo/agent/model versions, configuration, pricing currency/date, task/run ID and completeness of usage data |

Use disjoint billing categories under the provider's pricing rules; do not count
reasoning tokens twice when included in output. Report both total spend and
cost per accepted completion (including spend on failures). If no task completes,
that latter metric is undefined, not zero. For flat subscriptions, distinguish
estimated API-equivalent cost and quota consumption from actual billed money.

Real usage is primary. Local tokenizer estimates and hypothetical avoided reads
remain separately labelled estimates. The current Claude scaling factor is a
calibration heuristic, not an exact native-tokenizer guarantee. Missing billing
data makes a spend comparison incomplete rather than proving a saving.

**Exit:** Reproducible baseline report, frozen quality rubric and cost accounting.
No claim of equal quality from retrieval scores alone.

## Milestone 2 — Add an opt-in read-cost guard

Implement a deterministic policy in Core and a thin CLI/client adapter.

1. Add observe and enforce modes, with observe as the initial default for a newly
   installed guard. Existing integrations remain unchanged until explicitly enabled.
2. Inspect native Read requests using the actual requested offset/limit. Permit
   cheap or narrowly scoped reads. Compare estimated tokens with a configurable
   threshold; do not copy a fixed 350-line cutoff.
3. Start with structured Read payloads. Extend to a documented subset of simple
   shell reads only after parser/fixture coverage exists. Never execute a command
   to discover what it would read. Cover quoting, paths containing spaces,
   Windows paths and multiple input files in the supported subset.
4. Treat ambiguous pipelines, expansions, scripts and mixed read/write commands
   as unsupported: allow normal client handling and count an observation.
   Do not use a broad regex to claim complete shell coverage.
5. For a supported expensive read, issue a small actionable denial containing a
   safely encoded suggested outline/context lookup. Use existing slices, scopes
   and response budgets. Only include task context the client actually provides;
   a file-read event does not necessarily contain the user's task.
6. Do not assume a pre-tool hook can replace a tool result. Confirm that behavior
   against the client; the initial portable contract is deny plus a concrete
   next call. Suggested commands must quote untrusted paths safely.
7. Never block the alternative RepoContext call itself. Bound redirect attempts
   per request and provide a scoped escalation path for genuinely necessary
   full reads. Escalation concerns cost policy only, never client permissions.
8. Missing/stale indexes, oversized/unindexed files, unreadable files and hook
   errors must not create deadlocks or false evidence. Use an explicit, bounded
   refresh/retry where suitable; otherwise allow the client's normal read.
9. Keep the hook fast: no full re-index, network request or model call inside the
   guard. Reuse valid local metadata, bound I/O and enforce a short timeout.
   Initial engineering target: warm p95 below 100 ms on a declared benchmark
   machine; report cold-start cost separately and tune before declaring support.
10. Preserve privacy filters and repo-root/path resolution. A blocked or excluded
    path must not gain exposure through diagnostics or a new lookup mechanism.

**Exit:** Observe mode has measured coverage; enforcement tests show bounded
fallback, successful targeted reads and no infinite retry loop. No security or
quality gate is traded for fewer tokens.

## Milestone 3 — Manage evidence for the real context lifetime

Extend the existing session mechanism rather than creating a second cache.

1. Namespace state by canonical repository/worktree, client, agent instance and
   context epoch. Two agents must never silently share possession claims.
2. Bind lifecycle events to sessions only through verified client facilities.
   Cover new conversations, restart/resume, forked agents and context compaction.
3. After compaction or any uncertain retention event, invalidate prior possession
   claims by starting a fresh epoch unless the client provides explicit proof of
   retained units. Keeping a local receipt file does not mean the model remembers.
4. Persist only successful evidence delivery, with the exact unit/hash semantics
   already used by Receipt and SessionStore. An outline, summary, diff or slice
   cannot establish whole-file possession. If delivery cannot be established,
   prefer a repeat read over unsafe suppression.
5. Handle edits, deletes, renames and concurrent calls. Stale hashes cannot hide
   changed content. Session persistence failures may cost another read but must
   never prevent correct output.
6. Do not assume a pre-tool hook can mutate a running MCP server's environment.
   Define and test how the adapter passes the active epoch to CLI and MCP. If a
   client cannot safely synchronize it, disable automatic reuse for that path.
7. Keep manual --session / --seen / --known behavior compatible. Version persisted
   state if semantics change; discard unsafe old state rather than migrate guesses.

**Exit:** Lifecycle tests prove no false "already delivered" result after
compaction, resume uncertainty, concurrent agents, edits or failed delivery.

## Milestone 4 — Integrate and make savings auditable

1. Extend integrate with opt-in guard installation, check and removal. Update
   only RepoContext-owned entries in existing client settings; preserve unrelated
   hooks, permissions and MCP servers. Reject malformed settings without rewriting
   them. Installation and removal must be idempotent.
2. Verify launchers on supported operating systems and installation modes:
   local/global npm, .NET tool and packaged binaries. Document unsupported cases.
3. Keep always-loaded instructions within their existing 150-token gate; do not
   add more tool schemas without accounting for the current 1,700-token MCP gate.
4. Add bounded local guard counters: observed, allowed, redirected, escalated,
   unsupported, error and latency. Preserve REPOCTX_NO_STATS behavior. Never log
   source, raw shell commands, prompts or sensitive paths in the usage ledger.
5. Distinguish measured returned tokens, estimated avoided reads and imported
   real-agent billing in reports. A denied read alone earns no realized saving:
   the next call, subsequent full read and recovery can erase it.
6. Keep opt-in evaluation traces separate from normal stats; use public fixtures
   for shareable reports. Normal product operation remains offline.

**Exit:** Integration round-trips preserve user configuration; all overhead and
fallback costs are visible; no unsupported "90% saved" dashboard headline.

## Milestone 5 — Quality and release decision

Run existing deterministic evaluation gates unchanged, plus new hook/lifecycle
tests. Include malformed payloads, supported and unsupported shell syntax,
small slices of huge files, stale indexes, timeouts, compaction, parallel agents,
receipt invalidation, protected files and installation round-trips.

Then run the frozen real-agent comparison. Report per-task results as well as
aggregates so a gain on easy tasks cannot conceal damage to harder ones.

Promotion criteria:

- No lost required evidence on existing frozen retrieval gates and no new
  security/correctness regression in required task tests.
- No lower observed task acceptance or rubric quality, and no increased
  severe-defect or required-human-repair rate against current RepoContext.
- Lower measured total spend and cost per accepted completion. Initial product
  target: at least 15% lower cost per accepted completion versus current
  RepoContext; this is a target, not an achieved or promised saving.
- Report paired confidence intervals for quality and spend, including the
  maximum quality regression still compatible with the data. Absence of a
  statistically significant difference does not establish equal quality.
  Inconclusive evidence keeps enforcement experimental; it does not pass.
- Explicitly report latency, per-task cost regressions and failure modes.
  Do not enable a policy broadly if extra retries or operational friction erase
  its benefit.

Tests and a finite benchmark cannot prove identical quality on every future
task. Publish the workload, uncertainty and limits of any supported claim.
Promote enforce mode only for the client versions and policy scope supported by
the evidence; retain a simple off switch and normal-read fallback.

## Deferred — Smaller-model bulk reading and code writing

Do not include model delegation in 0.15.0. First measure the offline guard and
session improvements independently.

A later separate, opt-in local-model adapter may summarize large source sets.
It needs its own ADR and evaluation, explicit model/runtime selection, bounded
input/output/time, source/hash/line citations and access to exact evidence.
Generated summaries remain labelled lossy and never become whole-file receipts.
Count the helper's tokens, runtime and any extra main-agent verification.

A cheaper model can reduce price while increasing total tokens. Never call that
a token reduction without measuring both. Do not adopt "write code and never
read the result": changed code still needs proportionate diff review and tests.

## Versioning and release checklist

The current change contains this plan, its README link and a patch bump
**0.14.1 -> 0.14.2** in Directory.Build.props. It introduces no feature behavior.

The implementation introduces opt-in CLI/configuration/lifecycle contracts and
therefore targets **0.15.0**, following the repository's pre-1.0 minor-bump rule.
Recheck main and tags before implementing; if 0.15.0 is already used, choose the
next appropriate unused version. Never downgrade or reuse a published version.

- [ ] Complete milestones 1-5 and record results, rejected experiments and limits.
- [ ] Finalize the ADR and compatibility/migration notes.
- [ ] Update VersionPrefix in the implementation PR; derive npm/NuGet/binary
      versions from that single source. Do not manually edit generated manifests.
- [ ] Update README, guide/integration documentation and release notes with only
      implemented and verified behavior.
- [ ] Pass Release build, core/integration and frozen evaluation tests, npm
      launcher/package checks, MSBuild smoke-pack, cross-platform evaluation
      and the existing no-network CI job.
- [ ] Before merge, verify CI against the latest implementation commit.
- [ ] After merge, verify tag-on-version-change creates the expected version tag
      and release.yml publishes NuGet, npm, binaries and the GitHub release.
      Existing prerequisites include RELEASE_PAT, NUGET_USER and npm publishing
      configuration. Do not change credentials or publish early for this plan.
- [ ] Verify installed repoctx --version and package versions agree with the tag.
      A version bump or tag alone is not proof publication succeeded.

Merge of the planning patch follows the same existing release mechanism for
0.14.2; that release must not advertise the planned hooks as implemented.

## Execution order

1. Baseline harness and accounting.
2. Guard observe mode and focused policy tests.
3. Guard enforce mode with bounded escalation.
4. Context-lifetime-safe session integration.
5. Client installation and local observability.
6. Holdout evaluation, documentation and feature-version bump.

Keep these as reviewable increments. Implement no later optimization merely to
hit a token headline. Each retained change must support the product objective:
**the same task-result quality for less total cost**.
