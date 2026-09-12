# repocontext-tool

> Local-first, explainable project memory for AI coding agents.

Installs **`repoctx`**, a CLI that deterministically indexes a repository and
gives AI coding agents compact, explainable context: exactly the relevant files,
symbols, tests and relationships — with a machine-readable reason for every hit
and a hard token budget per answer.

It runs entirely offline. **No source code leaves the machine, there is no
telemetry, and no LLM or embedding calls are ever made.** The same query on the
same index always produces byte-identical output.

**Parsed with a full grammar:** TypeScript, TSX, JavaScript, C#.
**Outlined by line patterns:** Python, Go, Java, Kotlin, Scala, Groovy, Rust,
Ruby, PHP, Swift, Dart, C, C++, shell, PowerShell, Lua, Elixir, Perl, R,
Protobuf, GraphQL. **Structured as artifacts:** Markdown, YAML, JSON, XML
(including `.csproj`), Gherkin, SQL, Dockerfile, Terraform and more — so
tickets, specs and contracts are searchable next to the code. Everything else
that is text is still indexed, searchable and cross-linked.

## Why it is cheaper — and why the answers do not get worse

**The reads are the bill, not the answers.** Measured on the RepoContext
repository: an agent that asks for file pointers and then opens the top three
files pays ~886 tokens for the answer and **~5,336 for the reads**. Hand it the
evidence instead and the same task is answered in one call for ~2,110 — about a
third — because the follow-up reads never happen.

The savings come from mechanisms you can check, not from returning less:

- **Evidence, not a reading list.** `--detail slices` embeds symbol-aligned
  source spans; `--detail outline` surveys more files at less depth. An
  `outline` costs about a third of reading the file, so the decision to read is
  cheap too.
- **A ceiling that is measured, not estimated.** `--response-budget-tokens` is
  enforced against the exact rendered response, with no first-item exception.
  Nothing fits? You get an error and a retry budget — never a partial answer
  billed anyway.
- **Never pay twice.** Every span, symbol and pointer carries a receipt; echo it
  (or use `--session`, which costs zero output tokens) and that unit is
  acknowledged instead of resent. Measured on the frozen corpus, a repeat call
  drops from 1,916 to 621 tokens.
- **Cheap overhead.** The block loaded into every prompt is a ~100-token
  pointer, test-capped at 150; the full protocol arrives on demand.

And the quality does not quietly go with it: spans are symbol-aligned rather
than truncated, the dependency graph adds the test and the importer that plain
search misses, every hit carries its `reasons`, `--explain` names what was left
out and why, and 36 frozen retrieval tasks across four repositories gate every
change against dropping evidence it used to deliver. An experimental packer that
raised file recall to 94.7 % was rejected because it cut delivered relevant lines
almost in half.

What this does **not** claim: end-to-end agent task success or total session
spend. See [cost and quality][costdoc] for the full argument, the numbers and
the limits — and run `repoctx stats` to measure your own repository.

[costdoc]: https://github.com/Berthapp/RepoContext/blob/main/docs/cost-and-quality.md

## Install

```bash
npm install -g repocontext-tool
repoctx --version
```

Or per repository, so the whole team gets the same pinned version:

```bash
npm install --save-dev repocontext-tool
npx repoctx --version
```

No .NET runtime is required — this package ships a self-contained binary for
your platform.

## Quick start

```bash
repoctx init --agents     # write repoctx.config.json + agent instructions
repoctx index             # build the index (incremental afterwards)
repoctx context "change the login logic" --detail slices --response-budget-tokens 2000 --format md
```

`repoctx integrate` wires the tool into whichever coding agent the repository
already uses (Claude Code, Codex, Cursor, Copilot, Windsurf), including an MCP
server registration:

```bash
repoctx integrate            # detect the environment and write managed blocks
repoctx integrate --check    # CI-friendly drift check, changes nothing
```

## Supported platforms

| Platform | Package |
| --- | --- |
| Linux x64 | `repocontext-linux-x64` |
| Linux arm64 | `repocontext-linux-arm64` |
| macOS arm64 (Apple silicon) | `repocontext-darwin-arm64` |
| macOS x64 (Intel) | `repocontext-darwin-x64` |
| Windows x64 | `repocontext-windows-x64` |
| Windows arm64 | `repocontext-win32-arm64` |

They are optional dependencies, so `npm install` downloads only the one that
matches your machine. Alpine and other musl-based Linux distributions are not
covered by the prebuilt binaries; use the .NET tool there
(`dotnet tool install --global RepoContext.Tool`).

## Other ways to install

- .NET global tool: `dotnet tool install --global RepoContext.Tool`
- NuGet `PackageReference` (for environments that block tool installs):
  `dotnet add <project> package RepoContext.MSBuild`
- Self-contained archives on the [GitHub releases page][releases]

## Links

- [Documentation](https://berthapp.github.io/RepoContext/)
- [Source](https://github.com/Berthapp/RepoContext)
- [Cost and quality](https://github.com/Berthapp/RepoContext/blob/main/docs/cost-and-quality.md)
- [Token-savings methodology](https://github.com/Berthapp/RepoContext/blob/main/docs/token-savings.md)

Licensed under Apache-2.0.

[releases]: https://github.com/Berthapp/RepoContext/releases/latest
