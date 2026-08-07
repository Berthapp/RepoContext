# Architecture Decision Records

Short records of decisions left open by the build prompt. Numbered sequentially;
`0001` is reserved for the M0 parser decision.

- `0001-parser.md` — tree-sitter via TreeSitter.DotNet (M0 decision: GO).
- `0002-sqlite-native-bundle.md` — pin the patched SQLite native bundle.
- `0003-source-of-truth.md` — how "spec chapter N" references are resolved.
- `0004-data-model-and-contracts.md` — M1 SQLite schema, config, JSON contract.
- `0005-symbols.md` — M2 symbol table, route heuristic, split indexing.
- `0006-graph-and-context.md` — M3 edges, related, context pipeline.
- `0007-architecture-formats-release.md` — M4 architecture, --format md, release.
- `0008-mcp-server.md` — M5 MCP server (`repoctx mcp`, stdio, official SDK).
- `0009-token-lean-output.md` — compact JSON, null omission, graph-reason cap.
- `0010-token-frugal-context-protocol.md` — M6: real token counts, outline,
  detail levels, budget packing, known-state dedupe, changed.
- `0011-token-savings-stats.md` — M7: local usage log and the `stats`
  token-savings dashboard.
- `0012-token-optimization-levers.md` — M8: token calibration profiles,
  `--session` known-set, `changed --patch`, `prime`, slice output-lean
  measures (md charging, dedupe, `--strip-comments`), stats money view.
- `0013-agent-memory.md` — M9: agent-authored memory (`memory` command +
  MCP tools, JSONL store, hash-based staleness, context bundle folding).
- `0014-packagereference-distribution.md` — `RepoContext.MSBuild`: the CLI as
  a plain `PackageReference` (no `dotnet tool install`), MSBuild targets
  `RepoCtx` / `RepoCtxInstall` / `RepoCtxShim` / `RepoCtxMcpConfig`, and a
  cross-platform repository-local payload used by generated MCP configuration.
- `0015-state-identity-and-receipts.md` — state fingerprints, per-unit reuse
  receipts, fail-closed freshness, and query-aware evidence selection.
- `0016-exact-budgets-and-cost-semantics.md` — exact CLI/MCP response ceilings,
  distinct projected-read budgets, retry sizing, and explicit cost fields.
- `0017-whole-repository-coverage.md` — scan the whole repository by default
  (every nested project and subfolder), per-directory ignore files, reported
  project detection, a warning for pre-0.8 include roots, UTF-8 console output.
- `0018-npm-distribution-and-environment-integrations.md` — the
  `repocontext-tool` npm package (wrapper + per-platform binaries),
  `repoctx integrate` /
  `repoctx guide`, on-demand instructions via skills and rules, `--detail auto`,
  and the `REPOCTX_SESSION` ambient session.
