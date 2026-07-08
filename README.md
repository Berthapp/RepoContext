# RepoContext

> Local-first, explainable project memory for AI coding agents.

RepoContext is a local-first CLI tool (`repoctx`) that deterministically indexes
software repositories and gives AI coding agents compact, **explainable**
context: exactly the relevant files, symbols, tests and relationships — with a
machine-readable reason for every hit, and a hard token budget per answer.

It runs entirely offline. No source code leaves the machine, there is no
telemetry, and no LLM or embedding calls are ever made.

> **Status:** early development. See `docs/` for the product documentation and
> `CLAUDE.md` for the current milestone and build/test commands. A full user
> README (installation, quickstart, agent integration, configuration) lands in
> milestone M4.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (LTS)

## Build and test

```bash
dotnet build RepoContext.slnx -c Release
dotnet test  RepoContext.slnx -c Release
```

## Commands (surface)

```
repoctx init           # create the .repoctx/ index and config
repoctx index          # build or incrementally update the index
repoctx search <query> # full-text search
repoctx related <file> # imports, tests and dependents of a file
repoctx context <task> # compact, explained context bundle for a task
repoctx architecture   # structure, languages and central files
```

In milestone M-Skeleton every command is a stub; behaviour is implemented
milestone by milestone (M1–M4).

## License

Apache-2.0 (planned; see product documentation).
