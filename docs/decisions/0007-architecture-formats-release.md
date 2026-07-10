# ADR 0007 — M4 architecture, formats and release

- **Status:** accepted
- **Date:** 2026-07-10
- **Milestone:** M4

## Context

M4 completes the MVP: the `architecture` command, `--format md` for all
commands, docs, a perf smoke test, and the release pipeline.

## Decisions

- **`architecture` (F6):** a directory tree limited to **depth 3** with LOC and
  file-count aggregation (deeper files roll up into their depth-3 ancestor); a
  language distribution; **centrality** = the top-10 most-imported files
  (incoming import edges, from the M3 graph); and an **entrypoint heuristic** =
  known entry names (`Program.cs`, `main.*`, Next.js `page/layout/route`,
  `middleware.*`) **or** a source file with no incoming imports that itself
  imports others (a driver/root, which excludes leaf components).
- **`--format md`** added alongside `text`/`json` for every command. Markdown is
  for humans/PRs; JSON stays the machine contract (carries `schema_version`).
- **Perf smoke test:** a single generous check (full fixture index < 10 s) to
  catch pathological regressions. The product-doc NFR numbers are a measurement
  protocol, not a CI gate.
- **Release pipeline** (`.github/workflows/release.yml`, on `v*` tags): packs
  the **global tool** `RepoContext.Tool` (command `repoctx`) and publishes
  **self-contained** binaries for `linux-x64`, `win-x64`, `osx-arm64` as CI
  artifacts.
- **Grammar trimming:** `TreeSitter.DotNet` ships ~28 native grammars; a
  post-publish MSBuild target (`TrimTreeSitterGrammars`, enabled in the release
  workflow) deletes all but `tree-sitter` core + TypeScript/TSX/JavaScript/C#.
  Verified: the trimmed self-contained binary still indexes and searches. The
  global-tool package is left untrimmed (RID-agnostic; trimming per-RID native
  assets there is not worthwhile for the MVP).

## MVP acceptance (product-doc chapter 12)

Proven automatically (tests/CI): determinism (byte-identical output), no runtime
network (banned-API analyzer + `--network none` CI job), sensitive-file
exclusion (marker never in DB/output), incremental indexing, the ranking
scenario, and the F7 exit-code contract. Prepared for manual runs: the token
benchmark template (`docs/benchmark.md`) and the README (install, quickstart,
agent integration, configuration).
