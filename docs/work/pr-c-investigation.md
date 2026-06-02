# PR C — Issue 8.7 audit logging (backend): investigation

**Branch:** `phase-8/7-audit-logging-backend`
**Scope:** Backend only. Extend audit-row coverage of existing admin endpoints and add a paginated read endpoint `GET /api/admin/audit`. No schema changes, no UI, no RBAC.

---

## Current state

The `AuditLogs` table shipped in 8.9 (`Phase8AdminAuditLog` migration). Shape confirmed in
[`AuditLog.cs`](../../backend/src/Observability.Domain/Audit/AuditLog.cs) and the mapping in
[`ObservabilityDbContext.cs`](../../backend/src/Observability.Infrastructure/Persistence/ObservabilityDbContext.cs):

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK, client-assigned default |
| `OccurredAt` | `DateTime` (UTC) | indexed |
| `Action` | `string(64)` required | indexed as `(Action, OccurredAt)` |
| `ActorType` | `string(32)` required | only `admin_key` today |
| `ApplicationId` | `Guid?` | nullable |
| `EnvironmentId` | `Guid?` | nullable |
| `CorrelationId` | `string(64)?` | from `CorrelationIdMiddleware` response header |
| `DetailsJson` | `nvarchar(max)` required | serialized action-specific payload |

Two existing indexes — `OccurredAt` and `(Action, OccurredAt)` — already cover the read endpoint's
sort (`OccurredAt DESC`) and its most common filter (`action=` exact match). **No new index needed.**

The auth gate is [`AddAdminKeyAuth`](../../backend/src/Observability.Api/Middleware/AdminKeyAuthExtensions.cs):
header `X-Observability-Admin-Key`, compared fixed-time against config `Observability:AdminApiKey`.
A `grep` for `AddAdminKeyAuth` confirms it is applied only to the `/api/admin` group in
[`AdminEndpoints.cs`](../../backend/src/Observability.Api/Endpoints/AdminEndpoints.cs) — there are
**no other admin-shaped endpoints** in the codebase today.

### Existing audit writes

The `WriteAuditAsync` helper (adds the row to the change-tracker; the caller's `SaveChangesAsync`
commits it in the same transaction as the mutation) is the established pattern.

| Endpoint | Action constant | Fields populated |
|---|---|---|
| `POST /api/admin/apps` | `admin.app.created` | `ApplicationId`, `CorrelationId`; details `{ slug, created, environments[] }` |
| `POST /api/admin/apps/{slug}/environments/{env}/keys` | `admin.key.minted` | `ApplicationId`, `EnvironmentId`, `CorrelationId`; details `{ key_id, key_type }` |

Both set `ActorType = "admin_key"`. Confirmed these are the **only** two emitters.

---

## Required changes by layer

### Proposed new audit writes

The scope guard says *don't add admin endpoints just to write rows*, and *only audit endpoints that
already exist or are needed for imminent work*. Applying that:

| Endpoint | Exists today? | Decision |
|---|---|---|
| `POST /api/admin/apps` | yes | already audited — no change |
| `POST .../keys` (mint) | yes | already audited — no change |
| Key **revoke** (`.../keys/{id}/revoke`) | **no** — lands in 10.6 | out of scope; file as side-issue. The revoke PR adds its own `admin.key.revoked` write. |
| Dashboard reads | yes | explicitly out of scope — unauthenticated today, revisited under 8.6 RBAC |

**Conclusion: no new audit *writes* in this PR.** Every admin endpoint that currently exists already
emits a row. The only gap 8.7 leaves open is the *read* surface, which is this PR's real deliverable.
(Originally I expected to add writers here; investigation shows the two existing endpoints are already
covered, so the work collapses to the read endpoint + tests. Noting this explicitly so review can
confirm the narrowed scope.)

### Read endpoint contract — `GET /api/admin/audit`

Added to the existing `/api/admin` group (inherits `AddAdminKeyAuth`).

**Query params:**

| Param | Type | Behavior |
|---|---|---|
| `action` | string | exact match on `Action` (uses the `(Action, OccurredAt)` index) |
| `app` | string | slug **or** Guid. If it parses as a `Guid`, match `ApplicationId` directly; otherwise resolve slug → id via one `Applications` lookup. Unknown slug → empty result set (not 404 — a filter that matches nothing). |
| `from` / `to` | timestamp | `OccurredAt >= from && OccurredAt < to`. Reuses the dashboard's `ResolveRange` semantics (default last 24h if both omitted; UTC-normalized). |
| `page` / `page_size` | int | reuses dashboard `ResolvePaging` — `page` 0-based, `page_size` default 50, max 200. |

**Sort:** `OccurredAt DESC`.

**Auth:** same `X-Observability-Admin-Key` gate. Missing/wrong key → 401.

**Response envelope** — matches `/api/dashboard/events`:

```json
{
  "total": 137,
  "page": 0,
  "page_size": 50,
  "rows": [
    {
      "id": "8f3c…",
      "occurred_at": "2026-06-02T14:11:08.42Z",
      "action": "admin.key.minted",
      "actor_type": "admin_key",
      "application_id": "1a2b…",
      "environment_id": "9c8d…",
      "correlation_id": "req-abc123",
      "details_json": "{\"key_id\":\"…\",\"key_type\":\"ServerApi\"}"
    }
  ]
}
```

