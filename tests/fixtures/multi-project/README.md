# multi-project fixture

A workspace root that holds two independent projects plus a shared tools
directory — the layout a user gets by opening a folder with several checkouts
in it and running `repoctx init` at the top.

```
apps/web        Node/TypeScript project (package.json)
services/api    .NET project (SampleApi.csproj)
tools/scripts   loose scripts, no project file
```

Negative cases, all expected to stay out of the index:

- `apps/web/dist/` — default exclude, matched by basename at any depth.
- `apps/web/generated/` — nested `.repoctxignore`, scoped to `apps/web`.
