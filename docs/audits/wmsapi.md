# WMSAPI integration audit (Issue 7.2)

Inventory of what changes when **WMSAPI** onboards onto adaptive-observability as Phase 7's second tenant. Read-only investigation — **no code was changed** in WMSAPI, SCH_API, or this repo. Counterpart to [`sch-api.md`](./sch-api.md); WMSAPI diverges materially (no exception middleware, no correlation ID, custom JWT not MSAL on the API), so Phase 7 builds **net-new infrastructure** rather than porting SCH_API's scaffolding.

**Source audited:** `WMSAPI@origin/dev` (`bdadaptivewoundmsllc/WMSAPI`), tip `ed42420` (2026-05-31, *feat(ivr): add provider + wound type to reviewer queue rows*). **Target:** new `feature/adaptive-observability` branched off `WMSAPI@dev`.

> **Note on org:** WMSAPI lives on the `bdadaptivewoundmsllc` org, while WMSSite is on `adaptivesoftwarellc`. Same product, two orgs — relevant for CI secret placement (`AdaptiveObservability:ApiKey`).

## Re-verification of the three plan claims (snapshot 2026-04-30 → confirmed on `dev` 2026-05-31)

| Claim | Verdict | Evidence |
|---|---|---|
| **No global exception middleware** | ✅ Confirmed | Only [`Middleware/RequestLoggingMiddleware.cs`] exists (request/timing logger, not error handling). No `UseExceptionHandler`, `IExceptionHandler`, or `ProblemDetails`. Unhandled exceptions hit ASP.NET Core's default 500. |
| **No correlation-ID middleware** | ✅ Confirmed | Zero matches for `CorrelationId` / `X-Correlation` / `correlation_id` / `TraceIdentifier` / `Activity.Current` / `traceparent`. No request-id propagation anywhere. |
| **MSAL/Entra (not JWT)** on the API | ⚠️ **Inverted vs WMSSite** | WMSAPI uses **custom symmetric-key JWT bearer** (`Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.4, `SymmetricSecurityKey` from a `JWT_KEY` env var) — **no** `Microsoft.Identity.Web` / MSAL. The MSAL surface is on WMS**Site**; the API validates a backend-issued JWT carrying `UserID` + `RoleID` claims. This is actually **closer to SCH_API** than the plan implied. |

> The "WMS uses MSAL" note in the plan is true for the **frontend** ([wmssite.md](./wmssite.md)) but **not** the API. For Phase 7.4 identity, the API side resolves `distinct_id` from its own JWT claims exactly like SCH_API — no Entra `oid` reaches the backend identity helper.

## Stack

`net8.0`, nullable + implicit usings on. Solution = 5 projects: **WMSAPI** (web API), **WMSAPI.Tests** (xUnit), and three console tools (`AdminTasks`, `MigrationRunner`, `EligibilityDiagnostic`). Relevant packages on the main project: `Dapper 2.1.66`, `Microsoft.EntityFrameworkCore(.SqlServer) 8.0.4`, `Microsoft.AspNetCore.Authentication.JwtBearer 8.0.4`, `Swashbuckle.AspNetCore 6.4.0`, `RestSharp 112.1.0`, `Azure.Storage.Blobs`, `Microsoft.Azure.Databricks.Client`. **No** observability/telemetry packages (no Serilog/OpenTelemetry/AppInsights) — the SDK is the first.

## A. Files added (4)

| Path | What it does | Lines |
|---|---|---|
| `Middleware/GlobalExceptionMiddleware.cs` | Catches **unhandled** exceptions; emits `server_error_occurred` (HTTP method, status, `correlation_id`, `auth_type`) via `IAnalyticsService.CaptureError`. **Net-new** — SCH_API ported an existing one ([sch-api.md §B](./sch-api.md#b-files-modified-4--8-bg-services)); WMSAPI has none. See [Open decision 7.6](#open-decisions) — the catch-everything controllers blunt this. | ~80 |
| `Middleware/CorrelationIdMiddleware.cs` | Reads/generates `X-Correlation-ID`, stashes on `HttpContext`, echoes on the response. **Net-new** — WMSAPI has zero correlation handling. See [Open decision 7.5](#open-decisions). | ~40–50 |
| `Services/Observability/AnalyticsIdentity.cs` | `GetDistinctId(ClaimsPrincipal)` → `String(UserID)` / `api_client_{id}` / `anon`; `NormalizeRoute(path)`. Ported in spirit from [sch-api.md §A](./sch-api.md#a-files-added-5); reads WMSAPI's `UserID` claim. The SDK ships [`RouteNormalizer`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/RouteNormalizer.cs) — delegate path-stripping to it, keep the `ClaimsPrincipal` shortcut local (SDK has no `ClaimsPrincipal` helper). | ~35 |
| `Http/CorrelationIdDelegatingHandler.cs` | `DelegatingHandler` that propagates the inbound correlation ID onto outbound `HttpClient` calls (Phase 7.5). **Net-new** — no `DelegatingHandler` exists today. See [Open decision 7.5](#open-decisions). | ~40 |

## B. Files modified (3 + 2 BG services)

| Path | Adds | Lines |
|---|---|---|
| [`Program.cs`] | DI: `services.AddAdaptiveObservability(builder.Configuration.GetSection("AdaptiveObservability"))` (the SDK provides this `IConfiguration` overload directly — [`ServiceCollectionExtensions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/ServiceCollectionExtensions.cs)). Register the delegating handler on `AddHttpClient` (line ~134). Insert `app.UseCorrelationId()` + `app.UseGlobalException…` into the pipeline (current order lines ~442–448: `HttpsRedirection → Routing → Cors → Authentication → Authorization → RequestLogging → MapControllers`). Correlation must run early (before/at Routing); exception middleware must wrap the pipeline but sit **after** auth so `auth_type`/claims are populated. | ~12–15 |
| [`appsettings.json`] | New top-level `AdaptiveObservability` section (`ApiKey`, `HostUrl`, `Enabled`, `Environment`, `ReleaseSha`) alongside existing `Logging` / `AllowedHosts` / `Databricks`. Binds to [`AdaptiveObservabilityOptions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityOptions.cs) verbatim. | ~8 |
| [`WMSAPI.csproj`] | `<PackageReference Include="AdaptiveSoftwareLLC.ObservabilityClient" Version="0.1.*" />` (namespace `Adaptive.ObservabilityClient`). | ~1 |
| **2× background services** (see §F) | Inject `IAnalyticsService`; emit `background_job_failed` (`job_name`, `error_type`, `distinct_id: system:background-service`) from existing catch blocks. SDK dedups identical `(job_name, error_type)` within `BackgroundJobDedupWindow` (default 15 min). | ~2–4 each |

## C. Configuration keys (WMSAPI appsettings)

| Add | Source |
|---|---|
| `AdaptiveObservability:ApiKey` | Server API key minted via Phase 8.9 admin endpoints; Key Vault for non-Dev, `appsettings.Development.json` for Dev |
| `AdaptiveObservability:HostUrl` | `https://obs-api-dev.azurewebsites.net` (Dev) / prod equivalent |
| `AdaptiveObservability:Enabled` | Static per env (SDK no-ops when `false`) |
| `AdaptiveObservability:Environment` | `Development` / `Production` |
| `AdaptiveObservability:ReleaseSha` | Build-time CI inject (Phase 7.2 prereq) |

Key names match `AdaptiveObservabilityOptions` exactly — no rename needed (unlike SCH_API, which renamed `PostHog` → `AdaptiveObservability`). Existing config uses `DotNetEnv` (`JWT_KEY` etc. from env); the observability key can follow the same env-injection path.

## D. NuGet dependencies

| Add | Remove |
|---|---|
| `AdaptiveSoftwareLLC.ObservabilityClient` (`0.1.*`; namespace `Adaptive.ObservabilityClient`) | None — no PostHog/telemetry package to remove |

## E. HttpClient surface (Phase 7.5 propagation)

- `builder.Services.AddHttpClient()` (Program.cs ~134) — generic `IHttpClientFactory`; **no** named/typed clients, **no** existing `DelegatingHandler`.
- `InsuranceService` consumes `IHttpClientFactory` → **auto-inherits** the correlation handler once registered on the factory.
- `DatabricksService` uses `new HttpClient()` directly → **bypasses** the factory; correlation ID will **not** propagate unless refactored to the factory. Flag for 7.5.

## F. Background-service inventory (2 services, both already catch + log)

| # | Service | Path | Catch today | Emit point |
|---|---|---|---|---|
| 1 | `BackgroundProcessingService` | [`Services/BackgroundProcessingService.cs`] | inner + outer try/catch → `IApplicationLogger.LogErrorAsync` | add `background_job_failed` in both catches (~2 LOC) |
| 2 | `IvrAttachmentOrphanSweepService` | [`Services/IVR/IvrAttachmentOrphanSweepService.cs`] | try/catch (skips `OperationCanceledException`) → `ILogger.LogError` | add `background_job_failed` in the `Exception` catch (~2 LOC) |

Far fewer than SCH_API's 8 ([sch-api.md §F](./sch-api.md#f-background-service-inventory-8-services-all-emit-background_job_failed)). Catch blocks carry `job_name` + `error_type` only — never the exception message or job-input payload.

## G. Per-controller try/catch (conflicts with global exception middleware)

**27 controllers; nearly every action wraps its body in `try { … } catch (Exception ex) { return StatusCode(500, new { message, error = ex.Message }); }`.** High-volume examples: `UsersController` (~35 catches), `EligibilityController` (~32), `InsuranceController` (~53), `PatientController` (~29).

Two consequences:
1. **The new `GlobalExceptionMiddleware` will rarely fire** — exceptions are swallowed in-controller and never bubble. A net-new middleware alone yields almost no `server_error_occurred` coverage.
2. **`ex.Message` is currently returned in HTTP 500 bodies** — a pre-existing PHI-leak risk WMSAPI ships today (independent of this work, but worth flagging to the Phase 7 owner).

Reconciliation is [Open decision 7.6](#open-decisions).

## H. Identity surface

Custom JWT, symmetric key. Token (built in `UsersController.BuildAccessToken`) carries `UserID` + `RoleID` claims. Controllers read `User.FindFirst("UserID")?.Value`. Authorization policies: `Administrator` (`RoleID == "3"`), `IvrReviewer` (non-empty `RoleID`). `AnalyticsIdentity.GetDistinctId` reads `UserID` → `String(userId)`, falling back to `anon` for unauthenticated requests — matches [identity-rules.md](../identity-rules.md). No Entra `oid` on the API side.

## I. PHI/PII review checkpoints

- **`server_error_occurred`** carries HTTP method, status, `correlation_id`, `auth_type` — **never** the exception message, stack, request/response body, or unnormalized route. (Note the existing `ex.Message`-in-500-body behavior in §G is a *separate* pre-existing leak to address.)
- **`background_job_failed`** carries `job_name`, `error_type` only.
- **`distinct_id`** = `String(UserID)` / `api_client_{id}` / `anon` per [identity-rules.md](../identity-rules.md); server rejects email-shaped IDs.
- **`NormalizeRoute`** runs on every emission so `/patients/123`-style segments never leak.

## J. Conflict surface against current WMSAPI `dev`

None. All 4 added files are net-new; the 3 modified files + 2 BG services exist on `dev` without analytics. Cherry-pick is additive. Estimated PR diff (using global middleware, **not** touching the 27 controllers): **~13 files, ~230 lines added, ~0 removed**. If per-controller emission is chosen instead, add ~150–200 lines across controllers — see 7.6.

## Open decisions

These feed Phase 7 implementation. **The audit enumerates options + trade-offs only — the call is human.**

### 7.5 — Correlation-ID middleware shape
No correlation infra exists. Options:
- **A. Custom `CorrelationIdMiddleware` reading/writing `X-Correlation-ID`** (SCH-style). *Full control, simple header contract. Yet another bespoke middleware to own; ignores W3C tooling.*
- **B. Adopt W3C `traceparent` via `System.Diagnostics.Activity`.** *Standards-based, future OpenTelemetry-ready, framework-native. Heavier concept; WMSSite/SDK must agree on the header.*
- **C. Reuse ASP.NET Core's `HttpContext.TraceIdentifier`.** *Zero new middleware. Not propagated cross-service and not stable/shaped for the SDK — weakest for distributed tracing.*

Sub-question (7.5b): `DatabricksService`'s `new HttpClient()` bypasses the factory — refactor it onto `IHttpClientFactory` for propagation, or accept a propagation gap there?

### 7.6 — Global exception middleware vs per-controller reconciliation
27 controllers already catch-and-500, so a global middleware alone is nearly inert (§G). Options:
- **A. Global middleware only.** *One file, no controller churn. Captures only genuinely-unhandled exceptions — near-zero given current catch coverage. Low value until controllers change.*
- **B. Emit `server_error_occurred` from each controller catch block.** *Complete coverage immediately. ~150–200 lines across 27 controllers; large diff; easy to miss a block.*
- **C. Hybrid — global middleware now + funnel existing catches through one helper** (e.g. emit inside `IApplicationLogger.LogErrorAsync`, which the catches already call). *Broad coverage with a small diff by hooking the shared logger; also the natural place to stop returning `ex.Message` in 500 bodies. Couples analytics to the logging path — needs care so a logging failure never breaks request handling.*

### 7.7 — Background-service wiring (per service)
Two services, both already catch + log (§F). Options:
- **A. Emit `background_job_failed` directly in each catch** (SCH-style, explicit `distinct_id: system:background-service`). *Explicit and greppable. Duplicated 2-line block per service.*
- **B. Route both through a shared `IApplicationLogger` hook** (same funnel as 7.6-C). *One wiring point, DRY. Less explicit; job-name attribution must be threaded through the logger call.*
- **C. `IvrAttachmentOrphanSweepService` uses `ILogger`, `BackgroundProcessingService` uses `IApplicationLogger`** — unify the logging path first, then hook once. *Cleanest long-term. Pulls a small refactor into Phase 7 scope.*

## Cross-references
- SCH_API counterpart: [`sch-api.md`](./sch-api.md) — WMSAPI has **no** exception/correlation middleware to port (SCH_API did), **fewer** BG services (2 vs 8), and a heavier per-controller catch problem.
- Frontend counterpart: [`wmssite.md`](./wmssite.md) — MSAL lives there, not on the API.
- Identity rules: [`identity-rules.md`](../identity-rules.md).
- SDK surface: [`AdaptiveObservabilityOptions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityOptions.cs), [`ServiceCollectionExtensions`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/ServiceCollectionExtensions.cs) (`AddAdaptiveObservability`), [`IAnalyticsService`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/IAnalyticsService.cs), [`RouteNormalizer`](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/RouteNormalizer.cs).
