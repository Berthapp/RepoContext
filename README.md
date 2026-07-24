# RepoContext

> Local-first, explainable project memory for AI coding agents.

RepoContext is a local-first CLI (`repoctx`) that deterministically indexes a
software repository and gives AI coding agents compact, **explainable** context:
exactly the relevant files, symbols, tests and relationships — with a
machine-readable reason for every hit, and a hard token budget per answer.

It runs entirely offline. **No source code leaves the machine, there is no
telemetry, and no LLM or embedding calls are ever made.** The same query on the
same index always produces byte-identical output.

Supported languages: **TypeScript, TSX, JavaScript, C#**.

## Why: tokens are the bill

Every token figure repoctx reports is a real BPE count, and
`--response-budget-tokens 2000` is a hard ceiling measured against the exact
bytes emitted — not an estimate. In the current deterministic candidate
evaluation, repeating a slices request with its receipts cuts the core response
from 1,904 to 609 tokens (68%) while retaining every labelled must-find file,
symbol and span. See the [methodology, limitations and raw
artifacts](docs/token-savings.md); the candidate is a baseline for future
changes, not a retroactive pre/post quality comparison.

The loop an agent runs, on this repository:

<img alt="Animated terminal demo: repoctx context returning ranked source slices, repoctx outline showing a compact skeleton, and repoctx changed reporting modified and impacted files. The animation predates per-unit receipts; use --seen for partial evidence and reserve --known for files held in full." src="docs/assets/demo.svg" width="880">

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (LTS) to build; the released
  global tool needs only the .NET 10 runtime.

## Installation

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

Or download a self-contained binary for `linux-x64`, `win-x64` or `osx-arm64`
(no .NET runtime required) from the [latest release][releases], unpack it and
put `repoctx` on your `PATH`:

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
repoctx related src/auth/login.ts               # imports, dependents, tests
repoctx context "change the login logic"        # explained, budgeted bundle
repoctx context "add logout" --top 4 --response-budget-tokens 2000 --detail slices
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
| `index` | Build or incrementally update the index (stores real BPE token counts per file). | `--full` |
| `search <query>` | BM25 full-text search (content and symbols). | `--top`, `--symbols`, `--format` |
| `related <file>` | Imports, dependents and linked tests of a file. | `--format` |
| `context <task>` | Ranked, explained context bundle packed into a token budget. | `--top`, `--budget-tokens`, `--response-budget-tokens`, `--projected-read-budget-tokens`, `--detail paths\|outline\|slices`, `--seen <receipt>`, `--known <path>@<hash>`, `--session <name>`, `--strip-comments`, `--no-memory`, `--format` |
| `outline <file>` | A file's skeleton: symbols, signatures, doc summaries, exact full-read token cost. | `--format` |
| `changed` | Working-tree diff against the index, with impacted dependents. | `--patch`, `--format` |
| `prime` | Cache-stable repository primer for a cacheable prompt prefix (byte-identical for unchanged indexed content and token calibration). | `--files`, `--format` |
| `memory add <text>` | Store one agent-authored insight: a `note`, `decision` or `constraint`, optionally linked to files (hash-recorded) and scoped to a session. | `--kind`, `--file`, `--tag`, `--session`, `--format` |
| `memory search [query]` | Deterministic recall with reasons and hash-based `stale` flags; omit the query to list. | `--top`, `--kind`, `--file`, `--session`, `--stale`, `--format` |
| `memory rm <id>` | Remove one memory entry (curation). | `--format` |
| `architecture` | Structure (LOC tree), language distribution, centrality, entrypoints. | `--depth`, `--format` |
| `stats` | Token-savings dashboard aggregated from your local usage (see below). | `--format` (incl. `html`), `--open` |
| `mcp` | Run the MCP server over stdio for AI agents (see below). | — |

Exit codes: `0` success · `1` error · `2` no index · `3` invalid arguments.

### The token-frugal loop

All token figures are real BPE counts (`o200k_base`, computed offline at index
time), so budgets can be trusted. The intended agent workflow:

