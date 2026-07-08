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
src/RepoContext.Cli/    # System.CommandLine commands + formatter (produces `repoctx`)
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
- [ ] **M0** — parser spike (tree-sitter vs. fallback) → ADR 0001. *Hard stop.*
- [ ] **M1** — init, incremental index, FTS search.
- [ ] **M2** — symbol extraction + symbol search.
- [ ] **M3** — graph, `related`, `context` pipeline.
- [ ] **M4** — `architecture`, formats, docs, release.
- [ ] **M5** — MCP server (only on explicit instruction).
