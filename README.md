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

Every token figure repoctx reports is a real BPE count, and budgets are charged
at what the agent actually receives — so `--budget-tokens 2000` really means
about 2,000 tokens. Measured end-to-end on this repository
([methodology & full numbers](docs/token-savings.md)):

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/token-savings-dark.svg">
  <img alt="Measured: getting working context for the same task costs 6,222 tokens with pointer-plus-full-read workflows, 2,151 with a budgeted outline bundle and 2,110 with a budgeted slices bundle - 66 percent less." src="docs/assets/token-savings.svg" width="880">
</picture>

The loop an agent runs, on this repository:

<img alt="Animated terminal demo: repoctx context with a token budget returning ranked source slices with reasons and hashes; repoctx outline showing a file skeleton for a third of the read cost; repoctx changed reporting a modified file and its impacted dependents; and a repeated context call with --known returning a zero-cost unchanged marker." src="docs/assets/demo.svg" width="880">

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
already trust. Add it to one project of the repository (any target framework;
the machine needs the .NET 10 runtime, which the SDK you build with includes):

```bash
dotnet add src/YourProject package RepoContext.MSBuild
```

It is a development-only dependency: nothing is compiled into your assemblies,
copied to your output, or passed on to consumers of your package. It brings
two MSBuild targets:

```bash
# Run any repoctx command through MSBuild
# (runs in the directory you invoke it from):
dotnet msbuild src/YourProject -t:RepoCtx -p:RepoCtxArgs="init"
dotnet msbuild src/YourProject -t:RepoCtx -p:RepoCtxArgs="index"

# Or write stable wrapper scripts once (repoctx + repoctx.cmd,
# into .repoctx/bin/ under the current directory, git-ignored with .repoctx/):
dotnet msbuild src/YourProject -t:RepoCtxShim
```

From then on the wrapper is a fixed path — for humans, agent instructions and
MCP configs alike (use `.repoctx/bin/repoctx` as the MCP `command`):

```bash
./.repoctx/bin/repoctx context "change the login logic"
```

The wrappers embed the package's absolute path inside the local NuGet cache,
so re-run `-t:RepoCtxShim` after updating the package version.

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
repoctx context "add logout" --top 4 --budget-tokens 2000 --snippets
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
  "schema_version": 2,
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
| `context <task>` | Ranked, explained context bundle packed into a token budget. | `--top`, `--budget-tokens`, `--detail paths\|outline\|slices`, `--known <path>@<hash>`, `--session <name>`, `--strip-comments`, `--format` |
| `outline <file>` | A file's skeleton: symbols, signatures, doc summaries, exact full-read token cost. | `--format` |
| `changed` | Working-tree diff against the index, with impacted dependents. | `--patch`, `--format` |
| `prime` | Cache-stable repository primer for the top of an agent's prompt (byte-identical until code changes). | `--files`, `--format` |
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

1. `prime` — a byte-stable primer (languages, layout, entrypoints, key files)
   for the top of the prompt, behind a cache breakpoint. It changes only when
   code changes, so every later turn re-reads it at cached-token rates.
2. `context "<task>" --detail slices --budget-tokens 2000 --format md` —
   working context with source slices packed into the budget. Prefer `md` for
   slices: JSON escaping of embedded code is billed and adds 10-20 %. `--detail
   outline` surveys more files for fewer tokens; the default `paths` returns
   pointers plus exact full-read costs. `--strip-comments` drops comment
   banners from slices (lossy; line ranges become approximate).
3. `outline <file>` before any full read.
4. After editing: `changed --patch` returns just the changed hunks (far
   cheaper than a re-read) — and `repoctx index` when it reports `stale`.
5. Never pay twice. Pass `--session <name>` and `context` remembers the slices
   it delivered, returning unchanged files as zero-cost markers on later calls
   without you re-typing anything (echoed `--known <path>@<hash>` lists work
   too, but that text is output tokens, which cost more than input). Every
   `context` response also carries a `state` hash that moves whenever the
   index content changes.
6. Never re-derive. `memory add` stores a distilled insight after the work is
   done; `memory search` (and `context`, automatically) brings it back in the
   next session for a fraction of the re-discovery cost. See
   [Agent memory](#agent-memory-never-re-derive) below.

Using a non-OpenAI model? Set `tokens.profile` (e.g. `"claude"`) in
`repoctx.config.json` so budgets and reported counts match your tokenizer —
the index keeps raw counts, so no re-index is needed when you switch.

### The token-savings dashboard

`repoctx stats` estimates the net token impact of that loop from your recorded
successful usage (CLI and MCP alike):

```text
Token savings (o200k counts, 2026-07-01 to 2026-07-14):

  calls                      42
  response tokens        31,208
  reads replaced        104,566
  net saved              73,358  (70 % of replaced reads)
```

Every successful query response records two real token figures to a local log
(`.repoctx/stats.jsonl`): what the response cost, and the full file reads it is
assumed to make unnecessary. Embedded slices and non-empty outlines are credited
at the file's full-read cost; `--known` markers assume the caller actually holds
the matching file content. **Net saved** is replaced reads minus response cost,
summed over every recorded call. It is an estimate, not a guaranteed lower bound:
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
  compaction), while the session's known-set keeps tracking delivered files.

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
BPE counts. The economical loop:

1. Orient once: `repoctx prime` — a cache-stable primer; place it at the top
   of the prompt behind a cache breakpoint.
2. Working context: `repoctx context "<task>" --detail slices --budget-tokens 2000 --format md`
   (prefer `md` for slices — JSON escaping of code is billed; `--strip-comments`
   drops comment banners).
3. Track a session instead of echoing hashes: `repoctx context "<task>" --session <name>`
   returns unchanged files as zero-cost markers on later calls.
4. Before reading any file: `repoctx outline <file> --format json`.
5. Dependencies and tests: `repoctx related <file> --format json` instead of grep.
6. Find a symbol: `repoctx search "<term>" --symbols --format json`.
7. After editing: `repoctx changed --patch --format md` for just the changed
   hunks; when `stale`, run `repoctx index`.
8. Never re-derive: `repoctx memory search "<topic>" --format json` before
   exploring (`context` folds matching memories in automatically); verify
   entries flagged `stale`.
9. Remember what you learned: `repoctx memory add "<1-2 sentence insight>"
   --kind note|decision|constraint --file <path>` after completing a task.
```

## MCP server

Agents that speak the [Model Context Protocol](https://modelcontextprotocol.io)
can call RepoContext directly instead of shelling out. `repoctx mcp` runs an MCP
server over stdio and exposes seven non-destructive tools:

| Tool | Wraps | Arguments |
| --- | --- | --- |
| `repoctx.search` | `search` | `query`, `top`, `symbols` |
| `repoctx.get_context` | `context` | `task`, `top`, `budgetTokens`, `detail`, `known`, `session`, `stripComments`, `includeMemory` |
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
and never mutates the index. Successful calls append token counts to the local
usage ledger described above.

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

With GitHub Copilot agent mode in VS Code, commit a `.vscode/mcp.json`:

```json
{
  "servers": {
    "repoctx": {
      "type": "stdio",
      "command": "repoctx",
      "args": ["mcp"]
    }
  }
}
```

Build the index first (`repoctx init && repoctx index`); tools return an error
until an index exists.

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
| `tokens.factor` | Explicit calibration multiplier; overrides `tokens.profile`. |
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
| Results look stale | Re-run `repoctx index`; it is incremental and only re-reads changed files. |
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
