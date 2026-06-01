# PR C: Issue 8.7 audit logging — backend only

## Branch
`phase-8/7-audit-logging-backend`

## Goal
Extend `AuditLogs` usage so every admin endpoint writes a row, then expose a paginated `GET /api/admin/audit` endpoint. Sets up the data + API surface that the Phase 10.6 admin UI will consume.

**Backend-only PR.** UI ships with 10.6.

## Context

The `AuditLogs` table already exists (shipped in 8.9). Today only `POST /api/admin/apps` and the key-mint endpoint write rows. As 10.5 (export) and 10.6 (admin UI) land, more admin actions will need audit coverage — and the dashboard read view will need a read endpoint. This PR puts both in place now so the 10.5 / 10.6 PRs are pure additions, not retrofits.

This PR also closes the third acceptance criterion of Issue 8.7 ("Read-only audit view"). Mark it as such in the implementation commit.

## What to investigate

1. **Existing table shape** — read [`backend/src/Observability.Domain/Audit/AuditLog.cs`](../../../backend/src/Observability.Domain/Audit/AuditLog.cs) and the `AuditLogs` mapping in [`backend/src/Observability.Infrastructure/Persistence/ObservabilityDbContext.cs`](../../../backend/src/Observability.Infrastructure/Persistence/ObservabilityDbContext.cs). Confirm fields: `Id`, `OccurredAt`, `Action`, `ActorType`, `ApplicationId`, `EnvironmentId`, `CorrelationId`, `DetailsJson`.

2. **Existing audit-row writers** — read [`backend/src/Observability.Api/Endpoints/AdminEndpoints.cs`](../../../backend/src/Observability.Api/Endpoints/AdminEndpoints.cs). The `WriteAuditAsync` helper is the established pattern. Confirm only `admin.app.created` and `admin.key.minted` are emitted today.

3. **Endpoints that should also audit but don't yet**:
   - Key revoke (doesn't exist yet — file as a separate side-issue; out of scope for this PR)
   - Dashboard reads — explicitly out of scope (read access is unauthenticated today; will be revisited under 8.6 RBAC)
   - Anything else admin-shaped — grep for `AddAdminKeyAuth` in `Program.cs`

4. **Read endpoint contract** — `GET /api/admin/audit`:
   - Query params: `action=` (exact match), `app=` (slug or id), `from=` / `to=` (timestamps), `page=` / `page_size=`
   - Response envelope: match the existing `/api/dashboard/events` shape — `{ total, page, page_size, rows: [...] }`
   - Sort: `OccurredAt DESC`
   - Auth: same `X-Observability-Admin-Key` gate as the existing admin endpoints

5. **Retention question** — Do `AuditLogs` rows accumulate forever, or fall under 8.5 retention? Document the decision in the investigation doc; lean toward "longer than telemetry — 1 year default, configurable" since these are compliance-adjacent.

6. **10.6 UI compatibility** — read Issue 10.6's acceptance criteria in `DEVELOPMENT_PLAN.md`. The audit-log page in 10.6 will consume this endpoint. Make sure the response shape supports the planned UI columns (action, actor, app/env, when, details summary).

## Deliverable

### Phase 1 — investigation doc
File: `docs/work/pr-c-investigation.md`

Sections:
- **Existing audit writes** — table: endpoint, action constant, fields populated
- **Proposed new audit writes** — table: endpoint, proposed action constant
- **Read endpoint contract** — full route definition, query params, response shape, sample response
- **Retention recommendation** — proposed default + how 8.5 retention would honor it
- **Risk** — long-running operations writing audit rows prematurely (e.g., bulk export should write *after* stream completes, not at start)
- **Open questions**

Stop here and request review.

### Phase 2 — implementation (after approval)
- New `GET /api/admin/audit` endpoint in `AdminEndpoints.cs`
- Pagination + filtering match the dashboard pattern in [`DashboardEndpoints.cs`](../../../backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs)
- Integration tests:
  - Auth: 401 on missing/wrong admin key
  - Empty list when no rows match
  - Pagination correctness (page 0 → page N)
  - Filter by action returns matching rows only
  - Filter by date range works
- Update `DEVELOPMENT_PLAN.md` Issue 8.7: mark third acceptance criterion (Read-only audit view) as done, note that the UI portion remains in 10.6
- No new tables, no schema changes (table already exists from 8.9)

## Scope guards
- **No schema changes.** Table already exists.
- **No UI.** That's 10.6's scope.
- **No RBAC.** Endpoint stays on admin-key auth; 8.6 will swap the gate when it lands.
- **Don't add new admin endpoints just to write audit rows.** Only add audit writes to endpoints that already exist or are needed for known imminent work.

## Expected effort
~half-day. Investigation ~1 hour; implementation + tests ~3 hours.
