# PR A2 — Investigation: Pre-cutover gates (8.8 + 10.1 + 10.2 + 10.3)

Branch: `phase-prod/cutover-gates`. This is the **Phase 1** deliverable per [`pr-a2-cutover-gates.md`](pr-prompts/pr-a2-cutover-gates.md). **No implementation code has been written yet** — this doc documents state-before-code for each gate, proposes an approach, and stops for review.

All file references are at the current `phase-prod/cutover-gates` HEAD (branched from `main` @ `af5b59d`).

---

## 8.8 — Rate limiting + payload size limits

### What exists today
- [`Program.cs`](../../backend/src/Observability.Api/Program.cs): **no rate limiting, no body-size cap.** Pipeline is `CorrelationIdMiddleware` → dev-only CORS shim → endpoints. No `AddRateLimiter` / `UseRateLimiter`.
- [`ApiKeyAuthExtensions.cs:11-33`](../../backend/src/Observability.Api/Middleware/ApiKeyAuthExtensions.cs#L11-L33): the api-key resolves to a `ResolvedApiKey(ApplicationId, EnvironmentId, KeyType)` inside an **endpoint filter** on the `/api/ingest` group — i.e. it runs *during endpoint execution*, after the middleware pipeline.
- `JsonOptions` (lines 19-25) controls serialization only; it does **not** bound request-body size.

### What's missing
- A per-application request rate limit on the ingest surface.
- A 64 KB payload cap on ingest (Issue 8.8 acceptance criterion).
- A `429` + `Retry-After` shape.

### Proposed approach (no new dependency)
**Rate limiting** — built-in `Microsoft.AspNetCore.RateLimiting` (ships in the framework; no package add, satisfies the scope guard):
```csharp
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra))
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)ra.TotalSeconds).ToString();
        return ValueTask.CompletedTask;
    };
    o.AddPolicy("ingest", http => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: IngestPartitionKey(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = …, Window = TimeSpan.FromSeconds(…) }));
});
…
app.UseRateLimiter();              // after CorrelationId, before endpoints
ingest.RequireRateLimiting("ingest");
```

> **⚠️ Partition-key nuance (decision needed).** The plan suggests partitioning on the resolved `ApplicationId`. But `UseRateLimiter` runs in the **middleware** pipeline, *before* the `AddApiKeyAuth` endpoint filter resolves the key — so `http.GetResolvedApiKey()` is not yet populated when the partitioner runs. Two options:
> - **(a)** Partition on the raw `X-Observability-Key` header value (hashed). Simple, no re-resolution, but rate-limits *per key* not *per app* (an app with public+server keys gets two buckets).
> - **(b)** Resolve the key inside the partitioner (extra `IApiKeyResolver` call per request, cached) to partition on true `ApplicationId`.
> Recommend **(a)** for this PR — per-key limiting is the right DoS granularity anyway, and avoids a second hash/db hit on the hot path. Flagging because the prompt assumed `ApplicationId`.

**Payload cap** — body size is a Kestrel/endpoint concern, not `JsonOptions`. Apply per-endpoint metadata on the ingest group capping at 64 KB (`IRequestSizeLimitMetadata` / a small middleware reading `Content-Length` + `IHttpMaxRequestBodySizeFeature`), returning `413 Payload Too Large`. Kestrel's global default is 30 MB; we tighten only `/api/ingest/*`.

### Risk
- Rate-limit too tight → drops legitimate SCH burst traffic (e.g. error storms). Numbers must be sized against expected SCH volume — **need a target RPS** (open question).
- A body-size middleware scoped too broadly could clip dashboard/admin payloads (admin app-create is small, but flag). Scope strictly to `/api/ingest`.
- Partition-key choice (above) changes the blast radius of a noisy key.

### Test plan
- Integration test: burst N+1 requests within the window on `/api/ingest/events` → expect `429` with a `Retry-After` header on the rejected one.
- Integration test: POST a >64 KB body to `/api/ingest/events` → expect `413`; a normal body still `202`.
- Confirm dashboard/admin endpoints are unaffected by the cap.

---

## 10.1 — Multi-tenant isolation regression test

### What exists today (the good news — isolation is already enforced on ingest)
- [`IngestionEndpoints.cs:21-23`](../../backend/src/Observability.Api/Endpoints/IngestionEndpoints.cs#L21-L23):
  ```csharp
  var key = http.GetResolvedApiKey();
  var ctx = new IngestionContext(key.ApplicationId, key.EnvironmentId, correlationId);
  ```
  The tenant id comes from the **resolved key**, never the client.
- [`IngestionService.cs:63`](../../backend/src/Observability.Application/Ingestion/IngestionService.cs#L63) (and `:137` for errors): `ApplicationId = context.ApplicationId` — persisted id is the server-resolved one.
- [`IngestionDtos.cs:5-10`](../../backend/src/Observability.Application/Ingestion/IngestionDtos.cs#L5-L10): `EventIngestionRequest` has **no `application_id` field at all**. A spoofed `application_id` in `properties` is silently dropped by the allowlist (not in any event's allowed set; not forbidden) — so it can't even reach a column.

**Conclusion:** the ingestion path is structurally immune to cross-tenant spoofing. The 10.1 test *proves and locks in* that property; it does not fix a bug.

### What's missing
- No regression test that a second tenant's id can't be written via tenant A's key.
- The factory ([`IngestionWebApplicationFactory.cs`](../../backend/tests/Observability.IntegrationTests/IngestionWebApplicationFactory.cs)) seeds **one** app/env/key set — needs a second tenant.

### Known gap (NOT closed here — belongs to 8.6 RBAC)
- [`DashboardEndpoints.cs:8-11`](../../backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs#L8-L11) and [`SessionEndpoints.cs:14-15`](../../backend/src/Observability.Api/Endpoints/SessionEndpoints.cs#L14-L15): dashboard + timeline reads accept `?app=`/`{sessionId}` **unauthenticated**. Today, anyone on the trusted network can read any tenant's data by changing `?app=`. This is the documented Phase 8 RBAC TODO; **10.1 scopes to ingestion isolation only** and flags dashboard isolation as a 8.6 must-close.

### Proposed approach
Extend the factory with a second tenant (App B + Production env + its own `aoserv_…` server key), then a new `MultiTenantIsolationTests.cs`:
1. App A's server key + payload carrying a spoofed `application_id` in `properties` → assert the persisted `EventRecord.ApplicationId == AppA.Id` (and no row exists for App B).
2. App A's server key + dashboard `GET /api/dashboard/errors?app=<AppB.Id>` → **today returns App B's data**. Assert current behavior *and* mark with an explicit `// KNOWN GAP — 8.6 RBAC must make this 403/empty` so the test documents the gap rather than hiding it.
3. Repeat the read-isolation observation for `/api/dashboard/events` and `/api/sessions/{id}/timeline`.

> **Decision needed:** for test #2/#3, do we (a) assert-and-document the current leaky behavior now and convert to a 403 assertion when 8.6 lands, or (b) write them as skipped/`xfail` placeholders? Recommend **(a)** — a green test asserting the *known* current behavior with a loud comment is honest and flips cleanly in 8.6.

### Risk
- The shared `IClassFixture` factory is seeded once (`Interlocked` guard) — adding App B there touches every test that uses the factory. Low risk (additive), but worth a full-suite run.

### Test plan
The three methods above, plus a sanity assertion that App B's own key writes App B's id.

---

## 10.2 — PHI allowlist regression canary (scheduled)

### What exists today
- [`PropertyAllowlistValidator.cs:34-46`](../../backend/src/Observability.Application/Ingestion/PropertyAllowlistValidator.cs#L34-L46): forbidden keys → `422` + a persisted `SafetyViolation`; unknown keys dropped silently.
- [`EventCatalog.cs:55-67`](../../backend/src/Observability.Application/Ingestion/EventCatalog.cs#L55-L67): the **full forbidden-field list** (case-insensitive):
  ```
  email, username, display_name, displayName, first_name, last_name, name, full_name,
  dob, date_of_birth, ssn, raw_url, url, query_string, querystring,
  request_body, response_body, exception_message, error_message, message,
  stack_trace, stack, component_stack,
  jwt, token, access_token, refresh_token, bearer, password,
  policy_id, insurance_id, member_id, user_id
  ```
- Unit coverage exists ([`PropertyAllowlistValidatorTests.cs`](../../backend/tests/Observability.UnitTests/PropertyAllowlistValidatorTests.cs)) and an integration test ([`IngestionEndpointsTests.cs:135-165`](../../backend/tests/Observability.IntegrationTests/IngestionEndpointsTests.cs#L135-L165)). What's missing is a **deployed-environment** canary that proves the *running* Dev/Prod service still rejects PHI.
- Existing workflows: `backend.yml`, `sdks.yml`, `frontend.yml`, `sdk-publish.yml`. Canary goes alongside.
- Provisioning pattern: [`onboard-sch.ps1`](../../scripts/onboard-sch.ps1) hits `/api/admin/apps` + `/api/admin/.../keys` ([`AdminEndpoints.cs`](../../backend/src/Observability.Api/Endpoints/AdminEndpoints.cs)) with the KV admin key.

### What's missing
- `.github/workflows/canary.yml` (cron + `workflow_dispatch`).
- A `canary-test` app + server key in Dev and Prod.
- A cleanup/namespacing story so canary rows don't pollute SCH's dashboard.

### Proposed approach
- **Provisioning:** a small idempotent `scripts/provision-canary.ps1` modeled on `onboard-sch.ps1` — creates a `canary-test` app (Dev + Prod) and mints one `server_api` key per env, stored as GitHub secrets (`CANARY_KEY_DEV`, `CANARY_KEY_PROD`) and the canary app id (`CANARY_APP_ID`).
- **Canary job:** POST a known-forbidden field (e.g. `email`) to `/api/ingest/events` against the deployed env → **expect `422` `allowlist_violation`**. A `202` means the allowlist regressed → fail the job.
- **Failure path:** `gh issue create` on failure (per prompt). Teams webhook deferred to 8.4.
- **Local mirror:** an integration test running the same forbidden-field assertion in-process, so the canary logic is covered even when the workflow can't run.

> **Cleanup — decision needed.** Two options from the prompt:
> - **(a)** Filter `application_id == CANARY_APP_ID` out of dashboard queries. Cheap, no schema change, but **touches [`DashboardEndpoints.cs`](../../backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs)** — borderline against the "don't expand" guard, though the guard explicitly allows "filter or namespace clearly."
> - **(b)** Let 8.5 retention prune canary rows aggressively — but **8.5 isn't landed**, so this PR can't depend on it.
> Recommend **(a)**, implemented as a single shared `IsCanary` predicate, clearly commented. Also: the canary can send forbidden fields that hit the **`SafetyViolations`** table (not `Events`), so confirm whether violation rows also need namespacing/filtering on the dashboard (they aren't surfaced on the current dashboard cards — likely fine to leave).

### Risk
- A flaky network call to a deployed env makes the canary noisy → auto-files spurious issues. Mitigate with a small retry + only filing after consecutive failures.
- Provisioning script run against Prod mints a real key — must be stored as a secret, never logged (mirror `onboard-sch.ps1`'s "shown once" handling).

### Test plan
- Local integration test: forbidden field → `422` + `SafetyViolation` row (already partially covered; add the canary-shaped assertion).
- Dry-run `workflow_dispatch` against `obs-api-dev` before enabling cron.

---

## 10.3 — Self-monitoring + SLOs

### What exists today
- [`HealthEndpoints.cs`](../../backend/src/Observability.Api/Endpoints/HealthEndpoints.cs): `/health` returns `{status, version, sha}`. Both deploy jobs in [`backend.yml`](../../.github/workflows/backend.yml#L93-L107) smoke `/health` post-deploy. That's the only health signal today.
- **No external uptime monitoring, no `docs/slo.md`, no outage runbook.** `docs/` has architecture/provisioning/KV runbooks but nothing for platform availability.

### What's missing
- A stated SLO doc and an external availability monitor with alerting.
- A first-response runbook.

### Proposed approach
- `docs/slo.md` with the Issue 10.3 numbers: 99.5% availability on `/api/ingest/*` (rolling 30-day), p95 ingest latency < 200 ms, and error-budget burn-rate alert thresholds.
- `docs/runbooks/platform-outage.md`: uptime check fires → App Service health → SQL health → Key Vault access → restart App Service → escalate to Brandon.
- **Monitor:** the infra resource itself (Azure Monitor Availability Test, or whatever Adaptive already uses) is mostly a portal/IaC action, not repo code — **see open question #5**. I'll document the chosen tool's config in `docs/slo.md` and, if it's Azure Monitor, capture the resource definition (one per env).

### Risk
- Availability test config lives outside the repo (Azure portal) unless we add IaC — drift risk. Documenting the exact settings in `slo.md` mitigates.
- SLO numbers are only meaningful if latency is actually measured; `/health` ≠ ingest latency. May need a follow-up to emit ingest latency metrics (flag, don't build here).

### Test plan
- Docs review (numbers match Issue 10.3).
- Manual: trigger the availability test against `obs-api-dev`, confirm it alerts.

---

## Cross-cutting — Open questions — RESOLVED at review (2026-06-01)

1. **8.8 rate-limit numbers** → **Conservative default: 100 req / 10 s per key**, config-tunable. Adjust after observing SCH volume.
2. **8.8 partition key** → **Per-API-key (option a)** — avoids the middleware-vs-endpoint-filter ordering problem and a second hash/db hit on the hot path.
3. **10.1 dashboard tests** → **Assert-and-document (option a)** — green tests asserting the *current* leaky read behavior with a loud `// KNOWN GAP — 8.6` comment; flip to 403/empty assertions when 8.6 lands.
4. **10.2 canary cleanup** → **Filter `CANARY_APP_ID`** out of dashboard queries via a shared predicate (allowed by the "namespace clearly" scope guard).
5. **10.3 uptime tool** → **No existing monitor; set up Azure Monitor Availability Tests** (new) against `obs-api-dev` + `obs-api-prod`, reusing the existing Azure subscription.
6. **10.3 infra-as-code** → **Document the portal config in `slo.md`** (one resource per env); IaC deferred as a possible follow-up.
7. **CODEOWNERS** → No action; A1 already added `.github/workflows/canary.yml`. This PR creates that file. Owner mapping unchanged.

---

## Phase 2 (after approval) — implementation order

Per the prompt, land in this order so each gate reads independently in history:
1. **10.3 docs** — `docs/slo.md` + `docs/runbooks/platform-outage.md` + availability-test config for `obs-api-prod` (+ Dev).
2. **10.1 tests** — extend factory with App B; new `MultiTenantIsolationTests.cs` (3 methods + sanity).
3. **8.8 middleware** — `AddRateLimiter` + `Retry-After` + 64 KB cap on `/api/ingest`; 429/413 integration tests.
4. **10.2 canary** — `.github/workflows/canary.yml` + `scripts/provision-canary.ps1` + dashboard `CANARY_APP_ID` filter + local mirror test.

**Scope held:** all four gates in one PR (intentional); no 8.6 RBAC / dashboard auth / 8.7 audit work; no new third-party deps; canary rows namespaced so they don't pollute SCH's dashboard.

**Stop here and request review.**
