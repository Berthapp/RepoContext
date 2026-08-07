# ADR 0018 — npm distribution, environment-aware integrations, on-demand instructions

- **Status:** accepted
- **Date:** 2026-08-07
- **Milestone:** M10 (no index or JSON schema change; CLI surface additions only)

## Context

Two gaps had the same root cause: RepoContext is a .NET tool, and it was
distributed and integrated as one.

**Reach.** TypeScript, TSX and JavaScript are first-class indexed languages, and
the default configuration excludes `node_modules`, `dist`, `.next`, `.nuxt`,
`.svelte-kit`, `.turbo` and `.angular` — this tool is built for JavaScript
repositories. But the only ways to install it were `dotnet tool install`, a
NuGet `PackageReference` (ADR 0014) and a manual archive download. A TypeScript
team with no .NET on the machine had to be told to install a runtime first,
which is where most of them stopped.

**Instruction cost.** `init --agents` wrote the full usage protocol into
`CLAUDE.md` and `AGENTS.md`. Those files are loaded into **every** prompt of
every session, whether or not the task touches RepoContext at all. A tool whose
entire premise is "every token is a real, measured cost" was charging a fixed
several-hundred-token tax on all work in the repository — including work it
could not help with. Meanwhile the clients teams actually use (Claude Code,
Cursor, Windsurf) all grew a mechanism for exactly this problem: an instruction
file loaded only when its description matches the task.

Plan items I1 (non-destructive `integrate`) and I2 (client-specific templates)
covered part of this. The on-demand angle, the npm distribution and the ambient
session are new.

## Decisions

1. **npm distribution as `repocontext-tool`.** The npm ecosystem is where the
   TypeScript users are, and it can carry a binary without a runtime
   prerequisite. The layout is the one esbuild and swc established: a small
   wrapper package holding a Node launcher, plus one package per platform
   holding that platform's self-contained publish output, declared as
   **optional** dependencies constrained by `os`/`cpu` — so `npm install`
   downloads exactly one payload rather than six.

   - The command stays **`repoctx`**; only the package name differs from it,
     because `repoctx` is taken on npm by an unrelated maintainer.
   - **Name availability is not the same as name acceptability.** npm rejects a
     new package whose name differs from an existing one only by punctuation or
     case. `repocontext` returns 404 on the registry but is refused at publish
     time because `repo-context` exists; `repocontext-cli` is refused for the
     same reason by `repo-context-cli`. The wrapper is therefore
     `repocontext-tool`, which also mirrors the NuGet package
     `RepoContext.Tool`. The platform packages are unaffected — their names
     carry a platform suffix, so they collide with nothing — and they keep the
     `repocontext-` prefix, with one exception below. Check a candidate's
     punctuation-stripped form against the registry before adding any further
     package.
   - **A second, separate filter exists.** `repocontext-win32-x64` was refused
     with "name triggered spam detection" while the five sibling names were
     accepted, and the wrapper published 35 seconds later — so the rule is
     name-specific, not rate- or account-based. A `<name>-win32-x64` package is
     the exact shape a supply-chain attacker uses to hijack another project's
     optional dependencies, which is the likely trigger. That target therefore
     publishes as `repocontext-windows-x64`. Platform package names are
     consequently **opaque registry names**, not values to derive from the
     platform key; `TARGETS` in `platform.js` is the single mapping and a test
     pins the exception so it cannot be "tidied up" later.
   - The launcher `spawnSync`s the binary with `stdio: "inherit"`, so
     `repoctx mcp` keeps working: the MCP stdio transport speaks raw JSON-RPC
     over stdin/stdout and must not be buffered or line-translated by a shim.
   - Resolution failures are diagnosed, not crashed through: an unsupported
     platform, a musl-based Linux (the binaries are self-contained *glibc*
     builds), and a skipped optional dependency each produce a specific message
     naming the recovery, with the .NET tool as the fallback.
   - The release matrix grows `linux-arm64`, `osx-x64` and `win-arm64`. Six
     platforms is the minimum at which `npm install -g` does not fail on an
     ordinary developer machine; both native dependencies
     (`TreeSitter.DotNet`, `SQLitePCLRaw`) ship binaries for all six.
   - `Directory.Build.props` remains the single source of the version. The
     package builder reads `VersionPrefix` and pins every optional dependency
     to exactly that version, so a wrapper can never resolve a payload from a
     different release. CI runs the builder in `--dry-run` mode on every push,
     which exercises the generator without a .NET build.
   - Publishing prefers **trusted publishing** (OIDC), matching the keyless
     NuGet path already used for `RepoContext.Tool`: no long-lived credential
     is stored in the repository, and provenance is attested from the workflow
     identity rather than asserted by a flag. npm registers a trusted publisher
     per package and only for packages that already exist, so `NPM_TOKEN` is
     retained as the documented bootstrap path for a package's first version;
     the publish step selects the path by whether the secret is set, so the
     workflow does not change when the token goes away. `actions/setup-node`
     is used without `registry-url` because that input writes an auth line
     into `.npmrc`, which silently demotes OIDC to the legacy token path.

