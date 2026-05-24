# SCH_API integration audit (Issue 6.4)

Inventory of the PostHog scaffolding on `SCH_API@feature/posthog-implementation` (unmerged, 4 commits ahead of `dev` per [DEVELOPMENT_PLAN.md:402](../../DEVELOPMENT_PLAN.md#L402)). This audit drives the cherry-pick + rewire work in Issue 6.5.

**Source branch tip:** unmerged on SCH_API at audit time (2026-05-22). **Target:** new `feature/adaptive-observability` branched off current `SCH_API@dev`.

## A. Files added (5)

| Path | What it does | Port action |
|---|---|---|
| `src/SCH.Core/Interfaces/IAnalyticsService.cs` | Defines `Capture()`, `CaptureError()`, `Shutdown()`. Never throws; failures silent. | **Delete.** SCH adopts the SDK's `Adaptive.ObservabilityClient.IAnalyticsService` instead (decision recorded at [DEVELOPMENT_PLAN.md:301](../../DEVELOPMENT_PLAN.md#L301)). Update every `using SCH.Core.Interfaces` → `using Adaptive.ObservabilityClient`. |
| `src/SCH.Infrastructure/Services/Analytics/PostHogService.cs` | PostHog SDK wrapper (v2.5.0); 18-key allowlist; debug-logged failures. | **Delete.** Replace with `services.AddAdaptiveObservability(...)` — the SDK ships its own implementation. |
| `src/SCH.Infrastructure/Services/Analytics/NullAnalyticsService.cs` | No-op fallback. | **Delete.** SDK's `AddAdaptiveObservability(...)` no-ops when `Enabled: false`. |
| `src/SCH.Infrastructure/Services/Analytics/AnalyticsOptions.cs` | Config POCO: `Environment`, `ReleaseSha`. | **Replace** with the SDK's `AdaptiveObservabilityOptions` (config section renames `PostHog` → `AdaptiveObservability`). |
| `src/SCH.Infrastructure/Services/Analytics/AnalyticsIdentity.cs` | `GetDistinctId(ClaimsPrincipal)` → user id, `api_client_{id}`, or `anon`; `NormalizeRoute(path)` strips `/123/456` → `/{id}/{id}`. | **Port verbatim** as a SCH-internal helper. Identity rules ([identity-rules.md](../identity-rules.md)) preserved; the SDK does not provide a `ClaimsPrincipal` shortcut. |

## B. Files modified (4 + 8 BG services)

| Path | Adds | Port action |
|---|---|---|
| `src/SCH.API/Program.cs` | DI registration (`PostHog:Enabled` + non-empty API key gate); three dev-only test endpoints (`/api/dev/posthog-{test,500-test,job-fail-test}`). | Replace DI line with `services.AddAdaptiveObservability(builder.Configuration.GetSection("AdaptiveObservability"))`. Rename test endpoints to `/api/dev/observability-{test,500-test,job-fail-test}` and confirm `app.Environment.IsDevelopment()` gate (Issue 6.1). |
| `src/SCH.API/Middleware/GlobalExceptionMiddleware.cs` | Injects `IAnalyticsService`; emits `server_error_occurred` with HTTP method, status, correlation ID, auth type on 500s. | Port verbatim — `using Adaptive.ObservabilityClient` instead of `SCH.Core.Interfaces`. |
| `src/SCH.API/appsettings.json` | New `PostHog` section: `ApiKey`, `HostUrl`, `Enabled`, `Environment`, `ReleaseSha`. | Rename section `PostHog` → `AdaptiveObservability`; keep key names `ApiKey` / `HostUrl` / `Enabled` / `Environment` / `ReleaseSha` verbatim — they match [`AdaptiveObservabilityOptions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityOptions.cs) directly so the SDK binds via `services.Configure<AdaptiveObservabilityOptions>(config.GetSection("AdaptiveObservability"))`. Real values move to SCH's Key Vault for non-Dev. |
| `src/SCH.Infrastructure/SCH.Infrastructure.csproj` | Adds `PostHog.AspNetCore` v2.5.0. | Remove `PostHog.AspNetCore`; add `<PackageReference Include="AdaptiveSoftwareLLC.ObservabilityClient" Version="0.1.*" />`. (Namespace stays `Adaptive.ObservabilityClient` — only the nuget id differs.) |
| **8x background services** (see Section F) | Each injects `IAnalyticsService` and emits `background_job_failed` with `job_name` + `error_type` from catch blocks. | Port verbatim per service. ~10-line delta per file. |

## C. Configuration keys (in SCH_API appsettings)

| Drop | Add | Source |
|---|---|---|
| `PostHog:ApiKey` | `AdaptiveObservability:ApiKey` | Key Vault (`SchObservabilityApiKey`) for non-Dev; `appsettings.Development.json` for Dev |
| `PostHog:Enabled` | `AdaptiveObservability:Enabled` | Static per env |
| `PostHog:HostUrl` | `AdaptiveObservability:HostUrl` | `https://obs-api-dev.azurewebsites.net` for Dev; `https://obs-api-prod.azurewebsites.net` for Prod |
| `PostHog:Environment` | `AdaptiveObservability:Environment` | `Development` / `Production` |
| `PostHog:ReleaseSha` | `AdaptiveObservability:ReleaseSha` | Build-time CI inject (Issue 6.1 prereq) |

Key names match [`AdaptiveObservabilityOptions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityOptions.cs) verbatim — section rename is the only delta vs. the unmerged PostHog scaffolding.

## D. NuGet dependencies

| Remove | Add |
|---|---|
| `PostHog.AspNetCore 2.5.0` | `AdaptiveSoftwareLLC.ObservabilityClient` (target `0.1.*`) |

## E. Dev-only test endpoints

Three GET endpoints registered only when `app.Environment.IsDevelopment()` and `ExcludeFromDescription()`:

1. **`GET /api/dev/posthog-test`** → rename to **`/api/dev/observability-test`**; emits `dev_smoke_test`.
2. **`GET /api/dev/posthog-500-test`** → rename to **`/api/dev/observability-500-test`**; throws to exercise `GlobalExceptionMiddleware`.
3. **`GET /api/dev/posthog-job-fail-test`** → rename to **`/api/dev/observability-job-fail-test`**; emits `background_job_failed`.

Acceptance criteria for Issue 6.1: confirm `404` on these routes when `ASPNETCORE_ENVIRONMENT != Development`.

## F. Background-service inventory (8 services, all emit `background_job_failed`)

1. `BatchSubmissionBackgroundService`
2. `BillingTransferBackgroundService`
3. `ClaimDetailSyncBackgroundService`
4. `DmeResubmissionBackgroundService`
5. `PatientResyncBackgroundService`
6. `PhysicianSyncBackgroundService`
7. `StaleTaskNotificationBackgroundService`
8. `WoundSyncBackgroundService`

Each catch block uses `system:background-service` as `distinct_id` and includes `job_name` + `error_type` only — no exception message, no stack. BG dedup (Issue 4.8 static 15-min window) verified end-to-end once SCH_API hits `obs-api-dev`.

## G. Conflict surface against current SCH_API `dev`

None. All 5 added files are net-new; all 4 modified files exist on `dev` without analytics integration; all 8 BG services exist on `dev` without analytics-aware catch blocks. Cherry-pick is purely additive.

Estimated PR diff: ~13 files touched (excluding deleted PostHog files), ~200 lines added, ~0 removed.

## H. PHI/PII review checkpoints

- **`server_error_occurred`** carries `HTTP method`, `status code`, `correlation_id`, `auth_type`. **Never** the exception message, the stack trace, the request body, the response body, or any route segment that wasn't normalized.
- **`background_job_failed`** carries `job_name`, `error_type`. **Never** the exception message or any job-input payload.
- **Identity:** `GetDistinctId(ClaimsPrincipal)` returns `String(userId)` for human users, `api_client_{id}` for service principals, `anon` for anonymous (e.g. pre-auth) requests. Matches the identity rules verbatim.
- **`NormalizeRoute`** runs on every error emission so dynamic segments never leak.

## I. Open items feeding Issue 6.5

- **SDK install method:** Published as `AdaptiveSoftwareLLC.ObservabilityClient` on nuget.org (`Adaptive.*` prefix was reserved by another account, causing 409s on initial 0.1.0/0.1.1 attempts — see [DEVELOPMENT_PLAN.md](../../DEVELOPMENT_PLAN.md) for the diagnosis). The .NET namespace remains `Adaptive.ObservabilityClient` so SCH consumer code is unaffected by the registry-id change.
- **Correlation ID middleware:** SCH_API already has correlation-ID middleware (PostHog scaffolding consumes it); Issue 5.5 verification is a Dev-shakedown gate (Option A), not a 6.5 blocker.
- **Local SCH Dev appsettings:** must point at `https://obs-api-dev.azurewebsites.net` with a public-client + server API key minted via the Phase 8.9 admin endpoints (Issue 6.6).