**10.6 UI compatibility:** 10.6's audit-log page columns are *action, actor, app/env, when, details
summary*. The shape above carries all of them: `action`, `actor_type`, `application_id`/
`environment_id`, `occurred_at`, and `details_json` (the UI renders a summary client-side, same as the
dashboard's events table renders `properties_json`). The UI already loads `/api/apps`, so it can map
ids → app/env names client-side — consistent with how the dashboard resolves them today. (See open
question on whether to denormalize the slug server-side.)

### Retention recommendation

8.5 (per-app retention with a scheduled archive/delete job) **has not landed** — no retention worker
file exists yet, and 8.5's acceptance criteria are still open. So this PR only *documents* the
intended policy; it does not implement enforcement.

**Recommendation: audit rows retain longer than telemetry — default 365 days, configurable.**

- Rationale: audit rows are compliance-adjacent (who minted/created what, when). Telemetry events are
  operational and churn fast; audit rows are the paper trail.
- Surface the knob as `Observability:Retention:AuditLogDays` (default `365`), mirroring the
  per-app/per-type retention shape 8.5 will own. When 8.5's job lands, it sweeps `AuditLogs` by
  `OccurredAt < now - AuditLogDays`, **writing its own `admin.retention.swept` audit row per run**
  (8.5 already requires "audit log row per run" — that closes the loop: the retention job is itself
  audited).
- Precedent: 8.5 reserves a nullable `ReplayRetentionDays` column on `AppEnvironments`; audit
  retention can follow the same per-app-override-with-global-default pattern if needed later. Not
  needed now — a single global default is enough until a tenant asks for more.
- 10.6's design note (DEVELOPMENT_PLAN line ~989) already lists "audit log retention duration" as
  defined-here / enforced-by-8.5. This recommendation is consistent with that.

### Risk — premature audit writes on long-running operations

`WriteAuditAsync` enrolls the row in the change tracker and commits on the next `SaveChangesAsync`.
For the two current endpoints that is correct: the audit row commits atomically with the mutation.

The risk is for **future** long-running/streaming endpoints — specifically 10.5 bulk export. An
export that audits "export performed, N rows" must write the row **after** the stream completes with
the real row count, not at request start. Writing at start would (a) log a row count it doesn't know
yet and (b) record an export that may have failed mid-stream. **Guidance for 10.5: emit the export
audit row post-stream, inside the same scope, with the final row count.** Out of scope for this PR but
called out so the pattern isn't copied wrong.

No risk introduced by *this* PR — the read endpoint is a pure query (`AsNoTracking`, no writes).

---

## Test plan (Phase 2)

Add to [`AdminEndpointsTests.cs`](../../backend/tests/Observability.IntegrationTests/AdminEndpointsTests.cs)
(in-memory EF fixture, `IngestionWebApplicationFactory`):

- **Auth:** `GET /api/admin/audit` with missing key → 401; with wrong key → 401.
- **Empty list:** filter that matches nothing (`action=admin.nonexistent`) → `200`, `total = 0`,
  `rows = []`.
- **Pagination:** seed > `page_size` rows (mint several keys to generate `admin.key.minted` rows),
  assert `page=0` and `page=1` return disjoint sets and `total` is stable across pages.
- **Filter by action:** `action=admin.app.created` returns only rows with that action.
- **Filter by date range:** `from`/`to` bounding a subset returns only in-range rows; `OccurredAt DESC`
  order verified.
- (Bonus, cheap) **Filter by app slug and by app id** both resolve to the same rows.

Note: the existing fixture seeds apps/keys directly (not via the admin endpoints), so those seed rows
produce **no** audit rows. Tests that need audit rows will mint/create through the admin endpoints
first (as the existing `CreateApp`/`MintKey` tests already do), then read them back.

---

## Existing branch / PR findings

- Branch `phase-8/7-audit-logging-backend` created off `main` (`133827b`) for this work; no
  pre-existing branch or open PR for 8.7.
- 8.9 already shipped the table + the two writers + `AdminEndpointsTests`.
- 10.6 (`Self-service admin UI`, can land post-cutover) is the consumer; its acceptance criteria
  explicitly name this endpoint and say landing it closes 8.7's third criterion. This PR front-loads
  the endpoint so 10.6 is a pure UI addition.

---

## Open questions

1. **Denormalize app slug in the response?** Proposed: no — return `application_id`/`environment_id`
   and let the UI resolve via `/api/apps` (matches the dashboard). Cheap to add a server-side join
   later if 10.6 finds the client-side resolve awkward. Confirm we're OK deferring.
2. **`actor` filter param now?** Only `admin_key` exists today, so a filter would be a no-op. Proposed:
   omit until 8.6 RBAC introduces real actor identities. Confirm.
3. **Retention default 365 days** — confirm the number and the config key name
   (`Observability:Retention:AuditLogDays`). Pure documentation in this PR; no enforcement.
4. **Side-issue for key revoke** — OK to file `admin.key.revoked` audit write as part of the 10.6
   revoke-endpoint issue rather than a standalone issue?

---

**Recommendation:** scope is smaller than the prompt anticipated — no new audit *writes* are needed
(both existing admin endpoints already emit rows), so Phase 2 is the read endpoint + integration tests
+ the plan/retention doc updates. Requesting review before implementing Phase 2.
