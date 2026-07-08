# RepoContext – Build Prompt (Claude Code)

> This file is the build plan referenced throughout the project. It is stored
> for provenance. The binding product context lives in
> `../repocontext-produktdoku.md` (repo root). See ADR 0003 for how the
> "sources of truth" are resolved in this repository.

## Usage

1. Create a new repo and add the documents: `docs/repocontext-mvp-spezifikation.md`,
   `docs/repocontext-produktdoku-v2.md`, and this file as `docs/build-prompt.md`.
2. Start Claude Code in the repo root and send: "Read docs/build-prompt.md
   completely and execute it."
3. This prompt enforces a hard stop after M0 (parser decision) and a status
   report with approval after each further milestone.

## Assignment

Build a local-first CLI tool, RepoContext (`repoctx`), that deterministically
indexes software repositories and provides AI coding agents with compact,
explainable context. Build the foundation and MVP in this order: M-Skeleton,
M0, M1, M2, M3, M4. M5 (MCP server) only on explicit instruction.

## Non-negotiable constraints

- .NET 10 (LTS), C#, `<Nullable>enable</Nullable>`, `TreatWarningsAsErrors`.
- SQLite + FTS5 via Microsoft.Data.Sqlite.
- No network access at runtime, enforced twice: (a) BannedApiAnalyzers with
  BannedSymbols.txt (System.Net.Http.*, System.Net.Sockets.*,
  System.Net.WebClient, ...) for all projects under `src/`; (b) a CI job that
  runs the integration tests in a container with `--network none`.
- Determinism: identical index + identical query ⇒ byte-identical output.
  Stable sorting (score DESC, path ASC), no random values, no timestamps in
  results (allowed only in index statistics).
- Every JSON output includes `schema_version` (start: 1). Exit codes per F7
  (0 success, 1 error, 2 no index, 3 invalid arguments).
- Code, comments, commit messages, README: English. The German docs stay
  unchanged.
- No external services, no telemetry, no LLM or embedding calls.

## Working style

1. Milestone by milestone, in order, no jumping ahead.
2. Per milestone: unit tests (scanner, parser, ranking) and integration tests
   (CLI against fixtures). `dotnet build` and `dotnet test` green, then a short
   status report.
3. Conventional Commits, small logical units.
4. Hard stop after M0. After every further milestone: status report and wait
   for approval.
5. Open detail questions: smallest sensible decision + an ADR in
   `docs/decisions/`. Exception: changes to the data model or CLI/JSON
   contracts (F5/F7) → ask first.
6. Keep CLAUDE.md continuously updated.

## Milestones (summary)

- **M-Skeleton** — solution/projects per structure; Directory.Build.props (TFM
  net10.0, Nullable, WarningsAsErrors, BannedApiAnalyzers); xUnit + one
  integration test that builds the CLI and runs `repoctx --version`;
  System.CommandLine foundation with subcommand stubs (init, index, search,
  related, context, architecture) returning exit code 1 "not implemented";
  .gitignore incl. `.repoctx/`, .editorconfig, README stub, CLAUDE.md; CI (build
  + tests, plus a no-network integration job); test fixtures `sample-ts` and
  `sample-cs`. **DoD:** CI fully green.
- **M0** — parser spike: decide tree-sitter vs. fallback (Roslyn for C#,
  tree-sitter for TS/JS). Prototype in `spikes/parser/`. Output
  `docs/decisions/0001-parser.md`. **Hard stop.**
- **M1** — `init`, incremental `index`, `search` (BM25 over FTS).
- **M2** — symbol extraction (TS/JS + C#) and `search --symbols`.
- **M3** — import/test graph, `related`, `context` pipeline with reasons.
- **M4** — `architecture`, `--format md`, README, benchmark template, release
  pipeline.
- **M5** — MCP server (`repoctx mcp`, stdio) — only on explicit instruction.

## What not to do

- No features beyond the specification (no extra commands/config/languages).
- No LLM or embedding calls, no telemetry, no network code.
- No premature abstractions (no plugin system, no DI overkill).
- No changes to the documents in `docs/`, except ADRs and `benchmark.md`.
