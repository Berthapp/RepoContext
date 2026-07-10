<!-- BEGIN RepoContext (managed by `repoctx init`) -->
## Getting repository context with RepoContext

This repository is indexed by RepoContext (`repoctx`), a local-first, offline
context engine. Before reading files broadly, ask it for the relevant ones — it
returns ranked files with a machine-readable reason for every hit and a token budget.

- `repoctx context "<what you are about to do>" --format json` — ranked files, reasons, budget.
- `repoctx related <file> --format json` — a file's imports, dependents and tests.
- `repoctx search "<term>" --symbols --format json` — find a symbol.

Prefer these over reading the whole repository. Re-run `repoctx index` if the
working tree changed.
<!-- END RepoContext -->
