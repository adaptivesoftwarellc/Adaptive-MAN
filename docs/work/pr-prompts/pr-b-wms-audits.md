# PR B: Phase 7 WMS audits (7.1 + 7.2)

## Branch
`phase-7/wms-audits`

## Goal
Produce read-only audit docs for `WMSSite` (UI) and `WMSAPI` (backend) that enumerate exactly which files change when WMS onboards onto adaptive-observability. Mirrors the existing [`docs/audits/sch-ui.md`](../../audits/sch-ui.md) and [`docs/audits/sch-api.md`](../../audits/sch-api.md) shape.

**This is investigation only.** No code changes to WMS, SCH, or this repo's backend. The deliverable is two `.md` audit files.

## Context

Phase 7 onboards WMS as the second tenant. The plan's "Verified state (snapshot 2026-04-30)" notes flag three material differences from SCH:
- WMSSite is **JavaScript** (not TS) — `jsconfig.json`, no `tsconfig.json`
- WMS uses **MSAL** auth (Entra/AAD) — not custom JWT like SCH
- WMSAPI has **no global exception middleware and no correlation-ID middleware** (zero matches across the repo)

These mean Phase 7 is net-new infrastructure on the WMS side, not a SCH-style port. The audits feed five downstream Phase 7 decisions (7.3 JS/TS, 7.4 MSAL identity, 7.5 correlation-ID, 7.6 exception middleware, 7.8 ErrorBoundary strategy).

## What to investigate

### WMSSite

Repo location: **confirm from the user before starting** (likely `WMSSite` on the same GitHub org; active branch per the plan is `feature/provider-intake-dropdown`).

Read and catalog:
1. `package.json` — framework, bundler, MSAL packages, MUI packages, Axios
2. `src/main.jsx` / `src/index.jsx` / `src/App.jsx` — entry point, router setup, MSAL init, any existing telemetry
3. `src/sections/` and `src/pages/` — full route list. Flag PHI-sensitive routes (intake, provider notes, wound assessment, regional reports) — these are never-record per the plan
4. Any existing `services/analytics.*`, `utils/routeUtils.*`, `services/apiClient.*` — replace, wrap, or coexist?
5. Any existing React error boundaries — list each + scope
6. `.env.example` / Vite config — where would `VITE_OBSERVABILITY_*` env vars land?
7. MSAL identity surface — where is `useMsalAuthentication` / `useAccount` called? This is where `identify()` gets wired.

### WMSAPI

Repo location: **confirm from the user before starting** (likely `WMSAPI`; active branch per the plan is `feature/physician-list-endpoint`).

Read and catalog:
1. `.csproj` files — stack (.NET 8 per the plan), Dapper, EF Core, JWT bearer packages
2. `Program.cs` — middleware pipeline, DI registrations, MapControllers / MapEndpoints
3. Search `**/*Middleware*.cs` and `**/*Exception*.cs` — should be zero per the plan; **re-verify**. If anything has been added since 2026-04-30, note it.
4. Search for `CorrelationId|X-Correlation|correlation_id` — same. Re-verify.
5. All `IHostedService` / `BackgroundService` implementations — `BackgroundProcessingService` is known; **find the rest** (grep for `: BackgroundService` and `: IHostedService`).
6. `services.AddHttpClient(...)` calls — needed for Phase 7.5 correlation-ID propagation (DelegatingHandler injection).
7. Per-controller try/catch blocks that swallow exceptions — these conflict with a global exception middleware; inventory each so Phase 7.6 can reconcile.

## Deliverable

Two `.md` files matching the existing SCH audit shape:
- `docs/audits/wmssite.md`
- `docs/audits/wmsapi.md`

Each should include:

### Files added / modified
A file-by-file table: path, change type (add / modify / replace), one-line description, lines-of-change estimate.

### Env vars + config
List every env var or config key that needs to be added in WMS for the SDK to function (`VITE_OBSERVABILITY_*` for the UI; `AdaptiveObservability:*` config section for the API).

### Library impact
Which packages get added (`@adaptivesoftwarellc/observability-client-js` or `AdaptiveSoftwareLLC.ObservabilityClient`) and any that get removed.

### Open decisions
At the bottom of each doc, surface the relevant decision questions **without making the call**:
- **wmssite.md**: 7.3 (JS vs TS enforcement strategy — trade-offs of each option), 7.4 (MSAL identity rule — direct AAD oid vs hash vs internal id mapping), 7.8 (ErrorBoundary strategy)
- **wmsapi.md**: 7.5 (correlation-ID middleware shape), 7.6 (global exception middleware vs per-controller reconciliation), 7.7 (BG-service wiring per service)

These are human calls. The audit's job is to enumerate the options + trade-offs.

### Cross-references
Each audit should link to the SCH counterpart and flag where WMS differs.

## Scope guards
- No code changes to WMS, SCH, or this repo.
- No decisions made on 7.3 / 7.4 / 7.5 / 7.6 / 7.8 — surface the choices, list trade-offs, let humans decide later when Phase 7 implementation starts.
- Don't copy SCH audit text verbatim — WMS surface area is materially different. Cross-reference where meaningful, write fresh where the platforms diverge.
- If WMS repo access is unavailable, stop and ask the user how to proceed (read-only clone link, ZIP, etc.).

## Expected effort
~1 day. Reading WMSSite + WMSAPI thoroughly is the bulk; the writing follows.
