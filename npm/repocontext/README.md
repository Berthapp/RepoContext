# repocontext-tool

> Local-first, explainable project memory for AI coding agents.

Installs **`repoctx`**, a CLI that deterministically indexes a repository and
gives AI coding agents compact, explainable context: exactly the relevant files,
symbols, tests and relationships — with a machine-readable reason for every hit
and a hard token budget per answer.

It runs entirely offline. **No source code leaves the machine, there is no
telemetry, and no LLM or embedding calls are ever made.** The same query on the
same index always produces byte-identical output.

Supported languages: **TypeScript, TSX, JavaScript, C#**.

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
| Windows x64 | `repocontext-win32-x64` |
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
- [Token-savings methodology](https://github.com/Berthapp/RepoContext/blob/main/docs/token-savings.md)

Licensed under Apache-2.0.

[releases]: https://github.com/Berthapp/RepoContext/releases/latest
