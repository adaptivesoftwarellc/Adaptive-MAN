# PR E — Issue 10.5 bulk data export API (investigation)

## Summary

Three admin-gated, streaming NDJSON export endpoints under `/api/admin/export/*` for
events, errors, and safety violations. They mirror the dashboard filter syntax, stream via
`IAsyncEnumerable<T>` so nothing buffers in memory, cap each request to a 90-day window, and
write a single audit row **after** the stream finishes. This backs the "we own our data"
promise over PostHog: operators can extract everything without hand-written SQL.

## Endpoint contracts

All three sit behind the existing admin-key gate (`X-Observability-Admin-Key`, see
`AdminKeyAuthExtensions`). Response is `application/x-ndjson` — one JSON object per line,
`\n`-separated, no array brackets. Rows stream in ascending `Id` order (stable, idempotent
re-import).

### `GET /api/admin/export/events`
Query params: `app` (**required**, Guid), `from` (**required**, ISO-8601 UTC), `env` (optional,
Guid), `to` (optional, defaults to now), `event_name`, `distinct_id`, `correlation_id`, `format`
(optional, only `ndjson`). Range filters on `CreatedAt` (matches `/api/dashboard/events`); rows
stream ordered by `CreatedAt`, then `Id`.

Sample line:
```json
{"id":42,"application_id":"...","environment_id":"...","event_name":"page_viewed","distinct_id":"u_1","session_id":"s_1","correlation_id":"c_1","normalized_route":"/x","endpoint_group":"x","feature_area":"x","properties_json":"{\"a\":1}","release_sha":"abc","occurred_at":"2026-06-01T00:00:00Z","created_at":"2026-06-01T00:00:01Z"}
```

### `GET /api/admin/export/errors`
Query params: `app` (**required**), `env`, `from`, `to`, `format`. Range filters on `LastSeenAt`
(matches `/api/dashboard/errors`). Includes `fingerprint`, `occurrence_count`, `first_seen_at`,
`last_seen_at`, etc.

### `GET /api/admin/export/safety-violations`
Query params: `app` (**required**), `env`, `from`, `to`, `format`. Range filters on `CreatedAt`.
Includes `event_name`, `rejected_field`, `reason`, `created_at`.

Error responses (before any bytes stream):
- `401` — missing/wrong admin key (gate).
- `400 missing_filter` — no `app`, or no `from` (exports never assume a default window).
- `400 invalid_range` — `to <= from`.
- `400 range_too_large` — `to - from > 90 days`.
- `400 unsupported_format` — `format` present and not `ndjson`.

## Streaming approach

EF Core `.AsAsyncEnumerable()` on the existing `AsNoTracking()` projection, consumed in an
`await foreach`, serializing each row with the app's shared snake_case `JsonOptions` straight to
`HttpContext.Response.Body`, followed by a `\n`. Shape sketch:

```csharp
http.Response.ContentType = "application/x-ndjson";
await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
{
    await JsonSerializer.SerializeAsync(http.Response.Body, row, json, ct);
    await http.Response.Body.WriteAsync(Newline, ct);
    count++;
}
```

No new package — `System.Text.Json` + a newline byte is sufficient. Validation runs and returns
`IResult` *before* the content type/body are touched, so 400s still produce a clean JSON error
body; the success path writes directly and returns `Results.Empty`.

## Format choice rationale

NDJSON over the alternatives:
- **CSV** — flattens away `properties_json` nested data, the most valuable field for re-import
  and analysis. Would force lossy column unrolling server-side (out of scope).
- **Parquet** — needs a heavyweight dependency (Parquet.NET) and a schema registry; overkill for
  a streaming line protocol. Filed as a possible follow-up, not this PR.
- **JSON array** — can't stream cleanly (trailing-comma / bracket bookkeeping) and forces the
  consumer to buffer the whole document. NDJSON is line-delimited: consumers parse incrementally.

## Cap rationale

90-day window per request. A bulk export holds a DB connection + reader open for the duration of
the stream; an unbounded "export everything ever" request could hold it for hours and starve the
pool. 90 days is a generous quarter — large enough for compliance/warehouse pulls, small enough
to bound connection lifetime. Above 90 days → `400 range_too_large` telling the caller to chunk
into ≤90-day windows. **At exactly 90 days the request is allowed** (the check is `> 90 days`,
not `>=`). `from` is **required** — there is no implicit default window: a warehouse/migration
consumer calling without a range must get an error, not a silent last-24h slice that looks like a
full extract. `to` defaults to now.

## Risks

- **Memory pressure** — mitigated by `.AsAsyncEnumerable()` + direct-to-body writes; no
  `ToListAsync`, nothing buffers. Verified the projection streams under the InMemory provider in
  tests; SQL Server streams natively over the open reader.
- **Long-held DB connections** — mitigated by the 90-day range cap and ordering on the range
  column (`CreatedAt` / `LastSeenAt`) then `Id`. That ordering is aligned with the existing
  `(App, Env, CreatedAt)` / `(App, Env, LastSeenAt)` indexes (the clustered `Id` is the implicit
  trailing key), so SQL Server streams off the index instead of sorting the full result set to
  tempdb — which matters precisely for the large-volume warehouse-feed case. `Id` tiebreak keeps
  ordering deterministic for resumable / idempotent imports.
- **Audit fidelity** — the audit row is written in a `finally` **after** the stream completes, so
  partial failures are still recorded. `DetailsJson` carries `count`, `from`, `to`, `status`
  (`completed` / `failed` / `canceled`), and the active filters. The audit `SaveChanges` uses
  `CancellationToken.None` so a client disconnect can't also drop it, and runs on a **fresh
  `IServiceScopeFactory` scope** rather than the request-scoped `DbContext`: without MARS, the
  streaming query's `DataReader` may still be open on the partial-failure path, and reusing that
  connection for the audit write would throw — the exact case the audit row must capture.
- **Truncated vs. complete stream** — a mid-stream failure leaves an HTTP `200` with partial
  NDJSON; the consumer can't tell it apart from a complete export from the response alone. There
  is no clean in-band way to signal failure after the body has started. Mitigation: consumers
  should reconcile the line count they parsed against the `count` in the export's audit row
  (`GET /api/admin/audit?action=admin.export.events`).
- **Cross-tenant scope** — `app` is required (defense-in-depth alongside Issue 10.1). No
  unscoped "all tenants" export path exists through these endpoints.

## Open questions (resolved)

- **`properties_json` raw vs. unrolled** — return **raw** (the stored string). Unrolling is lossy
  and out of scope; the consumer parses. (Scope guard.)
- **Include `Id`?** — **yes.** Needed for idempotent re-imports and for chunked clients to
  resume / dedupe. Also serves as the deterministic tiebreak in the stream ordering
  (`CreatedAt`/`LastSeenAt`, then `Id`).
</content>
</invoke>
