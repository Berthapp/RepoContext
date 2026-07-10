# ADR 0006 — M3 graph, related and the context pipeline

- **Status:** accepted (surfaced for milestone approval)
- **Date:** 2026-07-08
- **Milestone:** M3

## Context

M3 adds the file graph, `related` (F4) and the `context` pipeline (F5, spec
chapter 6). On-disk `IndexSchema.Version` is bumped **2 → 3** (adds `edges`).

## Graph

- **`edges(src_file_id, dst_file_id, kind)`**, kind ∈ {`import`, `test`}.
- **Recomputed in full on every index run** (not incremental). Rationale: edges
  depend on the whole file/symbol set; a full recompute is simple and correct,
  and cheap at MVP scale. Revisit if it becomes a bottleneck.
- **TS/JS imports:** static `import`/re-export specifiers via tree-sitter;
  relative specifiers resolved against the file set trying extension variants
  (`.ts/.tsx/.d.ts/.js/...`) and `…/index.*`. **tsconfig `paths` aliases are not
  resolved** in the MVP (the product doc marks Roslyn/semantic resolution as
  post-MVP; the fixtures use relative imports). Bare specifiers are external and
  ignored.
- **C# imports (name-based):** identifiers in a file are intersected with the
  map of top-level type definitions (class/interface/struct/record/enum) from the
  `symbols` table; each referenced type defined elsewhere yields an edge, with
  name collisions resolved to the **smallest directory distance** (then path
  Ordinal). This is intentionally syntactic; real semantic resolution is a
  post-MVP Roslyn adapter.
- **Test edges:** a test file links to a subject by **name convention**
  (`login.test.ts`→`login.ts`, `__tests__/x`→parent `x`, `FooTests.cs`→`Foo.cs`)
  **or** by an **import edge** from the test to a source file.

## `related` (F4)

Emits `imports`, `imported_by`, `tests`, `tested_by` with a machine-readable
reason per entry. Text and JSON (JSON carries `schema_version`).

## Context pipeline (chapter 6)

1. **Query analysis:** tokenize, drop DE/EN stop words, expand config synonyms.
2. **Candidates:** FTS over content chunks, symbol chunks, and path matches
   (with an exact filename-stem bonus so `login.ts` outranks `LoginForm.tsx` for
   "login").
3. **Graph expansion:** bounded **breadth-first up to 2 hops** with per-hop decay
   (0.5). The spec says "1-hop"; we allow a decayed 2nd hop so a dependency of a
   dependency (`middleware → session ← login`) still surfaces, ranked far below
   the direct hit — which is exactly the documented ranking scenario
   (`login.ts` before `session.ts` before `middleware.ts`).
4. **Scoring:** each signal (fts/symbol/graph/path) is normalized to its max and
   combined with the **config `ranking.weights`**.
5. **Penalty:** vendor/generated files (`*.min.*`, `*.generated.*`, `vendor/`,
   `dist/`) × 0.3.
6. **Diversity:** repeated directories are demoted (0.9ⁿ) deterministically.
7. **Budgeting:** `--top` caps count; `--budget-tokens` drops files once the
   estimate (≈ bytes ÷ 4) is exceeded; `--snippets` attaches the best chunk.
8. **Reasons** are attached to every returned file (required).

Determinism throughout: stable sort (adjusted score DESC, path ASC), scores
rounded to 4 decimals.

## Consequences

- M4 (`architecture`) reuses `edges` for centrality (most-imported files).
