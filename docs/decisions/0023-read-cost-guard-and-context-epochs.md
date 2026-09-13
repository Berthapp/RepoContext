# ADR 0023 — The opt-in read-cost guard and context epochs

- **Status:** accepted; enforce mode experimental
- **Date:** 2026-09-12
- **Plan:** [same-quality, lower-cost agent workflows](../plans/same-quality-lower-cost.md)
- **Supersedes nothing.** Extends ADR 0012 (budgets), 0015 (receipts and state
  identity), 0016 (exact cost semantics) and 0018 (client integrations).

## Context

RepoContext already returns compact exact evidence. It cannot make an agent ask
for it: a client's own file-read tool is one call away, and an agent that reads a
900-line file in full has paid for it before RepoContext hears about the task.
Separately, `--session` remembers what was delivered — but a session file has no
idea whether the conversation that received the evidence still holds it. A
compaction, a resumed conversation or a fork silently invalidates that
assumption while the file keeps asserting possession.

The objective is **the same task-result quality at a lower total cost**. Fewer
returned tokens is a mechanism, not the criterion: a cheaper failed task, an
incomplete answer or a change needing more repair is not an improvement.

## Decision

### 1. A deterministic read-cost policy in Core

`RepoContext.Core.Guard.ReadCostPolicy` decides whether cheaper exact evidence
exists for a read a client is about to perform. It is a pure function of the
request, the index metrics, the mode and the redirect history: no network, no
model, no re-index, no command execution.

- The threshold is an **estimated token cost**, not a line count. Whole-file
  counts come from `files.token_count` and are scaled by the configured
  calibration profile; a partial read is prorated by lines, because per-line
  counts are not stored. Default: 2,000 tokens in the active profile, an
  engineering starting point, not a validated value.
- A read is priced by **the window actually requested**. A client asking for 40
  lines is doing the right thing and is not charged for the rest of the file.
- A file the index does not carry — never indexed, excluded, filtered as
  sensitive, larger than the size limit, or changed on disk since indexing — is
  never judged, never named and never suggested. The guard opens no lookup path
  to content the privacy filters keep out, and it never judges a read on metrics
  that no longer describe the file.

### 2. Three modes; the guard never grants a read

`off`, `observe` (evaluate and count, block nothing — the mode a newly installed
guard runs in) and `enforce` (deny a supported expensive read once, with a
concrete cheaper call). Existing integrations are unaffected until a guard is
installed explicitly.

The adapter emits `"permissionDecision": "deny"` or **no decision at all**. It
never emits `"allow"`: that would skip the client's own permission prompts, and a
cost policy must not be able to widen what an agent may read. The guard is an
optimization, never an access-control boundary, and it never overrides client
permissions, approval rules or repository exclusions.

### 3. Enforcement is bounded

A denial names the file, its estimated cost and the exact alternative calls
(`repoctx outline <file>`, `repoctx context "<task>" --path <file> --detail
slices`), with every path shell-quoted so an untrusted path stays one argument.
Repeating the same read is **allowed through** (`MaxRedirectsPerPath`, default
1). That bound is what makes enforcement safe: no agent can be caught between a
denial and evidence the index cannot supply, and there is no retry loop.

The guard never treats a `repoctx` invocation as a read, so the alternative it
suggests can never be blocked.

### 4. A small, documented shell subset — and nothing else

Structured `Read` payloads are the primary input. For `Bash`, only plainly
whole-file readers are modelled: `cat`, `head`, `tail`, `nl`, `bat`, `less`,
`more`, `type` and `sed -n '<start>,<end>p' <file>`. Quoting, paths with spaces,
Windows-style paths and several input files are covered by fixtures.

Pipelines, redirections, command substitution, variable expansion, globbing,
brace expansion, command lists, scripts and mixed read/write commands are
**unsupported**: the client handles them normally and the guard counts an
observation. No command is ever executed to discover what it would read. Missing
a read costs tokens; misreading one costs the user their work.

