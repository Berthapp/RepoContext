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
from 1,906 to 609 tokens (68%) while retaining every labelled must-find file,
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
> above instead.

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
repoctx context "add logout" --top 4 --response-budget-tokens 2000 --snippets
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
| `context <task>` | Ranked, explained context bundle packed into a token budget. | `--top`, `--budget-tokens`, `--response-budget-tokens`, `--projected-read-budget-tokens`, `--detail paths\|outline\|slices`, `--seen <receipt>`, `--known <path>@<hash>`, `--format` |
| `outline <file>` | A file's skeleton: symbols, signatures, doc summaries, exact full-read token cost. | `--format` |
| `changed` | Working-tree diff against the index, with impacted dependents. | `--format` |
| `architecture` | Structure (LOC tree), language distribution, centrality, entrypoints. | `--depth`, `--format` |
| `stats` | Token-savings dashboard aggregated from your local usage (see below). | `--format` (incl. `html`), `--open` |
| `mcp` | Run the MCP server over stdio for AI agents (see below). | — |

Exit codes: `0` success · `1` error · `2` no index · `3` invalid arguments.

### The token-frugal loop

All token figures are real BPE counts (`o200k_base`, computed offline at index
time), so budgets can be trusted. The intended agent workflow:

1. `context "<task>" --detail slices --response-budget-tokens 2000` — working
   context with symbol-aligned source `spans` packed into a **hard** response
   ceiling (`--detail outline` surveys more files for fewer tokens; the default
   `paths` returns pointers plus exact full-read costs).
2. Escalate only on a concrete gap: `search --symbols` when a file is missing,
   `outline <file>` when the symbol you need was not delivered, `related` for
   dependency/impact questions, `architecture --depth 1` for unfamiliar
   boundaries.
3. After editing: `changed` — and `repoctx index` when it reports `stale`.
4. Never pay twice, and never over-claim: see below.

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
tiny-budget requests cheap. See ADR 0013.

#### Reuse: receipts vs. full-file possession

These are **not** interchangeable, and conflating them was a real bug (ADR 0012):

- **`--seen <receipt>`** (repeatable) — each delivered pointer, span and outline
  symbol carries a `receipt`. Echoing one suppresses *exactly* that unit; every
  other part of the same file still arrives. Reused units are acknowledged in
  `reused` and never consume a `--top` slot, so echoing receipts buys new context
  rather than markers.
- **`--known <path>@<hash>`** — asserts you hold the **entire** file. Use it only
  when you actually read the whole file. The `hash` printed next to a *partial*
  result identifies the file version, not what you were sent; deriving `--known`
  from a slice or outline claims possession of lines you never received.

Every `context` response carries `content_state` (which file contents are
indexed), `analysis_state` (that content plus config and producer versions),
`evidence_id` and `representation_id`.

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

Every successful query response records two token figures to a local log
(`.repoctx/stats.jsonl`): exact response cost and an estimate of full-file reads
made unnecessary. Embedded spans and non-empty outlines are credited at the
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

1. `repoctx context "<task>" --detail slices --response-budget-tokens 2000 --format json`
2. Only if a file you need is missing: `repoctx search "<term>" --symbols --format json`.
3. Only if the symbol you need was not delivered: `repoctx outline <file> --format json`.
4. Only for dependency/impact questions: `repoctx related <file> --format json`.
5. Only for unfamiliar boundaries: `repoctx architecture --depth 1 --format md`.
6. After editing: `repoctx changed --format json`; when `stale`, run `repoctx index`.
7. Stop once nothing you need is missing.

Never pay twice, and never over-claim:

- `--seen <receipt>` suppresses exactly the pointer, span or symbol that receipt
  came from; the rest of the file still arrives.
- `--known <path>@<hash>` asserts you hold the **whole** file — never derive it
  from a slice or outline.
```

## MCP server

Agents that speak the [Model Context Protocol](https://modelcontextprotocol.io)
can call RepoContext directly instead of shelling out. `repoctx mcp` runs an MCP
server over stdio and exposes five non-destructive query tools:

| Tool | Wraps | Arguments |
| --- | --- | --- |
| `repoctx.search` | `search` | `query`, `top`, `symbols` |
| `repoctx.get_context` | `context` | `task`, `top`, `budgetTokens`, `responseBudgetTokens`, `projectedReadBudgetTokens`, `detail`, `known`, `seen` |
| `repoctx.get_related_files` | `related` | `file` |
| `repoctx.get_outline` | `outline` | `file` |
| `repoctx.get_changes` | `changed` | — |

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

You can also add a `.repoctxignore` file (gitignore syntax) for extra
exclusions. The index (`.repoctx/`) contains code excerpts and is a sensitive
artifact; it is git-ignored automatically.

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
