# Sample App Architecture

This is a small Next-style application used as a RepoContext test fixture.

## Overview

The app is split into an `app/` directory (routes) and a `src/` directory
(domain logic, components and shared libraries).

## Authentication

Authentication lives under `src/auth`. `login.ts` exposes `loginUser`, which
validates credentials via `permissions.ts` and starts a session through
`session.ts`. Requests are guarded by `src/middleware.ts`, which rejects any
request without an active session.

## Data Flow

1. `app/page.tsx` renders `LoginForm`.
2. `loginUser` creates a `Session`.
3. `middleware.ts` validates the session on subsequent requests.
4. API routes under `app/api` check per-action permissions.
