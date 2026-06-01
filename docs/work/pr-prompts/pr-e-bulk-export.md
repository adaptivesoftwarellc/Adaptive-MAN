# PR E: Issue 10.5 bulk data export API

## Branch
`phase-10/5-bulk-export`

## Goal
Three streaming bulk-export endpoints that let operators extract events / errors / safety-violations as NDJSON without manual SQL. Backs the platform's anti-PostHog-lock-in promise.

## Context

The pitch over PostHog was "we own our data." Today, the only retrieval paths are paginated dashboard endpoints with a max page size of 200. No bulk export. Anyone wanting to feed a data warehouse, do compliance analysis, or migrate off this platform hits hand-written SQL or scrapes the dashboard endpoints page-by-page.

This PR ships the API. Downstream integrators (warehouses, ETL jobs) wire to it. We are not building a data warehouse here.

## What to investigate

### Existing read patterns
1. Read [`backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs`](../../../backend/src/Observability.Api/Endpoints/DashboardEndpoints.cs):
   - `GetEvents` shows the EF projection shape we want to mirror (consistency: users learn one filter syntax)
   - `GetErrors` — same
   - Filter params: `app=`, `env=`, `from=`, `to=`, `event_name=`, `distinct_id=`, `correlation_id=`
   - `ResolveRange` + `ResolvePaging` helpers — reuse the range parser; skip paging (export streams everything)

### Streaming approach
2. ASP.NET Core minimal APIs + `IAsyncEnumerable<T>` — returns chunked responses natively without buffering in memory.
3. EF Core `.AsAsyncEnumerable()` — verify it works against SQL Server with the existing projection patterns (it does; check for connection-lifetime quirks).
4. Response content type: `application/x-ndjson` (or `application/jsonl` — confirm which is canonical at implementation time).
5. NDJSON writer — one JSON object per line, `\n` separator, no surrounding array brackets. System.Text.Json + `Utf8JsonWriter` + a newline is sufficient — no new package needed.

### Auth + audit
6. Reuse the admin-key gate from [`backend/src/Observability.Api/Middleware/AdminKeyAuthExtensions.cs`](../../../backend/src/Observability.Api/Middleware/AdminKeyAuthExtensions.cs).
7. Audit row per export — write **after stream completes**, not at start, so partial-failure exports are visible in the audit trail.
8. Coordinate with PR C (audit logging) — the action constant should be something like `admin.export.events`, `admin.export.errors`, `admin.export.safety_violations`. If PR C lands first, this PR uses the established pattern.

### Bounds
9. Time-range cap — recommended 90 days per request. Larger requests get a 400 explaining how to chunk. Prevents accidental "export everything ever" requests that hold DB connections for hours.
10. Decide: do exports require an explicit `app` + `env` filter, or allow unscoped exports? Lean toward requiring `app` — prevents cross-tenant exports through this endpoint (defense-in-depth alongside 10.1).

## Deliverable

### Phase 1 — investigation doc
File: `docs/work/pr-e-investigation.md`

Sections:
- **Endpoint contracts** — full route, query params, headers, response shape sample
- **Streaming approach** — `IAsyncEnumerable<T>` + NDJSON writer; small code shape sketch
- **Format choice rationale** — NDJSON wins over CSV (loses `properties_json` nested data) and Parquet (heavyweight dependency); document for future readers
- **Cap rationale** — why 90 days; what happens above; what happens at exactly 90 days
- **Risk** —
  - Memory pressure (mitigated by streaming; verify with EF Core `.AsAsyncEnumerable()`)
  - Long-held DB connections (mitigated by range cap)
  - Audit row write-after-stream (don't write at start)
  - Cross-tenant scope (require `app` filter)
- **Open questions** — `properties_json` raw vs. unrolled; whether to include `Id` (probably yes, for idempotent re-imports)

Stop here and request review.

### Phase 2 — implementation (after approval)
1. **Three new endpoints** under `/api/admin/export/*`:
   - `GET /api/admin/export/events?app=&env=&from=&to=&event_name=&distinct_id=&correlation_id=&format=ndjson`
   - `GET /api/admin/export/errors?app=&env=&from=&to=`
   - `GET /api/admin/export/safety-violations?app=&env=&from=&to=`

2. **NDJSON writer helper** — a small static method or extension; takes an `IAsyncEnumerable<T>` and a `Stream`, writes `JsonSerializer.Serialize(stream, item)` + `\n` per item.

3. **Range validation** — 400 with explanatory body if `to - from > 90 days`.

4. **App filter required** — 400 if missing.

5. **Audit row write-after-stream** — use a `finally` block or `await using var scope` pattern so the row writes even on partial failure (and records the failure state in `DetailsJson`).

6. **Tests**:
   - Auth: 401 on missing/wrong admin key
   - Range cap: 400 on > 90-day range
   - Missing `app`: 400
   - Seeded data → exported NDJSON → parse each line → row count + content match the DB
   - Audit row appears with correct action + actor

## Scope guards
- **No data warehouse / S3 push.** The PR ships the API; integrators wire downstream consumers.
- **No CSV or Parquet.** NDJSON only. If asked, file as a follow-up.
- **No async / job-based export.** These are synchronous streamed responses. If huge ranges become a real need, file separately.
- **Don't expose properties beyond what `Events` already stores.** No unrolling `properties_json` server-side — return raw, consumer parses.

## Expected effort
~1 day. Investigation ~half-day; implementation + tests ~half-day.