1. Optionally run `prime` for a new, unfamiliar repository when the agent can
   keep a byte-stable primer (languages, layout, entrypoints, key files) behind
   a cache breakpoint. Skip this call for focused/familiar work or clients that
   cannot retain a cached prefix. The primer is byte-identical for unchanged
   indexed content and token calibration.
2. `context "<task>" --detail slices --response-budget-tokens 2000 --format md`
   — working context with symbol-aligned source `spans` packed into a **hard**
   response ceiling. Markdown avoids JSON's escaping cost for embedded code;
   use JSON when a client needs to parse the envelope. `--detail outline`
   surveys more files for fewer tokens, while the default `paths` returns
   pointers plus exact full-read costs. `--strip-comments` can remove comment
   banners from slices (lossy; line ranges become approximate).
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
tooling) show where the savings come from. For a visual dashboard, run
`repoctx stats --open` — it writes a self-contained HTML page (charts, no
external resources, works fully offline) to `.repoctx/stats.html` and opens it
in your default browser; `--format html` prints the same page to stdout. There
is deliberately no localhost server: the browser renders the local file, and
RepoContext stays network-free. Set `REPOCTX_NO_STATS=1` to disable recording;
delete the log file to reset the dashboard. See ADR 0011.

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

## Agent integration

RepoContext is agent-agnostic — any agent with shell access can use it. The
fastest way to wire it in is to let `init` write the instructions for you:

```bash
repoctx init --agents     # also create/update CLAUDE.md and AGENTS.md
```

On an interactive terminal, plain `repoctx init` asks whether to do this; pass
`--agents` to opt in without the prompt (e.g. in scripts) or `--no-agents` to
skip it. The managed block is delimited by `<!-- BEGIN/END RepoContext -->`
markers, so re-running `init` updates it in place without touching the rest of
the file or duplicating the block. Any file that already exists is appended to,
never overwritten.

