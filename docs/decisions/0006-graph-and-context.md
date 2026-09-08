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

## September 2026 correction: local resolution and visible uncertainty

The original MVP rules above are historical. Graphs now rebuild only when the
indexed content or analysis producer changes, using stored references and
configuration inside the indexing transaction.

TS/JS resolution supports runtime extension substitution, index files, the
nearest indexed tsconfig/jsconfig, paths/baseUrl and local JSONC `extends`.
Package exports and package-based configuration inheritance are not evaluated.
Missing local targets, missing aliases, unsupported packages, malformed
configuration and cyclic or missing configuration inheritance remain unresolved.
An unsupported inherited configuration prevents alias inference; ordinary
relative imports can still resolve independently of that configuration.

C# references exclude declaration names, comments and strings. Namespace facts
and indexed SDK project references narrow the local type candidates. The
ordinary project layout assumes ownership by the single `.csproj` in the nearest
ancestor directory and follows literal, unconditional `ProjectReference` entries
transitively. Files without an indexed ancestor project retain namespace/name
inference. Multiple owners or reachable duplicate types remain unresolved.

This is a syntax approximation, not MSBuild or Roslyn evaluation. In particular,
SDK default globs can include files inside nested project directories; nearest
project ownership does not model those overlapping compile sets. Custom compile
items, custom default exclusions, conditional or expression-based references,
disabled reference output, and indexed Directory.Build.props/targets that modify
compile/project membership are reported as unsupported. Project configurations
or imported build files absent from the index cannot supply facts. Arbitrary
SDK/imported targets, compiler aliases and semantic type binding remain outside
this resolver's scope.

An unevaluated `Import` alone leaves project scope unknown. In that case,
globally unambiguous namespace/type matches retain the existing syntax-inferred
edges, so shared analyzer/build settings do not erase ordinary dependencies.
Unknown project scope never selects between duplicate type declarations or
ambiguous project owners. Explicit unsupported compile/reference changes in
the project or its literal reference closure still prevent resolution.

`related` and MCP `get_related_files` expose unresolved references from the
queried file through an optional JSON `unresolved` array, with `kind`, `value`,
`line` and `reason`. A line of zero means the stored module reference has no line
evidence. Text and Markdown show the same failures, including when no graph edge
exists. Unknown C# framework/package type names are omitted from diagnostics;
local candidates that are ambiguous or outside project scope are reported.
C# import relations carry `csharp-syntax` in `reasons` and are labelled as syntax
inference in human-readable output. Edges and diagnostics use one database read
snapshot, so live source/config edits appear only after indexing.
