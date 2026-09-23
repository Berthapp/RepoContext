# RepoContext

> Local-first, explainable project memory for AI coding agents.

RepoContext is a local-first CLI (`repoctx`) that deterministically indexes a
software repository and gives AI coding agents compact, **explainable** context:
exactly the relevant files, symbols, tests and relationships — with a
machine-readable reason for every hit, and a hard token budget per answer.

It runs entirely offline. **No source code leaves the machine, there is no
telemetry, and no LLM or embedding calls are ever made.** The same query on the
same index always produces byte-identical output.

The point is the bill: an agent that is handed the right evidence does not read
half the repository to find it. [Why it is cheaper — and why the answers do not
get worse](#why-it-is-cheaper--and-why-the-answers-do-not-get-worse).

**Parsed with a full grammar:** TypeScript, TSX, JavaScript, C#.

**Outlined by line patterns** — declarations, with names, kinds and ranges:
Python, Go, Java, Kotlin, Scala, Groovy, Rust, Ruby, PHP, Swift, Dart, C, C++,
shell, PowerShell, Lua, Elixir, Perl, R, Protobuf, GraphQL.

**Structured as artifacts:** Markdown, MDX, AsciiDoc, reStructuredText, HTML,
XML (including `.csproj`, `.wsdl`, `.xsd`, `.resx`), YAML, JSON/JSONL, Gherkin
`.feature`, TOML, INI, `.properties`, CSV/TSV, SQL, Makefile, Dockerfile,
Terraform/HCL.

Everything else that is text is still indexed and searchable, and every file's
references are extracted, so the whole repository is cross-linked. Binary files
are the only category that cannot be described — and `index` reports how many
there were. See [Working with artifacts](#working-with-artifacts-tickets-specs-requirements).

## What changes in 0.15.2

A compatibility fix for MCP clients. The tools are now named with an underscore
(`repoctx_search`, `repoctx_get_context`, …) instead of a dot
(`repoctx.search`). Clients that forward MCP tools to the Anthropic API, such as
GitHub Copilot in Visual Studio with a Claude model, failed every request with
`HTTP 400: tools.N.custom.name: String should match pattern` because that API
only accepts letters, digits, `_` and `-` in tool names.

**After upgrading:** restart the MCP server in your client, and replace the
dotted tool names in any prompts or agent instructions you wrote yourself.

## What changes in 0.15.1

A security release. Running RepoContext inside a hostile checkout can no longer
index files outside the repository, write through committed symbolic links,
stall indexing with a crafted ignore pattern, index private keys and credential
files, or serve an index, memories or sessions that came with the checkout
instead of being built on your machine. Two configurations that used to work
are now refused: an `include` entry that is absolute or uses `..`, and a
`.repoctx/` that is a symbolic link.

**After upgrading:** run `repoctx index` once (the first query asks for it), and
run `repoctx memory adopt` at a terminal to keep memories you recorded before.
See [Working in an untrusted repository](#working-in-an-untrusted-repository).

## What changes in 0.15.0, in plain language

The goal is **the same quality at a lower total cost per completed task**.
Version 0.15.0 adds an optional read-cost guard for Claude Code and makes
context reuse safer:

- **Read only what helps.** Before a large file read, the guard can suggest an
  outline or exact source excerpts. It starts in observe mode; blocking requires
  explicitly enabling experimental enforce mode.
- **Do not mistake old notes for current knowledge.** When the conversation is
  compacted or restarted, generated sessions stop treating previously delivered
  evidence as still available. A child agent must not inherit its parent's
  possession claims. Manually named sessions still need lifecycle management
  by their caller.
- **Keep work moving.** A repeated read can proceed through the client's normal
  handling. If the guard cannot safely record a redirect, it does not block the
  read. Existing client permissions still apply.
- **Preserve your setup.** Installation changes only RepoContext's own hooks;
  unrelated hooks and settings remain intact.
- **Count completed work, not just shorter responses.** Guard activity is shown
  separately from estimated savings. An incomplete benchmark cannot establish
  lower costs or equal quality.

These changes improve reliability; **their effect on total agent cost and task
quality has not yet been measured**. RepoContext still works locally without
calling a second model to summarize or write code. See the
[setup and limits](#the-read-cost-guard-opt-in-experimental) and
[how success will be measured](docs/cost-and-quality.md#what-0150-changes-for-users).

## Why it is cheaper — and why the answers do not get worse

**The reads are the bill, not the answers.** Measured on this repository for the
task *"improve token budget packing in the context engine"*: an agent that asks
for file pointers and then opens the top three files pays ~886 tokens for the
answer and **~5,336 for the reads** — ~6,222 in total. The answer is 14 % of the
cost. Shortening responses is rounding error; removing reads is the saving.

| Same task, same repository | Tokens |
| --- | ---: |
| Pointers, then read the top 3 files | **6,222** |
| `context --detail slices --budget-tokens 2000` — 3 best slices embedded | **2,110** |
| `context --detail outline --budget-tokens 2000` — 7 files surveyed | **2,151** |
| `outline` of the single 3,256-token main file | **1,111** |

Measured at M6 (ADR 0010), exact `o200k_base` counts. Your repository is not
this one — which is why `repoctx stats` measures yours instead of asking you to
believe these.

### How the bill drops

| Lever | What it removes | Measured |
| --- | --- | --- |
| **Evidence, not a reading list** — `--detail slices` embeds symbol-aligned source spans; `--detail outline` covers more files at less depth | the follow-up file read | ~a third of the pointer-plus-read loop (above) |
| **Decide before reading** — `outline`, `architecture --depth 1`, `changed` | reads that turn out to be unnecessary | 1,111 vs 3,256 tokens for the same file; ~300; 154 |
| **A ceiling that is measured, not estimated** — `--response-budget-tokens` is enforced against the exact rendered response | the occasional 9,000-token answer that blows the context window | hard, with no first-item exception (ADR 0016) |
| **Never pay twice** — per-unit `receipt`s via `--seen`, whole-file `--known`, or a `--session` that costs zero output tokens | re-delivery of evidence the agent already holds | repeat call 1,916 → **621** core tokens, content 756 → **49** ([baseline](docs/eval/baseline.md#reuse-economics)) |
| **Delta after edits** — `changed --patch` | re-reading a file you just changed | hunks, with `patch_tokens` vs `file_tokens` reported |
| **Cheap prompt overhead** — ~100-token pointer in `CLAUDE.md`/`AGENTS.md`, full protocol on demand via skill or `repoctx guide` | a protocol tax on every prompt, including tasks that never touch the tool | pointer capped by test at 150 tokens and < ⅓ of the protocol; MCP session surface 1,698, gated at 1,700 |
| **Cheaper serialization** — `--format md` avoids the JSON escape tax; `--compact` drops legacy duplicate fields | bytes that carry no extra evidence | 217,869 vs 246,297 tokens for identical evidence across 36 tasks — **11.5 %** |
| **One round trip instead of two** — `--detail auto`, `trace`, `--intent`, `--path`, `memory search` | the wasted call that returned the wrong shape | a recalled memory answers for ~40–80 tokens what re-deriving costs an outline plus reads |

Budgets are calibrated to your model: `tokens.profile` scales the stored
`o200k_base` counts (`claude` ≈ 1.2) at query time, rounding up, so a ceiling is
never measured in the wrong tokenizer.

### Why the quality holds

Cheap answers are easy; cheap answers that are still *right* are the work. What
keeps the two together:

- **Spans are symbol-aligned, never truncated.** Up to three non-overlapping
  ranges per file, symbol range first, reconstructed so `start_line..end_line`
  matches the delivered bytes exactly — whole declarations, not a function cut
  in half. A file with no reconstructable source degrades to a pointer instead
  of disappearing.
- **The graph supplies what lexical search misses.** Bounded two-hop expansion
  adds the test that covers a file and the module that imports it; vendor,
  generated, fixture, unrequested-test and unrequested-doc paths are penalized,
  and repeated directories demoted. Fewer tokens — and different ones.
- **Nothing is dropped silently.** Every hit carries `reasons`; `--explain`
  names up to eight *omitted* candidates with the constraint that excluded them,
  and marks a truncated sample `unlisted`.
- **An impossible budget is an error, not a thin answer** — exit `3` with a
  `retry_budget_tokens` guaranteed to fit, and no partial result.
- **Reuse never over-claims possession.** A receipt suppresses exactly one
  delivered unit; `--known` asserts the whole file. Conflating them would tell
  the model it holds lines it never received (ADR 0015).
- **Lossy is opt-in and flagged** — `--strip-comments` keeps any line that mixes
  code with a comment, and marks the item `stripped`.
- **Staleness is visible** — `content_state`/`analysis_state`, `--ensure-fresh`,
  and memories flagged `stale` with the files that drifted.
- **Quality is regression-gated.** 30 source-inspected retrieval tasks across
  four repositories (three external) plus six historical ones, labelled with
  exact line ranges *before* the corpus was first run, pinned by SHA-256; the
  tests reject any change that drops a previously delivered required file or
  labelled line on any output surface.
- **Determinism makes it auditable.** No embeddings, no model in the loop: a bad
  answer is reproducible and fixable rather than a different roll of the dice.

The standard is not rhetorical. An experimental packer that raised required-file
recall to **36/38 (94.7 %)**, past the roadmap's 90 % target, was **rejected**:
it delivered one fragment of more files, so relevant lines fell 204 → 115,
evidence-complete tasks 9/30 → 3/30, and the follow-up reads those tasks still
needed rose from 26 to 32. The patch is kept for reproduction and is not part of
the product.

### What is not claimed

The frozen corpora measure evidence retrieval and response cost — **not** a
coding agent's task success or total session spend. "Reads replaced" credits a
read that would probably have happened; it is an estimate, not a lower bound.
Under a tight 2,000-token JSON ceiling the holdout still delivers only 29/38
required file occurrences (76.3 %; Markdown 32/38; unbudgeted 38/38) — the 90 %
target is not met and is recorded as open. The opt-in read-cost guard has not
been shown to save anything either: its coverage and latency are measured, its
effect on quality and total spend is not, and the three-arm comparison that would
decide it is frozen but not yet executed. Full reasoning, evidence and limits:
**[Cost and quality](docs/cost-and-quality.md)** ·
[methodology and raw artifacts](docs/token-savings.md).

The loop an agent runs, on this repository:

<img alt="Animated terminal demo: repoctx context returning ranked source slices, repoctx outline showing a compact skeleton, and repoctx changed reporting modified and impacted files. The animation predates per-unit receipts; use --seen for partial evidence and reserve --known for files held in full." src="docs/assets/demo.svg" width="880">

## Requirements

- Nothing, if you install from npm: that package ships a self-contained binary.
- [.NET 10 SDK](https://dotnet.microsoft.com/) (LTS) to build; the released
  global tool needs only the .NET 10 runtime.

## Installation

From npm — no .NET runtime required, and the natural route for a
TypeScript/JavaScript repository:

```bash
npm install -g repocontext-tool
repoctx --version
```

The package is called `repocontext-tool` (mirroring the NuGet package
`RepoContext.Tool`); the command it installs is `repoctx`. Pin it per
repository instead, so the whole team gets the same version:

```bash
npm install --save-dev repocontext-tool
npx repoctx --version
```

Prebuilt binaries cover Linux (x64, arm64), macOS (Apple silicon, Intel) and
Windows (x64, arm64); npm downloads only the one that matches your machine.
Alpine and other musl-based distributions are not covered — use the .NET tool
there.

As a .NET global tool (needs the .NET 10 runtime):

```bash
dotnet tool install --global RepoContext.Tool
repoctx --version
```

Or pinned per repository as a local tool (the manifest is committed, so the
whole team gets the same version):

```bash
dotnet new tool-manifest        # once per repo, creates .config/dotnet-tools.json
dotnet tool install RepoContext.Tool
dotnet repoctx --version
```

> **Note:** `RepoContext.Tool` is a .NET *tool* — a standalone `repoctx`
> executable, not a library. Adding it to a project as a `PackageReference`
> (Visual Studio NuGet Package Manager or `dotnet add package`) fails with
> `NU1212`/`NU1213` by design. Install it with `dotnet tool install` as shown
> above — or, if you want a `PackageReference`, use `RepoContext.MSBuild`
> (next section).

### As a plain PackageReference — when `dotnet tool install` is blocked

Many corporate environments allow NuGet packages but block installing dotnet
tools. **`RepoContext.MSBuild`** delivers the same `repoctx` CLI as a regular
package: it arrives with the `dotnet restore` your build already runs — no
tool installation, no extra download, nothing outside the NuGet feed you
already trust. Add it to **exactly one project in the repository** (any target
framework; the machine needs the .NET 10 runtime, which the SDK you build with
includes):

```bash
dotnet add src/YourProject package RepoContext.MSBuild
```

Do not add it to every project through `Directory.Build.props`: auto-setup is
repository-scoped, so one project must own it.

It is a development-only dependency: nothing is compiled into your assemblies,
copied to your output, or passed on to consumers of your package. **The next
build sets everything up automatically** — no further commands:

- first build only: `repoctx init --agents` (writes `repoctx.config.json`,
  git-ignores `.repoctx/`, creates/updates the `CLAUDE.md` and `AGENTS.md`
  agent instructions);
- every build: an incremental `repoctx index`, so the index follows your
  code without anyone remembering to run it;
- a portable copy of the packaged CLI in `.repoctx/bin/tool/`, refreshed only
  when the package version changes (a one-line source stamp keeps unchanged
  builds cheap);
- wrapper scripts `repoctx` / `repoctx.cmd` in `.repoctx/bin/` (git-ignored),
  both pointing at that stable local copy;
- a `.vscode/mcp.json` registering the repoctx [MCP server](#mcp-server) for
  Copilot agent mode and other MCP clients — only when none exists yet; an
  existing `mcp.json` is never touched. It invokes the workspace-relative
  local DLL through `dotnet exec`, so the same committed file works on Windows,
  macOS and Linux.

**Install the package, build once, code with your agent** — no commands:

```bash
dotnet build      # ...and everything is ready:
./.repoctx/bin/repoctx context "change the login logic"
```

The wrapper is a fixed path for humans and agent instructions; MCP launches the
same local payload directly. The repository root is detected as: an existing
`repoctx.config.json` above the project, else the solution directory, else the
directory the build was started from — override with `-p:RepoCtxRoot=...`.

Auto-setup is skipped for IDE design-time builds, runs once per build when the
package is referenced by exactly one project (never per target framework), and
never fails your build — repoctx problems surface as warnings. Because setup
and indexing are repository-wide, separate project builds cannot share an
MSBuild critical section. Opt out per property (in the project file or via
`-p:`):
`RepoCtxAutoSetup=false` (everything), `RepoCtxAutoAgents=false` (init runs
with `--no-agents`: no `CLAUDE.md`/`AGENTS.md`), `RepoCtxAutoIndex=false`
(no per-build index), `RepoCtxAutoShim=false` (no wrapper scripts),
`RepoCtxAutoMcp=false` (no `.vscode/mcp.json`).

For manual control (e.g. with auto-setup off), four MSBuild targets remain:

```bash
# run any repoctx command through MSBuild, in the detected repository root:
dotnet msbuild src/YourProject -t:RepoCtx -p:RepoCtxArgs="index"
# refresh the repository-local portable payload:
dotnet msbuild src/YourProject -t:RepoCtxInstall
# (re)write the wrapper scripts:
dotnet msbuild src/YourProject -t:RepoCtxShim
# write .vscode/mcp.json (if missing):
dotnet msbuild src/YourProject -t:RepoCtxMcpConfig
```

Or download a self-contained binary for `linux-x64`, `linux-arm64`, `win-x64`,
`win-arm64`, `osx-x64` or `osx-arm64` (no .NET runtime required) from the
[latest release][releases], unpack it and put `repoctx` on your `PATH`:

```bash
tar -xzf repoctx-linux-x64.tar.gz     # or unzip repoctx-win-x64.zip
./repoctx --version
```

From source instead:

```bash
dotnet pack src/RepoContext.Cli -c Release
dotnet tool install --global --add-source src/RepoContext.Cli/bin/Release RepoContext.Tool
# or run directly:
dotnet run --project src/RepoContext.Cli -- <command> [options]
```

[releases]: https://github.com/Berthapp/RepoContext/releases

## Quickstart

```bash
cd your-repo
repoctx init                       # create .repoctx/ and repoctx.config.json
repoctx index                      # build the index (incremental afterwards)

repoctx search "authentication"                 # BM25 full-text search
repoctx search "login" --symbols                # search symbols only
repoctx related src/auth/login.ts               # imports, dependents, tests, documents
repoctx trace PAY-142                           # everything about one ticket/link/symbol
repoctx context "change the login logic"        # explained, budgeted bundle
repoctx context "add logout" --top 4 --response-budget-tokens 2000 --detail slices
repoctx context "refund window" --path services/api   # scope to one area
repoctx architecture                            # structure, languages, centrality
```

Re-run `repoctx index` after changes — it diffs by content hash and updates only
what changed. Every command accepts `--format text|json|md`.

### Example

```
$ repoctx context "change the login logic" --top 4

Context for "change the login logic" (3 term(s)):
  1. src/auth/login.ts        0.6744  source  [L13-19]  ~166 tokens
      reasons: fts, symbol:loginUser, path-name-match, tested-by:src/auth/__tests__/login.test.ts
  2. src/auth/permissions.ts  0.4903  source  [L1-1]    ~129 tokens
      reasons: fts, symbol:Action, imported-by:src/auth/login.ts
  3. src/auth/__tests__/login.test.ts 0.2885 test [L1-18] ~171 tokens
      reasons: fts, test-of:src/auth/login.ts
  4. src/auth/session.ts      0.0920  source  [L17-23]  ~112 tokens
      reasons: imported-by:src/auth/login.ts

Budget: 4 file(s) · ~578 estimated tokens
```

### JSON output

Every command supports `--format json` for machine consumption. The contract is
stable and deterministic (snake_case keys, always `schema_version`, same input ⇒
byte-identical output). Because the JSON consumers are AI agents that pay per
token, documents are emitted **compact** — a single line, no indentation, and
null-valued optional fields (such as `heading`) omitted (ADR 0009). Pipe
through `jq` when reading as a human, or use the `text`/`md` formats. For
example (pretty-printed here for readability only):

```
$ repoctx search "login" --top 2 --format json
```

```json
{
  "schema_version": 3,
  "command": "search",
  "query": "login",
  "count": 2,
  "results": [
    {
      "path": "src/components/LoginForm.tsx",
      "kind": "source",
      "score": 2.0843,
      "start_line": 8,
      "end_line": 24,
      "chunk_kind": "symbol",
      "heading": "LoginForm",
      "reasons": ["fts"]
    },
    {
      "path": "src/auth/permissions.ts",
      "kind": "source",
      "score": 1.9299,
      "start_line": 1,
      "end_line": 1,
      "chunk_kind": "symbol",
      "heading": "Action",
      "reasons": ["fts"]
    }
  ]
}
```

`reasons` is machine-readable and explains every hit (e.g. `fts`,
`symbol:loginUser`, `imported-by:<file>`, `test-of:<file>`, `path-name-match`).
In `context` results, at most two full-path graph reasons are listed per file;
further links fold into a `graph:+N` summary (the full edge list is available
via `repoctx related`).

## Commands

| Command | Purpose | Key options |
| --- | --- | --- |
| `init` | Create `.repoctx/` and `repoctx.config.json`; add `.repoctx/` to `.gitignore`. Optionally add usage instructions to `CLAUDE.md` / `AGENTS.md`. | `--force`, `--agents`, `--no-agents` |
| `integrate` | Wire RepoContext into the coding agents this repository uses (instructions, skills, project rules, MCP registration, and optionally the read-cost guard). Never touches `repoctx.config.json`. | `--client`, `--check`, `--remove`, `--inline-playbook`, `--list`, `--guard`, `--guard-mode` |
| `guide` | Print the full usage protocol for an agent that needs it. Constant output, safe behind a cache breakpoint. | — |
| `index` | Build or incrementally update the index (stores real BPE token counts per file). | `--full` |
| `search <query>` | BM25 full-text search (content and symbols). | `--top`, `--symbols`, `--path`, `--format` |
| `related <file>` | Imports, dependents, linked tests, and the documents that describe a file. | `--format` |
| `trace <ref>` | Every file that declares or mentions one exact term: a work-item key (`ABC-123`), a link, a symbol name or a path. | `--top`, `--path`, `--format` |
| `context <task>` | Ranked, explained context bundle packed into a token budget. | `--top`, `--budget-tokens`, `--response-budget-tokens`, `--projected-read-budget-tokens`, `--detail auto\|paths\|outline\|slices`, `--seen <receipt>`, `--known <path>@<hash>`, `--session <name>`, `--ensure-fresh`, `--strip-comments`, `--no-memory`, `--path`, `--format`, `--compact`, `--intent fix\|explain\|review`, `--explain` |
| `outline <file>` | A file's skeleton: symbols, signatures, doc summaries, exact full-read token cost. | `--format` |
| `changed` | Working-tree diff against the index, with impacted dependents. | `--patch`, `--format` |
| `prime` | Cache-stable repository primer for a cacheable prompt prefix (byte-identical for unchanged indexed content and token calibration). | `--files`, `--format` |
| `memory add <text>` | Store one agent-authored insight: a `note`, `decision` or `constraint`, optionally linked to files (hash-recorded) and scoped to a session. | `--kind`, `--file`, `--tag`, `--session`, `--format` |
| `memory search [query]` | Deterministic recall with reasons and hash-based `stale` flags; omit the query to list. | `--top`, `--kind`, `--file`, `--session`, `--stale`, `--format` |
| `memory rm <id>` | Remove one memory entry (curation). | `--format` |
| `memory adopt` | Review memory lines this machine did not sign (recorded before 0.15.1, or from a checkout) and sign them. Interactive terminal only. | — |
| `architecture` | Structure (LOC tree), language distribution, centrality, entrypoints. | `--depth`, `--format` |
| `stats` | Token-savings dashboard aggregated from your local usage; opens in your browser when run in a terminal (see below). | `--format` (incl. `html`), `--open`, `--no-open` |
| `guard` | The opt-in read-cost guard: `hook` answers one client hook event, `check` explains one read, `status` prints the local counters (see below). | `--mode`, `--max-read-tokens`, `--client`, `--timeout-ms` |
| `mcp` | Run the MCP server over stdio for AI agents (see below). | — |

Exit codes: `0` success · `1` error · `2` no index · `3` invalid arguments · `4` drift (`integrate --check` only).

### The token-frugal loop

All token figures are real BPE counts (`o200k_base`, computed offline at index
time), so budgets can be trusted. The intended agent workflow:

1. Optionally run `prime` for a new, unfamiliar repository when the agent can
   keep a byte-stable primer (languages, layout, entrypoints, key files) behind
   a cache breakpoint. Skip this call for focused/familiar work or clients that
   cannot retain a cached prefix. The primer is byte-identical for unchanged
   indexed content and token calibration.
2. `context "<task>" --detail auto --response-budget-tokens 2000 --format md`
   — working context packed into a **hard** response ceiling. Markdown avoids
   JSON's escaping cost for embedded code; use JSON when a client needs to
   parse the envelope. `--strip-comments` can remove comment banners from
   slices (lossy; line ranges become approximate).

   `--detail auto` reads the shape of the task: change verbs (`fix`,
   `implement`, `refactor`, `debug`, …) return symbol-aligned source `spans`,
   survey terms (`where`, `which`, `architecture`, …) return outlines, which
   cover more files for fewer tokens. A task that is both gets spans — the
   survey half is answerable from source, the change half is not answerable
   from an outline. The rule table is deterministic and versioned (ADR 0018);
   name `slices`, `outline` or `paths` explicitly whenever you know better.
   The response reports the level it ran with, so nothing about the contract
   depends on the guess. This exists to remove one round trip: an agent that
   picks wrong pays for a useless response and then asks again.
3. Escalate only on a concrete gap: `search --symbols` when a file is missing,
   `outline <file>` when the symbol you need was not delivered, `related` for
   dependency/impact questions, and `architecture --depth 1` for unfamiliar
   boundaries.
4. After editing, use `changed --patch` for just the changed hunks. Run
   `repoctx index` and re-query when it reports `stale`.
5. Never pay twice, and never over-claim what is known: use receipts, whole-file
   assertions, or a named session as described below.
6. When `context` leaves a concrete knowledge gap, try `memory search` before
   re-deriving it (`context` already recalls matching memories automatically);
   `memory add` records a distilled finding after the work is done. See
   [Agent memory](#agent-memory-never-re-derive) below.

#### Budgets

| Option | Bounds | Hard? |
| --- | --- | --- |
| `--budget-tokens` | *charged work*: projected reads for `paths`, embedded content otherwise | compatibility cap (v2 basis) |
| `--response-budget-tokens` | exact model-visible response (CLI stdout includes its newline; MCP counts its text block) | **yes** |
| `--projected-read-budget-tokens` | full-file reads implied by delivered pointers | **yes** |

Only `--response-budget-tokens` promises a ceiling on what reaches the model.
Every supplied budget must pass; none overrides another. There is no first-item
exception — if a budget cannot fit the smallest useful response, the command
exits `3` with a deterministic `retry_budget_tokens=<n>` that is guaranteed to
fit, and emits no partial result. The retry value is intentionally conservative
rather than an exhaustively searched mathematical minimum, keeping malformed
tiny-budget requests cheap. See ADR 0016.

JSON callers can opt into [compact context JSON](docs/decisions/0021-compact-context-json.md)
to remove legacy duplicate fields within the same response budget:

```bash
repoctx context "add logout" --detail slices --response-budget-tokens 2000 --format json --compact
```

#### Reuse: receipts vs. full-file possession

These are **not** interchangeable, and conflating them was a real bug (ADR 0015):

- **`--seen <receipt>`** (repeatable) — each delivered pointer, span and outline
  symbol carries a `receipt`. Echoing one suppresses *exactly* that unit; every
  other part of the same file still arrives. Reused units are acknowledged in
  `reused` and never consume a `--top` slot, so echoing receipts buys new context
  rather than markers.
- **`--known <path>@<hash>`** — asserts you hold the **entire** file. Use it only
  when you actually read the whole file. The `hash` printed next to a *partial*
  result identifies the file version, not what you were sent; deriving `--known`
  from a slice or outline claims possession of lines you never received.
- **`--session <name>`** — persists reuse bookkeeping locally so clients need
  not echo receipts and hashes on every call. It preserves the same distinction:
  partial evidence must never be promoted to a whole-file possession claim.

Every `context` response carries `content_state` (which file contents are
indexed), `analysis_state` (that content plus config and producer versions),
`evidence_id` and `representation_id`.

Using a non-OpenAI model? Set `tokens.profile` (e.g. `"claude"`) in
`repoctx.config.json` so budgets and reported counts match your tokenizer —
the index keeps raw counts, so no re-index is needed when you switch.

### Diagnose selection and request task companions

Use `--explain` to see which generated candidates lost a ranking slot or failed
a budget. The response includes up to eight omitted files with their rank,
score, limiting constraint and a structured `outline` lookup. `unlisted` makes
sample truncation explicit. Diagnostics count toward the response budget; the
sample list can shrink to zero. Scoped queries only diagnose their scoped pool.

```bash
repoctx context "change login" --explain --format json --compact --response-budget-tokens 2000
```

When you know the implementation target, `--intent` prioritizes directly linked
companions: tests and dependencies for `fix`, dependencies and documents for
`explain`, or dependents and tests for `review`.

```bash
repoctx context "src/auth/login.ts" --intent fix --detail slices --format json --compact --response-budget-tokens 2000
repoctx context "loginUser" --intent review --detail slices --format md --response-budget-tokens 2000
```

Promotion requires one explicitly named source path or an unambiguous exact
symbol query. General questions retain normal ordering. The `intent` field
reports the requested purpose; `intent:<purpose>:<role>` item reasons identify
actual promotions. Existing source-span packing, reuse and hard budgets still
apply. A linked test is a suggested companion, not proof of test coverage.
`--intent explain` selects a purpose; `--explain` requests diagnostics. They can
be combined. MCP exposes the same options as `intent` and `explain` on
`repoctx_get_context`. See the [contract and evaluation](docs/decisions/0022-context-selection-intent.md).

### The token-savings dashboard

`repoctx stats` estimates the net token impact of that loop from your recorded
successful usage (CLI and MCP alike):

```text
Token savings (per-call calibrated counts, 2026-07-01 to 2026-07-14):

  calls                      42
  response tokens        31,208
  reads replaced        104,566
  net saved              73,358  (70 % of replaced reads)
```

Every successful query response records two token figures to a local log
(`.repoctx/stats.jsonl`): exact response cost and an estimate of full-file reads
made unnecessary, using the token calibration active for that call. A historical
log can therefore mix profiles if the configuration changes. Embedded spans and
non-empty outlines are credited at the
file's full-read cost. Only an explicit matching full-file `--known` assertion
can credit a reused read; span, symbol and pointer receipts receive no
speculative full-file credit because they do not prove a read was avoided.
**Net saved** is replaced reads minus response cost, summed over every recorded
call. It is an estimate, not a guaranteed lower bound:
discovery calls (`search`, `related`, `changed`, `architecture`, and
`context --detail paths`) receive no credit, while credited content assumes a
full read would otherwise have happened.
Breakdowns per command and per day (`--format md`/`json` for reports and
tooling) show where the savings come from.

**`repoctx stats` shows you the visual dashboard.** Typed at a terminal, it
prints the summary above *and* writes a self-contained HTML page (charts, no
external resources, works fully offline) to `.repoctx/stats.html`, then opens
it in your default browser. Everything that says a machine is reading instead
keeps it on stdout: a redirected or piped stream, an explicit `--format`, or
`--no-open` — so `repoctx stats > report.txt` and CI runs behave exactly as
before, and nothing opens before the first query is recorded. `--open` forces
the browser anyway (`REPOCTX_NO_LAUNCH=1` suppresses just the launch), and
`--format html` prints the page to stdout. There is deliberately no localhost
server: the browser renders the local file, and RepoContext stays
network-free.

The page leads with three figures — reads replaced, response cost, and the
difference — and then draws them **cumulatively over your calls, in the order
they were recorded**: one curve for what reading those files in full would have
cost, one for what repoctx actually returned, and the band between them as the
running saving. That shape is the useful part: it shows *where* the savings
came from, which is usually a handful of many-file `context` calls rather than
a steady drip. Hover (or focus the plot and use the arrow keys) for a single
call; grouped bars per command and per day and a full table follow below, so no
figure is hover- or colour-gated. Logs longer than 120 calls are bucketed into
120 points — the curve is thinned, never the sum. Set `REPOCTX_NO_STATS=1` to
disable recording; delete the log file to reset the dashboard. See ADR 0011.

### Agent memory: never re-derive

The index remembers what the code *is*; agent memory remembers what agents
*learned* about it — and both stay local, deterministic and explainable.
RepoContext never writes a memory itself (no LLM, no generated prose): an
agent deposits distilled insights, and the tool stores, recalls, explains and
stale-flags them.

```bash
repoctx memory add "JWT chosen over cookie sessions: mobile clients cannot hold cookies." \
  --kind decision --file src/auth/login.ts --tag auth
repoctx memory search "auth" --format json     # deterministic recall, with reasons
repoctx context "change the login logic"       # matching memories ride along in the bundle
```

Three shapes, one store (`.repoctx/memory.jsonl`, git-ignored like the rest
of `.repoctx/`):

- **`note`** — long-term knowledge ("PaymentService retries 3×, see `retry()`").
- **`decision`** — reasoning memory: a recorded *why*, so the next agent does
  not re-litigate it.
- **`constraint`** — an invariant or warning ("`Action` is public API — do not
  rename").
- `--session <name>` scopes an entry short-term: a scratchpad note visible
  only to calls carrying that session (it survives an agent's context-window
  compaction), while the session tracks delivered evidence receipts and only
  explicit whole-file possession assertions.

Every entry is content-addressed (re-adding updates instead of duplicating),
capped at 2,000 characters, and linked files record their content hash — when
a linked file changes, recall flags the entry `stale` with the drifted paths,
so outdated knowledge is visible instead of silently trusted. `context` folds
at most 3 matching memories into a reserve of at most a fifth of the token
budget (opt out with `--no-memory`); a repo without memories produces
byte-identical output to previous versions. The economics: a re-derived
"how does X work here" costs an outline (~1,100 tokens on this repo) to
several file reads — a recalled memory answers it for ~40-80.

## Working with artifacts: tickets, specs, requirements

Most repositories that agents work in are not only code. A team that pulls Jira
tickets and Confluence pages into the repository — through MCP or an export job
— and keeps requirements, acceptance criteria and API contracts next to the
implementation ends up asking questions that span all of it: *what belongs to
PAY-142*, *does the code still match the specification*, *which test proves this
requirement*.

RepoContext indexes those files as first-class artifacts.

**They get an outline.** A 900-line YAML requirements file, a Confluence export,
a `.feature` file, an XML contract, a traceability `.csv` — `repoctx outline`
returns their structure (headings, keys, scenarios, columns) with line ranges
and the exact cost of reading the whole thing, so an agent can decide what to
open instead of reading it to find out. The same holds for code in any language
listed above, grammar or not.

**They get linked to the code.** Every file's references are extracted at index
time: repository paths it names, work-item keys (`ABC-123`), absolute links, and
code symbols it mentions. Paths and unambiguous symbol names become `reference`
edges, so `related` answers "which specification describes this file" and
`changed` reports the documents a code change may have invalidated.

**One term resolves exactly.** `repoctx trace` is the lookup that replaces a
repository-wide grep:

```
$ repoctx trace PAY-142

Trace "PAY-142": 0 definition(s), 6 file(s) mentioning it
  mentioned in:
    - artifacts/confluence/refund-policy.md (doc)  L8  ~118 tokens if read  [key]
    - artifacts/jira/PAY-142.json (config)  L2  ~135 tokens if read  [key]
    - artifacts/jira/PAY-155.json (config)  L6  ~47 tokens if read  [key]
    - requirements/REQ-payments.yaml (config)  L4  ~80 tokens if read  [key]
    - src/billing/refund.ts (source)  L5  ~117 tokens if read  [key]
    - tests/billing/refund.test.ts (test)  L3  ~55 tokens if read  [key]
Reading every file above would cost ~552 tokens.
```

`trace` also resolves a symbol name (returning its declaration *and* the
documents naming it), an absolute link (a Confluence page shared by a ticket and
a spec), and a repository path — including documents that name it only by its
file name. A key that matches nothing returns the keys sharing its prefix.

It is deliberately **exact**, not ranked: `ABC-123` means that work item, not
"documents about ABC". `search` and `context` remain the fuzzy surfaces —
and `context` picks the key up too, so `repoctx context "write tests for
PAY-142"` returns ticket, specification, requirement, implementation and test
in one budgeted answer, each carrying `ref:PAY-142` as its reason.

Work-item detection covers the `ABC-123` shape (uppercase, so `UTF-8` and
`SHA-256` are not mistaken for tickets). Add your own shapes with
`artifacts.keyPatterns` in `repoctx.config.json`.

### If the artifacts are not committed

Fetched tickets and exported pages often live in a git-ignored directory, and
RepoContext honours `.gitignore` by default — so they would not be indexed at
all. Re-include them for RepoContext only, with a `.repoctxignore` next to the
`.gitignore` that excludes them:

```
!artifacts/
```

`.repoctxignore` is evaluated after `.gitignore` at the same level, so this
brings the directory back into the index without touching what git does. The
index stays git-ignored either way, and `sensitiveFiles` still cannot be
re-included.

### Scoping a query to one area

In a large repository each agent usually owns one area. `--path` narrows
`search`, `context` and `trace` before ranking, so the rest of the repository
costs neither relevance nor tokens:

```bash
repoctx context "refund window" --path services/api --path libs/billing
repoctx trace PAY-142 --path src
repoctx search "retry" --path "packages/**/src"
```

Patterns use `GLOB` semantics — `*` matches any characters including `/`, `?`
matches one — and a plain path selects it and everything below it. The scope is
enforced in the query itself and in graph expansion, so a neighbouring file
outside the scope is never returned.

### What this costs

The index gets bigger: references are rows on your disk. Queries do not — the
reference index is what lets `trace` answer in one indexed lookup, and structure
symbols are what let an agent skip a document instead of reading it. Turn the
edge sources off with `artifacts.linkPaths` / `artifacts.linkSymbols` if you
want the smaller index.

## Agent integration

RepoContext is agent-agnostic — any agent with shell access can use it. Let
`integrate` wire it into whichever agents this repository already uses:

```bash
repoctx integrate            # detect the environment and write the managed files
repoctx integrate --list     # what each client would get
repoctx integrate --check    # CI-friendly drift check; writes nothing, exits 4 on drift
repoctx integrate --remove   # take the managed blocks back out
```

| Client | Detected by | Files it maintains |
| --- | --- | --- |
| `claude-code` | `.claude/`, `CLAUDE.md` | `CLAUDE.md` pointer, `.claude/skills/repocontext/SKILL.md`, `.mcp.json` |
| `cursor` | `.cursor/`, `.cursorrules` | `.cursor/rules/repocontext.mdc` (`alwaysApply: false`), `.cursor/mcp.json` |
| `copilot` | `.github/copilot-instructions.md`, `.vscode/` | `.github/copilot-instructions.md`, `.vscode/mcp.json` |
| `windsurf` | `.windsurf/` | `.windsurf/rules/repocontext.md` |
| `agents` | `AGENTS.md` (and the fallback when nothing is detected) | `AGENTS.md` |

Pick explicitly with `--client claude-code --client cursor` if detection is not
what you want.

### The read-cost guard (opt-in, experimental)

Everything above makes cheap evidence *available*. It cannot make an agent ask
for it: a client's own file-read tool is one call away, and a 900-line file is
paid for before RepoContext hears about the task. The guard closes that gap for
Claude Code, and it is off until you install it:

```bash
repoctx integrate --client claude-code --guard                     # observe: count only
repoctx integrate --client claude-code --guard-mode enforce        # deny an expensive read once
repoctx integrate --client claude-code --remove                    # take it back out
repoctx guard status                                               # what it has done locally
repoctx guard check src/big.ts                                     # explain one read, write nothing
```

In **observe** mode nothing is blocked; the guard only records how often it
*would* act, which is how you find out whether enforcing is worth it here. In
**enforce** mode a read whose estimated cost exceeds `--max-read-tokens`
(default 2,000, in your configured token profile) is denied once, with the
cheaper call named:

```
reading src/big.ts costs about 10,800 estimated tokens. Exact evidence for the
same file is cheaper. Repeat this read to get the whole file anyway.
Cheaper exact evidence for the same file:
  repoctx outline src/big.ts
  repoctx context '<the task in your own words>' --path src/big.ts --detail slices
```

The rules it keeps, all covered by tests:

- **It is a cost policy, not a permission boundary.** It can only ever deny. It
  never emits an allow decision, so it cannot widen what an agent may read, and
  it never overrides your permissions, approval rules or exclusions.
- **Enforcement is bounded.** Ask for the same file again and the read goes
  through. There is no loop and no way to be stuck.
- **It never blocks the alternative.** A `repoctx` call is never treated as a
  file read.
- **It judges only what it can see.** A file the index does not carry — excluded,
  sensitive, too large, or changed on disk since indexing — is never judged,
  never named and never suggested.
- **It never guesses at your shell.** Only plain readers (`cat`, `head`, `tail`,
  `nl`, `bat`, `less`, `more`, `type`, `sed -n '12,40p'`) are modelled.
  Pipelines, redirections, substitutions, globs and command lists are handled
  normally by the client. No command is ever executed to find out what it reads.
- **It fails open.** A malformed payload, a missing or stale index or any
  internal error allows the read; the hook always exits 0.
- **Your settings stay yours.** Only RepoContext's own entries in
  `.claude/settings.json` are added, updated or removed; a file that cannot be
  parsed is reported and left untouched. Installation and removal are idempotent.

It also binds evidence reuse to the real context lifetime: the guard announces a
session name at the start of a conversation, and a compaction, resume, clear or
fork starts a new one, so a local session file can never keep claiming possession
for a context that no longer holds the evidence.

Other clients keep their existing behaviour — there is no verified contract for
intercepting their native reads, and an MCP registration or `AGENTS.md` is not
one. `integrate --guard` says so rather than silently skipping them.

**Status:** enforce mode is experimental. Its coverage on one repository and its
latency are measured in
[`docs/eval/agent/local-measurements.md`](docs/eval/agent/local-measurements.md)
— including the fact that the 100 ms latency target is *not* met on the machine
measured. Whether it lowers total cost at equal quality is the open question the
frozen comparison in [`docs/eval/agent/`](docs/eval/agent/) exists to answer, and
that comparison has not been run. `repoctx stats` reports guard activity in its
own section, outside the savings arithmetic, because a denied read is not a
realized saving. See [ADR 0023](docs/decisions/0023-read-cost-guard-and-context-epochs.md).

Two properties matter. **It never touches `repoctx.config.json`** — unlike
`init --agents`, which needs `--force` on an initialized repository and would
overwrite your configuration along the way. And **it never overwrites what it
does not own**: instruction files are maintained through
`<!-- BEGIN/END RepoContext -->` markers with everything outside them preserved
byte-for-byte, and an MCP configuration file that already exists is reported and
left alone, because it may register other servers.

### Why the instructions are short

`CLAUDE.md`, `AGENTS.md` and `.github/copilot-instructions.md` are loaded into
**every** prompt of every session — including the ones RepoContext cannot help
with. So the managed block in them is a pointer of about 100 tokens: what the
tool is, the one call to start with, and where the rest lives. A test caps it at
150 tokens and asserts it stays under a third of the full protocol, so this is a
gate rather than a promise.

The full protocol — escalation rules, evidence reuse, budgets, calibration —
arrives only when a task actually needs it:

- **Claude Code, Cursor, Windsurf** get it as a skill or project rule, loaded on
  demand when its description matches the task.
- **Every other agent** runs `repoctx guide`, which prints the same text. That
  costs the protocol once per task instead of once per prompt.

Prefer the old behaviour? `repoctx integrate --inline-playbook` puts the full
protocol in the always-loaded files.

`repoctx init --agents` still writes `CLAUDE.md` and `AGENTS.md` directly, with
the same pointer block, so the two commands never fight over the region.

### Ambient evidence reuse

`--session <name>` is the cheapest reuse the tool offers: the agent echoes
nothing, so it costs no output tokens at all. Its weakness is that it has to be
remembered on every call. Set `REPOCTX_SESSION` and both the CLI and the MCP
server use it when no session is passed; an explicit `--session` still wins.

```bash
REPOCTX_SESSION=my-agent repoctx context "change the login logic" --detail auto
```

Choose a name that identifies **one** agent instance. A session records what was
delivered, so two agents sharing a name in one repository would let the second
receive reuse markers for evidence it never saw. That is why the generated MCP
configuration deliberately does not set the variable for you — only whoever
launches the agent knows how many are running.

A name you choose stays yours and behaves exactly as before. With the read-cost
guard installed, Claude Code is additionally *told* a name at the start of each
conversation, one that belongs to that context: after a compaction, a resume or a
fork the name changes, and the retired one delivers evidence again rather than
suppressing it. Keeping a receipt file is not proof that a model still remembers
anything, so anything that might have dropped evidence costs one repeated read
instead of producing a false possession claim.

## MCP server

Agents that speak the [Model Context Protocol](https://modelcontextprotocol.io)
can call RepoContext directly instead of shelling out. `repoctx mcp` runs an MCP
server over stdio and exposes eight non-destructive tools:

| Tool | Wraps | Arguments |
| --- | --- | --- |
| `repoctx_search` | `search` | `query`, `top`, `symbols`, `path` |
| `repoctx_get_context` | `context` | `task`, `top`, `budgetTokens`, `responseBudgetTokens`, `projectedReadBudgetTokens`, `detail`, `known`, `seen`, `session`, `stripComments`, `includeMemory`, `path`, `ensureFresh`, `compact`, `intent`, `explain` |
| `repoctx_trace` | `trace` | `reference`, `top`, `path` |
| `repoctx_get_related_files` | `related` | `file` |
| `repoctx_get_outline` | `outline` | `file` |
| `repoctx_get_changes` | `changed` | `patch` |
| `repoctx_memory_add` | `memory add` | `text`, `kind`, `files`, `tags`, `session` |
| `repoctx_memory_search` | `memory search` | `query`, `top`, `kind`, `file`, `session`, `stale` |

(`memory rm` is deliberately CLI-only: deleting team knowledge is curation and
stays under human supervision.)

Each tool returns the same JSON as the corresponding `--format json` command
(carrying `schema_version` and per-result `reasons`). The server runs the index
from the working directory, communicates over stdin/stdout only (no network),
and never mutates the index. Successful calls may append token counts to the
local usage ledger described above, so the tools are not advertised as strictly
read-only/idempotent at the MCP protocol level.

Register it with an MCP-capable client, for example:

```json
{
  "mcpServers": {
    "repoctx": {
      "command": "repoctx",
      "args": ["mcp"],
      "cwd": "/path/to/your/repo"
    }
  }
}
```

**On Windows, that `command` only works for the .NET tool.** An npm install puts
no executable on the PATH — only the shims `repoctx`, `repoctx.cmd` and
`repoctx.ps1` — and MCP clients spawn the server without a shell, which Windows
cannot do for any of the three. When the repository pins `repocontext-tool` as
an npm dependency, `repoctx integrate` therefore writes a `node`-based
registration, identical on every platform and free of shims:

```json
{
  "mcpServers": {
    "repoctx": {
      "command": "node",
      "args": ["node_modules/repocontext-tool/bin/repoctx.js", "mcp"]
    }
  }
}
```

With a *global* npm install there is no repository-relative launcher to point
at; on Windows, register it by hand as `"command": "cmd"` with
`"args": ["/c", "repoctx", "mcp"]`, or install the .NET tool instead.

With Claude Code, run this inside the repository:

```bash
claude mcp add repoctx -- repoctx mcp
```

With GitHub Copilot agent mode in VS Code, commit a `.vscode/mcp.json`
(installed via `RepoContext.MSBuild`? The first build wrote this file for you
already, using the portable local payload):

```json
{
  "servers": {
    "repoctx": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["exec", "${workspaceFolder}/.repoctx/bin/tool/repoctx.dll", "mcp"],
      "cwd": "${workspaceFolder}"
    }
  }
}
```

Build the index first (`repoctx init && repoctx index`); tools return an error
until an index exists.

`RepoCtxMcpConfig` never overwrites an existing user-owned file. If version
0.6.1 generated the old extensionless-shim command, delete that unchanged
generated file and rerun `RepoCtxMcpConfig`, or replace it with the
`dotnet exec` configuration above.

## Configuration

`repoctx init` writes `repoctx.config.json` (camelCase keys):

```json
{
  "include": [],
  "exclude": [".git", "node_modules", "dist", "build", "out", "bin", "obj", "target", "..."],
  "respectGitignore": true,
  "sensitiveFiles": [".env*", "*.secret.*", "appsettings.Production.json"],
  "indexing": { "maxFileSizeKb": 512, "includeTests": true, "includeDocs": true },
  "artifacts": {
    "keyPatterns": [],
    "linkPaths": true,
    "linkSymbols": true,
    "maxRefsPerFile": 400
  },
  "ranking": {
    "weights": { "fts": 0.4, "symbol": 0.3, "graph": 0.2, "path": 0.1 },
    "synonyms": { "checkout": ["payment", "billing"] }
  }
}
```

| Key | Meaning |
| --- | --- |
| `include` | Directories to scan, each recursively. **Empty (the default) scans the whole repository**, so every project and subfolder below the root is indexed. Set it only to deliberately narrow the scope. Entries must be relative and stay inside the repository: absolute paths and `..` are rejected, and a root that is a link leaving the repository is skipped. |
| `exclude` | Directory/file globs to skip (gitignore syntax). A bare name like `dist` matches at any depth, so it also covers nested projects. |
| `respectGitignore` | Also honor `.gitignore` — the root one and any in subdirectories. |
| `sensitiveFiles` | Never indexed — neither content nor path. Private keys and credential stores (`*.pem`, `*.key`, `*.p12`, `*.pfx`, `*.jks`, `*.keystore`, `id_rsa`/`id_ed25519`/…, `.npmrc`, `.pypirc`, `.netrc`, `.git-credentials`, `.pgpass`, `*.tfstate`) are excluded in addition, and cannot be re-included. |
| `indexing.maxFileSizeKb` | Skip files larger than this. |
| `indexing.includeTests` / `includeDocs` | Include test / documentation files. |
| `artifacts.keyPatterns` | Extra regular expressions for work-item keys, in addition to the built-in `ABC-123` shape. Invalid patterns are ignored. |
| `artifacts.linkPaths` | Link a document to the files whose path it names. |
| `artifacts.linkSymbols` | Link a document to the file that uniquely defines a symbol it names. |
| `artifacts.maxRefsPerFile` | Upper bound on stored *artifact* references per file and kind — the paths, keys, links and symbols a file names (default 400). The references the dependency graph is resolved from (module imports and C# type uses) are outside it: truncating those would drop real dependencies from the graph. |
| `ranking.weights` | Signal weights used by `context` (fts, symbol, graph, path). |
| `ranking.synonyms` | Query-term expansions used by `context` — map the vocabulary your team queries with onto the terms the code uses, including terms in another language than the code. |
| `tokens.profile` | Calibrate reported counts/budgets to a tokenizer: `o200k`/`openai` (default) or `claude`. |
| `tokens.factor` | Explicit calibration multiplier in `(0, 100]`; overrides `tokens.profile` (invalid values fall back to raw counts). |
| `pricing.inputPerMtok` | Input price per million tokens; enables the money view in `stats`. |
| `pricing.currency` | Currency label for the `stats` money view (default `USD`). |

You can also add a `.repoctxignore` file (gitignore syntax) for extra
exclusions. Both `.gitignore` and `.repoctxignore` are read per directory: a
file in a subdirectory applies to that subtree and overrides rules inherited
from above, so each project in a multi-project checkout keeps its own rules.

### Repositories with several projects

`repoctx init` at the top of a folder that holds several projects covers all of
them — there is no per-project setup. `init` reports what it found:

```
$ repoctx init
Initialized RepoContext in /work/checkouts
  wrote repoctx.config.json
  scope: the whole repository, 412 file(s) selected
  projects: 2 detected
    apps/web (node, package.json)
    services/api (dotnet, SampleApi.csproj)
```

Project detection is informational only — directories that belong to no project
(loose scripts, docs, `tools/`) are indexed just the same.

> **Upgrading from ≤ 0.7:** configurations written by an older `init` pin
> `include` to `["src", "app", "lib", "docs"]`, which is anchored at the
> repository root and therefore misses nested projects. `repoctx index` now
> warns when those roots do not exist. Delete the `include` key (or set it to
> `[]`) and re-run `repoctx index`.

The index (`.repoctx/`) contains code excerpts — and, with M9,
agent-authored memory notes about the code — so it is a sensitive artifact;
it is git-ignored automatically.

## Privacy

RepoContext never sends repository data anywhere and contains no telemetry. The
`stats` dashboard is fed by a strictly local usage log (`.repoctx/stats.jsonl`,
git-ignored, token counts and command names only — never content); nothing is
transmitted, and `REPOCTX_NO_STATS=1` disables it. RepoContext cannot stop a
downstream agent from forwarding the excerpts it returns to an LLM provider —
for maximum privacy use a local/self-hosted agent and model, and list sensitive
files in `sensitiveFiles` / `.repoctxignore`.

## Working in an untrusted repository

Agents are often pointed at code nobody on the team wrote, and everything in
that checkout — including `repoctx.config.json`, ignore files and symbolic
links, which git checks out verbatim — is chosen by its author. RepoContext
treats it accordingly ([ADR 0024](docs/decisions/0024-untrusted-checkouts.md)):

- **Nothing outside the repository is indexed.** `include` cannot name an
  absolute path or `..`, and links — as an include root or anywhere below it —
  are never followed.
- **Nothing outside the repository is written.** `.repoctx/` and everything in
  it must be real directories and files; instruction files, rules, MCP
  registrations and `.claude/settings.json` are written only when they resolve
  inside the repository (`CLAUDE.md -> AGENTS.md` keeps working). A refused
  write is one line on stderr and exit code `1`.
- **Credentials stay out of the index** even when the repository's own
  configuration clears `sensitiveFiles` (see [Configuration](#configuration)).
- **Hostile patterns cannot stall indexing.** Ignore globs and configured key
  patterns match in linear time.
- **Terminal output is text.** At an interactive terminal, control characters
  in repository content are shown as visible symbols (`␛`) instead of being
  executed by the terminal; piped output is byte-identical.
- **Only what this machine wrote is trusted** ([ADR 0025](docs/decisions/0025-machine-bound-provenance.md)).
  A committed `.repoctx/` could otherwise carry an index whose "source" exists
  in no file, or memories presented to every agent as your team's knowledge.
  RepoContext signs its index, memory lines and session files with a per-user
  key (`RepoContext/machine.key` in your local data directory, mode `0600`, or
  `REPOCTX_KEY_FILE`); anything without a valid signature is discarded or
  ignored. Memories recorded before 0.15.1 become live again once you review
  them with `repoctx memory adopt`, which runs only at an interactive terminal
  so an agent cannot vouch for them. An index is a cache and cannot be copied
  between machines — the next `repoctx index` rebuilds it.

What RepoContext does not do is judge repository *text*: a prompt injection in
a README reaches the agent as evidence, exactly as a direct file read would.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `No index found. Run 'repoctx index' first.` (exit code 2) | Run `repoctx init` then `repoctx index` in the repository root. |
| `index` warns that text files exceed `indexing.maxFileSizeKb` | Those files are not indexed at all. Raise the limit in `repoctx.config.json` (large exported specifications routinely pass 512 KB), or list them in `.repoctxignore` to accept the gap deliberately. |
| `index` or `changed` lists files as `unreadable` | The process could not open them — a lock, a permission, or a mount that went away. They keep their existing index rows rather than being treated as deleted, so a later run picks them up unchanged. |
| `changed`/`index` reports far more files than expected, and warns about an ignore file | A `.gitignore` or `.repoctxignore` could not be read, so the directories its rules exclude were walked and indexed. The warning names the file; fix its permissions and re-run `repoctx index`. |
| `File not found in index: ...` from `related` | The file is not indexed — check `include`/`exclude`, `.repoctxignore`, `sensitiveFiles` and `indexing.maxFileSizeKb`, then re-run `repoctx index`. |
| `index` reports `files: 0`, or a project below the root is missing | An older config pins `include` to root-level directories. Remove the key (or set it to `[]`) to scan the whole repository and re-run `repoctx index`. |
| `outline` is empty for a file | Its format has no structure RepoContext knows (a `.txt`, a log, an unusual config). The file is still indexed and searchable; only the skeleton is missing. Binary files are not indexed at all — `index` reports how many. |
| Results look stale | Re-run `repoctx index`; unchanged files are neither reparsed nor re-read — only the hash pass touches them. |
| `trace` finds nothing for a ticket key | Keys are matched uppercase in the `ABC-123` shape. Add your project's shape to `artifacts.keyPatterns` and re-run `repoctx index`. `trace` lists the keys sharing your prefix when it finds none. |
| Fetched tickets/pages are missing from the index | They are probably git-ignored, and `respectGitignore` is on. Add a `.repoctxignore` with `!<dir>/` to re-include the directory for RepoContext only, then re-run `repoctx index`. |
| A document is not linked to the code it describes | Linking needs the document to name the repository path or a symbol declared in exactly one file. Check `artifacts.linkPaths` / `artifacts.linkSymbols` and re-index. |
| `Refusing to use '…/.repoctx/…': it is a symbolic link` or `Refusing to write '…': a symbolic link resolves it outside the repository` | Something in the checkout is a link where RepoContext writes. `.repoctx/` must be a real directory, and managed files must resolve inside the repository. Replace the link (for a relocated index, use a real directory) and retry. |
| `The index was not built by RepoContext on this machine … Run 'repoctx index'` | The index came with the checkout, predates 0.15.1, or your machine key changed. It was discarded; `repoctx index` rebuilds it from the repository. |
| `N memory line(s) are not signed by this machine and were ignored` | Memories recorded before 0.15.1, or planted by a checkout. Run `repoctx memory adopt` at a terminal, read the list, and answer `y` only if you wrote them. |
| `Warning: no machine key could be read or created at …` | RepoContext cannot tell its own index and memories from a checkout's. Set `REPOCTX_KEY_FILE` to a writable path outside the repository; a file of the wrong size at that path is never overwritten. |
| `include entries must be relative paths inside the repository` | An `include` entry is absolute or uses `..`. Initialize RepoContext at the directory that contains everything you want indexed instead. |
| Exit code 3 | Invalid arguments — check option spelling and values (e.g. `--top` must be > 0, `--format` must be `text`, `json` or `md`). |

## Development

See `CLAUDE.md` for build/test commands, repository structure and conventions,
`docs/build-prompt.md` for the milestone plan, and `docs/decisions/` for the
architecture decision records. `docs/benchmark.md` holds the performance
benchmark protocol; [token accounting](docs/token-savings.md) documents the
measured response costs and simulated evidence-gathering workflows, and
[cost and quality](docs/cost-and-quality.md) explains every saving mechanism
alongside the gates that stop a saving from costing relevant evidence.

The [same-quality, lower-cost implementation plan](docs/plans/same-quality-lower-cost.md)
specifies the current optimization work. Included in the 0.15.0 changes and recorded in
[ADR 0023](docs/decisions/0023-read-cost-guard-and-context-epochs.md): the opt-in
read-cost guard, context-lifetime-safe evidence reuse, and the frozen three-arm
agent cost/quality comparison in [`docs/eval/agent/`](docs/eval/agent/). The
comparison itself has not been executed. Executable scenario fixtures, scenario
and cache controllers, and a real usage adapter are still missing, in addition
to pinned clients, credentials and dated pricing. The full paid comparison is
blocked until those requirements are met. Enforce mode remains experimental and
no saving is claimed. Local-model delegation stays deferred.

### Releasing

Releases are cut by merging, not by hand:

1. Bump `<VersionPrefix>` in `Directory.Build.props` inside the feature PR
   (contract changes bump the minor version while pre-1.0).
2. Merge to `main`. The `Tag on version change` workflow notices the new
   version, pushes `v<version>`, and `release.yml` publishes to NuGet and npm,
   builds the self-contained binaries and publishes the GitHub release
   automatically once all package and binary jobs succeed.

A merge that leaves `VersionPrefix` untouched releases nothing. One-time
setup: an Actions secret `RELEASE_PAT` (fine-grained PAT, this repository
only, Contents: Read and write) — required because tags pushed with the
default workflow token do not trigger `release.yml`.
NuGet Trusted Publishing also requires `NUGET_USER`; a release fails explicitly
if it is missing. Re-running the release skips existing package versions and
updates an existing GitHub release, including publishing a remaining draft.

#### npm authentication

The npm job publishes through **trusted publishing**: npm exchanges the job's
GitHub OIDC identity for a short-lived credential, so no long-lived token is
stored in the repository and provenance is attested automatically. Register a
trusted publisher on npmjs.com for **each** of the seven packages
(`repocontext-tool` and the six platform packages, whose exact names are listed
in `npm/repocontext/lib/platform.js`),
under *Package settings → Trusted publishing*:

| Field | Value |
| --- | --- |
| Organization or user | `Berthapp` |
| Repository | `RepoContext` |
| Workflow filename | `release.yml` (filename only, not a path) |
| Environment | *(leave empty)* |

These fields are case-sensitive and must match GitHub exactly.

npm can only register a trusted publisher for a package that already exists,
so a package's **first-ever** version needs one of:

- an `NPM_TOKEN` Actions secret (granular token, read and write). The job uses
  it automatically when present, then you delete the secret and register the
  trusted publishers; or
- a one-off `npm publish` from a machine where you are logged in
  (`npm login`), which stores nothing in CI.

Both paths are supported without editing the workflow: `NPM_TOKEN` wins when
set, OIDC is used when it is absent. Publishing is skipped per package when
that exact version already exists, so re-running a release is safe.

Trusted publishing needs npm ≥ 11.5.1 on Node ≥ 22.14, which the job installs
itself. Note that `actions/setup-node` is used **without** `registry-url`: that
input writes an auth line into `.npmrc`, and any auth line makes npm take the
legacy token path and ignore OIDC.

The npm packages are assembled from the same self-contained publish output as
the release archives, by `npm/build-packages.mjs`. `Directory.Build.props` stays
the single source of the version: the builder reads `VersionPrefix` and pins
every platform package to it. To inspect what a release would publish:

```bash
node npm/build-packages.mjs --dry-run --out npm/dist   # manifests only
node --test npm/repocontext/test/platform.test.mjs     # launcher resolution
```

## License

[Apache-2.0](LICENSE) — see also [NOTICE](NOTICE).
