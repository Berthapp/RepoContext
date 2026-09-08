<!-- BEGIN RepoContext (managed by `repoctx init`) -->
## RepoContext

This repository is indexed by RepoContext (`repoctx`), a local, offline context
engine built to save you tokens. Prefer it over reading files broadly. Start a
task with one budgeted call and escalate only on a concrete gap:

```
repoctx context "<task>" --ensure-fresh --detail auto --response-budget-tokens 2000 --format md
```

Windows: if PowerShell refuses `repoctx`, call `repoctx.cmd` instead.

For the full protocol — evidence reuse, budgets, outlines, impact, memory — run
`repoctx guide`.
<!-- END RepoContext -->
