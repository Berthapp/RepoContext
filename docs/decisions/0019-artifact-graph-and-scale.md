# ADR 0019 — The artifact layer: cross-artifact references, scoped queries, and a graph that does not re-read the repository

- **Status:** accepted
- **Date:** 2026-08-20
- **Release:** 0.10.0 (JSON `schema_version` 3 → 4; index schema v5; rebuild required)

## Context

ADR 0017 made RepoContext index *every* file below the root. That fixed
coverage, but coverage is not the same as usefulness. A large repository worked
on by a team of agents typically contains far more than code:

- exported work items (`artifacts/jira/PAY-142.json`),
- exported or hand-written documentation (`artifacts/confluence/*.md`, HTML
  pages),
- requirements and traceability files (`requirements/*.yaml`),
- acceptance criteria as Gherkin (`features/*.feature`),
- API contracts, schemas, configuration.

These files were indexed, but only as anonymous 60-line blocks:

1. **They had no structure.** `outline` returned nothing for a Markdown page, a
   YAML requirements file or a feature file, so the only way to find out what
   was in a 900-line specification was to read all of it — the exact cost this
   tool exists to remove.
2. **They had no links.** The graph knew imports and tests. Nothing connected
   `PAY-142.json` to `refund.ts`, `refund-policy.md` to the code it describes,
   or a requirement to the test that verifies it. A task like "reconcile the
   ticket with the documentation and write the missing tests" — the concrete
   workflow this ADR was written for — had no index support at all; it degraded
   to a repository-wide grep followed by reading whatever came back.
3. **There was no way to look one thing up exactly.** `search` and `context`
   rank by similarity, which is right for a natural-language task and wrong for
   "everything belonging to PAY-142". A key that appears once in prose competes
   with hundreds of fuzzy matches.

Two further problems appear only at size, and both are about cost rather than
capability:

4. **Every index run read the whole working tree twice.** Once to hash for
   change detection, once more in `GraphBuilder`, which re-opened and re-scanned
   every indexed file to recompute edges. An incremental run that changed one
   file still paid a full pass over the repository. C# resolution additionally
   ran a regex over each file's full text and matched the result against every
   declared type.
5. **Every query considered the whole repository.** In a monorepo where each
   agent owns one area, every call ranks and pays for candidates from areas that
   agent will never touch.

## Decisions

### 1. Artifacts get structure symbols

`StructureExtractor` derives symbols from non-code text files by extension:
Markdown/MDX, AsciiDoc and HTML headings; YAML, JSON and JSONL keys (to two
levels, as dotted paths); Gherkin features, scenarios and examples; TOML/INI
sections; SQL objects. Each symbol carries a line range that ends where the next
peer begins, plus the first prose line of its body as a summary.

The extraction is line based and parser-free on purpose. A repository of
exported artifacts contains half-valid YAML, JSON fragments and hand-edited
tables; a strict parser returns nothing for exactly the files an agent most
needs an outline of. Line scanning degrades gracefully — an unparsable region
contributes no anchor — and stays deterministic, which the whole tool depends
on. A per-file cap of 400 symbols bounds what a generated contract can cost.

**Structure symbols deliberately get no full-text chunk of their own.** They are
symbols for the purpose of `outline`; they are not symbols for the purpose of
the symbol *channel* of ranking. Their text is already indexed as part of the
section or block chunk they head, so a second chunk would duplicate content in
the FTS index and let a document heading compete with declarations in the
channel that is supposed to mean "something is defined here". This was not
theoretical: with symbol chunks enabled, `README.md` displaced `src/auth/login.ts`
as the first result for "change the login logic".

### 2. References are extracted once and stored

Schema v5 adds a `refs` table: `(file_id, kind, value, line)`. Six kinds are
extracted while a file's bytes are already in memory for hashing and chunking:

| kind | extracted from | used for |
| --- | --- | --- |
| `import` | TS/JS module specifiers | import edges |
| `type` | type-like identifiers in C# | import edges |
| `path` | repository paths named in prose, links, comments | reference edges, `trace` |
| `key` | work-item keys (`ABC-123`) and configured patterns | `trace`, context seeding |
| `link` | absolute http(s) URLs, normalized | `trace`, context seeding |
| `symbol` | code-shaped names in documents | reference edges, `trace` |

Two shapes are deliberately excluded. `key` matching is uppercase-only and
skips a denylist of standards and encodings, because `UTF-8`, `SHA-256` and
`ISO-8601` are otherwise perfect `ABC-123` matches and would fill the index with
traceable "tickets". `symbol` requires a case hump or an underscore, so a
document mentioning "session" is not linked to every file that declares one.
Path mentions are matched against a case-sensitive extension list, so
`System.Text.Json` stays a namespace.

Storage is bounded: `artifacts.maxRefsPerFile` (default 400) caps each kind, and
the retained subset is chosen by sorted value rather than by first occurrence,
so the cap cannot reshuffle an index between two runs over the same content.

### 3. Keys and links are indexed, not materialized as edges

Files sharing a work-item key are **not** connected pairwise. Ten files carrying
`PAY-142` would cost 90 directed edges, and a repository-wide epic key would be
quadratic. The `refs` table answers "who mentions this" with one indexed lookup
instead, which is what `trace` and context seeding use.

Only `path` and `symbol` references become graph edges (`reference`, directed
from the mentioning file to the mentioned one), capped at 64 per file so a
release-notes document that names two hundred files cannot dominate expansion.

### 4. The graph is rebuilt from the index, not from disk

