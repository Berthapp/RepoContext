# CLAUDE.md

Guidance for working in this repository. Keep this file current: build/test
commands, structure, conventions, and the active milestone.

## What this is

RepoContext — a local-first .NET 10 CLI (`repoctx`) that deterministically
indexes repositories and serves compact, explainable context to AI coding
agents. See `repocontext-produktdoku.md` (product doc, German, at the repo root)
for the product context and `docs/build-prompt.md` for the build plan.

## Sources of truth

- **`repocontext-produktdoku.md`** (repo root) — product principles and scope
  (binding; German, do not edit).
- **`docs/build-prompt.md`** — milestones, working style, constraints.
- There is no separate `repocontext-mvp-spezifikation.md` in this repo. Where
  the build prompt references "spec chapter N", the corresponding decision is
  derived from the product doc + build prompt and recorded as an ADR in
  `docs/decisions/`. See ADR 0003.

## Non-negotiable constraints

- .NET 10, C#, `Nullable=enable`, `TreatWarningsAsErrors=true` (set in
  `Directory.Build.props`).
- **No network access at runtime.** Enforced at compile time by
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` for every
  project under `src/` (RS0030 is an error), and at runtime by a CI job that
  runs the integration tests with `--network none`.
- **Determinism:** identical index + identical query ⇒ byte-identical output.
  Stable sort (score DESC, path ASC), no random values, no timestamps in
  results (timestamps allowed only in index statistics).
- Every JSON output carries `schema_version` (currently
  `RepoContextInfo.SchemaVersion` = 1).
- Exit codes (spec F7): 0 success, 1 error, 2 no index, 3 invalid arguments —
  see `ExitCode`.
- Code, comments, commit messages, docs: **English**. The German docs stay as-is.

## Build & test commands

```bash
# Build everything (enforces warnings-as-errors + banned-API analyzer).
dotnet build RepoContext.slnx -c Release

# Run all unit + integration tests.
dotnet test RepoContext.slnx -c Release

# A single project / test.
dotnet test tests/RepoContext.Core.Tests -c Release
dotnet test tests/RepoContext.Integration.Tests -c Release --filter "FullyQualifiedName~CliContractTests"
```

The SDK is provided by Ubuntu's `dotnet-sdk-10.0` package; `dotnet` lives at
`/usr/lib/dotnet`. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1` for quiet
output.

## Repository structure

```
src/RepoContext.Core/   # scanner, parser adapters, store, query/ranking (library)
src/RepoContext.Cli/    # System.CommandLine commands + formatter + MCP server (produces `repoctx`)
tests/RepoContext.Core.Tests/          # unit tests
tests/RepoContext.Integration.Tests/   # end-to-end tests through the CLI
tests/fixtures/sample-ts/   # Next-style TS/JS fixture (incl. negative cases)
tests/fixtures/sample-cs/   # ASP.NET-style C# fixture (incl. negative cases)
spikes/                 # throwaway spike code (M0); never referenced by src/
docs/decisions/         # ADRs (0001-parser.md, ...)
```

- `Directory.Build.props` (root): TFM, Nullable, warnings-as-errors, version.
- `src/Directory.Build.props`: adds the banned-API analyzer for production code.
- The integration tests run the real `repoctx` binary via `CliHarness`, which
  locates `src/RepoContext.Cli/bin/<config>/net10.0/repoctx.dll`.

## Conventions

- Conventional Commits (`feat(core): ...`, `test(cli): ...`), small logical units.
- File-scoped namespaces, `sealed` by default, XML doc on public members.
- Open detail decisions → smallest sensible choice + an ADR in `docs/decisions/`.
  **Exceptions that require asking first:** changes to the data model or to the
  CLI/JSON contracts (F5/F7).

## Milestone status

- [x] **M-Skeleton** — solution, projects, `Directory.Build.props`, banned-API
  analyzer, System.CommandLine stubs (init/index/search/related/context/
  architecture) with F7 exit codes, xUnit + integration harness, fixtures, CI.