Claude Code reads `CLAUDE.md`; GitHub Copilot (agent mode), Cursor and most
other agents read `AGENTS.md` — so a repository that ran `repoctx init --agents`
works with all of them out of the box. (Copilot's inline completions and
classic chat don't run tools, so RepoContext applies to agent mode only.)

To add it by hand instead, drop a snippet like this into your agent
instructions (e.g. `CLAUDE.md`, `AGENTS.md`):

```markdown
## Getting context

This repository is indexed by RepoContext (`repoctx`); token figures are real
BPE counts. Start with one budgeted call, then escalate only on a concrete gap:

1. Optionally orient with `repoctx prime` for a new, unfamiliar repository, but
   only when the client can retain it behind a cache breakpoint.
2. Working context: `repoctx context "<task>" --detail slices
   --response-budget-tokens 2000 --format md` (`md` avoids JSON escaping for
   embedded code; `--strip-comments` is a lossy option for comment banners).
3. Only if a file is missing: `repoctx search "<term>" --symbols --format json`.
4. Only if a symbol is missing: `repoctx outline <file> --format json`.
5. Only for dependency or impact questions:
   `repoctx related <file> --format json`.
6. Only for unfamiliar boundaries:
   `repoctx architecture --depth 1 --format md`.
7. After editing: `repoctx changed --patch --format md`; when `stale`, run
   `repoctx index`.
8. If `context` leaves a concrete knowledge gap, use
   `repoctx memory search "<topic>" --format json`; after completing difficult
   work, record only a distilled finding with `repoctx memory add`.
9. Stop once no evidence needed for the task is missing.

Never pay twice, and never over-claim:

- `--seen <receipt>` suppresses exactly the pointer, span or symbol that receipt
  came from; the rest of the file still arrives.
- `--known <path>@<hash>` asserts you hold the **whole** file — never derive it
  from a slice or outline.
- `--session <name>` persists reuse bookkeeping locally without changing that
  partial-versus-whole-file distinction.
```

## MCP server

Agents that speak the [Model Context Protocol](https://modelcontextprotocol.io)
can call RepoContext directly instead of shelling out. `repoctx mcp` runs an MCP
server over stdio and exposes seven non-destructive tools:

| Tool | Wraps | Arguments |
| --- | --- | --- |
| `repoctx.search` | `search` | `query`, `top`, `symbols` |
| `repoctx.get_context` | `context` | `task`, `top`, `budgetTokens`, `responseBudgetTokens`, `projectedReadBudgetTokens`, `detail`, `known`, `seen`, `session`, `stripComments`, `includeMemory` |
| `repoctx.get_related_files` | `related` | `file` |
| `repoctx.get_outline` | `outline` | `file` |
| `repoctx.get_changes` | `changed` | `patch` |
| `repoctx.memory_add` | `memory add` | `text`, `kind`, `files`, `tags`, `session` |
| `repoctx.memory_search` | `memory search` | `query`, `top`, `kind`, `file`, `session`, `stale` |

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
  "include": ["src", "app", "lib", "docs"],
  "exclude": ["node_modules", "dist", "bin", "obj", ".next", ".git"],
  "respectGitignore": true,
  "sensitiveFiles": [".env*", "*.secret.*", "appsettings.Production.json"],
  "indexing": { "maxFileSizeKb": 512, "includeTests": true, "includeDocs": true },
  "ranking": {
    "weights": { "fts": 0.4, "symbol": 0.3, "graph": 0.2, "path": 0.1 },
    "synonyms": { "zahlung": ["payment", "billing"] }
  }
}
```

| Key | Meaning |
| --- | --- |
| `include` | Root directories to scan. |
| `exclude` | Directory/file globs to skip (gitignore syntax). |
| `respectGitignore` | Also honor the repository's root `.gitignore`. |
| `sensitiveFiles` | Never indexed — neither content nor path. |
| `indexing.maxFileSizeKb` | Skip files larger than this. |
| `indexing.includeTests` / `includeDocs` | Include test / documentation files. |
| `ranking.weights` | Signal weights used by `context` (fts, symbol, graph, path). |
| `ranking.synonyms` | Query-term expansions used by `context`. |
| `tokens.profile` | Calibrate reported counts/budgets to a tokenizer: `o200k`/`openai` (default) or `claude`. |
| `tokens.factor` | Explicit calibration multiplier in `(0, 100]`; overrides `tokens.profile` (invalid values fall back to raw counts). |
| `pricing.inputPerMtok` | Input price per million tokens; enables the money view in `stats`. |
| `pricing.currency` | Currency label for the `stats` money view (default `USD`). |

You can also add a `.repoctxignore` file (gitignore syntax) for extra
exclusions. The index (`.repoctx/`) contains code excerpts — and, with M9,
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

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `No index found. Run 'repoctx index' first.` (exit code 2) | Run `repoctx init` then `repoctx index` in the repository root. |
| `File not found in index: ...` from `related` | The file is not indexed — check `include`/`exclude`, `.repoctxignore`, `sensitiveFiles` and `indexing.maxFileSizeKb`, then re-run `repoctx index`. |
| Results look stale | Re-run `repoctx index`; unchanged files are not reparsed, though the local hash/graph pass still reads the indexed corpus. |
| Exit code 3 | Invalid arguments — check option spelling and values (e.g. `--top` must be > 0, `--format` must be `text`, `json` or `md`). |

## Development

See `CLAUDE.md` for build/test commands, repository structure and conventions,
`docs/build-prompt.md` for the milestone plan, and `docs/decisions/` for the
architecture decision records. `docs/benchmark.md` holds the performance
benchmark protocol; `docs/token-savings.md` documents the measured end-to-end
token savings of the M6 context protocol.

### Releasing

Releases are cut by merging, not by hand:

1. Bump `<VersionPrefix>` in `Directory.Build.props` inside the feature PR
   (contract changes bump the minor version while pre-1.0).
2. Merge to `main`. The `Tag on version change` workflow notices the new
   version, pushes `v<version>`, and `release.yml` publishes to NuGet, builds
   the self-contained binaries and drafts the GitHub release.
3. Review and publish the draft release.

A merge that leaves `VersionPrefix` untouched releases nothing. One-time
setup: an Actions secret `RELEASE_PAT` (fine-grained PAT, this repository
only, Contents: Read and write) — required because tags pushed with the
default workflow token do not trigger `release.yml`.

## License

[Apache-2.0](LICENSE) — see also [NOTICE](NOTICE).
