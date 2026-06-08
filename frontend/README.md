# Observability Dashboard (frontend)

React + Vite + TypeScript + Tailwind dashboard for the Adaptive Observability platform.

## Develop

```bash
npm install
npm run dev      # http://localhost:5173
npm run build    # type-check (tsc) + production bundle
npm run lint
```

The dashboard talks to the Observability API (default `http://localhost:8080`); override with
`VITE_OBSERVABILITY_API_URL`.

## Demo data (mocks)

So the UI can be built and reviewed without a running backend or a populated DB, the dashboard
ships a deterministic mock layer ([src/lib/mock.ts](src/lib/mock.ts)).

- **On by default in `npm run dev`**, **off in a production build** — a deployed dashboard always
  hits the real API.
- Toggle at runtime via the **"Demo data"** switch at the bottom of the sidebar (persists in
  `localStorage`). Force it with `VITE_USE_MOCKS=true|false`.
- A **"Demo data"** badge shows in the top bar whenever mocks are active.

## Conventions (important for real-data correctness)

- **Catalog is the source of truth.** [src/lib/catalog.ts](src/lib/catalog.ts) mirrors the backend
  event catalog (event names for the Events filter) and defines the error categories.
- **Error category is *derived*, not stored.** `ErrorRecord.error_type` holds the specific
  exception class (e.g. `NullReferenceException`, `TypeError`) — **not** a category. The category
  shown in the Errors filter (Backend / Frontend / Background job) is derived from which fields are
  populated, matching the backend ingestion classifier and the `category` query param on
  `GET /api/dashboard/errors`:
  - `exception_type` set → **backend server error**
  - else `job_name` set → **background job failure**
  - else → **frontend exception**
- **Filters scope by app + env, and the API requires GUIDs.** "Quick view" preset links use the
  app *slug* + env *name* (stable across deployments); the FilterBar resolves those to the real
  GUIDs before querying.
- **UI preferences are per-browser** (`localStorage`): rows-per-page, sidebar/section collapse,
  demo toggle, and saved Quick views. These become per-user once auth lands (Phase 8).

## Layout

- `src/pages/` — Health, Errors, Events, Sessions, Session timeline, Apps
- `src/components/` — Sidebar, FilterBar, Card, Sparkline, shared UI primitives, icons
- `src/lib/` — API client (`api.ts`), filters, catalog, mock data, `usePageSize`

## Tests

Unit tests for the pure logic in `src/lib` run under [vitest](https://vitest.dev):

```bash
npm test          # run once
npm run test:watch
```

Covered (`src/lib/*.test.ts`, 27 tests):
- `errorCategory` — the backend-mirroring classifier (exception_type → server, etc.)
- `resolveRange` — preset windows + custom passthrough
- `buildQuery` — API query-param mapping (keeps `page=0`, omits empties, right param names)
- `usePageSize` — localStorage persistence + clamping
- `mockErrors` / `mockEvents` / `mockSessions` — category/event/errors-only filters, paging, determinism

Component / visual tests are intentionally out of scope while the UI is still evolving — revisit
with a couple of Playwright happy-path smoke tests once it stabilizes. (`tsc` + `eslint` +
`npm run build` remain part of the gate.)
