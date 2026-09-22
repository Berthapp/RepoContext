# ADR 0024 — Treat the checkout as untrusted input

- **Status:** accepted
- **Date:** 2026-09-22
- **Contract:** `include` must stay inside the repository; nothing is read or
  written through a symbolic link that leaves it; `.repoctx/` holds no links

## Context

Agents are routinely pointed at repositories nobody on the team wrote: a
dependency under investigation, a contributor's fork, a reproduction from an
issue. Everything in such a checkout is chosen by its author — including
`repoctx.config.json`, `.gitignore`, and symbolic links, which git checks out
verbatim. A security review on 2026-09-22 reproduced five ways an ordinary
command turned that content against the machine running it:

| Committed by the checkout | Effect before this ADR |
| --- | --- |
| `"include": ["../secret"]`, or an include root that is a link | files outside the repository indexed and served to the agent (and from there to its model provider) |
| `.repoctx/stats.html -> ~/.profile`, `.repoctx/sessions/s.json.tmp -> …` | `stats`, `context --session` overwrote the link target |
| `.repoctx/index.db-wal -> …` | SQLite writes WAL pages into an arbitrary file |
| `CLAUDE.md -> ~/.bashrc`, `.claude -> ~/.claude` | `init --agents`, `integrate` appended to or installed hooks outside the repository |
| one `.gitignore` line `**a**a**a**a**a**a**a**a**a**a**a**b` | `index` (and every build running it) never finished |

## Decision

1. **Include roots stay inside.** Configuration loading rejects an `include`
   entry that is absolute or has a `..` segment. The scanner independently skips
   any root that resolves outside the repository through a link, so a
   configuration built in code cannot escape either. Links below a root were
   already never followed.
2. **The index directory holds no links.** Every store under `.repoctx/`
   (index database and its `-wal`/`-shm`/`-journal` files, usage ledger,
   sessions, context epochs, guard state, memory, dashboard) refuses to open a
   path when `.repoctx/` or any entry below it on the way is a link, dangling
   or not. Replacements stage at `<file>.tmp`, but whatever already sits there
   is removed first (a link is unlinked, never followed) and the staging file
   is created exclusively before the atomic rename.
3. **Managed repository files must resolve inside.** `repoctx.config.json`,
   `.gitignore`, instruction files, skills, rules, MCP registrations and
   `.claude/settings.json` are written only when, with every link resolved, they
   land inside the repository. `..` is applied to the resolved location, not
   the spelling. A link that stays inside (`CLAUDE.md -> AGENTS.md`) keeps
   working, because that setup is common and harmless.
4. **Credentials are never indexed.** A short built-in list — private keys,
   keystores, `.npmrc`/`.pypirc`/`.netrc`/`.git-credentials`/`.pgpass`,
   Terraform state — is excluded in addition to `sensitiveFiles`, after it, so a
   `!` rule in a repository's own configuration cannot re-include them.
5. **Pattern matching is linear.** Ignore globs compile to the non-backtracking
   regex engine. Configured `artifacts.keyPatterns` use it whenever the pattern
   allows; a pattern that needs backtracking keeps its timeout and is disabled
   for the rest of the run after the first one fires.
6. **Terminals get text, not commands.** Query output on an interactive
   terminal has control characters replaced with visible stand-ins, so an escape
   sequence in repository content cannot retitle the window, write the
   clipboard or paint over lines. Redirected output — what agents and scripts
   read — stays byte-identical, so determinism and token accounting are
   unchanged.

A refused write is reported as one line on stderr with exit code `1`, not a
stack trace. Best-effort stores (ledger, sessions, guard state) skip the write
and let the query answer, exactly as for any other I/O failure.

## Consequences

- A deliberately linked `.repoctx/` (for example onto another disk) is now
  refused; replace the link with a directory.
- An `include` that reached a sibling directory (`../shared`) is now a
  configuration error; initialize RepoContext at the common parent instead.
- Files matching the built-in credential list leave the index at the next
  `repoctx index`.
- The configuration hash and every stored analysis are unchanged, so no index
  rebuild is forced and the evaluation goldens do not move.
- Not addressed here: content that is hostile *as text* (prompt injection in a
  README or a comment) reaches the agent as evidence, as it would through any
  file read. RepoContext does not interpret repository text, and flagging it is
  a separate problem.
