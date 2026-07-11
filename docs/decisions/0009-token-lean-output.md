# ADR 0009 — Token-lean output for AI consumers

- **Status:** accepted
- **Date:** 2026-07-11

## Context

RepoContext's purpose is saving AI agents tokens, yet its own responses were
spending them: measured on this repository's index (63 files), a typical
`context` bundle cost ~1,800–2,200 estimated tokens, of which

- **20–32 % was JSON indentation** (`WriteIndented = true` on every
  `--format json` document and, via the shared serializers, on every MCP tool
  result — whitespace that only ever reaches a model, never a human), and
- **up to half was `reasons` strings**: every graph-expanded candidate collects
  one reason *per neighbor edge* (`imported-by:<full path>`, …), uncapped, so a
  hub file carried 13–17 reasons (~560–760 chars each bundle item).
- `search` results also serialized `"heading": null` for every non-symbol hit.

The JSON format has no human consumers to serve: humans get `text` (default)
and `md`; the product doc and the generated agent instructions steer agents to
`--format json` and the MCP tools.

## Decisions

1. **Compact JSON everywhere.** One shared serializer policy
   (`RepoContext.Cli.Output.OutputJson`): no indentation (documents are a
   single line) and `DefaultIgnoreCondition = WhenWritingNull`, so null-valued
   optional fields (`heading`, `snippet`) are omitted instead of serialized.
   Applies to all four `--format json` documents and therefore, unchanged, to
   the MCP tools — ADR 0008's CLI ↔ MCP **byte-equality holds**. Field names,
   value semantics, ordering, determinism (same index + query ⇒ byte-identical
   output) and `schema_version` (= 1) are untouched; only insignificant
   whitespace and null members changed, which any JSON parser reads
   identically. Humans pipe through `jq` or use `text`/`md`.
2. **Graph reasons are capped per item** in the `context` engine — the only
   unbounded reason class. After deduplication the first two graph reasons
   (insertion order: earlier hops from stronger seeds first) are kept; the
   remainder folds into one machine-readable summary reason `graph:+N`
   (regex `^graph:\+[0-9]+$`), inserted directly after the kept graph reasons.
   Non-graph reasons (`fts`, `symbol:<name>`, `path-match`, `path-name-match`,
   `penalty:vendor-or-generated`) occur at most once per item and are never
   dropped, so explainability ("a reason for every hit", product doc) is
   preserved and the full edge list stays available via `repoctx related`.
3. Reason capping happens in `ContextEngine`, not the formatter, so `text` and
   `md` output shrink identically.

## Measured effect (this repository, chars/4 ≈ tokens)

| Query | Before | After | Saved |
| --- | ---: | ---: | ---: |
| `context "add a new output format for the search command"` | ~1,832 tok | ~684 tok | **62 %** |
| `context "fix token budget estimation …" --top 12` | ~2,154 tok | ~1,020 tok | **53 %** |
| `search "IndexStore" --symbols` | ~158 tok | ~107 tok | 32 % |
| `related src/RepoContext.Core/Storage/IndexStore.cs` | ~640 tok | ~445 tok | 30 % |

## Consequences

- Consumers that parsed the JSON keep working; anything that compared raw
  bytes against pre-0009 output (none known — the determinism guarantee is
  per-version) must re-baseline.
- ADR 0004's example output predates this decision; its serialization details
  (indentation, `"heading": null`) are superseded by this ADR.
- If a future consumer needs the dropped edges, it should call
  `repoctx related` rather than widening the cap.
