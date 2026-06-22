# adaptive-observability

Internal analytics, error-tracking, and session-timeline platform for Adaptive's
PHI/PII-bearing apps. First tenant is the Wound Management System (WMSSite + WMSAPI),
with further internal apps to onboard after. Ingests telemetry under strict server-side
privacy rules. The event/identity/privacy contracts trace their shape to the original
PostHog Phase 1 catalog, but onboarding tenants instrument net-new against the SDKs.
Monorepo: ASP.NET Core 8 API + React/Vite dashboard + client SDKs.

## Commands
Run each from its own subdirectory (this is a polyglot monorepo, not a JS workspace).

### backend/ (ASP.NET Core 8)
- Restore/build: `dotnet build Observability.sln`
- Run API: `dotnet run --project src/Observability.Api/Observability.Api.csproj` (http://localhost:8080, `/health`)
- Unit tests: `dotnet test tests/Observability.UnitTests`
- Integration tests: `dotnet test tests/Observability.IntegrationTests` (EF Core InMemory — no Docker needed)
- Single test: `dotnet test tests/Observability.UnitTests --filter "FullyQualifiedName~SomeTestName"`

### frontend/ (React + Vite + TS + Tailwind)
- Install: `npm install` • Dev: `npm run dev` (http://localhost:5173)
- Build (also typechecks): `npm run build` • Lint: `npm run lint`
- Test: `npm test` • single: `npm test -- src/lib/foo.test.ts` • watch: `npm run test:watch`

### packages/observability-client-js/ (npm package)
- `npm install` • Build: `npm run build` (tsup) • Lint/typecheck: `npm run lint` (`tsc --noEmit`) • Test: `npm test`

### Full stack
- `docker compose up` — SQL Server + API (:8080) + dashboard (:5173)

## Tech stack
- Backend: C# / .NET 8, minimal-API endpoints, EF Core (SQL Server; InMemory for tests), Worker service
- Frontend: TypeScript 5.6, React 18, Vite 5, React Router 6, TanStack Query 5, Recharts, Tailwind 3
- SDKs: observability-client-js (tsup), observability-client-dotnet
- Package manager: **npm** (lockfile is package-lock.json — do not use pnpm/yarn). Node 20.
- Runtime/CI: GitHub Actions, deploys to Azure App Service via OIDC

## Project structure
- `backend/src/` — `Observability.{Api,Application,Domain,Infrastructure,Worker,Benchmarks}`
  - Dependency direction: Api/Worker → Application + Infrastructure → Domain. **Domain has no project deps.**
  - `Api/Endpoints/` — minimal-API endpoint groups (Ingestion, Dashboard, Session, Admin, Auth, Export, Health)
  - `Application/` — Ingestion, Alerting, Retention logic; `Infrastructure/` — Persistence, Authentication, Migrations
- `frontend/src/` — `pages/`, `components/`, `lib/` (api client, filters, catalog, mock data)
- `packages/` — client SDKs (JS + .NET)
- `docs/` — architecture, privacy-rules, event-catalog, identity-rules, api-contract (read these before changing ingestion/dashboard)
- Do not edit: `frontend/dist/`, `*/bin/`, `*/obj/`, `packages/*/dist/`, `publish/`, `publish.zip`

## Conventions
- Indent: 2 spaces for JS/TS/JSON/YAML/CSS/MD, 4 spaces for C# (`.editorconfig`); LF, final newline.
- C#: `Nullable` enabled, `TreatWarningsAsErrors=true` (except CS1591) — warnings break the build. Fix them.
- Frontend ES modules; model new endpoint groups after `backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs`.
- **Privacy is server-enforced and non-negotiable.** Never store names, emails, raw URLs/query strings,
  request/response bodies, exception *messages*, or stack traces. Unsafe fields are rejected to
  `SafetyViolations`, not dropped. See `docs/privacy-rules.md` before touching ingestion.
- Frontend: error *category* (Backend/Frontend/Background job) is **derived** from populated fields,
  not stored; `src/lib/catalog.ts` mirrors the backend event catalog. See `frontend/README.md`.

## Testing
- Backend: xUnit-style `dotnet test`; integration tests use `WebApplicationFactory<Program>` + EF InMemory.
- Frontend: Vitest; tests live beside source as `src/lib/*.test.ts` (pure-logic only by design).
- Dashboard ships a deterministic mock layer (`src/lib/mock.ts`), on by default in `npm run dev`,
  off in production builds. Toggle with `VITE_USE_MOCKS`.

## Git & PRs
- Branch from `main`; observed naming: `phase-<n>/<short-desc>` (e.g. `phase-10/8-dogfood-sdk`).
- Do **not** add "Co-Authored-By: Claude" / "Generated with Claude Code" trailers or any similar
  attribution to commit messages or PR descriptions.
- Path-filtered CI must be green before merge:
  - `backend.yml` — restore, build (Release), unit + integration tests
  - `frontend.yml` — `npm ci`, `npm run lint`, `npm run build`
  - `sdks.yml` — build + test for each client SDK
- `push` to `main` triggers Dev deploy, then Prod deploy gated on the `prod` GitHub Environment.

<!-- Maintainer note: keep under ~200 lines. Per-area deep rules belong in docs/ or .claude/rules/.
     backend/README.md notes the EnsureCreatedAsync→MigrateAsync switch; Migrations now exist under
     Infrastructure/Migrations, so verify that README guidance before relying on it. -->