- [x] **M0** — parser spike → **GO: tree-sitter via `TreeSitter.DotNet` 1.3.0**
  for TS/TSX/JS/C# (ADR 0001; overall 0.34 ms/file, all 3 RIDs covered).
  *Hard stop — awaiting approval before M1.*
- [x] **M1** — `init`, incremental `index` (hash diff), FTS `search` (BM25,
  text/json). Scanner (ignore/sensitive/binary/size/kind), chunker, SQLite store
  (WAL + FTS5), determinism. Data model & contracts in ADR 0004.
  *Awaiting approval before M2.*
- [x] **M2** — symbol extraction (tree-sitter TS/TSX/JS/C#) + `search --symbols`;
  symbols table, JSDoc/XML docs, route heuristic, camel/snake split indexing
  (ADR 0005, schema v2). *Awaiting approval before M3.*
- [x] **M3** — file graph (edges, schema v3), `related` (F4), `context` pipeline
  (query analysis DE/EN + synonyms, fts/symbol/path candidates, bounded 2-hop
  graph, weighted scoring, vendor penalty, diversity, token budget, reasons).
  ADR 0006. *Awaiting approval before M4.*
- [x] **M4** — `architecture` (F6: LOC tree, languages, centrality, entrypoints),
  `--format md` for all commands, full README + `docs/benchmark.md`, perf smoke
  test, release pipeline (global tool + self-contained RIDs, grammar trim).
  ADR 0007. **MVP complete.**
- [x] **M5** — MCP server (`repoctx mcp`, stdio) via the official SDK
  (`ModelContextProtocol.Core`, low-level, no DI host). Three read-only tools
  (`repoctx.search`, `repoctx.get_context`, `repoctx.get_related_files`) reuse
  the deterministic engines and return the same JSON contract as
  `--format json`. ADR 0008.
- [x] **M5.1** — token-lean output (ADR 0009): all `--format json`/MCP
  responses are compact single-line JSON (null-valued optional fields omitted);
  `context` caps full-path graph reasons at 2 per file with a `graph:+N`
  summary. Measured 30–62 % fewer response tokens; CLI ↔ MCP byte parity and
  determinism unchanged.
- [x] **M6** — token-frugal context protocol (ADR 0010, JSON `schema_version` 2,
  index schema v4): real BPE token counts (`o200k_base`, offline) stored per
  file; `outline` command + `repoctx.get_outline` (file skeletons); `context
  --detail paths|outline|slices` with budget packing charged at what the agent
  actually consumes; `--known path@hash` → zero-cost `unchanged` markers +
  `state` hash; `changed` command + `repoctx.get_changes` (tree↔index diff with
  impacted dependents); `architecture --depth`; agent instructions teach the
  economical loop. `docs/token-savings.md` documents measured end-to-end
  savings.

<!-- BEGIN RepoContext (managed by `repoctx init`) -->
## Getting repository context with RepoContext

This repository is indexed by RepoContext (`repoctx`), a local-first, offline
context engine built to save you tokens. Prefer it over reading files broadly;
all token figures it reports are real BPE counts. The economical loop:

1. Orient once: `repoctx architecture --depth 1 --format md`.
2. Working context: `repoctx context "<task>" --detail slices --budget-tokens 2000 --format json`
   — ranked source slices packed into the budget, each with reasons and a `hash`.
   Use `--detail outline` to survey more files for fewer tokens.
3. Before reading any file: `repoctx outline <file> --format json` — symbols,
   signatures and the exact full-read token cost, at a fraction of that cost.
4. Dependencies and tests: `repoctx related <file> --format json` instead of grep.
5. Find a symbol: `repoctx search "<term>" --symbols --format json`.
6. After editing: `repoctx changed --format json`; when it reports `stale`,
   run `repoctx index` (fast, incremental) and re-query.
7. Never pay twice: echo hashes you already hold, e.g.
   `repoctx context "<task>" --known <path>@<hash>` — unchanged files come
   back as zero-cost markers.
<!-- END RepoContext -->
