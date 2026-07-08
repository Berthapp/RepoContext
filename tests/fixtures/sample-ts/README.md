# Sample TS App

A tiny Next-style TypeScript project used as a fixture for RepoContext
integration tests. It intentionally contains authentication logic, API routes,
tests, documentation and a few negative cases (secrets, generated code, vendored
and binary files) that the indexer must handle correctly.

## Structure

- `src/auth` - login, session and permission logic
- `src/lib` - shared crypto and logging helpers
- `src/components` - React components
- `app` - routes and API endpoints
- `docs` - architecture notes
