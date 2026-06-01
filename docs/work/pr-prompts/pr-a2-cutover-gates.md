# PR A2: Pre-6.8 SCH-Prod-cutover gates (8.8 + 10.1 + 10.2 + 10.3)

## Branch
`phase-prod/cutover-gates`

## Goal
Land four pre-cutover hardening gates as a single coherent PR so reviewers can read the "Prod-readiness" story as one unit:

- **8.8** — Rate limiting + payload size limits
- **10.1** — Multi-tenant isolation regression test
- **10.2** — PHI allowlist regression canary (scheduled)
- **10.3** — Platform self-monitoring + SLOs

Issue 6.8 (SCH Prod cutover) should not execute until this PR is merged.

## Context

The platform is a custom PostHog replacement. SCH is the first onboarded tenant; WMS will be the second. Before SCH Prod traffic flows, the platform should:
- Refuse DoS-shaped requests (8.8)
- Have a test that proves cross-tenant isolation cannot leak data (10.1)
- Have a scheduled job confirming the PHI allowlist still rejects forbidden fields (10.2)
- Be externally monitored with stated SLOs (10.3)

## What to investigate

For each gate, document state-before-code:

### 8.8 — Rate limiting + payload limits
- Read `backend/src/Observability.Api/Program.cs` and `backend/src/Observability.Api/Middleware/*` to see if any rate limiting is configured today.
- Look at how ASP.NET Core 8's built-in `AddRateLimiter` integrates (no new package needed).
- Read the api-key resolution at `backend/src/Observability.Api/Middleware/ApiKeyAuthExtensions.cs` — for per-application rate limiting, the resolved `ApplicationId` is the partition key.
- Default ingest payload size in `Microsoft.AspNetCore.Http.Json` options — what is it, and how do we cap at 64 KB per Issue 8.8's acceptance criterion?
- 429 + `Retry-After` shape — ASP.NET Core 8 includes a built-in `OnRejected` hook; use that.

### 10.1 — Multi-tenant isolation test
- Read `backend/src/Observability.Application/Ingestion/IngestionService.cs` (or wherever event creation lives) and confirm the `ApplicationId` field on the persisted record comes from the **resolved key**, not the client payload. Quote the line.
- Read `backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs` — the dashboard endpoints accept `?app=` and don't currently authenticate. **This is a known gap** that 8.6 RBAC closes; scope the 10.1 test to the *ingestion* path for now and note dashboard isolation as a follow-up that 8.6 must close.
- Read `backend/tests/Observability.IntegrationTests/IngestionWebApplicationFactory.cs` for the existing seeding pattern. Extend with a second app + env + key set.
- Tests to write:
  1. App A's `aoserv_…` key + payload with spoofed `application_id` field → persisted row has App A's id
  2. App A's `aoserv_…` key + dashboard query `?app=<App-B-id>` → empty result or 403 (decide; today it would return B's data — note that as a known gap for 8.6)
  3. Repeat for `/api/dashboard/errors`, `/api/sessions/{id}/timeline`

### 10.2 — PHI allowlist canary
- Read `backend/src/Observability.Application/Ingestion/PropertyAllowlistValidator.cs` (or its current location — search for `forbidden` or `SafetyViolation`) to get the **full forbidden-field list**.
- Find existing GitHub Actions workflow patterns: `.github/workflows/backend.yml`, `.github/workflows/sdks.yml`. The canary workflow goes alongside these.
- Decide: where do the canary's app + key live?
  - Recommended: a `canary-test` app row seeded via the 8.9 admin endpoints in Dev + Prod
  - Provisioning script: extend `scripts/onboard-sch.ps1` pattern or add a small idempotent provisioning script
- Cleanup strategy: rows the canary creates should not pollute long-term storage. Either:
  - Filter them out by `application_id == canary_app_id` in dashboard queries (cheap, no schema change)
  - Or have 8.5 retention prune them aggressively (need 8.5 to land first)
- Failure path: GitHub issue auto-creation via `gh issue create`. Teams webhook is nice-to-have but adds a config dependency — skip until 8.4.

### 10.3 — Self-monitoring + SLOs
- Search the docs and ask the user whether Adaptive already uses a particular uptime tool. Reuse if so.
- If not, recommend **Azure Monitor Availability Tests** (cheapest path; already in the subscription; one resource per environment).
- Author `docs/slo.md` with the numbers from Issue 10.3:
  - 99.5% availability on `/api/ingest/*` (rolling 30-day)
  - p95 ingest latency < 200ms
  - Error budget burn-rate alert thresholds
- Author `docs/runbooks/platform-outage.md` with first-response steps: "uptime check fires → check App Service health → check SQL health → check KV access → restart App Service → if still down, escalate to Brandon."

## Deliverable

### Phase 1 — investigation doc
File: `docs/work/pr-a2-investigation.md`

Per-gate sections:
- **What exists today** (file paths + summary, ≤5 lines)
- **What's missing**
- **Proposed approach** (which library, which pattern, sample code shape)
- **Risk** — what could break, what regressions could slip
- **Test plan**

Plus a cross-cutting section:
- **Open questions for human approval before implementation**

Stop here and request review.

### Phase 2 — implementation (after approval)
Land in this order so each gate reads independently in commit history:
1. **10.3 docs first** — `docs/slo.md` + `docs/runbooks/platform-outage.md` + the Azure Monitor Availability Test resource (or equivalent) configured for `obs-api-prod`
2. **10.1 integration tests** — new test file, second-tenant seeding in the existing factory, three test methods
3. **8.8 middleware** — rate limiter registration + payload size middleware + `Retry-After` configuration + integration test confirming 429 on burst
4. **10.2 canary workflow** — `.github/workflows/canary.yml` (cron + workflow_dispatch), the canary-app provisioning script, integration test that runs the same checks locally

## Scope guards
- All four gates in one PR is intentional. Don't split.
- Don't expand into 8.6 RBAC, dashboard auth, or 8.7 audit-logging — those are separate PRs.
- 10.2 canary must not write rows that pollute SCH's dashboard once SCH is onboarded. Filter or namespace clearly.
- Don't introduce new third-party dependencies — built-in ASP.NET Core rate limiter is enough for 8.8.

## Expected effort
~3–4 days. Investigation doc ~half-day; implementation ~3 days.
