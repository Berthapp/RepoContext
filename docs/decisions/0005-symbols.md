# ADR 0005 — M2 symbol model, route heuristic and split indexing

- **Status:** accepted (surfaced for milestone approval)
- **Date:** 2026-07-08
- **Milestone:** M2

## Context

M2 adds symbol extraction (via the tree-sitter parser from ADR 0001) and symbol
search. This extends the data model, so the additions are recorded here (per
ADR 0003). On-disk `IndexSchema.Version` is bumped **1 → 2** (forces a rebuild).

## Decisions

- **`ILanguageParser`** is the single seam (no plugin system). `TreeSitterParser`
  implements it for TS/TSX/JS/C#, caching one `Language`/`Parser`/`Query` per
  language. Queries capture the name node (kind = capture name) and the whole
  declaration node (`@def`) for line range + signature.
- **`symbols` table**: `id, file_id → files ON DELETE CASCADE, name, kind,
  start_line, end_line, signature, doc`. Kinds (lowercase): `class`, `interface`,
  `struct`, `record`, `enum`, `function`, `method`, `property`, `typealias`,
  `route`.
- **Symbol search**: each symbol also produces a `symbol` **chunk** in FTS whose
  content is `name + split-tokens + kind + signature + doc`, with the symbol name
  in `heading`. `search --symbols` restricts matches to these chunks. This keeps
  the JSON contract unchanged (the symbol name rides in the existing `heading`).
- **Identifier splitting** at index time: camelCase / snake_case / kebab / digit
  boundaries with acronym handling (`loginUser` → login, user; `APIController` →
  api, controller). Enables `login` to match `loginUser`.
- **Doc extraction** (text-based, robust to grammar differences): JSDoc `/** */`
  or C# `///` immediately above a declaration; C# `<summary>` inner text is used
  and XML tags stripped.
- **Route heuristic**: TS/JS — exported `GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS`
  functions in `app/**/route.ts(x)` become `route`. C# — methods inside a class
  whose name ends `Controller` or that carries `[ApiController]` become `route`.

## Consequences

- Indexing now parses every supported source file; measured well within budget
  (ADR 0001: ~0.34 ms/file). The integration tests exercise native grammar
  loading, which doubles as the CI parser smoke test.
- M3 uses the `symbols` table for graph edges, test linking and `context`.
