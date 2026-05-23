# SCH_UI integration audit (Issue 6.2)

Inventory of the PostHog scaffolding on `SCH_UI@feature/posthog-implementation` (unmerged, 4 commits ahead of `dev` per [DEVELOPMENT_PLAN.md:402](../../DEVELOPMENT_PLAN.md#L402)). This audit drives the cherry-pick + rewire work in Issue 6.3.

**Source branch tip:** `d6a6285` (2026-04-29). **Target:** new `feature/adaptive-observability` branched off current `SCH_UI@dev`.

## A. Files added (2)

| Path | What it does | Port target |
|---|---|---|
| `sch-ui/src/services/analytics.ts` | Typed PostHog wrapper: `captureEvent()`, `identifyUser()`, `resetUser()`, `capturePage()` with compile-time `AllowedEventProperties` enforcement. Swallows errors. | Rewrite implementation against `@adaptivesoftwarellc/observability-client-js`; keep the export shape identical so call sites don't change. |
| `sch-ui/src/utils/routeUtils.ts` | `normalizeRoute()` (strips `:id`, `:uuid`, `:token`), `routeToFeatureArea()` (12 app sections), `normalizeEndpoint()`. | Either delegate to the SDK's normalizer (`Adaptive-MAN/packages/observability-client-js/src/route.ts`) or keep verbatim — the SDK's per-segment check is a strict improvement, see [DEVELOPMENT_PLAN.md:312](../../DEVELOPMENT_PLAN.md#L312). Decision: thin wrapper that calls SDK + appends SCH's richer `featureAreaMap`. |

## B. Files modified (5)

| Path | Adds | Cherry-pick? |
|---|---|---|
| `sch-ui/src/main.tsx` | ~20 lines: PostHog init, gated on `VITE_POSTHOG_KEY`/`VITE_POSTHOG_HOST`; session-recording UAT-only; super-properties `app: 'sch-ui'` + `environment`. | Replace with `init({ ingestUrl, apiKey, environment, releaseSha, debug })` call into adaptive SDK (signature from [`packages/observability-client-js/src/index.ts`](../../packages/observability-client-js/src/index.ts) `InitOptions`). Map from `VITE_OBSERVABILITY_URL`/`VITE_OBSERVABILITY_KEY`. Session recording → drop (replay is Phase 9). Super-properties (`app`) are not part of `InitOptions` — multi-app attribution comes from the server-side `applicationId` resolved off the API key. |
| `sch-ui/src/App.tsx` | ~60 lines: `RouteTracker` fires `capturePage()` on route change with normalized route + feature_area; global `error`/`unhandledrejection` listeners emit `frontend_exception`. | Port verbatim — call signature matches the SDK. |
| `sch-ui/src/services/apiClient.ts` | ~25 lines: Axios response interceptor emits `api_request_failed` with `status_code`, `endpoint_group`, `method`, `correlation_id`, `is_network_error`. | Delete the inline interceptor and replace with one line: `import { attachAxiosInterceptor } from '@adaptivesoftwarellc/observability-client-js/axios'; attachAxiosInterceptor(apiClient);`. The SDK's `/axios` entry point exports `attachAxiosInterceptor` (and `wrapFetch`); the underlying `captureFailedRequest` lives at the package root and is invoked internally by the interceptor. |
| `sch-ui/src/store/authStore.ts` | ~7 lines: on login, calls `identifyUser(userId, { roles, has_provider_link })` + fires `auth_login_success`; on logout, fires `auth_logout` + `posthog.reset()`. | Port verbatim — `roles` audit (Issue 6.1 prereq) must confirm role strings are generic. `posthog.reset()` becomes `analytics.reset()`. |
| `sch-ui/src/components/common/ErrorBoundary.tsx` | ~8 lines: `componentDidCatch` fires `frontend_exception` with `source: 'ErrorBoundary'`, `error_type`, `component_stack_depth`. | Port verbatim. |

## C. Environment variables added

| Variable | Notes |
|---|---|
| `VITE_POSTHOG_KEY` | **Drop.** Replace with `VITE_OBSERVABILITY_KEY`. |
| `VITE_POSTHOG_HOST` | **Drop.** Replace with `VITE_OBSERVABILITY_URL`. |

`.env.example` does not currently list either set — the PostHog branch documented them only in `documentation/POSTHOG_PLAN.md`. Issue 6.1 prereq: add `VITE_OBSERVABILITY_*` to `sch-ui/.env.example`.

## D. Dependencies

| Add | Remove |
|---|---|
| `@adaptivesoftwarellc/observability-client-js` (peer-loaded `axios` already in SCH; peer-loaded `react` already in SCH) | `posthog-js` — must not enter `package.json` per Issue 6.3 acceptance criteria |

`package-lock.json` regenerates as part of the integration PR.

## E. Phase 1 event inventory (must be preserved verbatim)

| Event | Properties | Identity | Notes |
|---|---|---|---|
| `auth_login_success` | `roles[]`, `has_provider_link` | `String(userId)` | Roles audit pending (6.1) |
| `auth_logout` | (none) | reset after emit | |
| `page_viewed` | `route` (normalized), `feature_area` | current `distinct_id` | RouteTracker driven |
| `api_request_failed` | `status_code`, `endpoint_group`, `method`, `correlation_id`, `is_network_error` | current `distinct_id` | Network errors use `status_code: 0` |
| `frontend_exception` | `source`, `error_type`, `component_stack_depth` (boundary only) | current `distinct_id` | No error message, no stack |

No event renames against `POSTHOG_EVENT_CATALOG.md`. Identity rules unchanged from [identity-rules.md](../identity-rules.md): human users keyed by `String(userId)` without prefix.

## F. Conflict surface against current SCH_UI `dev`

None. All 5 modified files exist on `dev` without analytics instrumentation; cherry-pick is additive. The only delta in dependencies is the new SDK + removal of the never-landed `posthog-js`. Estimated PR diff: ~150 lines added, ~0 removed (no PostHog code to delete on `dev`).

## G. PHI/PII review checkpoints

- **No usernames, emails, DOBs, raw URLs, query strings, request/response bodies, exception messages, stack traces, or JWTs in any captured event.** Verified against the property allowlist in `analytics.ts`.
- **Session recording stays disabled.** UAT-only `__IS_UAT__` gate from main.tsx is dropped — replay is Phase 9.
- **`page_viewed.route`** must always pass through `normalizeRoute()` before emit. Path segments matching `:id`, `:uuid`, `:token` are stripped before the event leaves the browser.
- **`identify()` props** never contain raw email or display name. `roles[]` audit (6.1) confirms generic role strings only.

## H. Open items feeding Issue 6.3

- **Decision needed at cherry-pick time:** import `routeUtils.ts` verbatim, or delete it and call the SDK's normalizer? Recommended: keep SCH's `featureAreaMap` (richer than SDK default) and delegate the path-stripping to the SDK.
- **SDK install method:** `@adaptivesoftwarellc/observability-client-js@0.1.0` is unpublished as of 2026-05-22. Issue 6.3 cannot start until the SDK is on npm. See [`.github/workflows/sdk-publish.yml`](../../.github/workflows/sdk-publish.yml) for the publish pipeline; user must add `NPM_TOKEN` secret.
- **Session bracketing:** SDK auto-brackets sessions per Issue 4.11. No manual `init({ trackSessions: false })` opt-out for SCH.
