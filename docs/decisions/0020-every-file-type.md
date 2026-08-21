# ADR 0020 — Every file type: declarations without a grammar, structure for the rest

- **Status:** accepted
- **Date:** 2026-08-20
- **Release:** 0.10.0 (no JSON `schema_version` change; index rebuild required)

## Context

ADR 0019 gave documents, tickets and contracts an outline and cross-linked them
with the code. It left a hole that is larger than it looks.

Symbols came from exactly two places: the bundled tree-sitter grammars
(TypeScript, TSX, JavaScript, C#) and the artifact structure extractor
(Markdown, AsciiDoc, HTML, YAML, JSON, Gherkin, TOML/INI, SQL). Everything else
was indexed as anonymous line blocks. Concretely, in a repository that mixes
languages — which is most of them — the following had **no outline, no
symbol-search presence and no way for a document to link to them**:

- **Source in every language without a bundled grammar**: Python, Go, Java,
  Kotlin, Rust, Ruby, PHP, Swift, C/C++, Scala, Dart, shell, PowerShell, and the
  interface languages next to them (`.proto`, `.graphql`).
- **XML in all its forms** — `.csproj`, `.wsdl`, `.xsd`, `.resx`, exported pages.
- **Tabular artifacts** (`.csv`, `.tsv`) — which is what a traceability matrix
  usually is.
- **reStructuredText**, `.properties` and `.conf` settings, `Makefile`,
  `Dockerfile`, Terraform/HCL.

A second, quieter problem: `architecture` and `prime` reported every one of
those files as language `none`, so the language mix of a polyglot repository was
simply wrong.

And a third: the exclusions RepoContext applies itself — binary files, files
over `indexing.maxFileSizeKb` — were invisible, so "everything below the root is
indexed" was a claim the user could not check.

## Decisions

### 1. Declarations are extracted for every source language, grammar or not

`DeclarationExtractor` reads declarations by line for Python, Go, JVM languages
(Java, Kotlin, Scala, Groovy), Rust, Ruby, PHP, Swift, C/C++, Dart, shell,
PowerShell, Lua, Elixir, Perl, R, Protobuf and GraphQL. Names are mapped onto
the existing symbol vocabulary, so an outline of a Go file reads like an outline
of a C# one, and `search --symbols` reaches all of them.

These are **searchable** symbols, unlike the artifact structure symbols of ADR
0019: a Python `def refund_allowed` is a declaration in the same sense a C#
method is, and belongs in the symbol channel.

The patterns are deliberately conservative, because the two failure modes are
not symmetric: a missed declaration costs an agent one extra call, a wrong one
sends it to the wrong file and costs the tokens of reading it. Two consequences
are visible in the tests and intended:

- A Java method must carry a modifier. `void packagePrivate()` is missed rather
  than guessed at, because nothing distinguishes it from a call by one line.
- C and C++ emit only tagged types (`struct`, `class`, `enum`, `union`,
  `namespace`). A one-line C function definition cannot be told apart from a
  prototype or a call often enough to be worth the wrong answers.

Where a language writes several things with one keyword, the rest of the line
decides: Go's `type Session struct` is a struct, `type ID = string` an alias.

### 2. Structure is extracted for the remaining text formats

`StructureExtractor` gains XML (any dialect), reStructuredText, `.properties` /
`.conf`, delimited tables, `Makefile`, `Dockerfile` and HCL/Terraform. Three of
these needed a judgement call rather than a pattern:

- **XML is emitted one level deeper than JSON or YAML.** An XML document wraps
  everything in a root element, so its third level is another format's second —
  and it is the level that carries the interesting items. Elements are labelled
  by their identifying attribute where they have one: `ItemGroup` says nothing,
  `PackageReference Serilog` does.
- **A delimited table is described by its columns, not its rows.** Emitting a
  symbol per row would turn a traceability matrix into thousands of useless
  outline entries; what an agent needs before reading it is what is in it.
- **`.properties` keys are grouped by their prefix.** A hundred
  `spring.datasource.*` entries are one entry in an outline, not a hundred.

reStructuredText assigns heading levels in order of first appearance of the
underline character, which is what the format itself specifies.

Both extractors share one line-scanning core (`StructureAnchors`), so ranges,
summaries, caps and determinism behave identically everywhere.

### 3. Languages are named

`SourceLanguage` gains the languages and formats above, so `architecture` and
`prime` describe a polyglot repository correctly instead of reporting `none`.
Only the original four are *parsed* by a grammar; the rest are recognised for
labelling and dispatch. The label is stored on the file row, so this needs the
same rebuild the parser-version bump already forces.

### 4. C# type resolution is restricted to C# declarations

`GetTypeDefiners` fed C# import-edge resolution with every class-like symbol in
the index. That was harmless while only C# and TypeScript produced them; now
that Python, Java, Go and Dart do, a C# file naming `Config` could be linked to
a Python class that happens to share the name. Only C# type uses are resolved
this way, so only C# declarations belong in the pool.

### 5. What is not indexed is now reported

`repoctx index` prints the exclusions RepoContext applies itself:

```
  skipped: 12 binary  1 over 512 KB  0 unreadable
```

Binary files are the one category the tool cannot describe, and the user is
entitled to know how large it is rather than being told "everything is indexed".
Files that could not be opened at all are counted separately from `unchanged`,
which means *verified* identical. Ignore rules are the user's own decision and
are deliberately not second-guessed here. The oversize warning of ADR 0019 still
names the paths and the fix.

`changed` gained the same honesty: an unreadable file is listed under
`unreadable` rather than absorbed into "index is current", because "I could not
check this" is not the same answer as "this is unchanged". An already-indexed
file that cannot be opened keeps its index row throughout — a moment's lock must
not cost it coverage, and it must not be reported as deleted.

Every file access on these paths is guarded. Before this, a permission-denied
file, a directory removed mid-walk, an unreadable `.gitignore`, or a file locked
between hashing and reading each aborted the entire command — the worst possible
trade in a repository of thousands of files.

Three things are kept apart here, because conflating them is what made this
area churn through several rounds of review:

- **What the scan could not look at**, which callers use to *retain* index rows
  rather than prune them. It carries an internal root sentinel and is never
  published as-is.
- **What is reported**, which names files and directories but never the
  sentinel and never a path the configuration removed from view. An entry that
  cannot even be classified is suppressed when it is sensitive or excluded
  under *either* reading: a trailing-slash pattern (`secrets/`) only matches the
  directory reading, so deciding the two independently would publish the name of
  a path the user hid. The cost is a rare pruned row the next run restores; the
  cost of the alternative is a leaked path, which nothing restores.
- **Ignore rules that could not be read**, which is the one failure here that
  makes the index *bigger*: the directories those rules exclude were walked and
  indexed instead. It gets its own warning naming the file and the fix.

## Consequences

- **A full rebuild is required.** The parser producer version moves, and the
  stored language labels change with it.
- **Indexes get bigger and outlines appear where there were none.** A repository
  of Python or Go services goes from zero symbols to a full skeleton. That is
  the point; `outline` on a file is what replaces reading it.
- **`kind` changes for some files.** Extensions whose declarations are now
  extracted are classified `source` rather than `other` (`.sh`, `.scala`,
  `.dart`, `.lua`, `.proto`, …), and settings formats are classified `config`.
- **Relevance is unchanged.** Every metric in `docs/eval/baseline.md` is
  identical; token counts move by a few because the producer version is part of
  the rendered analysis identity.
- **Coverage is now checkable rather than promised.** `tests/fixtures/polyglot-repo`
  is the claim in miniature — ten files, ten languages and formats, an outline
  asserted for each — so an extractor that regresses fails a test instead of
  quietly returning nothing.

## Limits, stated plainly

- Binary formats are not read. A PDF or DOCX specification is skipped and
  counted, not parsed.
- The line patterns are approximations, not parsers. They will miss declarations
  written unusually (a Java method with no modifier, a C function definition,
  a Python decorator-heavy DSL). The bias is deliberate and documented above.
- A text file whose format none of the extractors knows (`.txt`, a log, an
  unusual config) is still indexed — its content is searchable and its
  references are extracted — it simply has no skeleton to show.
- A path *mention* must carry a lowercase extension to be recognised, so a
  document naming a dot-file (`.editorconfig`) does not create a reference edge
  to it. Loosening that would make every mention of `.NET` a path. `trace` still
  resolves such a file and reports it as indexed.
