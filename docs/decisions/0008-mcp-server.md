# ADR 0008 — M5 MCP server

- **Status:** accepted
- **Date:** 2026-07-10
- **Milestone:** M5

## Context

M5 adds the MCP (Model Context Protocol) server so AI agents can call the
RepoContext query engine directly as tools, without shelling out. The product
doc (§11) names the tools `repoctx.search`, `repoctx.get_context` and
`repoctx.get_related_files` and lists `get_architecture`/`get_tests` explicitly
as *later*; §13 mandates the "official MCP C# SDK". The MVP's non-negotiables
still apply: no network at runtime, determinism, one JSON contract with
`schema_version`, and no premature abstraction / DI overkill.

## Decisions

- **Command:** a new `repoctx mcp` subcommand runs a stdio MCP server. It writes
  only MCP protocol messages to stdout and blocks until the client closes stdin
  (or Ctrl+C), then exits `0`. This is additive — no existing command, exit
  code, or JSON contract changes.
- **SDK:** the official SDK, but the lower-level **`ModelContextProtocol.Core`**
  package (v1.4.1), not the full `ModelContextProtocol` meta-package. Core hosts
  the server via `McpServer.Create(StdioServerTransport, McpServerOptions)`
  with **no generic host and no DI container**, which honours the build prompt's
  "no DI overkill" while still being the official SDK. Referenced only by the
  CLI project.
- **No-network holds.** The stdio transport uses stdin/stdout only. The
  banned-API analyzer scans *our* source (not the referenced SDK), so the
  reference is fine; the existing `--network none` CI job runs the MCP
  integration tests unchanged, proving the server needs no network at runtime.
- **Three read-only tools**, matching §11 (architecture/tests deferred, per the
  product doc):
  - `repoctx.search` → `store.Search` (BM25, optional `symbols`),
  - `repoctx.get_context` → `ContextEngine`,
  - `repoctx.get_related_files` → `Related.Query`.
  Each is annotated `ReadOnly`/`Idempotent`/non-`Destructive`/non-`OpenWorld`.
- **One contract.** Every tool returns the **exact JSON** produced by the
  corresponding `--format json` CLI output (same `schema_version`, same
  serializers in `RepoContext.Cli.Output`), as MCP text content. CLI and MCP
  therefore share a single, deterministic contract; an integration test asserts
  byte-equality (modulo line endings).
- **Per-call index resolution.** Handlers resolve the index from the working
  directory via `RepoLayout.Discover` and open a short-lived `IndexStore` per
  call, exactly like the CLI — so a re-index is picked up without restarting the
  server, and no file lock is held between calls.
- **Errors, not exceptions.** The SDK masks thrown exception messages
  ("An error occurred…"). So the "no index", "outside repository", "not indexed"
  and invalid-argument cases return a `CallToolResult { IsError = true }` with a
  human-readable message instead of throwing. This mirrors the CLI's stderr
  messages (the CLI's exit-code channel has no per-call equivalent in MCP).

## Notes

- Tool names keep the product doc's dotted form (`repoctx.search`); the SDK
  accepts it. The server is named `repoctx` with `ServerInstructions` describing
  the deterministic, explainable, local-first contract.
- Tested end-to-end with the SDK's own `McpClient` spawning the real `repoctx`
  binary over stdio (`McpServerTests`): tool discovery, each tool's JSON,
  CLI/MCP parity, and the error paths.
