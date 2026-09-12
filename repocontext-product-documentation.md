# RepoContext – Product documentation

**Version:** 2.0 (revised) · **Date:** 2026-07-08 · **Companion document:** `repocontext-mvp-spezifikation.md`

**Key changes from v1:**

1. New core principle "deterministic and explainable" as the central differentiator.
2. Example outputs corrected: structured facts derivable from the index, instead of prose summaries that could not be produced without an LLM.
3. New section "Competition and differentiation" (missing entirely from v1).
4. Monetization reworked: open core, MCP server in the free tier, PR Context Pack as the concrete reason to buy the team tier.
5. Scattered MVP details replaced by a roadmap; technical detail moved into the MVP specification.

> **Language note:** this document was originally written in German and was
> translated to English on 2026-09-12. See the update note in
> [ADR 0003](docs/decisions/0003-source-of-truth.md).

---

## 1. What RepoContext is

> **Local-first, explainable project memory for AI coding agents.**

RepoContext is a local-first, self-hostable tool that indexes the knowledge held in a software repository and serves it to AI coding agents on demand. Agents no longer have to load large parts of a repository into their context; they receive exactly the relevant files, symbols, tests and relationships — with a reason for every hit.

As a result:

* tokens are saved — measurably, see [section 7](#7-token-usage-why-the-cost-drops-and-the-quality-does-not),
* answers get faster,
* agents make fewer wrong assumptions,
* project knowledge is available in a structured form,
* source code stays local by default.

RepoContext is not itself an AI model and causes no token usage of its own.

---

## 2. Problem

AI coding agents routinely work with too much or too little context:

* The agent reads too many files and the prompt grows unnecessarily large.
* High token usage, slow answers.
* A missing overview of the project leads to wrong changes.
* Uncertainty about which data is sent to AI providers.

The larger the repository, the harder it becomes to give an agent exactly the right knowledge — and the more expensive every failed attempt gets.

---

## 3. Solution

RepoContext builds a local, deterministic index of the project: project structure, important files, classes and functions, API routes, dependencies between files, tests, configuration, and README and documentation content.

AI agents query that index over the CLI or MCP:

```
$ repoctx context "change the login logic" --top 4

Context for "change the login logic" (3 term(s)):
  1. src/auth/login.ts        0.6744  source  [L13-19]  ~166 tokens
      reasons: fts, symbol:loginUser, path-name-match, tested-by:src/auth/__tests__/login.test.ts
  2. src/auth/permissions.ts  0.4903  source  [L1-1]    ~129 tokens
      reasons: fts, symbol:Action, imported-by:src/auth/login.ts
  3. src/auth/__tests__/login.test.ts 0.2885 test [L1-18] ~171 tokens
      reasons: fts, test-of:src/auth/login.ts
  4. src/auth/session.ts      0.0920  source  [L17-23]  ~112 tokens
      reasons: imported-by:src/auth/login.ts

Budget: 4 file(s) · ~578 estimated tokens
```

The agent receives compact, justified context instead of a whole repository. Every line carries its reason (`reasons`), its line range and its exact token cost. How much that lowers the bill, and why answer quality does not drop with it, is [section 7](#7-token-usage-why-the-cost-drops-and-the-quality-does-not).

**Important distinction from v1:** the output consists exclusively of facts derivable from the index (files, symbols, edges, tests, reasons). There are no generated prose summaries ("authentication runs over JWT") — that would require an LLM. Prose-like hints, where they exist, come from real sources (README, docstrings) and are emitted as a quote with its location.

---

## 4. Core principles

**1. Local-first.** RepoContext runs locally in the project or on the developer's machine by default. Source code is not automatically sent to external services.

**2. Deterministic and explainable.** The same query on the same index produces the same answer. Every hit carries a machine-readable reason (`reasons`). That makes results reproducible, debuggable and auditable — unlike embedding-based black-box ranking.

**3. No forced AI.** Indexing, search and analysis work without an LLM: filesystem analysis, parsers, symbol analysis, full-text search, dependency mapping, a local database.

**4. Self-hostable.** Teams can run a shared RepoContext server inside their own network.

**5. Agent-friendly.** Interfaces: CLI (universal — any agent with shell access), MCP server, local HTTP API, later CI/CD and IDE integration.

**6. Minimal context.** As little context as possible, as much as necessary — with a hard token budget per answer.

---

## 5. What RepoContext is not

RepoContext is not a replacement for Claude Code, Cursor, Copilot or other agents, and not an AI model of its own. It is a context layer between repository and agent:

```
Repository
    ↓
RepoContext index
    ↓
AI agent
    ↓
LLM
```

---

## 6. Competition and differentiation

*As of July 2026 — refresh before launch, the field moves quickly.*

| Approach | Examples | How RepoContext differs |
|--------|-----------|------------------------|
| Agent-internal search at runtime | Claude Code, Copilot | Works, but still reads many files per task. RepoContext is **complementary**: the pre-digested index is usable from any agent over CLI/MCP. |
| Repo map inside the agent | aider (repo-map) | Conceptually closest (also tree-sitter based), but tied to aider — no standalone layer, no team index. |
| LSP/MCP symbol navigation | Serena | Session-oriented navigation; no persistent, team-wide index, no PR context. |
| Embedding indexing | Cursor codebase index | Black-box ranking, partly cloud-processed, tool-bound. |
| Repo packers | Repomix | The opposite approach: maximal instead of minimal context. |
| Code intelligence platform | Sourcegraph | Powerful, but heavyweight, server-centric, a different price segment. |

**The three-part USP:**

1. **Deterministic and explainable** — every hit with a reason, reproducible, auditable.
2. **Local-first and agent-agnostic** — works offline, with any agent, without vendor lock-in.
3. **Team knowledge layer with PR context** — self-hosted, one central index across several repositories; none of the tools above serves exactly that.

**Honest assessment:** the window of opportunity is real. Agents keep improving their own repository search. That is why the MVP stays small and the core hypothesis is measured early (token benchmark, see MVP specification ch. 13).

---

## 7. Token usage: why the cost drops, and the quality does not

RepoContext itself causes no token usage. Tokens arise only when an agent or LLM processes text:

* RepoContext reads files locally → no tokens
* RepoContext builds and searches the index → no tokens
* RepoContext returns justified excerpts to the agent → those excerpts cost tokens

### The reads are the bill, not the answers

Measured on RepoContext's own repository (task *"improve token budget packing in the context engine"*, exact `o200k_base` counts, ADR 0010): an agent that asks for file paths and then reads the three best files pays **~886 tokens for the answer and ~5,336 for the reads — ~6,222 in total.** The answer is 14 % of the bill.

| Same task, same repository | Tokens |
| --- | ---: |
| Paths, then read the top 3 files | **6,222** |
| `context --detail slices --budget-tokens 2000` (3 excerpts embedded) | **2,110** |
| `context --detail outline --budget-tokens 2000` (7 files surveyed) | **2,151** |
| `outline` of the 3,256-token main file | **1,111** |

Writing shorter answers is a rounding error; **avoiding reads is the saving.** That is exactly what the product is built around:

* **Evidence instead of a reading list.** `--detail slices` puts symbol-aligned source excerpts directly into the answer, `--detail outline` surveys more files at less depth. The follow-up read disappears.
* **Decide before reading.** An `outline` costs roughly a third of the file; `architecture --depth 1` ~300 tokens, `changed` 154 on a clean tree.
* **A ceiling that is measured, not estimated.** `--response-budget-tokens` is checked against the exactly rendered answer, with no exception for the first hit. If nothing useful fits, you get an error with a concrete retry budget — never a partial answer that is billed anyway.
* **Never pay twice.** Every excerpt, symbol and pointer carries a `receipt`. Echo it — or use a `--session`, which costs **zero output tokens** — and the unit is acknowledged instead of delivered again. Measured on the frozen corpus: repeat call **1,916 → 621** tokens, embedded content 756 → 49.
* **Delta instead of re-read.** After an edit, `changed --patch` returns hunks rather than the whole file.
* **Cheap overhead.** The block loaded into *every* prompt is a ~100-token pointer (capped at 150 by a test); the full protocol arrives only when needed. The MCP session costs 1,698 tokens once, with a test gate at 1,700.
* **Cheaper serialization.** `--format md` avoids the JSON escape tax and `--compact` removes duplicate legacy fields: 217,869 instead of 246,297 tokens for identical evidence across 36 tasks (−11.5 %).
* **Budgets in the right tokenizer.** `tokens.profile` scales the stored counts at query time (`claude` ≈ 1.2) and rounds up — a ceiling measured in the wrong tokenizer would not be one.

### Why the quality does not suffer for it

Any tool gets cheaper by delivering less. These properties keep the two apart — they live in the code and its tests, not in intentions:

* **Excerpts are symbol-aligned, not truncated.** Up to three non-overlapping ranges per file, the symbol range first, reconstructed from index chunks — complete declarations instead of halved functions.
* **The graph adds what search alone misses.** Two-hop expansion brings in the test for a file and the importing module; vendor, generated and unrequested fixture/test/doc paths are demoted.
* **Nothing disappears silently.** Every hit carries `reasons`, and `--explain` names omitted candidates together with the constraint that limited them.
* **Reuse never over-claims.** A `receipt` acknowledges exactly one delivered unit, `--known` asserts the whole file (ADR 0015) — otherwise the model would be credited with lines it never saw.
* **Quality is test-gated, not asserted.** 36 frozen retrieval tasks across four repositories, labelled with exact line ranges *before* the corpus was first run; the tests reject any change that loses a previously delivered required file or required line.
* **The proof:** an experimental packer raised file recall to **36/38 (94.7 %)** — past the roadmap target of 90 %. It was **rejected**, because it delivered only a fragment of each file: relevant lines 204 → 115, fully evidenced tasks 9/30 → 3/30. A saving that costs relevant evidence is not a saving.

### What is explicitly not claimed

The measured corpora capture evidence gathering and response cost — **not** a coding agent's success on a real task, and not the total cost of a session. "Reads replaced" credits a read that would probably have happened: an estimate, not a guaranteed lower bound. Under a tight 2,000-token JSON limit the holdout still delivers only 29 of 38 expected file occurrences (76.3 %; Markdown 32/38; without a limit 38/38) — the 90 % target is not met and is documented as open work.

Every repository is different, which is why `repoctx stats` measures your own saving locally (`pricing.inputPerMtok` also shows it in money). Full reasoning, figures and limits: [`docs/cost-and-quality.md`](docs/cost-and-quality.md).

---

## 8. Data protection

RepoContext sends no repository data to external services by default and contains no telemetry in its core.

**Important, honest limitation:** RepoContext can stop its own tool from sending data. It cannot stop an external AI agent from forwarding the excerpts it received to an AI provider.

For maximum safety the recommendation is: local or self-hosted RepoContext, an internal agent, a local or company-owned LLM, and `.repoctxignore` for sensitive files.

In addition: the index itself (`.repoctx/`) contains code excerpts and is a sensitive artifact — `repoctx init` adds it to `.gitignore` automatically. Files matched by `.repoctxignore` and `sensitiveFiles` are indexed neither by content nor by path.

---

## 9. Architecture

**Local variant:**

```
Project
├── .repoctx/
│   ├── index.db          (SQLite + FTS5)
│   └── config-cache
├── repoctx.config.json
└── Source code
```

Flow: `repoctx init` → `repoctx index` → the agent asks for context → RepoContext returns a justified selection.

**Self-hosted team variant (post-MVP):**

```
Git repositories
    ↓
RepoContext worker (CI-triggered)
    ↓
Self-hosted RepoContext server
    ↓
Team index database
    ↓
MCP / API / CI integration
    ↓
AI agents
```

---

## 10. Product scope and roadmap

| Stage | Content | Details |
|-------|--------|---------|
| **MVP** | CLI, local index (incremental), `search` / `related` / `context` / `architecture`, text/json/md formats, privacy features, optional MCP server. Languages: TypeScript/JavaScript + C#. | `repocontext-mvp-spezifikation.md` |
| **v0.2** | MCP server (if dropped from the MVP), watch mode, more languages (Python, Go) | – |
| **v0.3** | `pr-context` + CI integration — the bridge to the team product (ch. 12) | – |
| **v0.4** | Optional **local** embeddings (opt-in, stays offline), Roslyn adapter for real C# reference resolution | – |
| **Team server** | Shared index across several repositories, git integrations, roles and permissions, SSO, audit logs | only after single-user value is proven |

---

## 11. Main components

**CLI** — the entry point and the universal agent interface:

```
repoctx init
repoctx index
repoctx search "authentication"
repoctx related src/services/UserService.cs
repoctx context "change the payment logic"
repoctx architecture
```

**Local index** — structured project data: file paths and types, symbols (classes, functions, interfaces, routes), imports/exports, test links, documentation sections, dependencies between files.

**MCP server** — direct tool access for agents, a thin wrapper over the same query engine:

```
repoctx.search
repoctx.get_context
repoctx.get_related_files
(later: get_architecture, get_tests, get_pr_context)
```

**Self-hosted server (post-MVP)** — for teams: several repositories, a shared index, roles and permissions, SSO, audit logs, CI/CD integration, central rules — without uploading code to a vendor.

---

## 12. Strongest team use case: PR Context Pack (v0.3)

RepoContext automatically produces a context report per pull request — for agents, reviewers and CI:

```
$ repoctx pr-context 123

Changed files:
 - src/auth/login.ts
 - src/auth/session.ts
Affected areas (from the import graph):
 - src/middleware.ts, src/auth/permissions.ts  (direct dependents)
Relevant tests:
 - auth.test.ts, session.test.ts  (linked, not updated in this PR)
Notes (graph facts):
 - session.ts has 4 direct dependents, 1 of them without linked tests
Suggested agent context:
 - 6 files, ~2,100 tokens  →  repoctx context --pr 123
```

The same rule applies here: notes are graph facts ("N dependents, M without tests"), not generated risk assessments in prose. The PR Context Pack is the concrete reason to buy the team tier: it delivers value that neither a single agent nor the local variant can provide.

---

## 13. Tech stack (short form)

.NET 10 (LTS) · SQLite + FTS5 · tree-sitter for TypeScript/JavaScript and C# (Roslyn later as a semantic adapter) · System.CommandLine · the official MCP C# SDK · distribution as a `dotnet global tool` plus self-contained binaries for Windows, macOS and Linux.

Rationale and alternatives: MVP specification, ch. 8.

---

## 14. Example configuration

```json
{
  "include": ["src", "app", "lib", "docs"],
  "exclude": ["node_modules", "dist", "bin", "obj", ".next", ".git"],
  "respectGitignore": true,
  "sensitiveFiles": [".env*", "*.secret.*", "appsettings.Production.json"],
  "indexing": {
    "maxFileSizeKb": 512,
    "includeTests": true,
    "includeDocs": true
  },
  "ranking": {
    "weights": { "fts": 0.4, "symbol": 0.3, "graph": 0.2, "path": 0.1 },
    "synonyms": { "checkout": ["payment", "billing"] }
  }
}
```

`synonyms` maps any vocabulary your team queries with onto the terms the code
actually uses — including terms in another language than the code.

Plus `.repoctxignore` (gitignore syntax):

```
.env
*.secret.json
/private
/certificates
/customer-data
```

---

## 15. Monetization (revised)

**Guiding principle: nothing that drives adoption is paywalled.** Recommended model: open core — the core as open source (Apache-2.0), monetization through team and enterprise features.

| Tier | Audience | Content | Price hypothesis |
|------|------------|--------|-----------------|
| **Free (OSS core)** | individual developers | CLI, local index, `search`/`related`/`context`/`architecture`, **MCP server**, all privacy features | 0 |
| **Pro** *(optional, later)* | power users | local embeddings, Roslyn semantics, multi-repo workspace, extended reports | ~5–10 USD/month — introduce only once demand is visible |
| **Team** | small and mid-sized teams | self-hosted server, shared index, GitHub/GitLab/Azure DevOps integration, CI indexing, **PR Context Packs**, team rules, roles and permissions | ~15–25 USD/user/month |
| **Enterprise** | larger companies | on-prem, SSO, audit logs, custom parsers, security review, support, private deployments | individual |

**Changes from v1, and why:**

* **MCP moved from Pro to Free.** MCP is the main adoption channel for agents; gating it strangles distribution before it starts.
* **Pro cut down sharply and made optional.** Willingness to pay among individual developers is notoriously low; the free tier has to be complete enough that the tool is loved.
* **The PR Context Pack was promoted from "use case" to the concrete team purchase reason.**
* **Prices are hypotheses** (anchor: common developer tools sit at 19–20 USD/user/month) — validate with 5–10 target customers before launch.

**Monetization path:** the free tool is loved by a developer → the developer brings it into the team → a shared index and PR context justify the team licence → compliance requirements (SSO, audit, on-prem) lead to enterprise.

---

## 16. Positioning and messaging

**Category:** context layer / project memory for AI coding agents.

**One-liner:** *Local-first, explainable project memory for AI coding agents.*

**Three core messages:**

1. **Read less, decide better.** The agent gets only what is relevant — with a reason for every hit.
2. **No black box.** Deterministic, reproducible, auditable. No embedding index can do that.
3. **Your code stays with you.** Offline, self-hostable, no telemetry.

**Do not position as:** an AI tool, a Copilot competitor or a code search engine. RepoContext makes existing agents better instead of competing with them.

---

## 17. Elevator pitch

**Short version (one sentence):**

> RepoContext makes repositories AI-ready: a local, explainable index gives coding agents exactly the context they need — without source code leaving the machine.

**Long version:**

> RepoContext is a local-first, self-hostable context layer for AI coding agents. The tool indexes repositories locally — deterministically and without an LLM of its own — and returns exactly the relevant files, symbols and tests on demand, with a traceable reason for every hit. Agents therefore read less, consume fewer tokens and make better decisions, while source code never leaves the machine. For teams, RepoContext is available as a self-hosted server with a shared index across several repositories and automatic context reports per pull request.
