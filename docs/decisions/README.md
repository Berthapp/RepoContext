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
