# ADR 0004 — M1 data model, config and output contracts

- **Status:** accepted (surfaced for milestone approval)
- **Date:** 2026-07-08
- **Milestone:** M1

## Context

The build prompt references a "spec chapter 7" data model and "F5/F7"
CLI/JSON contracts and asks that these be confirmed rather than decided
silently. Since there is no external spec (ADR 0003), M1 defines them here.

## SQLite schema (`.repoctx/index.db`, WAL)

- `meta(key TEXT PK, value TEXT)` — `schema_version`, `tool_version`,
  `config_hash`, `indexed_at_utc` (the only timestamp; in stats, not results),
  `file_count`, `chunk_count`.
- `files(id, path UNIQUE, kind, language, size_bytes, line_count, content_hash)`.
- `chunks(id, file_id → files ON DELETE CASCADE, kind, start_line, end_line, heading, content)`.
- `chunks_fts` — FTS5 (`content`, `unicode61 remove_diacritics 2`), `rowid = chunks.id`.

`kind`/`language`/`chunk.kind` are stored lowercase (`source`, `typescript`,
`preamble`, …). A separate `IndexSchema.Version` (currently 1) governs on-disk
rebuilds and is distinct from the JSON `schema_version`.

## Config (`repoctx.config.json`, camelCase)

Defaults follow the product doc chapter 14: `include` = [src, app, lib, docs];
`exclude` = [node_modules, dist, bin, obj, .next, .git]; `respectGitignore` =
true; `sensitiveFiles` = [.env*, *.secret.*, appsettings.Production.json];
`indexing` = { maxFileSizeKb: 512, includeTests: true, includeDocs: true };
`ranking.weights` = { fts .4, symbol .3, graph .2, path .1 } (used from M3).
A SHA-256 of the serialized effective config is stored as `config_hash`.

## Incrementality

Diff by `content_hash` (SHA-256 of file bytes): new → insert, changed → replace
chunks, missing → delete. `--full`, a `config_hash` change, or a schema-version
change force a full rebuild.

## Scanning rules

Include roots, then `exclude` + `.gitignore` (root, if `respectGitignore`) +
`.repoctxignore`, all gitignore-syntax; sensitive files excluded entirely
(never stored as content or path); binaries (extension list + NUL sniff),
oversize files, and symlinks skipped; kind = source/test/doc/config/other.

## JSON output contract (`search --json`)

snake_case, every document carries `schema_version` (= 1). *Serialization
details of the example below (indentation, `"heading": null`) are superseded by
ADR 0009: documents are emitted compact and null-valued optional fields are
omitted.*

```json
{ "schema_version": 1, "command": "search", "query": "login", "count": 1,
  "results": [ { "path": "src/auth/login.ts", "kind": "source", "score": 1.2274,
    "start_line": 1, "end_line": 20, "chunk_kind": "preamble", "heading": null,
    "reasons": ["fts"] } ] }
```

Determinism: results sorted by `score` DESC then `path` ASC (Ordinal); scores are
`-bm25` rounded to 4 decimals. Exit codes per F7 (0/1/2/3): `search`/`index`
without an index → 2; empty/invalid args → 3.

## Consequences

- M2 adds `symbol` chunks and a `symbols` table without breaking this shape.
- If these contracts need to change later, bump `schema_version` /
  `IndexSchema.Version` and note it here.
