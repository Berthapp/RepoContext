# Why it costs less — and why the answers do not get worse

Every token an AI coding agent reads is billed. RepoContext exists to make that
bill smaller **without** making the agent's picture of your repository thinner.
Those two goals pull against each other, so this page separates them: first the
mechanisms that remove tokens, then the rules and gates that stop a saving from
quietly becoming a worse answer — each with the measurement that backs it and a
pointer to the artifact you can re-run.

Nothing here requires trusting a vendor benchmark. Every figure below comes from
a file in this repository, produced by a deterministic harness that runs offline.

- [Where the money actually goes](#where-the-money-actually-goes)
- [Seven levers](#seven-levers)
- [Why quality holds](#why-quality-holds)
- [The proof that we mean it](#the-proof-that-we-mean-it)
- [What is *not* claimed](#what-is-not-claimed)
- [Measure it on your own repository](#measure-it-on-your-own-repository)

## Where the money actually goes

The expensive part of agent work is not the tool call. It is what the agent
reads afterwards.

Measured on this repository for the task *"improve token budget packing in the
context engine"* ([ADR 0010](decisions/0010-token-frugal-context-protocol.md)):
an agent that asks for file pointers and then opens the top three files pays
**~886 tokens for the answer and ~5,336 for the reads — ~6,222 in total.** The
answer is 14 % of the bill. The reads are the other 86 %.

That is the whole thesis in one number. Shortening responses is rounding error;
removing *reads* is the saving. So RepoContext is built to answer a task with
the evidence already inside the response, to prove what an agent already holds
so it is never sent twice, and to bound the response exactly so a call can never
overshoot.

| Same task, three loops | Tokens |
| --- | ---: |
| Pointers, then read the top 3 files | **6,222** |
| `context --detail slices --budget-tokens 2000` (3 best slices embedded) | **2,110** |
| `context --detail outline --budget-tokens 2000` (7 files surveyed) | **2,151** |
| `outline` of the single 3,256-token main file | **1,111** |

Source: ADR 0010, measured at M6 on this repository (75 files, exact
`o200k_base` counts). Your repository is not this repository — which is why
`repoctx stats` measures yours instead of asking you to believe these.

## Seven levers

### 1. Deliver the evidence, not a reading list

`context --detail slices` embeds symbol-aligned source spans in the answer, so
the file read that used to follow simply does not happen. `--detail outline`
covers more files at less depth for survey questions. The measured effect is the
table above: **about a third of the pointer-plus-read loop.**

### 2. Let the agent decide *before* it reads

When a full read is genuinely needed, the decision itself should be cheap.
`outline` returns a file's skeleton — symbols, signatures, doc summaries, line
ranges and the exact full-read cost — for **1,111 tokens against the file's
3,256** (ADR 0010). `architecture --depth 1` is a ~300-token orientation, and
`changed` reports a clean working tree for 154 tokens.

### 3. A ceiling that is measured, not estimated

`--response-budget-tokens` is enforced against the **exact rendered response**
— the CLI counts its trailing newline, MCP counts its text content block
([ADR 0016](decisions/0016-exact-budgets-and-cost-semantics.md)). There is no
first-item exception, and the engine throws if the packer ever accepts a
response above the ceiling. If nothing useful fits, the command emits **no
partial result**: it exits `3` with a deterministic `retry_budget_tokens` that
is guaranteed to fit.

This is the difference between a budget and a hope. A tool that merely *aims*
for 2,000 tokens still produces the occasional 9,000-token answer — and that
answer is billed, blows a hole in the context window, and is often what pushes a
session into compaction.

### 4. Never pay for the same bytes twice

Every delivered pointer, span and outline symbol carries a `receipt`. Echo one
with `--seen` and exactly that unit is suppressed — the rest of the file still
arrives — and acknowledged units **do not consume a `--top` slot**, so the freed
space buys new evidence rather than markers.

Measured on the frozen evaluation corpus (query `change budget packing`, slices,
top 3 — [baseline](eval/baseline.md#reuse-economics)):

| | core tokens | embedded content | results | reused |
| --- | ---: | ---: | ---: | ---: |
| first call | 1,916 | 756 | 3 | 0 |
| repeat with receipts | 621 | 49 | 1 | 4 |

Three further shapes of the same idea:

- `--known <path>@<hash>` acknowledges a file the caller holds **in full**.
- `--session <name>` keeps that bookkeeping locally, so the agent echoes
  *nothing*. Echoed hashes are model **output** tokens, priced several times
  input on every major model ([ADR 0012](decisions/0012-token-optimization-levers.md));
  a session costs zero.
- `changed --patch` returns unified hunks after an edit instead of the file
  again, and reports `patch_tokens` against `file_tokens` so the choice is
  visible.

### 5. Keep the prompt around the call cheap

Instruction files are loaded into **every** prompt of every session, including
the ones RepoContext cannot help with. So the managed block in `CLAUDE.md`,
`AGENTS.md` and `.github/copilot-instructions.md` is a pointer of about a
hundred tokens — capped by a test at **150 tokens** and asserted to stay under a
third of the full protocol. The protocol itself arrives only when a task needs
it: as a Claude Code skill, a Cursor/Windsurf rule, or `repoctx guide`.

For MCP the equivalent fixed cost is the server instructions plus eight tool
schemas: **1,698 tokens once per session**, held under a 1,700-token test gate
so growth is a decision rather than an accident.

And `prime` is deliberately byte-stable across re-indexing — volatile aggregates
are quantized, nothing is ordered by score, no timestamps — so a repository
primer can sit behind a prompt-cache breakpoint, where cached input costs about
a tenth of fresh input. A primer that changes on every index would invalidate
the agent's whole prefix cache instead.

### 6. Cheaper serialization of identical evidence

JSON escaping (`\n`, `\"`) is billed, and only JSON pays it — so slices are
charged as raw text for `text`/`md` and in serialized form only for `json`, and
the agent instructions steer slice work to Markdown. For callers that must parse
an envelope, `--compact` drops legacy duplicate fields. Measured over all 36 frozen holdout tasks
rendering the **same unbudgeted evidence**: **217,869 tokens compact against
246,297 default — 11.5 % less** ([holdout report](eval/holdout/README.md)).

### 7. Remove the round trip that produced nothing

A wasted call is the most expensive kind of saving to miss: the agent pays for a
useless response *and* asks again.

- `--detail auto` reads the task's shape from a small, versioned, deterministic
  rule table — change verbs get source spans, survey terms get outlines — and
  the response reports the level it actually ran with.
- `trace ABC-123` resolves one work-item key, link, symbol or path in a single
  indexed lookup, replacing a repository-wide grep and the reads that follow it.
- `--intent fix|explain|review` promotes the directly linked companions (test,
  dependency, dependent, document) of a named implementation file.
- `--path` scopes the query before ranking, so the rest of the repository costs
  neither relevance nor tokens.
- `memory search` answers a settled "why is it like this here" for ~40–80
  tokens, against the outline plus reads it would take to re-derive it.

### Calibration: budgets in *your* model's units

The index stores raw `o200k_base` counts, and `tokens.profile` scales them at
query time (`claude` ≈ 1.2, because o200k undercounts Claude tokenization on
typical source by roughly 15–25 %). Scaling rounds **up**, so budgets never
undershoot, and switching model families never requires a re-index. A ceiling
measured in the wrong tokenizer is not a ceiling.

## Why quality holds

A cheaper answer is worthless if it is also a worse one. These are the rules
that keep the two apart — all of them properties of the code, not intentions.

**Spans are symbol-aligned, never truncated.** `SelectSpans` takes up to three
non-overlapping ranges per file in relevance order — the matching symbol's
range first, then the best full-text chunk, then the preamble — and emits them
in source order. Text is reconstructed from indexed chunks, so
`start_line..end_line` matches the delivered bytes exactly. You get whole
declarations, not a paragraph cut mid-function. If no source can be
reconstructed, the file degrades to a pointer rather than disappearing.

**The graph supplies what lexical search alone misses.** Bounded two-hop
expansion with per-hop decay adds the test that covers a file and the module
that imports it, ranked below the direct match. Vendor and generated paths are
penalized, as are fixtures, tests and documents the task did not ask for, and
repeated directories are demoted — so the budget is spent on evidence the task
needs instead of on near-duplicates. Fewer tokens, and different ones.

**Nothing is dropped silently.** Every hit carries machine-readable `reasons`.
`--explain` returns up to eight *omitted* candidates with rank, score and the
constraint that excluded them, and marks a truncated sample `unlisted`. An agent
can see the shape of what it did not get — the opposite of a context window that
silently ran out.

**An impossible budget is an error, not a thin answer.** No partial success;
exit `3` plus a retry budget guaranteed to fit.

**Reuse never over-claims possession.** A receipt suppresses exactly one
delivered unit; `--known` is an explicit assertion that the caller holds the
whole file. Conflating them was a real bug
([ADR 0015](decisions/0015-state-identity-and-receipts.md)): deriving `--known`
from a slice tells the model it holds lines it was never sent. That is the
dangerous way to save tokens, and the distinction is preserved through sessions
too.

**Lossy transforms are opt-in and flagged.** `--strip-comments` is line-based
and conservative — a line mixing code and a trailing comment is kept intact, so
code is never lost — and the item is flagged `stripped` so callers know its line
ranges became approximate.

**Staleness is visible rather than assumed.** Responses carry `content_state`
and `analysis_state`; `--ensure-fresh` re-indexes before querying; memory
entries whose linked files changed come back flagged `stale` with the drifted
paths.

**Memory can never crowd out the source.** Recalled memories are admitted
before file evidence because they are cheap, but if their reserve leaves no
source item deliverable, they are dropped and the bundle is repacked — a stale
note can never turn a code query into a memory-only false success.

**Quality is regression-gated, not asserted.** A frozen corpus of **30
source-inspected retrieval tasks across four repositories** (three of them
external open-source projects) plus six historical tasks, with exact line-range
labels **written before the corpus was ever run** and no ranking tuned on the
results. The manifest and every source archive are pinned by SHA-256, and the
tests reject any change that drops a previously delivered required file or
labelled source line, on every output surface. See the
[holdout report](eval/holdout/README.md).

**Determinism makes all of this auditable.** The same index and query produce
byte-identical output, with no embeddings and no model in the loop. A bad
answer is reproducible, explainable from its `reasons`, and therefore fixable —
rather than a different roll of the dice next time.

**Even the savings figure is conservative.** In the `stats` ledger, discovery
calls (`search`, `related`, `changed`, `architecture`, `context --detail paths`)
are credited **nothing**; a span or pointer receipt earns no speculative
full-read credit because it proves possession, not an avoided read; only
delivered content and an explicit matching whole-file claim count. Net saved can
go negative on a discovery-heavy day, and the dashboard shows it.

## The proof that we mean it

After the holdout corpus was frozen, an experimental two-pass packer was tried:
admit one relevant unit per file before adding more units to files already
selected. It **raised** required-file recall from 29/38 to **36/38 (94.7 %)** —
comfortably past the 90 % target that was on the roadmap.

It was rejected.

Delivering one fragment of more files meant delivering less of what each task
actually needed: relevant lines fell from 204/449 to **115/449**,
evidence-complete tasks from 9/30 to **3/30**, and the oracle-assisted follow-up
reads those tasks still required rose from 26 to 32 (43,184 → 53,734 tokens).
The headline metric improved while the agent's real cost got worse. The patch is
preserved in [`packing-experiment.patch`](eval/holdout/packing-experiment.patch)
for reproduction and is **not part of the product**.

That is the standard this page is written to: a token saving that costs relevant
evidence is not a saving, and a number that improves while the work gets harder
is not an improvement.

## What is *not* claimed

Being precise about the limits is what makes the rest worth reading.

- **No end-to-end agent benchmark.** The frozen corpora measure evidence
  retrieval and response cost. They do not measure whether a coding agent
  completed a task, or what a whole session cost. Any such claim would need a
  model in the loop, which this tool deliberately does not have.
- **"Reads replaced" is counterfactual.** It credits a read that would
  *probably* have happened. It is an estimate, not a guaranteed lower bound —
  labelled as such in the dashboard and in
  [the accounting](token-savings.md#usage-dashboard-semantics).
- **Tight budgets still lose files.** Under a 2,000-token JSON ceiling the
  holdout delivers 29/38 required file occurrences (76.3 %); Markdown 32/38;
  without a ceiling, 38/38, and every required file is an eligible candidate.
  The 90 % target under a tight budget is **not met**, and is recorded as open
  work rather than rounded away.
- **The external holdout projects are small.** Three single-author async
  JavaScript/TypeScript libraries; file recall there is easier than in a large
  application. A second C# repository and large external repositories remain
  unmeasured.
- **Scaling numbers are from synthetic probes.** At five repetitions, a
  reported p95 is the sample maximum, not a production tail estimate.
- **The M6 figures are from M6.** The 6,222 → 2,110 comparison was measured on
  this repository at that milestone. It illustrates the mechanism; it is not a
  promise about yours.

## Measure it on your own repository

The honest answer to "how much will this save *us*" is that nobody knows until
you run it, so the tool measures itself locally.

```bash
repoctx stats
```

Every successful query appends two figures to a git-ignored local log
(`.repoctx/stats.jsonl`, token counts and command names only — never content):
what the response actually cost, and the full-file reads it is credited with
replacing. Typed at a terminal, `stats` also writes a self-contained offline
HTML dashboard and opens it; piped or with an explicit `--format`, it stays on
stdout. Set `pricing.inputPerMtok` in `repoctx.config.json` to see the same
figures in money. `REPOCTX_NO_STATS=1` turns recording off entirely, and
deleting the log resets it.

The dashboard leads with the cumulative curve — what reading those files in full
would have cost, what RepoContext returned, and the band between them — because
the shape matters: savings usually come from a handful of many-file `context`
calls, not a steady drip.

## Read further

- [Token savings — measurement and accounting](token-savings.md) — what each
  layer counts, and what it refuses to count.
- [Evaluation baseline](eval/baseline.md) — per-task relevance and cost metrics.
- [Frozen retrieval diagnostics](eval/holdout/README.md) — the 36-task holdout,
  its labels, and the rejected packing experiment.
- [ADR 0010](decisions/0010-token-frugal-context-protocol.md) ·
  [ADR 0012](decisions/0012-token-optimization-levers.md) ·
  [ADR 0016](decisions/0016-exact-budgets-and-cost-semantics.md) —
  the protocol, the levers, and exact budget semantics.