2. **Progressive disclosure of the usage protocol.** The instruction text is
   split in two, by what it costs:

   - `AgentInstructions.PointerBlock` — always loaded. What the tool is, the one
     call that answers most tasks, and where the rest lives. It is capped by a
     test at 150 `o200k` tokens and asserted to be under a third of the full
     block, so the saving is a gate rather than a claim.
   - `AgentInstructions.Playbook` — loaded on demand. The full protocol:
     escalation rules, evidence reuse, budgets, calibration.

   The playbook reaches an agent by whichever mechanism its client has: a Claude
   Code skill (`.claude/skills/repocontext/SKILL.md`), a Cursor rule with
   `alwaysApply: false`, a Windsurf `model_decision` rule, or — for clients with
   no such mechanism — the new **`repoctx guide`** command, which prints it. A
   client without on-demand loading therefore pays for the protocol once per
   task instead of once per prompt, and only when the agent decided it needed
   the tool.

   The description on those files is the load trigger, so it names situations
   ("locating code, explaining or changing existing behaviour, checking what an
   edit impacts, finding the tests for a file") rather than the tool.

   `--inline-playbook` restores the old behaviour for anyone who prefers the
   full text in the file. Both texts remain constants — no timestamps, no
   repository state — so the ADR 0012 §4 byte-stability invariant holds and a
   re-index never invalidates an agent's prompt-prefix cache.

3. **`repoctx integrate` (plan item I1).** Idempotent, non-destructive, and
   strictly separate from configuration: it never reads or writes
   `repoctx.config.json`. That separation is the reason it exists —
   `init --agents` on an initialized repository requires `--force`, which also
   overwrites the user's configuration, so refreshing instructions used to carry
   a risk that had nothing to do with instructions.

   - Markdown files are maintained through the existing marker pair. Content
     outside the markers is preserved byte-for-byte; `--remove` takes the block
     back out and deletes only a file that RepoContext itself created (nothing
     but whitespace, or nothing but the front matter it wrote).
   - Client configuration files (`.mcp.json`, `.cursor/mcp.json`,
     `.vscode/mcp.json`) are written **only when absent**. An existing one may
     register other servers whose shape RepoContext must not guess at, so it is
     reported as skipped and left alone — the same rule the MSBuild package
     already applies (ADR 0014).
   - Nothing is written outside the repository root. Windsurf's MCP
     registration is machine-global, so that integration ships rules only.
   - `--check` writes nothing and exits **4** on drift. A new exit code rather
     than a reused one, so CI can distinguish "integration is stale" from "the
     tool failed".
   - Detection is presence-based (`.claude/`, `.cursor/`, `.windsurf/`,
     `.github/copilot-instructions.md`, `.vscode/`, `AGENTS.md`) and falls back
     to `AGENTS.md`, the broadest convention — an unwired agent is the case the
     command exists for.

4. **`--detail auto` (plan item I3, partial).** A versioned rule table maps the
   shape of the task onto a detail level: change verbs (`fix`, `implement`,
   `refactor`, …) select slices, survey terms (`where`, `which`,
   `architecture`, …) select outlines, anything else selects slices. Action
   beats survey when both appear — the survey half of such a task is answerable
   from slices, the change half is not answerable from an outline. German terms
   are included because `QueryAnalyzer` already treats German as a first-class
   query language, and configured synonyms reach the table because it runs on
   analyzed terms.

   Resolution happens **before** the engine runs, and the response reports the
   concrete level exactly as if the caller had named it. There is deliberately
   no new wire field: a `detail_reason` in the document would have to be
   included in the Q3 cost oracle as well, or a hard `--response-budget-tokens`
   ceiling would be measured against bytes that are not the ones emitted. The
   saving here is a removed round trip, not a changed contract.

   The budget-fallback half of I3 (retry at a cheaper representation when the
   preferred one does not fit) is **not** implemented and remains open.

5. **`REPOCTX_SESSION` (ambient reuse).** `--session` is the cheapest reuse the
   tool offers — the caller echoes nothing, so it costs no output tokens at all
   — and its only weakness is that it must be remembered on every call. The CLI
   and the MCP `get_context` tool now fall back to the `REPOCTX_SESSION`
   environment variable when no session is passed. An explicit argument always
   wins. Determinism is unaffected: the resolved name selects a caller-supplied
   input file exactly as an explicit `--session` does.

   **It is deliberately not written into generated MCP configuration.** A
   session records what was *delivered*; two agents sharing one session name in
   one repository would let the second receive reuse markers for evidence it
   never saw — precisely the false-cache-hit class ADR 0015 closed. A session
   name must identify one agent instance, which only whoever launches the agent
   can know. Memory tools keep requiring an explicit session, because there the
   name scopes what is stored, not what is reused.

## Consequences

- **No schema change.** `RepoContextInfo.SchemaVersion` stays 3; no JSON field
  was added, removed or given a new meaning.
- **New CLI surface:** `integrate`, `guide`, `--detail auto`, exit code 4.
  `init --agents` now writes the pointer instead of the full protocol, and the
  block it writes is byte-identical to the one `integrate` writes into the same
  files, so the two commands never fight over the region.
- **The MCP session overhead moves, by 24 tokens.** Advertising `auto` in the
  tool description, the `detail` parameter description and the server
  instructions costs 1,339 → 1,363 `o200k` tokens once per MCP session. That is
  the price of the option being discoverable at all, and it is repaid by the
  first avoided corrective call. `docs/eval/baseline.md` and `docs/eval/raw/`
  carry the new figure; the derived `wire MCP` totals move by the same 24. That
  movement is the point of the review, which is the documented condition for
  rewriting the snapshot.
- **Two distributions of the same binary.** The npm platform packages and the
  GitHub release archives are built from one publish step, so they cannot
  diverge. The npm builder fails the release when a runtime identifier in the
  matrix has no publish output, rather than shipping a wrapper that advertises a
  platform package which does not exist.
- **Alpine is not covered.** The self-contained builds link glibc. The launcher
  says so and points at the .NET tool rather than failing with a loader error.
  A `linux-musl-x64` runtime identifier would close this and is not blocked by
  anything except the release matrix.