`GraphBuilder` no longer opens a single repository file. It loads files,
declared types and stored references, then resolves:

- relative TS/JS specifiers against indexed paths (unchanged logic),
- C# type uses against the nearest declaring file (unchanged logic, now fed by
  stored identifiers instead of a fresh regex pass),
- path mentions exactly, else by *unique* path suffix — ambiguity is left
  unresolved, because a wrong edge spends an agent's budget on the wrong file,
- symbol mentions only when the name is declared in exactly one file.

Hashing, the one pass that must still touch every file, now runs in parallel and
writes into a pre-sized array by scan index, so the result is identical to a
sequential run. Unchanged files are never opened a second time.

Type resolution was rewritten alongside it. It scanned every declared type for
every C# file — O(files x types), which only stopped being the second-order
problem once the disk reads were gone; declarations are now looked up by name.

The evaluation golden records the effect: repository bytes read per index run
fall from 16,456 to 8,228 — exactly half, the entire graph pass — on cold,
no-op and one-file-change runs alike. On a generated 14,000-file repository a
no-op index falls from 2.2 s to 1.1 s at half the bytes read, while a cold
index costs 5 % more because it now also extracts references and outlines
documents (`docs/benchmark.md`).

### 5. `trace` answers exact lookups

`repoctx trace <ref>` resolves one term — a work-item key, an absolute link, a
symbol name or a repository path — to every file that declares or mentions it,
with line anchors, file kinds, and what reading those files in full would cost.
It is exact, not fuzzy: `ABC-123` means that work item, not "documents about
ABC". Mixing exact and fuzzy retrieval in one command would make neither
trustworthy; `search` and `context` remain the ranked surfaces.

A key that resolves to nothing returns the keys sharing its prefix, because a
mistyped or not-yet-referenced ticket is a common case and answering it costs a
handful of tokens instead of a failed second query.

### 6. `--path` scopes a query before ranking

`search`, `context` and `trace` accept a repeatable `--path`. Patterns use
SQLite `GLOB` semantics and are evaluated by SQLite itself, so there is exactly
one implementation and an in-memory filter can never disagree with the stored
query. A pattern without wildcards is a prefix: `services/api` selects that path
and everything below it.

Scope binds **graph expansion too**. A neighbour the caller excluded is not a
result, however strongly it is linked — otherwise `--path src` would quietly
return documentation through a reference edge.

### 7. Documents are impacted by changes

`changed` already reported importers and linked tests of a modified file. It now
also reports the documents that name it, with reason `mentions:<path>`. A
specification or ticket describing the code an agent just changed is the first
thing that may have gone stale, and it is the trigger for the reconcile half of
the workflow this ADR serves.

### 8. The size limit reports what it drops

`indexing.maxFileSizeKb` (512 KB) excluded text files silently. Coverage has
been subtractive since ADR 0017, and a subtractive rule is only safe while it
fails visibly: a 900 KB exported specification that never enters the index is,
to an agent, indistinguishable from one that does not exist — and exported
artifacts are exactly what this limit tends to catch. `index` now writes a
warning naming the count and the first few paths, on stderr, exit code
unchanged. Binary files stay silent: they are not candidates in the first place.

### 9. What this costs

The MCP session surface (server instructions plus every tool schema) grew from
1,363 to 1,602 tokens, and the test gate moved from 1,500 to 1,700. That is a
real, once-per-session cost for an eighth tool and a shared scope parameter,
paid against a `trace` call that replaces a repository-wide grep and the reads
that follow it. The gate exists so that growth is a decision rather than an
accident; the exact figure stays in `docs/eval/baseline.md`.

Index size grows with the reference rows. That is the trade the artifact layer
makes: bytes on the local disk, which are cheap, against tokens in a model's
context window, which are not.

## Consequences

- **A full rebuild is required.** Index schema v5, and the parser, graph,
  ranking and evidence producer versions all move, so the first `index` after
  upgrading rebuilds everything. Outstanding receipts from a previous version
  are not honoured — the existing fail-closed path (ADR 0015) already covers
  this.
- **`schema_version` becomes 4.** `related` gains the `references` /
  `referenced_by` relations, `context` gains the `ref:`, `mentions:` and
  `mentioned-by:` reasons, and `trace` is a new document.
- **Relevance is unchanged.** Every metric in `docs/eval/baseline.md` — `r@1`,
  `r@3`, `r@8`, `ndcg@8`, symbol and span recall, density, `gap` — is identical
  to the 0.9 baseline. Core token counts move by a few tokens because
  configuration-derived identity hashes are rendered into responses.
- **`graph files analyzed` changes meaning.** It counted files whose content was
  re-read; it now counts files whose stored references were resolved. In the
  evaluation corpus that is 4 rather than 6, because two files contribute no
  references at all.
- **Fetched artifacts may need re-including.** Tickets and pages pulled into a
  repository are frequently git-ignored, and `respectGitignore` is on by
  default, so they are outside the index until a `.repoctxignore` re-includes
  them (`!artifacts/`). The per-directory ignore stack of ADR 0017 already
  supports this; it is now documented, because this is the layout the artifact
  layer exists for. Sensitive patterns remain non-re-includable.
- **Artifact linking can be turned off.** `artifacts.linkPaths` and
  `artifacts.linkSymbols` disable the two reference-edge sources; structure
  symbols and key/link tracing remain.

## Relation to the plan

This closes the artifact half of the "repository, not just source tree"
direction opened by ADR 0017, and supersedes nothing. The `repoctx doctor`
command proposed by work package Q6 remains open.
