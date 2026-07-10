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

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (LTS) to build; the released
  global tool needs only the .NET 10 runtime.

## Installation

From source (until packages are published):

```bash
dotnet pack src/RepoContext.Cli -c Release
dotnet tool install --global --add-source src/RepoContext.Cli/bin/Release RepoContext.Tool
```

Or run directly:

```bash
dotnet run --project src/RepoContext.Cli -- <command> [options]
```

## Quickstart

```bash
cd your-repo
repoctx init                       # create .repoctx/ and repoctx.config.json
repoctx index                      # build the index (incremental afterwards)

repoctx search "authentication"                 # BM25 full-text search
repoctx search "login" --symbols                # search symbols only
repoctx related src/auth/login.ts               # imports, dependents, tests
repoctx context "change the login logic"        # explained, budgeted bundle
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

## Commands

| Command | Purpose | Key options |
| --- | --- | --- |
| `init` | Create `.repoctx/` and `repoctx.config.json`; add `.repoctx/` to `.gitignore`. | `--force` |
| `index` | Build or incrementally update the index. | `--full` |
| `search <query>` | BM25 full-text search (content and symbols). | `--top`, `--symbols`, `--format` |
| `related <file>` | Imports, dependents and linked tests of a file. | `--format` |
| `context <task>` | Ranked, explained, budgeted context bundle for a task. | `--top`, `--budget-tokens`, `--snippets`, `--format` |
| `architecture` | Structure (LOC tree), language distribution, centrality, entrypoints. | `--format` |

Exit codes: `0` success · `1` error · `2` no index · `3` invalid arguments.

## Agent integration

RepoContext is agent-agnostic — any agent with shell access can use it. Add a
snippet like this to your agent instructions (e.g. `CLAUDE.md`):

```markdown
## Getting context

Before reading files, ask RepoContext for the relevant ones:

- `repoctx context "<what you are about to do>" --format json` — ranked files
  with reasons and a token budget.
- `repoctx related <file> --format json` — a file's imports, dependents and tests.
- `repoctx search "<term>" --symbols --format json` — find a symbol.

Prefer these over reading the whole repository. Re-run `repoctx index` if the
working tree changed.
```

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

RepoContext never sends repository data anywhere and contains no telemetry. It
cannot stop a downstream agent from forwarding the excerpts it returns to an LLM
provider — for maximum privacy use a local/self-hosted agent and model, and list
sensitive files in `sensitiveFiles` / `.repoctxignore`.

## Development

See `CLAUDE.md` for build/test commands, repository structure and conventions,
`docs/build-prompt.md` for the milestone plan, and `docs/decisions/` for the
architecture decision records. `docs/benchmark.md` holds the token-savings
benchmark protocol.

## License

Apache-2.0 (planned; see the product documentation).