### 5. Context epochs bind reuse to the real context lifetime

`RepoContext.Core.Context.ContextEpochs` records, per repository, a monotonic
epoch for each agent. The key is the client id plus a digest of the client's
session id — never the raw id, which identifies a conversation.

Every lifecycle event the adapter cannot prove harmless starts a new epoch:
`SessionStart` (whatever its matcher value) and `PreCompact`. The mapping of
matcher values to reasons is recorded for humans, not relied on for correctness:
an unrecognized or renamed field still starts an epoch, so it can cost a repeated
read and never a false possession claim.

The v2 session name carries an epoch and a fresh generation token
(`rcx-claude-code-<digest>-g<generation>-e<n>`). Generations prevent receipt
resurrection after a retirement, eviction, deleted ledger or damaged ledger.
Unknown or missing epoch state invalidates generated names. Legacy generated
names are invalidated; ordinary manual names such as `review-e1` remain manual. `SessionStore`
loads and saves **nothing** for an epoch-bound name whose epoch has moved on, so
a local file cannot keep asserting possession on behalf of a replaced context.
Manual `--session` and `REPOCTX_SESSION` names are not epoch-bound and keep their
existing semantics exactly.

The adapter cannot mutate a running MCP server's environment, and it does not
try. The active name is announced to the agent through the `SessionStart` hook's
`additionalContext`; the CLI takes it as `--session`, the MCP `get_context` tool
as its `session` argument. A client that announces nothing simply gets no
automatic reuse — the safe direction.

### 6. Installation touches only what RepoContext owns

`repoctx integrate --guard [--guard-mode enforce]` adds `PreToolUse`,
`SessionStart`, `PreCompact`, `SessionEnd` and `SubagentStart` entries to `.claude/settings.json`;
`integrate --remove` takes them out. Only exact generated command forms (and the explicitly recognized legacy form)
are owned. Commands merely mentioning repoctx are preserved. A foreign hook
sharing a group retains its matcher when our entry moves. Every other hook, permission, environment
variable and MCP server is written back unchanged. A settings file that cannot be
parsed is reported, left untouched, and fails the command, so an uninstalled
guard is never mistaken for an installed one. Both operations are idempotent, and
`integrate` without `--guard` still never touches client settings.

Entries are written in shell form, which Claude Code runs through `sh` on Unix
and PowerShell on Windows — the form that works with the `.cmd` shim an npm
install leaves on Windows. A repository that pins `repocontext-tool` gets a
`node`-launched entry, chosen from the repository, never from the author's
operating system.

### 7. Counters are activity, not savings

`.repoctx/guard.json` holds bounded counters — observed, allowed, redirected,
denied, escalated, unsupported, error and a fixed-bucket latency histogram — and
honours `REPOCTX_NO_STATS`. It contains **no path, command, prompt, source or
session id**. The redirect bookkeeping that bounds enforcement is kept even when
statistics are disabled, because it is policy state rather than usage data; it
stores a truncated path digest and a small integer, bounded per epoch and in the
number of epochs.

`repoctx stats` reports these counts in their own section, outside the savings
arithmetic, with the reason stated: a denied read is not a realized saving,
because the next call, a later full read and any recovery are spend this ledger
cannot see. Only the paired real-agent comparison in `docs/eval/agent/` can turn
guard activity into a cost claim.

## Client capability matrix

| Client | Read-cost guard | Context epochs | Basis |
| --- | --- | --- | --- |
| Claude Code | supported (opt-in) | supported | Official hook reference, verified 2026-09-12: `PreToolUse` payload (`hook_event_name`, `tool_name`, `tool_input`, `session_id`, `cwd`), exit-code semantics, `hookSpecificOutput.permissionDecision`, `SessionStart.additionalContext`, project `.claude/settings.json` scope |
| Cursor | not supported | not supported | No verified contract for intercepting the client's own file reads |
| GitHub Copilot | not supported | not supported | Same |
| Windsurf | not supported | not supported | Same; its MCP registration is machine-global, outside the repository |
| AGENTS.md clients | not supported | not supported | Instructions cannot intercept another client's native reads |

