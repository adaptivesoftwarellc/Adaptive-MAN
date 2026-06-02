# WMSSite integration audit (Issue 7.1)

Inventory of what changes when **WMSSite** onboards onto adaptive-observability as Phase 7's second tenant. Read-only investigation — **no code was changed** in WMSSite, SCH_UI, or this repo. Counterpart to [`sch-ui.md`](./sch-ui.md); WMSSite diverges from SCH_UI materially (JavaScript, MSAL, no existing telemetry), so this is **net-new instrumentation**, not a cherry-pick.

**Source audited:** `WMSSite@origin/dev` (`adaptivesoftwarellc/WMSSite`), tip `b1793d9` (2026-05-31, *feat(ivr): reviewer-queue filters, provider sort, Excel export*). **Target:** new `feature/adaptive-observability` branched off `WMSSite@dev`.

> **Why `dev`, not the plan's `feature/provider-intake-dropdown`:** matches the SCH_UI precedent (audits targeted a new branch off `dev`), and `dev` is the integration base Phase 7 work will branch from. The 2026-04-30 snapshot's "active branch" note is stale — `origin/dev` already carries the IVR reviewer-queue work.

## Stack delta vs SCH_UI (the three material differences)

| Dimension | SCH_UI | WMSSite | Impact |
|---|---|---|---|
| Language | TypeScript | **JavaScript** (`jsconfig.json`, no `tsconfig.json`; all `.js`/`.jsx`) | SDK's compile-time `PropsFor<E>` event-name enforcement is lost — see [Open decision 7.3](#open-decisions). |
| Auth | Custom JWT | **MSAL/Entra** (`@azure/msal-browser` `^4.24.0`) exchanged for a backend JWT | `identify()` source differs — see [Open decision 7.4](#open-decisions). |
| Existing telemetry | `services/analytics.ts` scaffolding (PostHog) to rewire | **None** — clean slate | Every touch point is an **add**, not a modify-in-place. No `posthog-js` to remove. |

Confirmed: React `^18.2.0`, Vite `^7.1.7`, `react-router-dom` `^6.16.0` (`useRoutes()`), `axios` `^1.6.8`, `@mui/material` `^5.15.15`. No analytics/telemetry package present (no posthog/amplitude/segment/app-insights).

## A. Files added (2)

| Path | What it does | Notes |
|---|---|---|
| `src/services/analytics.js` | Thin wrapper around `@adaptivesoftwarellc/observability-client-js`: re-exports `init / identify / reset / track / capturePageView / captureException`, swallowing errors. Mirrors the **export shape** of SCH_UI's [`analytics.ts`](./sch-ui.md#a-files-added-2) so call sites stay stable. | New file (~60–90 LOC). SCH_UI rewired an existing file; WMSSite has none, so this is net-new. The SDK's `identify(distinctId: string)` takes **only a string** — login events (`roles`, etc.) are emitted via a separate `track('auth_login_success', …)` call, not as identify props. |
| `src/utils/routeUtils.js` | `normalizeRoute()` + `routeToFeatureArea()` for WMSSite's ~28 routes. | New file (~50–80 LOC). Recommended: delegate path-stripping to the SDK's [`normalizeRoute`](../../packages/observability-client-js/src/route.ts) (re-exported from the package root) and keep only a WMSSite-specific `featureAreaMap`. Same pattern SCH_UI landed ([sch-ui.md §A](./sch-ui.md#a-files-added-2)). |

## B. Files modified (5)

| Path | Adds | Lines |
|---|---|---|
| [`src/main.jsx`] | `init({ ingestUrl, apiKey, environment, releaseSha, enabled })` before `ReactDOM.render`, gated on `VITE_OBSERVABILITY_*`. Signature from [`InitOptions`](../../packages/observability-client-js/src/index.ts). Session recording stays off (replay is Phase 9 — drop the `VITE_IS_UAT` analog). No `MsalProvider` exists here, so init slots cleanly into the provider tree (`HelmetProvider → BrowserRouter → QueryClientProvider → AuthProvider`). | ~12–15 |
| [`src/App.jsx`] | A `RouteTracker` that calls `capturePageView(normalizedRoute, featureArea)` on `useLocation()` change; plus `window` `error` + `unhandledrejection` listeners emitting `captureException({ source, errorType })`. WMSSite has **neither today**. The existing [`use-scroll-to-top`](src/hooks/use-scroll-to-top.js) hook already fires on `pathname` change — RouteTracker mirrors that hook point. | ~15–20 |
| [`src/auth/api.jsx`] | Replace nothing; **append** one interceptor: `import { attachAxiosInterceptor } from '@adaptivesoftwarellc/observability-client-js/axios'; attachAxiosInterceptor(api); attachAxiosInterceptor(fileapi);`. Must chain **after** the existing 401 silent-refresh response interceptor (lines ~70–112) so it observes final failures without disturbing the single-flight refresh / `__silentAuth` logic. Two axios instances exist (`api`, `fileapi`) — both need it. | ~3–5 |
| [`src/auth/index.jsx`] (`AuthProvider`) | In `login()` (after token persist, ~line 24): `analytics.identify(String(userId))` + `track('auth_login_success', { roles })`. In `logout()`: `track('auth_logout')` then `analytics.reset()`. **`userId` is not currently in `AuthProvider` state** — it's decoded from the JWT in [`PermissionsContext.jsx`](src/auth/PermissionsContext.jsx) (`jwtDecode(authToken).UserID`). Either lift that decode into `login()` or read it from `PermissionsContext`. See [Open decision 7.4](#open-decisions). | ~6–10 |
| [`src/components/ErrorBoundary/index.jsx`] | In `componentDidCatch` (line ~22, currently `console.error` only): `analytics.captureException({ source: 'ErrorBoundary', errorType: error.name, componentStackDepth })`. App-wide boundary already wraps `App` ([`App.jsx`] line ~9). | ~5 |

## C. Environment variables added

| Variable | Notes |
|---|---|
| `VITE_OBSERVABILITY_URL` | SDK ingest endpoint → `InitOptions.ingestUrl`. Dev: `https://obs-api-dev.azurewebsites.net`. |
| `VITE_OBSERVABILITY_KEY` | Public client API key → `InitOptions.apiKey`. Minted via Phase 8.9 admin endpoints. |
| `VITE_OBSERVABILITY_ENABLED` | Optional kill-switch → `InitOptions.enabled` (SDK no-ops when `false`). |

WMSSite has **no committed `.env.example`** (Vite SPA convention). Existing `import.meta.env.VITE_*` keys in use: `VITE_API_URL`, `VITE_MSAL_CLIENT_ID`, `VITE_MSAL_TENANT_ID`, `VITE_MSAL_REDIRECT_URI`, `VITE_IS_UAT`. **Phase 7.1 prereq:** create `WMSSite/.env.example` documenting the MSAL set **and** the new `VITE_OBSERVABILITY_*` set. `vite.config.js` needs no change — no env interpolation happens there (build already does `drop_console`, which is fine; the SDK does not rely on `console`).

`releaseSha`: no build-time version injection today (`package.json` `version` is static `1.8.0`). To populate `InitOptions.releaseSha`, add a `VITE_OBSERVABILITY_RELEASE_SHA` CI inject — same gap SCH_UI flagged.

## D. Dependencies

| Add | Remove |
|---|---|
| `@adaptivesoftwarellc/observability-client-js` (`axios` + `react` already present as peers) | None — no `posthog-js` ever landed in WMSSite |

`package-lock.json` regenerates with the integration PR. SDK publish gating is shared with SCH ([sch-ui.md §H](./sch-ui.md#h-open-items-feeding-issue-63)) — Issue 7.1 implementation cannot start until the package is on npm.

## E. Route inventory + PHI flags

WMSSite declares routes via `useRoutes()` in [`src/routes/sections.jsx`]; all but `/login` and `/404` are wrapped in `DashboardLayout`. `page_viewed.normalized_route` **must** pass through `normalizeRoute()` before emit so dynamic segments (`:patientId`, `:woundId`) never leave the browser.

**PHI-sensitive — never-record / always-normalized (per the plan):**

| Route | Component | PHI risk |
|---|---|---|
| `/patients`, `/patients/intakes` | IVR / PatientIntakeList | patient roster + intake |
| `/eligibility-queue`, `/insurance/eligibility-request` | EligibilityQueue | patient eligibility |
| `/insurance/prior-authorization` | PriorAuthorization | patient insurance claims |
| `/ivr/submit/:patientId`, `/ivr/submit/:patientId/:woundId` | IvrSubmissionPage | **wound assessment** — dynamic IDs must strip to `/ivr/submit/{id}/{id}` |
| `/reports/intakes`, `/reports/regional-intakes` | Intake / RegionalIntake report | aggregated intake (regional reports) |

**Lower-risk but flag for scrub review:** `/settings/intake`, `/settings/prior-authorization` (templates may embed PHI-shaped sample data).

**Non-PHI (safe):** `/products`, `/blog`, `/import`, `/skin-log/*`, `/worklist`, `/master-schedule`, `/history`, `/settings/users`, `/settings/enter-payors`, `/settings/ivr*`, `/settings/eligibility-config`, `/reports`, `/reports/visits`, `/reports/questionnaire-submissions`, `/reports/postal-code-heatmap`, `/changepassword`, `/register`.

## F. PHI/PII review checkpoints

- **No usernames, emails, display names, DOBs, raw URLs, query strings, request/response bodies, exception messages, stack traces, or tokens** in any captured event. WMSSite's `captureException` carries only `error_type` + `source` (+ optional `component_stack_depth`) — never `error.message`.
- **`distinct_id` = `String(userId)`**, the WMS-internal numeric `UserID` decoded from the backend JWT. **Not** the MSAL `oid`, **not** email/username. Per [identity-rules.md](../identity-rules.md): raw numeric ID, no `user_` prefix; server-side rejects email-shaped IDs.
  - ⚠️ The MSAL `account.username` (email) and `account.name` are available transiently after redirect but are **not** retained in WMSSite state today — and must **not** be introduced as the identity key.
- **`page_viewed.normalized_route`** always normalized; `:patientId`/`:woundId` stripped before emit.
- **Session recording stays disabled** — replay is Phase 9.

## G. Conflict surface against current WMSSite `dev`

None. All 5 modified files exist on `dev` without analytics; both added files are net-new. Estimated PR diff: **~150–200 lines added, ~0 removed** (no telemetry to delete). Larger LOC than SCH_UI's port because the two helper files are written fresh rather than rewired.

## Open decisions

These feed Phase 7 implementation. **The audit enumerates options + trade-offs only — the call is human.**

### 7.3 — JS vs TS enforcement strategy
WMSSite is JavaScript, so the SDK's compile-time event-name/property safety (`EventName`, `PropsFor<E>` in [`events.ts`](../../packages/observability-client-js/src/events.ts)) is unavailable. Options:
- **A. Accept JS, runtime-guard.** Validate event name/props inside `analytics.js` at runtime; log/drop unknowns in dev. *Cheapest; no toolchain change. No compile-time guarantee — typos ship.*
- **B. Typed `analytics.d.ts` shim + `// @ts-check`.** Hand-write a `.d.ts` for the wrapper and enable `checkJs` on that file via `jsconfig.json`. *Editor + CI typo-catching with no migration. Shim drifts from the SDK's real types unless kept in sync.*
- **C. Convert touched files to `.ts`/`.tsx`.** Introduce `tsconfig.json` for just the 7 SDK-touched files. *Full type safety on the analytics surface. Opens a TS/JS mixed-build question for a 368-file JS app — scope creep risk.*

### 7.4 — MSAL identity rule
What does `identify()` receive, given MSAL/Entra auth? The SDK `identify()` takes a single string and [identity-rules.md](../identity-rules.md) mandates a PHI-free stable key. Options:
- **A. WMS-internal `UserID`** (decoded from the backend JWT in `PermissionsContext`). *Consistent with SCH and identity-rules verbatim; joins to WMS audit logs. Requires surfacing `UserID` into `login()` (today only `PermissionsContext` decodes it).* — aligns with the existing rules.
- **B. AAD `oid` (Entra object id).** *Stable, cross-app, no DB lookup. Opaque GUID that doesn't join to WMS's numeric `UserID`; introduces a second identity space vs SCH.*
- **C. Hash of `oid`/email.** *Extra de-identification. Unnecessary — `oid` already carries no PHI — and breaks audit-log joins. Adds a hashing dependency.*

### 7.8 — ErrorBoundary strategy
WMSSite has a **single app-wide** `ErrorBoundary` (no per-route boundaries) and **no** global `window` error listeners today. Options:
- **A. Instrument the existing boundary + add global listeners** (the SCH_UI shape). *Minimal, matches SCH. A render error below the single boundary takes down the whole tree before/while it reports.*
- **B. Add per-section boundaries around PHI-heavy routes** (IVR submit, eligibility) **and** instrument. *Localized failures + richer `feature_area` on exceptions. More files touched; widens this PR's scope.*
- **C. Boundary-only, no global listeners.** *Smallest surface. Misses async/unhandled-rejection errors that never hit a React boundary — the majority of API-layer failures.*

## Cross-references
- SCH_UI counterpart: [`sch-ui.md`](./sch-ui.md) — diverges on language (TS vs JS), auth (JWT vs MSAL), and starting point (rewire vs net-new).
- Identity rules: [`identity-rules.md`](../identity-rules.md).
- SDK surface: [`packages/observability-client-js/src/index.ts`](../../packages/observability-client-js/src/index.ts) (`InitOptions`, `identify`, `capturePageView`, `captureException`), [`/axios`](../../packages/observability-client-js/src/axios.ts) (`attachAxiosInterceptor`), [`/route`](../../packages/observability-client-js/src/route.ts) (`normalizeRoute`, `getFeatureArea`).
