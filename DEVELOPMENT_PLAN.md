# `adaptive-observability` — Development Plan

> Internal analytics, error-tracking, and session-timeline platform. Replaces an already-shipped PostHog Phase 1 integration in SCH with a custom system that onboards multiple internal apps under strict PHI/PII rules.

## Goal

Build a standalone repo that ingests safe events/errors from frontend + backend SDKs, persists them in Azure SQL, surfaces them in a React admin dashboard, and supports per-app onboarding with environment-specific keys and allowlists. Migrate SCH off PostHog onto this platform without losing any signal already captured.

## Scope (MVP)

Custom event ingestion · Custom error ingestion · Strict privacy allowlists · App/environment registration · API keys · React dashboard · Session timeline · React frontend SDK · .NET backend SDK · Azure Key Vault integration · **SCH PostHog→adaptive-observability migration**.

**Deferred (post-MVP, planned):** visual session replay via **rrweb** — designed for in Phase 4 (SDK leaves a slot), implemented in **Phase 9**. Disabled by default; gated on a separate privacy review.

**Deferred (out of scope, no plan):** autocapture, feature flags, A/B testing, funnels, heatmaps, surveys.

## Status

| Phase | State |
|---|---|
| 0 — Foundation & Repo Setup | **Done.** Removed from this doc; see `git log`. |
| 1 — Backend Ingestion MVP | **Done.** Removed from this doc; see `git log`. |
| 2 — Azure Key Vault & Deployment | **Partial.** KV config provider, fail-fast validation, setup docs, and end-to-end `az` CLI runbook shipped (2.2 + 2.5 + runbook). **Dev hosting + MI live**: `obs-api-dev` App Service + `id-observability-dev` user-assigned MI provisioned with OIDC federated credentials; CI deploys on push to main (closes 2.3 for Dev). EF migrations cutover landed (`EnsureCreatedAsync` → `MigrateAsync` with `Initial` migration covering Phase 1+4+5 schema). Outstanding: 2.1 UAT/Prod vaults; 2.3 UAT/Prod App Service instances; 2.4 Dev DB smoke + UAT/Prod DB cutover. |
| 3 — React Dashboard MVP | **Done.** Removed from this doc; see `git log`. |
| 4 — Client SDKs | **Done for MVP.** Both SDKs scaffolded; JS SDK auto-brackets sessions (4.11) and SCH route-normalization audit landed (4.2 closed with a parity test set). Issues 4.7 closed (won't-do) and 4.8 handed to 8.2 (Brandon confirmed 2026-04-30). |
| 5 — Session Timeline | **Hardened.** Sessions schema + ingest + derived timeline + cross-process correlation + UI shipped on `phase-5/session-timeline`. SDK auto-bracket gap closed via Issue 4.11. **5.7 landed (2026-05-22):** full 8-cell benchmark grid run before/after on local Docker MSSQL; `Phase5HardeningIndexes` additive migration ships `Events(ApplicationId, EnvironmentId, SessionId, OccurredAt)` + `Errors(ApplicationId, EnvironmentId, LastCorrelationId)`. p95 stays under 200ms through the 10k-events/session architecture-doc upper bound; 100k cell confirms the documented materialization boundary (see [`docs/perf.md`](docs/perf.md)). **4.11 live-ingest harness PASSED end-to-end against `obs-api-dev` (2026-05-22)** using a public key minted via the Phase 8.9 admin endpoints. Outstanding: 5.5 cross-process correlation verification owned by Phase 6.1; re-run grid against Azure SQL Dev after 2.4 UAT/Prod lands. |
| 6 — SCH Onboarding | Open. **Re-scoped 2026-04-30:** PostHog Phase 1 was never merged from `feature/posthog-implementation` into SCH `dev` or `main` (verified). PostHog migration is dropped; SCH onboards as a fresh integration. PostHog branches retained as scaffolding reference only. |
| 7 — WMS Onboarding | Open. Targets `WMSSite` (UI) + `WMSAPI` (backend), replacing the original `SecondApp_*` placeholders. |
| 8 – 9 | Open. Documented below. |

## Constraints

- **Privacy first.** No patient names, emails, usernames, DOBs, raw URLs, query strings, request/response bodies, exception messages, stack traces, or JWTs. Allowlists enforced server-side at ingestion — unsafe fields are *rejected and logged*, not silently dropped. Rules already validated by the PostHog effort.
- **Azure-native.** ASP.NET Core 8/9, Azure SQL, Azure Key Vault with managed identity in deployed environments. **Azure Blob Storage** added in Phase 9 for replay chunks (not in MVP).
- **No new third-party dependencies without approval.** Phase 9 introduces `rrweb` + `rrweb-player` (MIT) — flagged as a net-new dependency requiring explicit approval at Phase 9 entry.
- **Separate repo** from SCH and other onboarded apps.
- **Contract continuity.** Event names, identity rules, allowed property shapes, and route normalization must match the existing `POSTHOG_EVENT_CATALOG.md` so SCH migration is a swap, not a rewrite.

## Existing assets to leverage (from PostHog Phase 1)

These are **inputs**, not duplicated work. The plan references them throughout.

**SCH_UI (`feature/posthog-implementation`):**
- `sch-ui/src/services/analytics.ts` — typed PostHog wrapper with compile-time event allowlist. **The new FE SDK API surface must match this** so cutover is import-line-only.
- `sch-ui/src/utils/routeUtils.ts` — route + endpoint normalization (strips IDs, UUIDs, tokens; maps to feature areas). Reuse the rules verbatim.
- `sch-ui/src/components/common/ErrorBoundary.tsx` — captures `error_type`, `source`, `component_stack_depth` only. Pattern is correct.
- `sch-ui/src/services/apiClient.ts` — Axios interceptor that emits `api_request_failed` with status_code, correlation_id, endpoint_group, method, is_network_error.
- `sch-ui/src/store/authStore.ts` — `posthog.identify(String(userId))` + `auth_login_success` / `auth_logout` flow.
- `sch-ui/src/main.tsx` — init pattern (autocapture: false, capture_pageview: false, replay disabled in prod, maskAllInputs).
- `sch-ui/src/App.tsx` — RouteTracker, global window error + unhandled rejection capture.

**SCH_API (`feature/posthog-implementation`):**
- `src/SCH.Core/Interfaces/IAnalyticsService.cs` — `Capture()`, `CaptureError()`, `Shutdown()`. **The new BE SDK must implement this interface** so migration is a DI registration swap.
- `src/SCH.Infrastructure/Services/Analytics/PostHogService.cs` — reference implementation; allowlist enrichment, swallows analytics failures.
- `src/SCH.Infrastructure/Services/Analytics/NullAnalyticsService.cs` — no-op pattern for disabled state.
- `src/SCH.Infrastructure/Services/Analytics/AnalyticsIdentity.cs` — distinct ID + route normalization rules.
- `src/SCH.Infrastructure/Services/Analytics/AnalyticsOptions.cs` — config shape (Enabled, HostUrl, ApiKey, Environment, ReleaseSha).
- `Program.cs` — conditional registration; dev-only test endpoints (must not exist in non-Dev).
- `GlobalExceptionMiddleware.cs` — `server_error_occurred` emission on true 500s.
- All 8 background services emit `background_job_failed` from catch blocks.

**Shared:**
- `POSTHOG_EVENT_CATALOG.md` — committed in both SCH repos. **Source of truth for the new platform's initial event catalog.**

**Identity rules (already live, must be preserved):**
- Human users: `String(userId)` (no `user_` prefix, no email/username/displayName)
- API clients: `api_client_{id}`
- Background jobs: `system:background-service`
- Dev test events: `test:dev`

**Phase 1 event set (already in code, must be preserved verbatim):**
`auth_login_success`, `auth_logout`, `page_viewed`, `api_request_failed`, `frontend_exception`, `server_error_occurred`, `background_job_failed`, plus dev-only `posthog_test_event` (renamed for the new platform).

**Deferred PostHog hardening items** (folded into Phase 6.1 as fresh-onboarding prerequisites — they apply to adaptive-observability integration whether PostHog ships or not):
- BG job failure dedup/cooldown (15–30 min window) on SCH_API
- `release_sha` populated in deployed environments
- Dev-only test endpoint (was `/api/dev/posthog-test`) locked to Development only and renamed
- Generic role names audit (no user-specific labels)
- Correlation ID confirmed as true request trace ID end-to-end
- `.env.example` updated with `VITE_OBSERVABILITY_KEY` / `VITE_OBSERVABILITY_HOST` (replaces unmerged `VITE_POSTHOG_*`)
- UAT replay masking audit before any prod replay discussion
- `PostHog.AspNetCore v2.5.0` pre-release dependency: **no longer a risk** — it never reached SCH `dev`/`main`. Branches retained for scaffolding reference only.

**Phase 2 deferred event ideas** (input to future event catalog updates, not part of this plan's MVP):
- SCH_UI: `order_created`, `order_submitted`, `report_generated`, `document_uploaded`
- SCH_API: `order_state_changed`, `claim_submission_failed`, `external_api_error`

## High-Level Architecture

```
Onboarded Apps (SCH_UI, SCH_API, SecondApp_UI, SecondApp_API, ...)
   │  ├── observability-client-js     (page events, FE exceptions, failed API calls, session ctx)
   │  └── observability-client-dotnet (server errors, job failures, correlation IDs, release meta)
   ▼
Observability API  (ingestion, validation, allowlist, dedupe, auth)
   ▼
Azure SQL          (Applications, Environments, Events, Errors, Sessions, ApiKeys, SafetyViolations, ...)
   ▼
React Admin Dashboard (health, error explorer, event explorer, session timeline, onboarding)
```

## Tech Stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core 8/9 + EF Core |
| Database | Azure SQL |
| Secrets | Azure Key Vault + managed identity |
| Hosting | Azure App Service or Container Apps |
| Frontend | React + TypeScript + Vite |
| Frontend libs | React Router, TanStack Query, Recharts, Tailwind, shadcn/ui |
| FE SDK | `packages/observability-client-js` (TS) — API shape mirrors SCH_UI's `analytics.ts`; lazy `replay/` submodule (Phase 9) |
| BE SDK | `packages/observability-client-dotnet` — implements SCH's `IAnalyticsService` (replay is FE-only; BE contract unchanged) |
| Replay (Phase 9) | `rrweb` (record) + `rrweb-player` (playback), MIT — gated on approval; off by default |
| Replay storage (Phase 9) | Azure Blob Storage (chunks) + Azure SQL metadata; **never** Azure SQL for chunk bodies |

## Repo Structure

```
adaptive-observability/
├── README.md
├── DEVELOPMENT_PLAN.md
├── docs/
│   ├── architecture.md
│   ├── privacy-rules.md
│   ├── event-catalog.md          (seeded from POSTHOG_EVENT_CATALOG.md)
│   ├── identity-rules.md
│   ├── route-normalization.md
│   ├── onboarding-guide.md
│   ├── azure-key-vault-setup.md
│   ├── api-contract.md
│   └── migration/posthog-to-adaptive.md
├── backend/
│   ├── src/
│   │   ├── Observability.Api/
│   │   ├── Observability.Application/
│   │   ├── Observability.Domain/
│   │   ├── Observability.Infrastructure/
│   │   └── Observability.Worker/
│   ├── tests/
│   │   ├── Observability.UnitTests/
│   │   └── Observability.IntegrationTests/
│   └── Dockerfile
├── frontend/
│   ├── src/{app,pages,components,services,hooks,types}/
│   └── Dockerfile
├── packages/
│   ├── observability-client-js/
│   └── observability-client-dotnet/
├── docker-compose.yml
└── .github/workflows/
```

---

# Phases

Each phase has a **Goal**, **Exit criteria**, and **Issues** ready to file in GitHub. Each issue follows:

```
### Title
**Description:** ...
**Acceptance criteria:**
- [ ] ...
**Investigation questions:**
- ...
```

---

## Phase 0 — Foundation & Repo Setup

**Status: Done.** Repo scaffolding, docs, docker-compose, CI, and `/health` endpoint are committed. See `git log` for details.

---

## Phase 1 — Backend Ingestion MVP

**Status: Done.** Domain entities, ingestion endpoints (`/api/ingest/events`, `/api/ingest/errors`), API key auth, allowlist validator, `SafetyViolations` write path, correlation-ID middleware, dev smoke test, and integration tests are committed. See `git log` for details.

---

## Phase 2 — Azure Key Vault & Deployment Setup

**Goal:** Deployed Observability API loads secrets from Key Vault via managed identity. No secrets in code or app settings.

**Exit criteria:** Backend in Azure (App Service or Container Apps) connects to Azure SQL using a connection string sourced from Key Vault, with no plaintext secrets in any committed file.

**Done in this phase already:**
- Issue 2.2 (KV config provider with fail-fast) — `backend/src/Observability.Api/Configuration/KeyVaultConfiguration.cs`.
- Issue 2.5 (`docs/azure-key-vault-setup.md`) — provisioning steps + rotation runbooks.
- Dev portion of 2.1 — `AdaptiveToolsKeyVault` (centralus, RBAC) holds four placeholder secrets tagged `purpose=adaptive-observability`. UAT/Prod vaults are not yet provisioned.
- End-to-end `az` CLI runbook ([`docs/azure-provisioning-runbook.md`](docs/azure-provisioning-runbook.md)) covering Dev KV, user-assigned MI, App Service Linux on a shared plan, ObservabilityDev DB with public-network + firewall, MI SQL grant, and the real connection string in KV.
- **Issue 2.3 closed for Dev** — `obs-api-dev` App Service running on shared plan in `AdaptiveTools` RG; user-assigned MI `id-observability-dev` attached with `Key Vault Secrets User` on the Dev vault. CI workflow in `.github/workflows/backend.yml` deploys via OIDC federated credentials on push to main (no GitHub secrets stored), stamps `RELEASE_SHA`, and smokes `/health` post-deploy.
- **Issue 2.4 partial** — EF migrations cutover landed: `EnsureCreatedAsync` replaced with `MigrateAsync` (relational-only guard preserves InMemory tests), `Initial` migration generated covering the Phase 1+4+5 schema, and a design-time DbContext factory committed for tooling. Dev DB provisioning + ingestion smoke still owed.

### Issue 2.1 — Provision UAT and Prod vaults

**Description:** Dev shares the existing `AdaptiveToolsKeyVault`. UAT and Prod still need dedicated `kv-observability-uat` and `kv-observability-prod` for blast-radius isolation. Provision alongside their respective hosting envs (no point standing up an empty vault).

**Decisions made (Brandon, 2026-04-30):**
- **IaC tool:** stay on `az` CLI scripts.
- **Dev vault:** Brandon will provision a fresh dedicated Key Vault for adaptive-observability rather than continuing to share `AdaptiveToolsKeyVault`. He owns the provisioning.

**Acceptance criteria:**
- [ ] `kv-observability-uat`, `kv-observability-prod` provisioned in their App Service's region
- [ ] Soft-delete on both; purge protection on prod
- [ ] RBAC-only (no access policies); App Service MI granted `Key Vault Secrets User`, scoped to that vault only

### Issue 2.3 — Hosting environment + managed identity for the Observability API

**Description:** The deployed API needs a hosting environment whose system-assigned managed identity has `Key Vault Secrets User` on its same-environment vault.

**Investigation findings (subscription `Adaptive Subscription`, snapshot 2026-04-30):**
- No App Services, App Service Plans, Function Apps, or Container Apps exist.
- `Microsoft.App` resource provider is **not registered**, so Container Apps requires an extra `az provider register -n Microsoft.App` step.
- One resource group exists (`AdaptiveTools`, centralus). Either colocate or spin up `rg-observability-{env}`.

**Recommended path:** App Service Linux + user-assigned MI in `centralus` (matches SQL + KV regions). Container Apps was ruled out below.

**Decisions made (Brandon, 2026-04-30 / 2026-05-02):**
- **Hosting platform:** App Service Linux. Brandon owns App Service provisioning so it's wired to adaptive-email login.
- **Resource group:** colocate under existing `AdaptiveTools` RG.
- **Identity flavor:** user-assigned managed identity.
- **App Service Plan:** reuse a single shared plan; provision a new App Service *instance* per environment (Dev first). Brandon owns plan provisioning.
- **SKU / app name / slot strategy:** Brandon picks at provision time. Plan-level reuse means Dev shares whatever SKU the plan ships with.

**Acceptance criteria (Dev — closed):**
- [x] App Service Plan provisioned, hosting Dev App Service instance `obs-api-dev`
- [x] User-assigned MI `id-observability-dev` created and attached to the App Service
- [x] MI granted `Key Vault Secrets User` on the Dev vault, scoped to that vault only
- [x] `KeyVault__Uri` app setting points at the Dev vault
- [x] `/health` returns 200 from the deployed API; KV-backed config resolves on startup (verified by CI smoke after `5bd404c`)

**Acceptance criteria (UAT/Prod — open):**
- [ ] Provision UAT and Prod App Service instances on the shared plan
- [ ] User-assigned MIs (or environment-scoped federated credentials) wired with `Key Vault Secrets User` on the respective vaults
- [ ] CI deploy job extended to UAT/Prod with appropriate gating

### Issue 2.4 — Move database secret to Key Vault

**Description:** Replace the placeholder `ObservabilityDbConnection` in Key Vault with a real connection string and have the deployed API connect to Azure SQL through it. Read-only investigation surfaced complications the original plan didn't anticipate.

**Investigation findings (snapshot 2026-04-30):**
- One Azure SQL server: `adaptivetoolssql` (centralus). AAD admin: `brandon@adaptivesoftwarellc.com`.
- **SQL auth is disabled** (`azureAdOnlyAuthentication=true`). Username/password connection strings will not work — the deployed API must auth via Managed Identity (`Authentication=Active Directory Default` in the connection string).
- **Public network access is disabled** on the server. The App Service must reach SQL via VNet integration + a private endpoint, or the server's public-access posture must be reversed (regression of an explicit hardening decision).
- One paused database (`MaintenanceDB`, GP_S_Gen5_1 serverless). No `Observability*` database exists.
- The existing `DependencyInjection.cs` uses `UseSqlServer(connectionString)` which already handles `Authentication=Active Directory Default` — **no code change required** for MI auth.

**Path A (recommended):** Reuse `adaptivetoolssql` for Dev; create a new `ObservabilityDev` database; connect via MI through VNet-integrated App Service + private endpoint.

**Path B:** Stand up a dedicated `sql-observability-{env}` server. Cleaner isolation, more cost, more setup. Probably right for Prod; overkill for Dev.

**Acceptance criteria:**
- [ ] `ObservabilityDev` database created (Path A) or new SQL server + db (Path B)
- [ ] App Service can reach SQL (VNet integration + private endpoint, or alternative)
- [ ] App Service MI granted DB access via `CREATE USER [<app-name>] FROM EXTERNAL PROVIDER` + `db_datareader` / `db_datawriter` / `db_ddladmin`
- [ ] `ObservabilityDbConnection` secret in KV holds the real passwordless connection string
- [ ] `appsettings.*.json` in deployed envs contain no connection string
- [ ] Deployed API connects; ingestion smoke test writes a row

**Decisions made (Brandon, 2026-04-30 / 2026-05-02):**
- **Server topology:** Path A — reuse `adaptivetoolssql`; new `ObservabilityDev` database for Dev (and per-env DBs for UAT/Prod when those land).
- **Network access:** Re-enable public network access on `adaptivetoolssql` + add a firewall rule for the App Service outbound IPs. **Note:** this reverses the prior "public access disabled" hardening — App Service outbound IPs change on plan scale events, so this option carries a small ongoing maintenance cost (firewall rule must be re-synced if the plan changes). Recorded for visibility; revisit at UAT/Prod if posture concerns surface.
- **Human dependency:** Brandon will run the `CREATE USER … FROM EXTERNAL PROVIDER` T-SQL when the App Service MI is ready.
- **Migration strategy:** Generate `dotnet ef migrations add Initial`, switch `EnsureCreatedAsync` → `MigrateAsync` as part of this issue (before the first non-Dev deploy). **Shipped in commit `75ef382` — `Initial` migration covers Phase 1+4+5 schema; `MigrateAsync` guards InMemory tests via the relational provider check; design-time factory committed for tooling.**

**Decisions still needed:**
- **Database SKU for `ObservabilityDev`:** match `MaintenanceDB` shape (`GP_S_Gen5_1` serverless, ~$5–15/mo idle) or fixed-capacity? Default to serverless unless told otherwise.

---

## Phase 3 — React Dashboard MVP

**Status: Done.** Dashboard shell, persistent app/env/date filter, health page with cards + sparklines, errors table + detail drawer, event explorer with JSON viewer + CSV export, sessions placeholder, and admin/apps page are committed. Backend `/api/apps` and `/api/dashboard/*` endpoints back the UI. Auth is a placeholder until Phase 8. See `git log` for details.

---

## Phase 4 — Client SDKs

**Goal:** SDKs whose API surfaces match SCH's existing `analytics.ts` and `IAnalyticsService` so SCH migration is mechanical, and so future apps onboard without custom tracking code.

**Exit criteria:** Both SDKs versioned and documented. A drop-in replacement PR in SCH (Phase 6) changes only imports, DI registration, and config — not call sites.

**Done in this phase already (on `phase-4/client-sdks`):**
- 4.1 — `observability-client-js` core API: `init`, `identify`, `track`, `capturePageView`, `captureException`, `captureFailedRequest`. Compile-time event allowlist via TS unions, sessionStorage session id, no-op-if-not-initialized. **Decision:** rewrote from spec rather than copying `analytics.ts` so the SDK has zero SCH-internal dependencies.
- 4.3 — Axios interceptor + native `fetch` wrapper. Opt-in.
- 4.4 — React error boundary that captures `error_type` / `source` / `component_stack_depth` only.
- 4.5 — Batched transport with size/interval flush, exponential backoff + jitter, all errors swallowed. Dev-only warnings gated by an `init({ debug })` flag.
- 4.6 — `AdaptiveObservabilityService : IAnalyticsService`, `AddAdaptiveObservability(...)` DI extension, background `Channel<T>`, never throws into host. **Decision:** the SDK ships its own `IAnalyticsService` interface (under `Adaptive.ObservabilityClient`); SCH adopters delete `SCH.Core.Interfaces.IAnalyticsService` and update `using` statements.
- 4.7 — Backend `RouteNormalizer.Normalize(path)` + `EndpointGroup(...)` + `NormalizeFromContext(HttpContext)`. **Caveat below: the `RouteData`/endpoint-template path was dropped in favor of `Request.Path` because endpoint-metadata reflection is fragile across MVC and Minimal APIs.**
- 4.8 — `BackgroundJobFailures` sidecar table with `LastSuppressedAt` + window-aware upsert; integration test confirms 100 identical failures collapse to one incident with `count=100`. **Caveat below: window is currently a static 15-minute default; per-app override is deferred to Phase 8.2 hardening.**
- 4.9 — Replay slot: `InitOptions.replay` shape, `IReplayAdapter` interface, default no-op adapter, no rrweb dependency. Unit test confirms `replay.enabled: true` with the no-op adapter is a no-op, not a throw.
- 4.10 — SDK READMEs (`packages/observability-client-js/README.md`, `packages/observability-client-dotnet/README.md`) with under-50-LOC quickstarts; migration cheatsheet at `docs/migration/posthog-to-adaptive.md`.

### Issue 4.2 — FE route normalization: validate against SCH fixture set

**Status:** **closed with an audit.** SCH_UI's `routeUtils.ts` (commit `cf7f65d`) vendored into [`packages/observability-client-js/src/__tests__/sch-fixtures/`](packages/observability-client-js/src/__tests__/sch-fixtures/) as a read-only reference. New [`schParity.test.ts`](packages/observability-client-js/src/__tests__/schParity.test.ts) runs 50 cases against a realistic SCH path set (drawn from `Layout.tsx` + `Reports.tsx`) and asserts both byte parity on static paths and the two intentional divergences below.

The audit surfaced two real bugs in SCH_UI's normalizer that Adaptive-MAN's implementation avoids:
1. SCH's `/[A-Za-z0-9_-]{20,}/` regex over-matches long literal segments (e.g. `/coordinator-dashboard` → `/:token`). Adaptive-MAN's per-segment check correctly leaves them literal.
2. SCH applies `/\d+/ → :id` before its UUID regex, which strips a UUID's leading digit and prevents the UUID rule from matching. Adaptive-MAN's whole-segment UUID check is unaffected.

These are improvements, not regressions — SCH cutover will see better normalization on those routes. The pinned tests will surface drift if anyone changes either side.

**Acceptance criteria:**
- [x] SCH_UI route rules vendored into `packages/observability-client-js/src/__tests__/sch-fixtures/`
- [x] Realistic SCH path set tested against this normalizer; divergences are intentional improvements, documented inline
- [ ] (Folded into 6.3) Extend `FEATURE_AREA_RULES` or expose a runtime `featureAreaMap` option so SCH ships its richer feature-area map without forking the SDK

### Issue 4.7 — `RouteData`-aware path normalization

**Status:** **Closed as won't-do** (Brandon confirmed 2026-04-30). Path-based fallback (`RouteNormalizer.NormalizeFromContext` reads `Request.Path`) covers Minimal APIs cleanly and is sufficient for the apps in scope. Endpoint-metadata reflection differs between MVC and Minimal APIs and silently breaks normalization without throwing — not worth re-introducing without a concrete MVC catch-all use case. Revisit only if a future onboarded app needs it.

### Issue 4.8 — Per-app BG dedup window

**Status:** **Per-app override handed to Phase 8.2** (Brandon confirmed 2026-04-30). The dedup table + window logic + 100-failure integration test ship as-is with a static 15-minute default; the per-app override scope is folded into 8.2's "hardens (per-app override, audit of suppressed-vs-incident counts)" so we don't fragment the work.

### Issue 4.11 — JS SDK auto-bracket sessions (Phase 5 integration gap)

**Status:** **Implemented** (`packages/observability-client-js/src/sessionBracket.ts` + wiring in `index.ts`). 7 new vitest cases cover first-call bracketing across `track`/`capturePageView`/`captureException`, idempotency, `trackSessions: false` opt-out, `shutdown()` end-call, and `reset()` re-bracketing.

**Implementation notes:**
- `beforeunload` uses `fetch({ keepalive: true })` rather than `navigator.sendBeacon` because the api-key middleware reads `X-Observability-Key` from request headers and `sendBeacon` cannot set custom headers. Modern browsers complete keepalive fetches across navigation just like sendBeacon would.
- `init({ trackSessions: false })` is the documented opt-out for hosts that bracket sessions manually.

**Open acceptance criteria:**
- [ ] Integration test that runs the JS SDK against the real ingestion API and confirms a `Sessions` row appears with `started_at` and `last_seen_at` populated. (Deferred to Phase 6 cutover prep where a live ingest API exists.)

**Resolved questions:**
- **.NET SDK session bracketing:** **FE-only** (Brandon confirmed 2026-04-30). Server-side telemetry uses `system:*` distinct ids and rarely benefits from session timelines.

---

## Phase 5 — Session Timeline

**Goal:** Per-session ordered timeline of events/errors/API failures in the dashboard. Replay-style debugging *without* recording screens.

**Exit criteria:** Clicking a session shows an ordered timeline including correlated backend errors.

**Done in this phase already (on `phase-5/session-timeline`):**
- 5.1 — `Sessions` table with `(ApplicationId, EnvironmentId, SessionId)` unique index and `(LastSeenAt)` index. **Decision (see 5.2 below):** no `SessionEvents` materialized table — timeline is derived at query time from `Events` + `Errors`.
- 5.3 — `POST /api/ingest/sessions/start` and `/end` under the api-key-protected ingest group; idempotent. A duplicate `/start` updates the existing row; an orphan `/end` (no prior `/start`) is dropped silently and the endpoint still returns 202 — previously inserted a malformed closing-only row.
- 5.4 — `GET /api/sessions/{sessionId}/timeline` returns ordered entries tagged `event` | `error`; `is_api_failure` boolean on event entries flags `api_request_failed`. Each entry carries its `correlation_id`. The cross-process error join chunks correlation ids in batches of 1,000 to stay under SQL Server's ~2,100-parameter IN-clause limit, with a regression test that exercises the chunked path.
- 5.5 — Cross-process correlation: backend errors that share a `CorrelationId` with any event in the session surface inline, tagged `source: "cross_process"`. The session row stamps `HasError = true` whenever any error ingestion arrives with a session id.
- 5.6 — Session timeline UI: vertical timeline with type-coded markers (event / api failure / FE error / BE cross-process error), errors-only toggle, sticky details drawer with raw JSON.

### Issue 5.2 — Spike PR for derived vs materialized

**Status:** the *decision* is recorded in [`docs/architecture.md`](docs/architecture.md) (derived for MVP, revisit when per-session entry counts push past ~10k). Full 8-cell grid now captured in [`docs/perf.md`](docs/perf.md) (2026-05-22) with before/after numbers for the indexes shipped under 5.7. Verdict: derived holds through 10k events/session with the new indexes; 100k-cell confirms the materialization boundary.

**Acceptance criteria:**
- [x] Benchmark spike with seeded synthetic data; latency results recorded in `docs/perf.md`
- [x] Full 8-cell grid run (including the deferred 10k and 100k target-event cells)
- [ ] Re-run grid against Azure SQL `ObservabilityDev` after Phase 2.4 lands
- [ ] Confirm the derived approach holds at ingestion volumes from real onboarded apps

### Issue 5.5 — End-to-end correlation id propagation (cross-link to Phase 6)

**Status:** the join works correctly when both processes set the same `X-Correlation-Id`. SCH currently propagates correlation ids end-to-end *in PostHog code* but this has not been independently verified for the new platform's ingestion path. Already listed as a Phase 6.1 prereq; cross-linked here so the Phase 5 surface flags it.

**Acceptance criteria:**
- [ ] (Owned by Phase 6.1) Trace a single SCH UAT request from FE → BE → ingestion and confirm the same correlation id lands on both the FE `api_request_failed` event and the BE `server_error_occurred` error.

### Issue 5.7 — Phase 5 hardening (unblocked grid cells + index review)

**Description:** Close the actionable Phase 5 work that no longer depends on Phase 2.4 or Phase 6. Two strands:

1. **Run the deferred benchmark grid cells.** The 10k and 100k target-events rows in [`docs/perf.md`](docs/perf.md) are the materialization-breakeven test; running them locally against Docker MSSQL is cheap and decides whether the derived approach needs the index call-outs below or actual materialization.
2. **Index review surfaced by the captured 5.2 cells.** Two non-covered predicate columns were flagged during the spike but not tracked as work:
   - `Errors.LastCorrelationId` — the cross-process join in [`SessionTimelineQuery`](backend/src/Observability.Infrastructure/Sessions/SessionTimelineQuery.cs) filters on it; current `Errors` indexes don't cover it. Invisible at today's Error-table sizes; projected scan once a real onboarded app produces sustained error volume.
   - `Events(ApplicationId, EnvironmentId, SessionId)` — the per-session events scan filters on `SessionId` but the index is keyed on `CreatedAt`, forcing key lookups. Only matters above ~10k events/session; the 100k grid cell is the test.
3. **Close the deferred 4.11 live-ingestion test now that `obs-api-dev` exists.** Previously punted to Phase 6 cutover prep because no live ingestion API was reachable; that constraint is gone as of `5bd404c`.

**Acceptance criteria:**
- [x] `dotnet run -c Release --project backend/src/Observability.Benchmarks -- --grid` executed; the four deferred cells filled in `docs/perf.md` *(2026-05-22, local Docker MSSQL)*
- [x] `docs/perf.md` "Verdict" section updated — `Events(ApplicationId, EnvironmentId, SessionId, OccurredAt)` shipped after the 1k/10k cells showed 44–67% p95 reductions; derived approach holds through 10k events/session; 100k-cell confirms the existing documented materialization boundary (no escalation needed unless a real onboarded app approaches that shape)
- [x] Decision on `Errors(ApplicationId, EnvironmentId, LastCorrelationId)` recorded — index shipped in the same migration after the paired 5k cross-process cells showed a 44% p95 reduction at 10k target events
- [x] 4.11's deferred integration test executed against `obs-api-dev` *(2026-05-22)*: harness emitted `dev_smoke_test`, the `Sessions` row for the bracketed session shows `started_at = 2026-05-22T23:31:12.8955548` and `ended_at = 2026-05-22T23:31:13.0826519` after `shutdown()`. App + public key provisioned via the just-shipped 8.9 admin endpoints (`POST /api/admin/apps/dev-smoke/environments/Development/keys`).
- [x] New indexes ship as an additive EF migration (no schema rewrite) and the `Initial` migration is left untouched — see [`Phase5HardeningIndexes`](backend/src/Observability.Infrastructure/Migrations/20260522222614_Phase5HardeningIndexes.cs)

**Out of scope (explicitly):**
- Re-running anchor cells against Azure SQL — still blocked on 2.4 DB provisioning, owned by 5.2.
- Cross-process correlation trace from a real SCH request — still owned by 6.1.
- Materializing `SessionEvents` — if the grid cells force this, file a new issue rather than expanding 5.7.

---

## Phase 6 — SCH Onboarding (PostHog skipped)

**Re-scope decision (2026-04-30):** `feature/posthog-implementation` was never merged into SCH_API or SCH_UI's `dev`/`main` (verified: branch is 4 commits ahead of `dev` on each repo, contained in no other branch; `dev` has zero PostHog references outside `POSTHOG_EVENT_CATALOG.md`). The PostHog Phase 1 work is therefore reference scaffolding, not a live integration. SCH onboards onto adaptive-observability as a fresh integration. Dual-write parity, PostHog cutover, and PostHog dependency removal (former 6.6 / 6.7 / 6.8) are dropped from scope.

**Goal:** Ship SCH_UI + SCH_API onto adaptive-observability as the first onboarded app pair, leveraging the unmerged PostHog branches as scaffolding for emission points (event names, identity rules, ErrorBoundary, Axios interceptor, GlobalExceptionMiddleware, BG-service catch blocks).

**Exit criteria:** SCH_UI + SCH_API emit the Phase 1 event set to adaptive-observability UAT for 5 business days with zero `SafetyViolations`; privacy reviewer sign-off committed; Prod stable for 1 week.

**Strategy:** Cherry-pick the analytics scaffolding from `feature/posthog-implementation` into a new `feature/adaptive-observability` branch on each SCH repo, replacing PostHog SDK calls with `observability-client-{js,dotnet}` calls. The SCH `IAnalyticsService` interface is replaced by the SDK's own (under `Adaptive.ObservabilityClient`); SCH adopts that one rather than its local copy.

### Issue 6.1 — Hardening prereqs (in this repo and SCH)
**Description:** Items previously folded into "deferred PostHog hardening" still apply to a fresh adaptive-observability integration. Resolve before SCH UAT.
**Acceptance criteria:**
- [ ] 4.11 (SDK auto-bracket sessions) shipped — without this, SCH `Sessions` rows are never written and Phase 5 timelines stay empty
- [ ] 4.2 (SCH route fixture port) — port the validated regression suite from SCH_UI's `routeUtils.ts` tests into `packages/observability-client-js/src/__tests__/`
- [ ] 5.5 verification harness — trace one SCH UAT request FE → BE → ingestion and confirm the same correlation id lands on both `api_request_failed` (FE) and `server_error_occurred` (BE)
- [x] EF `Initial` migration generated and `EnsureCreatedAsync` switched to `MigrateAsync` (owned by Phase 2.4) — required before the first non-Dev deploy *(shipped `75ef382`)*
- [ ] Phase 2.3 hosting + 2.4 DB cutover at least Dev+UAT — without these the API has nowhere to receive SCH events
- [ ] BG job dedup confirmed working in SCH_API integration (4.8 static 15-min default acceptable; per-app override deferred to 8.2)
- [ ] `release_sha` populated in SCH_API + SCH_UI deployed envs via CI build-time injection
- [ ] Generic role names audit on `auth_login_success` (no user-specific labels)
- [ ] `.env.example` in SCH_UI includes `VITE_OBSERVABILITY_KEY` / `VITE_OBSERVABILITY_HOST`
- [ ] Dev-only test endpoint (was `/api/dev/posthog-test`) replaced with `/api/dev/observability-test`, confirmed unreachable outside Development

### Issue 6.2 — Audit SCH_UI integration touchpoints
**Description:** Catalog files that change in the new `feature/adaptive-observability` branch off `dev`. Expected (mirrors the unmerged PostHog scaffolding): `services/analytics.ts`, `utils/routeUtils.ts`, `main.tsx`, `App.tsx`, `services/apiClient.ts`, `store/authStore.ts`, `components/common/ErrorBoundary.tsx`, env files.
**Acceptance criteria:**
- [ ] `docs/audits/sch-ui.md` lists every file added/modified
- [ ] Lists every env var added (`VITE_OBSERVABILITY_*`)
- [ ] Confirms no PostHog packages enter `package.json`

### Issue 6.3 — Implement adaptive-observability in SCH_UI
**Description:** Cherry-pick analytics scaffolding from `feature/posthog-implementation` and rewire onto `observability-client-js`. The SDK API surface mirrors the PostHog branch's `analytics.ts` so most scaffolding ports unchanged; PostHog imports and `posthog.*` direct calls are replaced.
**Acceptance criteria:**
- [ ] `feature/adaptive-observability` branched from current `dev` on SCH_UI
- [ ] `analytics.ts` (or equivalent) backed by `observability-client-js`
- [ ] All Phase 1 emission points wired (login/logout, page views, API failures, exceptions)
- [ ] Compile-time event allowlist preserved (TypeScript unions)
- [ ] No `posthog-js` dependency added

### Issue 6.4 — Audit SCH_API integration touchpoints
**Description:** Catalog files that change. Expected: `Program.cs` (DI), `appsettings.json` (`AdaptiveObservability` section), new `AdaptiveObservabilityService` (or direct SDK consumption), `GlobalExceptionMiddleware.cs`, all 8 BG services.
**Acceptance criteria:**
- [ ] `docs/audits/sch-api.md` lists every file added/modified
- [ ] Lists every config key added
- [ ] Confirms no `PostHog.AspNetCore` reference enters `SCH.Infrastructure.csproj`

### Issue 6.5 — Implement adaptive-observability in SCH_API
**Description:** Cherry-pick analytics scaffolding from `feature/posthog-implementation` and wire to the SDK's `AddAdaptiveObservability(...)`. SCH adopts the SDK's own `IAnalyticsService` (under `Adaptive.ObservabilityClient`) rather than its local copy from the unmerged PostHog branch.
**Acceptance criteria:**
- [ ] `feature/adaptive-observability` branched from current `dev` on SCH_API
- [ ] DI registration via `AddAdaptiveObservability(...)`
- [ ] `GlobalExceptionMiddleware` ported (emits `server_error_occurred` on true 500s only)
- [ ] All 8 BG services emit `background_job_failed` from catch blocks
- [ ] `appsettings.json` gains `AdaptiveObservability` section
- [ ] Correlation ID middleware ported

### Issue 6.6 — Onboard SCH apps in adaptive-observability dashboard
**Description:** Create dashboard rows + provision keys. Pure admin work in this repo's dashboard.
**Acceptance criteria:**
- [ ] `SCH_UI` and `SCH_API` rows created with Dev/UAT/Prod environments
- [ ] Public + server API keys provisioned and stored in SCH's secret stores (Key Vault)
- [ ] Smoke event from each environment lands in adaptive-observability with correct app/env attribution

### Issue 6.7 — UAT soak + privacy validation
**Description:** Replaces former dual-write parity gate. SCH UAT runs on adaptive-observability for 5 business days; daily safety-violation check.
**Acceptance criteria:**
- [ ] 5 business days of UAT traffic
- [ ] Zero `SafetyViolations` rows
- [ ] Daily soak log committed (`docs/migration/sch-uat-soak.md`)
- [ ] Privacy/compliance reviewer sign-off committed

### Issue 6.8 — SCH Prod cutover
**Description:** After UAT soak passes, deploy to SCH Prod with a documented rollback.
**Acceptance criteria:**
- [ ] Rollback plan documented (DI registration revert + env-var flip; FE config flip via `VITE_OBSERVABILITY_*` removal)
- [ ] Prod deploy executed
- [ ] 1 week stable in Prod with zero `SafetyViolations`
- [ ] `feature/posthog-implementation` branches archived in both SCH repos (kept for reference, not deleted)
- [ ] `POSTHOG_EVENT_CATALOG.md` in both SCH repos replaced with a stub linking to `docs/event-catalog.md` in this platform

### Issue 6.9 — SCH-specific dashboard preset
**Description:** Saved dashboard view with SCH selected by default. Replaces the planned "SCH Phase 1 Health Dashboard." This is the only Phase 6 issue that lands in *this* repo (frontend-only).
**Acceptance criteria:**
- [ ] Saved view reachable via dashboard nav
- [ ] Cards match the original PostHog dashboard plan (`POSTHOG_DASHBOARD_PLAN.md` in SCH)

---

## Phase 7 — WMSSite + WMSAPI Onboarding

**Goal:** Onboard `WMSSite` (UI) + `WMSAPI` (backend) using the SDKs and integration pattern validated by SCH onboarding. Replaces the original `SecondApp_*` placeholders.

**Exit criteria:** WMS apps emit the Phase 1 event set to adaptive-observability UAT with zero `SafetyViolations`; multi-app dashboard switching validated; cross-process timeline join works for at least one WMS error.

**Verified state (snapshot 2026-04-30):**
- **WMSSite** (active branch `feature/provider-intake-dropdown`): React 18 + Vite, **JavaScript/JSX (not TypeScript)** — `jsconfig.json`, no `tsconfig.json`. Auth: **MSAL** (`@azure/msal-browser`, `@azure/msal-react`) — Entra/Azure AD, not custom JWT. UI: MUI (not Tailwind/shadcn). Data: TanStack Query + Axios. Sensitive surfaces visible in `src/sections/` include intake, provider notes, wound assessment, regional reports.
- **WMSAPI** (active branch `feature/physician-list-endpoint`): .NET 8 ASP.NET Core, **Dapper-heavy + EF Core**, JWT bearer auth (paired with MSAL). `BackgroundProcessingService` exists. **No global exception middleware** (no `*Middleware*.cs` or `*Exception*.cs` files). **No correlation ID anywhere** — zero matches across the repo on `CorrelationId|X-Correlation|correlation_id`.

These differences from SCH (JS not TS, MSAL not custom JWT, no exception middleware, no correlation ID) make this onboarding net-new infrastructure, not a port. Issues 7.3–7.7 below are the prereqs that did not exist in Phase 6.

### Issue 7.1 — Audit WMSSite
**Description:** Catalog routing, MSAL auth integration, Axios usage, existing error boundaries, env config, and PHI-sensitive routes.
**Acceptance criteria:**
- [ ] `docs/audits/wmssite.md` complete
- [ ] Lists existing React error boundaries (if any) — strategy decision feeds 7.8
- [ ] Lists Axios instances — strategy decision feeds 7.5 (correlation-id forwarding)
- [ ] Lists routes that must never emit `page_viewed` (PHI-bearing)

### Issue 7.2 — Audit WMSAPI
**Description:** Middleware pipeline, exception handling pattern (per-controller catches expected — no global middleware exists), all `IHostedService`/`BackgroundService` implementations, outbound HttpClient usage.
**Acceptance criteria:**
- [ ] `docs/audits/wmsapi.md` complete
- [ ] Inventory of per-controller try/catch blocks (input to 7.6 reconciliation)
- [ ] List of all BG services beyond `BackgroundProcessingService` (input to 7.7)
- [ ] List of outbound HttpClient registrations (input to 7.5 propagation)

### Issue 7.3 — JS-vs-TS SDK consumption strategy for WMSSite
**Description:** WMSSite is JavaScript, so the SDK's compile-time event allowlist (TypeScript unions) is not enforced at host build time. Decide the developer-experience guarantee for event-name correctness. The server-side allowlist (Phase 1.4 + `SafetyViolations`) is the only safety net regardless of choice; this decision is about *catching typos earlier*.
**Decisions needed:**
- Ship `.d.ts` types only — rely on JSDoc + editor IntelliSense (lowest friction, weakest guarantee)
- Require `// @ts-check` on analytics-touching files — per-file enforcement, no project-wide TS migration
- Add an ESLint rule that flags `track('foo')` calls where `'foo'` is not in a known list (loudest, most maintenance)
- Defer to runtime-only — accept that typos surface as `SafetyViolations` rows, not build failures
**Acceptance criteria:**
- [ ] Decision recorded in `docs/audits/wmssite.md` with rationale
- [ ] If `.d.ts`/`@ts-check`/ESLint chosen, the convention is enforced before 7.10 ships
- [ ] `docs/onboarding-checklist.md` (issue 7.13) gains a "TS or JS host?" question reflecting this learning

### Issue 7.4 — MSAL identity rule for WMS
**Description:** SCH used `String(userId)` (internal int from a custom auth store). WMS authenticates via Entra/AAD; the natural distinct id is the AAD `oid` claim (a stable per-user GUID within tenant). This is a one-way decision — re-keying identity later loses session continuity for every existing user.
**Decisions needed:**
- Use AAD `oid` directly (stable GUID; identifying within tenant; not PHI per se but tenant-correlatable)
- Hash it (`sha256(tenantId + oid)`) — privacy-cleaner, harder to correlate with admin reports manually
- Map AAD `oid` → internal user int via a WMSAPI lookup, use the int (matches SCH's pattern; requires a backend round-trip on `identify()`)
**Acceptance criteria:**
- [ ] Decision recorded in `docs/identity-rules.md` with rationale and a worked example
- [ ] WMSSite `identify()` honors the rule
- [ ] WMSAPI distinct-id strategy for server events documented in same doc (likely `oid`-derived for user-attributed events; `system:background-service` and `api_client_{id}` rules unchanged)
- [ ] No raw email, UPN, or `name` claim ever passed to `identify()` or as an event property

### Issue 7.5 — Add correlation-ID middleware to WMSAPI
**Description:** WMSAPI has no correlation-ID middleware (zero matches across the repo). Without it, Phase 5's cross-process error join is a no-op for WMS — clicking an event in the timeline cannot surface the BE error that caused it. This is net-new infrastructure for WMSAPI, not a port from SCH.
**Acceptance criteria:**
- [ ] Middleware reads incoming `X-Correlation-Id` (or generates a GUID v4), exposes via `HttpContext.Items["CorrelationId"]` and `Activity.Current?.SetTag(...)`
- [ ] Sets the same id on the response header
- [ ] Logger scope (`ILogger.BeginScope`) enriches every log line with the id
- [ ] Outbound `HttpClient` registrations gain a delegating handler that propagates the id on downstream calls (fed by 7.2's HttpClient inventory)
- [ ] WMSSite Axios interceptors generate `crypto.randomUUID()` per request and set `X-Correlation-Id`
- [ ] One end-to-end test: trigger a 500 from WMSSite; confirm same correlation id reaches both the FE `api_request_failed` and the BE `server_error_occurred`

### Issue 7.6 — Add global exception middleware to WMSAPI
**Description:** WMSAPI has no `GlobalExceptionMiddleware` (no `*Middleware*.cs`/`*Exception*.cs` files). Without it there is no centralized emission point for `server_error_occurred`. Audit existing per-controller try/catch first to avoid double-emit on routes that already swallow exceptions.
**Acceptance criteria:**
- [ ] Per-controller catches inventoried in 7.2 are reconciled (kept, removed, or made non-swallowing) so the middleware sees the exceptions worth emitting
- [ ] Middleware registered after auth, before MVC
- [ ] Emits `server_error_occurred` only on true unhandled exceptions (5xx), never on 4xx or expected business errors
- [ ] Uses correlation id from 7.5
- [ ] Response sanitized — no exception messages or stack traces leak to clients
- [ ] Integration test confirms emission on a forced exception, no emission on a controlled 400

### Issue 7.7 — WMSAPI background-service error wiring
**Description:** `BackgroundProcessingService` exists; 7.2's audit will surface any others. All `IHostedService`/`BackgroundService` implementations must emit `background_job_failed` from catch blocks. BG dedup (4.8 static 15-min default) is acceptable; per-app override deferred to 8.2.
**Acceptance criteria:**
- [ ] All BG services from 7.2 inventory wired
- [ ] `background_job_failed` emits with `job_name`, `error_type`, `correlation_id` (generated per-iteration if no inbound request)
- [ ] Integration test: 100 identical failures within 15 min → 1 incident with `count=100`

### Issue 7.8 — WMSSite ErrorBoundary strategy
**Description:** Audit existing React error boundaries (output of 7.1) before wiring the SDK's. WMSSite uses MUI heavily and may have feature-area boundaries; replacement vs. wrapping vs. coexistence is a deliberate choice.
**Acceptance criteria:**
- [ ] Strategy chosen and documented in `docs/audits/wmssite.md`: replace top-level only / wrap existing / add layer
- [ ] `frontend_exception` emits `error_type`, `source`, `component_stack_depth` only — no message, no stack, no props
- [ ] `window.onerror` and `unhandledrejection` listeners installed once at app root (mirrors SCH pattern)

### Issue 7.9 — `WMS_EVENT_CATALOG.md`
**Description:** App-specific events on top of the global Phase 1 set. WMS-sensitive routes (intake, provider notes, wound assessment) explicitly listed as never-record. Privacy reviewer sign-off required before UAT — WMS surfaces are different enough from SCH that the SCH allowlist tuning does not transfer.
**Acceptance criteria:**
- [ ] App-specific events listed with allowed props
- [ ] Never-record route list reviewed against current WMSSite routes (input from 7.1)
- [ ] References `docs/event-catalog.md` and `docs/identity-rules.md` for global rules
- [ ] Privacy reviewer sign-off committed

### Issue 7.10 — Onboard WMSSite (integration)
**Description:** Mirror the SCH integration pattern. Adapted for JS-not-TS (per 7.3), MSAL identity (per 7.4), MUI ErrorBoundary strategy (per 7.8).
**Acceptance criteria:**
- [ ] Branch `feature/adaptive-observability` off `dev` on WMSSite
- [ ] All Phase 1 emission points wired
- [ ] `init()` ordering verified to run after MSAL ready, with early `page_viewed` events queued and flushed
- [ ] Zero PHI in any captured event (manual review of one full session)
- [ ] Zero `SafetyViolations` in 24h dev traffic before promoting to UAT

### Issue 7.11 — Onboard WMSAPI (integration)
**Description:** Depends on 7.5/7.6/7.7. DI-register `AddAdaptiveObservability(...)`; consume correlation id from 7.5 middleware; emit via 7.6 exception middleware and 7.7 BG wiring.
**Acceptance criteria:**
- [ ] Branch `feature/adaptive-observability` off `dev` on WMSAPI
- [ ] DI registration via `AddAdaptiveObservability(...)`
- [ ] Phase 1 server events emit (no exception messages/stacks)
- [ ] Smoke test confirms ingestion in adaptive-observability Dev with correct app/env attribution

### Issue 7.12 — Validate multi-app dashboard switching
**Description:** Filters scope cleanly across SCH + WMS apps; no cross-app data leakage.
**Acceptance criteria:**
- [ ] Manual smoke test against both app pairs
- [ ] Automated test foreshadowing Phase 8 RBAC: a user with access to SCH cannot query WMS data and vice versa

### Issue 7.13 — Third-app onboarding checklist
**Description:** Onboarding questions as a checklist file teams fill in before onboarding. Enriched by what WMS made us learn.
**Acceptance criteria:**
- [ ] `docs/onboarding-checklist.md` committed, including:
  - Frontend: framework, **TS or JS** (event-allowlist enforcement strategy, per 7.3), bundler, router, state mgmt
  - Backend: framework, ORM (EF/Dapper/other), **correlation-ID middleware in place or net-new** (per 7.5), **global exception middleware in place or net-new** (per 7.6), all `IHostedService` implementations
  - Auth: custom JWT, **AAD/MSAL** (distinct-id rule per 7.4), or other
  - Deployment env, DB type, PHI/PII presence, never-record routes, never-replay routes (Phase 9)

---

## Phase 8 — Alerts, Grouping & Production Hardening

**Goal:** Operate at production scale: alert on real incidents, group repeated errors, control access, retain data within policy.

**Exit criteria:** Production traffic from at least two onboarded apps; on-call gets only actionable alerts; RBAC enforced; retention job running on schedule.

### Issue 8.1 — Error fingerprinting (server-side hardening)
**Description:** Already present in 1.5; this hardens it (collision behavior, fingerprint version field).
**Acceptance criteria:**
- [ ] Fingerprint version stored on `Errors`
- [ ] Backfill job for past data
- [ ] Algorithm documented

### Issue 8.2 — BG job failure dedup hardening
**Description:** Already present in 4.8; this hardens (per-app override, audit of suppressed-vs-incident counts).
**Acceptance criteria:**
- [ ] Per-app window override
- [ ] Suppressed counts visible in dashboard

### Issue 8.3 — Alert rule engine
**Description:** Configurable rules.
**Acceptance criteria:**
- [ ] `AlertRules` table
- [ ] Types: count-over-window, new-error-after-release, error-rate-above-threshold, any-prod-job-failure
- [ ] Evaluator runs as `Worker` service

### Issue 8.4 — Notifications (email + Teams)
**Description:** Fire alerts to email + Microsoft Teams webhooks.
**Acceptance criteria:**
- [ ] Email via ACS or SendGrid (decide)
- [ ] Teams via incoming webhook
- [ ] Per-rule rate limit
**Investigation questions:**
- ACS vs. SendGrid — what does the company already use?

### Issue 8.5 — Retention policies
**Description:** Per-app retention with scheduled archive/delete. Replay retention is defined here but enforced once Phase 9 ships.
**Acceptance criteria:**
- [ ] Per-app setting (default 90d events, 180d errors, **14d replay** when Phase 9 lands)
- [ ] Worker runs nightly
- [ ] Audit log row per run
- [ ] Schema reserves a `ReplayRetentionDays` column on `AppEnvironments` (nullable until Phase 9)

### Issue 8.9 — Admin app/key provisioning endpoint

**Description:** The dashboard reads apps via `GET /api/apps` but exposes no way to *create* them. Every onboarding so far has assumed a hand-seeded `INSERT` into `Applications` + `AppEnvironments` + `ApiKeys` by whoever owns SQL admin on the target environment (Brandon for `ObservabilityDev`). This surfaced when trying to execute the 4.11 live-ingest harness against `obs-api-dev` (Phase 5.7): the harness script exists, but seeding the test app + API key requires direct DB access that the AAD-only `adaptivetoolssql` server scopes to Brandon. SCH (Phase 6.6) and WMS (Phase 7) onboarding will each hit the same wall.

A small admin-provisioning endpoint removes the SQL-hand-seed dependency for every onboarding. RBAC (8.6) lands later; until then the endpoint can be gated by a server-side admin secret pulled from Key Vault — same trust boundary the dashboard already operates inside.

**Acceptance criteria:**
- [x] `POST /api/admin/apps` — creates an `Application` + initial `AppEnvironment` rows. Idempotent on slug.
- [x] `POST /api/admin/apps/{slug}/environments/{env}/keys` — mints a fresh API key, returns plaintext **once**, persists only the hashed form via the existing `IApiKeyHasher`. Supports `key_type` (PublicClient / ServerApi).
- [x] Endpoints gated by an `X-Observability-Admin-Key` header validated against a Key Vault secret (`ObservabilityAdminKey` → `Observability:AdminApiKey`, added to `KeyVaultConfiguration.RequiredSecrets`; constant-time compare). Returns 401 if missing/wrong/unconfigured, identical shape to the existing api-key middleware.
- [x] Audit row written per call — new `AuditLogs` table (`Phase8AdminAuditLog` migration) with `Action`, `ActorType`, `ApplicationId`, `EnvironmentId`, `CorrelationId`, `DetailsJson`. Foreshadows 8.7's full audit surface.
- [x] Integration tests in [`AdminEndpointsTests`](backend/tests/Observability.IntegrationTests/AdminEndpointsTests.cs): idempotent app creation (201 on first / 200 on duplicate, single row + matching audit rows); key minting returns plaintext on first call only with the prefix (`aopub_` / `aoserv_`), and `ApiKeyResolver` accepts the minted plaintext; 401 on missing/wrong header; 404 on unknown app; 400 on invalid `key_type`.
- [x] `obs-api-dev` provisioned with the admin key + a test app/key for the 4.11 harness *(2026-05-22)*: `ObservabilityAdminKey` minted into `AdaptiveToolsKeyVault`; `aopub_…` public key minted for `dev-smoke`/`Development` via the live admin endpoint; harness PASSED end-to-end. Closes the deferred line in 5.7's acceptance criteria.
- [ ] When 8.6 RBAC lands, the admin-key gate is replaced by role-based auth without changing the endpoint shape.

**Investigation questions:**
- Where does the admin secret live for local dev vs. deployed envs? Same `appsettings.Development.json` pattern as `ApiKeyHashPepper` probably suffices.
- Should this endpoint also rotate keys (revoke + mint), or is rotation a separate issue? Lean: separate issue when it's actually needed.

### Issue 8.6 — RBAC
**Description:** Admin / Developer / Viewer / AppOwner.
**Acceptance criteria:**
- [ ] Roles persisted, applied at API + UI
- [ ] AppOwner cannot read other apps
- [ ] Admin/Developer access logged
**Investigation questions:**
- Identity source — Entra/AAD groups vs. local users?

### Issue 8.7 — Audit logging
**Description:** Audit dashboard access, settings changes, API key create/revoke.
**Acceptance criteria:**
- [ ] `AuditLogs` table
- [ ] All admin endpoints write audit rows
- [ ] Read-only audit view

### Issue 8.8 — Rate limiting + payload size limits
**Description:** Per-key rate; reject oversized payloads at the edge.
**Acceptance criteria:**
- [ ] Per-key req/sec configurable
- [ ] Default 64 KB payload max
- [ ] 429 + `Retry-After`

### Issue 8.9 — Ingestion queue
**Description:** Decouple receive from DB write at scale.
**Acceptance criteria:**
- [ ] Receive enqueues; worker drains
- [ ] In-process `Channel<T>` for MVP, Service Bus for scale
- [ ] Backpressure documented
**Investigation questions:**
- At what RPS does in-process backpressure stop being acceptable?

### Issue 8.10 — Index review + archival
**Description:** Review after first month of prod traffic.
**Acceptance criteria:**
- [ ] Slow-query review in `docs/perf.md`
- [ ] Indexes added with measured before/after

### Issue 8.11 — Key rotation runbook
**Description:** Exercise a Key Vault secret rotation in UAT.
**Acceptance criteria:**
- [ ] Runbook in `docs/azure-key-vault-setup.md`
- [ ] Rotation tested end-to-end

---

## Phase 9 — Session Replay (rrweb)

**Goal:** Add visual session replay via `rrweb`, scoped tightly: off by default, opt-in per app+env, masked aggressively, stored in Blob, retained briefly. Replay artifacts attach to the existing Phase 5 session timeline so debugging is "click the failure → watch the last 30 seconds."

**Exit criteria:** SCH UAT can opt in for a single feature area, capture-on-error mode produces a viewable replay attached to a `frontend_exception` event, masking audit signed off, prod remains disabled.

**Non-goals:** Always-on prod recording, full-session capture by default, replay of any surface flagged as PHI/PII.

**Dependency approval gate (blocks all Phase 9 issues):**
- [ ] `rrweb` + `rrweb-player` (MIT) approved as net-new dependencies
- [ ] Privacy/compliance sign-off on the masking policy in `docs/privacy-rules.md`
- [ ] Decision on Blob storage account topology (per-env vs. shared with lifecycle rules)

### Issue 9.1 — Decide replay scope and defaults
**Description:** Document what replay is and isn't for this platform. Lock defaults before any code lands.
**Acceptance criteria:**
- [ ] `docs/replay.md` covers: capture modes (always-on vs. capture-on-error vs. sampled), default = `captureOnError` with 30s circular buffer
- [ ] Per-app+env opt-in flag (`AppEnvironments.ReplayEnabled` already exists from Phase 1.1 — wired up here)
- [ ] Default `sampleRate` = 0; explicit per-app override required
- [ ] Prod cannot enable replay without an `ApprovedForProductionAt` timestamp set by an admin

### Issue 9.2 — Implement rrweb adapter in `observability-client-js`
**Description:** Replace the no-op adapter from Issue 4.9 with an rrweb-backed implementation. Lazy-loaded — only fetched if `replay.enabled: true`.
**Acceptance criteria:**
- [ ] `replay/` submodule code-split; main bundle size unchanged when replay is off
- [ ] `recorder.ts` calls `rrweb.record({ maskAllInputs, blockSelectors, ... })` from init config
- [ ] `buffer.ts` chunks events every 5–10s; in `captureOnError` mode keeps a 30s ring buffer and only flushes on error
- [ ] `transport.ts` gzip-compresses chunks (browser `CompressionStream`) and POSTs to `/api/ingest/replay/chunk` with `X-Session-Id` + `X-Chunk-Seq`
- [ ] Recorder stops at `maxSessionMinutes` (default 30) to bound storage per session
- [ ] `sessionId` matches the one used by `track()` — replay rows join cleanly to Phase 5 sessions

### Issue 9.3 — Centralized masking policy
**Description:** Masking config lives in one place per app, versioned, and shipped to the SDK at init. Selector lists for SCH must be reviewed by the same person who reviewed PostHog's privacy rules.
**Acceptance criteria:**
- [ ] `MaskingPolicies` table: Id, ApplicationId, EnvironmentId, Version, BlockSelectorsJson, MaskInputSelectorsJson, NeverRecordRoutesJson, CreatedAt, ApprovedByUserId
- [ ] FE SDK fetches the active policy at init (cached, with version pin)
- [ ] `MaskingPolicyVersion` stamped on every `SessionReplays` row so the policy in force at recording time is auditable
- [ ] SCH initial policy seeds: mask all inputs, block `[data-phi]`, `.patient-name`, `.dob`, etc. (port from any existing SCH replay-disable hints)

### Issue 9.4 — Domain models: `SessionReplays` + `SessionReplayChunks`
**Description:** Metadata in SQL, bytes in Blob.
**Acceptance criteria:**
- [ ] `SessionReplays`: Id, SessionId (FK), ApplicationId, EnvironmentId, StartedAt, EndedAt, ChunkCount, TotalBytes, MaskingPolicyVersion, ReleaseSha, CaptureMode (`always_on` | `capture_on_error` | `sampled`)
- [ ] `SessionReplayChunks`: Id, SessionReplayId, SeqNo, BlobUri, Bytes, ReceivedAt; unique index (SessionReplayId, SeqNo)
- [ ] `Sessions` row gains `HasReplay` derived flag (or computed view) so dashboards can filter
- [ ] EF migration is additive — Phase 1–8 schemas untouched

### Issue 9.5 — `POST /api/ingest/replay/chunk`
**Description:** Separate ingestion path. Public-key auth. Chunk written to Blob, metadata row inserted in SQL.
**Acceptance criteria:**
- [ ] Endpoint scoped to public_client keys; rejects server keys
- [ ] Hard cap 1 MB/chunk (per-app overridable); 413 on oversize
- [ ] Per-key replay-specific rate limit, separate from event ingestion (so replay storms can't starve analytics)
- [ ] Rejects if `AppEnvironments.ReplayEnabled = false` for the resolved app+env (defense-in-depth — the SDK should never have started, but server enforces too)
- [ ] Writes blob with content-addressed key: `{appSlug}/{env}/{sessionId}/{seqNo}.rrweb.gz`
- [ ] Inserts `SessionReplayChunks` row; on duplicate (SessionReplayId, SeqNo) returns 200 idempotently
**Investigation questions:**
- Single Blob container per env vs. per-app? (Per-env with prefix-based RBAC is simpler, per-app is cleaner for retention rules.)
- SAS-uploaded direct-to-Blob vs. proxy-through-API? Direct upload halves API CPU but complicates auth.

### Issue 9.6 — Replay viewer in the dashboard
**Description:** Add a player on the session timeline page (Phase 5.6). Streams chunks from Blob and feeds `rrweb-player`.
**Acceptance criteria:**
- [ ] Session timeline shows a "▶ Replay" affordance only when `HasReplay = true`
- [ ] Player loads chunks lazily in seq order, decompresses client-side
- [ ] Scrubber, speed control, and event markers from the timeline pinned to replay timestamps
- [ ] Viewer fetches chunks via short-lived signed URLs from the API (never expose blob credentials)
- [ ] Audit log row written when a replay is viewed (who, when, which session)

### Issue 9.7 — Capture-on-error wiring
**Description:** Make replay's killer feature trivial: any `captureException` or `captureFailedRequest` call optionally flushes the ring buffer.
**Acceptance criteria:**
- [ ] In `captureOnError` mode, error capture triggers `replay.flush()` before the event POST resolves
- [ ] Replay metadata links back to the triggering error's `CorrelationId`
- [ ] No replay flush if no error has occurred in the bounded session

### Issue 9.8 — Replay retention worker
**Description:** Specialize the Phase 8.5 retention job for replay. Replay TTL is much shorter than events.
**Acceptance criteria:**
- [ ] Default replay retention 14 days; per-app override
- [ ] Worker deletes Blob chunks first, then `SessionReplayChunks` rows, then `SessionReplays` row
- [ ] Failure to delete a blob is retried; never orphans bytes silently
- [ ] Audit log row per run with byte-count freed

### Issue 9.9 — RBAC for replay
**Description:** Replay is the most sensitive surface in the platform. Lock it down hardest.
**Acceptance criteria:**
- [ ] New role `ReplayViewer` (separate from `Developer`); not granted by default to anyone
- [ ] Even `Admin` does not get replay access without an explicit grant logged in `AuditLogs`
- [ ] AppOwner of one app cannot view another app's replays (already enforced by Phase 8 RBAC; covered by an integration test)
- [ ] Every replay view writes an audit row visible to compliance

### Issue 9.10 — UAT soak + masking audit
**Description:** Before any prod consideration, run replay in SCH UAT for 2 weeks, audit a stratified sample of recordings for any leaked PHI/PII.
**Acceptance criteria:**
- [ ] 2-week UAT soak with replay enabled on one feature area only
- [ ] Sample of N recordings reviewed by privacy reviewer; sign-off committed
- [ ] Zero leaked PHI/PII findings; any finding triggers masking policy bump and re-soak
- [ ] Storage cost / chunk-count metrics captured for capacity planning before any prod ramp
**Investigation questions:**
- Sample size N for masking audit? (Suggest: all recordings in week 1, stratified sample in week 2.)
- Do we need a "kill switch" config flag separate from `ReplayEnabled` to instantly disable replay across all apps in case of incident?

---

## Cross-Cutting

### Privacy review gates
- **Before Phase 6 SCH UAT entry:** event catalog committed in adaptive-observability matches the Phase 1 event set inherited from `POSTHOG_EVENT_CATALOG.md`; route-normalization fixtures from SCH_UI ported (4.2).
- **Before Phase 6 SCH Prod cutover:** 5 business days UAT with zero `SafetyViolations`; privacy/compliance reviewer sign-off committed.
- **Before Phase 7 WMS UAT entry:** `WMS_EVENT_CATALOG.md` committed with WMS-specific never-record routes; MSAL identity rule (7.4) recorded in `docs/identity-rules.md`; correlation-ID end-to-end test (7.5) green.
- **Before Phase 9 (replay) entry:** rrweb dependency approved; masking policy reviewed; Blob storage topology decided; `docs/replay.md` committed.
- **Before Phase 9 prod enablement (per-app):** 2-week UAT masking audit clean; `ReplayViewer` RBAC in place; replay-specific retention job verified; admin-set `ApprovedForProductionAt` recorded.

### Onboarding risks
- **PostHog scaffolding is unmerged, not deployed.** SCH `feature/posthog-implementation` was never merged to `dev`/`main` (verified 2026-04-30). Treating it as live infrastructure would silently misroute Phase 6 work; instead it is reused as scaffolding only. If anyone re-merges that branch, Phase 6 needs re-evaluation.
- **WMSAPI lacks correlation-ID and exception middleware** (verified — zero matches across the repo). Phase 7 net-new infra (7.5 + 7.6), not a port.
- **WMSSite is JavaScript, not TypeScript.** SDK's compile-time event allowlist becomes runtime-only unless 7.3's chosen strategy enforces it. Server-side `SafetyViolations` is the safety net.
- **MSAL identity is a one-way decision.** Re-keying `distinct_id` later loses session continuity for every existing user. 7.4 must land before WMSSite ships any `identify()` call.
- **Replay safety:** UAT replay masking has not been audited. Keep replay disabled until Phase 9 masking audit signs off; prod stays off-by-default per app even after sign-off.
- **Role names:** confirm `auth_login_success` `roles` property contains generic role names only, not user-specific labels.
- **Token threshold edge cases:** route normalization must not turn `posthog-500-test` into `posthog-{id}-test`. SCH_UI threshold tuning ported verbatim (4.2).
- **4xx tracking:** explicitly out of scope for Phase 1. Decision deferred to a future event-catalog update.

### Verification (end-to-end test plan)
1. CI runs unit + integration tests on every PR.
2. **Phase 4 / 7 specific:** SDK quickstart emits each Phase 1 event; dashboard shows them under the correct app/env; submitting an unsafe event (`{ "email": "x@y.com" }`) returns 422 and writes a `SafetyViolations` row with no `Events` row.
3. **Phase 6 specific:** SCH UAT runs on adaptive-observability for 5 business days with zero `SafetyViolations` and the privacy reviewer sign-off committed before Prod cutover. (Former dual-write parity gate dropped — see Phase 6 re-scope decision.)
4. **Phase 7 specific:** WMS end-to-end correlation-ID test (7.5) green — same id appears on both FE `api_request_failed` and BE `server_error_occurred` from one user-action trigger.

### Still-open cross-cutting questions
- ~~**IaC tool** (Bicep vs. Terraform vs. stay on `az` CLI)~~ — resolved 2026-04-30: stay on `az` CLI scripts.
- **Identity source for Phase 8 RBAC** (Entra/AAD groups vs. local users).
- **Email provider for alerts** (ACS vs. SendGrid) — Phase 8.
- **EF migration timing.** Phase 1 ships `EnsureCreatedAsync` for dev. Phase 4 + 5 added `BackgroundJobFailures` and `Sessions`, growing the schema. Phase 2.4 already owns the `migrations add Initial` + switch to `MigrateAsync` cutover; flagged here so the surface area is visible when that work runs.
- **Phase 9 replay:** Blob storage topology (per-env vs. per-app), direct-upload-via-SAS vs. proxy-through-API, default capture mode (recommended: `captureOnError`).

---

## Appendix A — PostHog Phase 1 inputs

This plan inherits from prior PostHog integration planning and implementation work on the SCH project. Key inputs:
- `POSTHOG_EVENT_CATALOG.md` — committed in both SCH repos; source of truth for the initial ported event catalog.
- `feature/posthog-implementation` branches in SCH_UI and SCH_API — production-bound implementations whose contracts (event names, identity rules, allowlists, route normalization) the new platform must preserve.
- Hardening prompts (SCH_API and SCH_UI) — the still-open items are folded into Phase 6 as cutover prerequisites.
- Phase 2 deferred event ideas — input to future event-catalog updates, not part of this plan's MVP.