Unsupported clients keep their existing behaviour and are told so by
`integrate --guard` rather than silently skipped. MCP registrations and
`AGENTS.md` instructions are **not** interception mechanisms and are not treated
as one.

## Consequences

- Enforce mode stays **experimental** until the three-arm comparison in
  `docs/eval/agent/` has been executed. Observe mode measures coverage without
  changing behaviour; that is what it is for.
- The guard adds latency to every inspected tool call. It reuses index metadata,
  bounds its I/O, gives up on its own budget and is killed by the client's
  `timeout` if it overruns; measured figures and their limits are recorded in
  `docs/eval/agent/local-measurements.md`.
- Token figures are estimates and are labelled as such everywhere. The
  calibration profile is a heuristic, not a native-tokenizer guarantee.
- A new epoch costs at most a repeated read. That is the deliberate direction of
  the trade: the opposite error is an answer built on evidence the model never
  saw.
- No new MCP tool schema is added, and the always-loaded instruction pointer is
  unchanged, so neither always-on gate (150 tokens of instructions, ~1,700 tokens
  of MCP schemas) grows.

## Rejected

- **A fixed line cutoff** (the 350-line rule the source whiteboard suggested):
  charges dense JSON and sparse source the same, while the caller is billed for
  tokens.
- **A regular expression over shell commands**: would "cover" pipelines and
  expansions while being wrong about most of them.
- **Replacing the tool result from the hook**: not a portable contract. The
  portable one is a denial plus a concrete next call.
- **Writing the session name into generated MCP configuration**: a fixed session
  name shared by two agents reopens the false-cache-hit class ADR 0015 closed.
- **Migrating pre-epoch session state on a guess**: unknown state is discarded,
  never resurrected as a possession claim.
- **Local-model bulk reading**: deferred by the plan; it needs its own ADR,
  evaluation and cost accounting, and a cheaper model can lower price while
  raising total tokens.

## Review corrections — 2026-09-13

- Persist a repeat marker successfully before emitting a denial; lock contention,
  write failure or state capacity limits permit normal client handling. Local
  state locks are non-blocking, and the deadline is checked again after evaluation.
- Missing session identity, damaged JSON/configuration and SQLite errors fail open.
  Offset-only reads and windows beyond EOF count only the remaining lines.
- Reject metrics after a configuration change or a newer file/ancestor ignore
  file timestamp, and reject symlink paths. This remains a metadata freshness
  heuristic: deliberately restored timestamps are not content-hash verification.
- Outline suggestions use absolute file paths so they also work from a subdirectory.
- SubagentStart retires the parent's announced session before a child can inherit
  its possession claims. Both re-read until a new lifecycle announcement. This
  conservative fallback avoids sharing evidence with children without pretending
  the adapter can change a running MCP server's environment.
- A malformed settings file makes integrate/check exit 1 while preserving the file.
- Context epochs include a random lifecycle generation; determinism still means
  identical query plus identical index and supplied session state. Runtime state
  identity is not a retrieval result or a benchmark measurement.
- The initial benchmark harness was incomplete beyond absent credentials. See
  [its corrected status](../eval/agent/README.md#status-in-this-repository).
- The hook's wall-clock budget (400 ms by default, measured from the moment the
  process begins reading the payload) means a loaded machine gets no enforcement
  at all: the guard stands aside and prints nothing. That is the intended
  production direction — nobody waits on a cost optimization — but it also makes
  any assertion about a *decision* a race with the machine the suite runs on, so
  the tests that pin policy behaviour set a generous budget explicitly. The
  fail-open itself keeps its own test at a zero budget.

Hook lifecycle reference checked during review:
https://code.claude.com/docs/en/hooks#subagentstart
A live pinned Claude client was unavailable here; CLI payload fixtures and CI
are the validation boundary, not an end-to-end client compatibility claim.
