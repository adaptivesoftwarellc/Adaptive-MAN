# Performance — session timeline derived query (Issue 5.2 + 5.7)

## What this measures

`Observability.Infrastructure.Sessions.SessionTimelineQuery.RunAsync` end-to-end, including:
- The session lookup
- The per-session events query (ordered)
- The chunked cross-process error join

This is the same code path the API endpoint `/api/sessions/{sessionId}/timeline` runs, lifted into a reusable query type. The endpoint adds JSON shaping on top, which is not what we're measuring — we're measuring the database round-trips that determine whether the derived approach holds.

## Methodology

- Harness: [`backend/src/Observability.Benchmarks`](../backend/src/Observability.Benchmarks/) — `Stopwatch` over 5 warmup + 50 measured iterations, fresh `DbContext` each iteration so connection pooling is exercised but tracking caches don't accumulate.
- Database: SQL Server 2022 in Docker (`mcr.microsoft.com/mssql/server:2022-latest`), local Docker Desktop on Windows 11, no resource limits applied.
- Schema: `EnsureCreatedAsync` — same model the production migration applies.
- Seed shape per cell:
  - 1 application + 1 environment + 1 target session
  - **target_events** events on the target session, with sequential correlation ids (1 in 5 named `api_request_failed`)
  - **filler_events** spread across 1,000 unrelated sessions to force the per-session predicate to skip rows
  - **cross_process_errors** error rows whose `LastCorrelationId` matches a target-session correlation id

## Results

Two passes run on 2026-05-22 against local Docker MSSQL.

**Before:** schema as of [`75ef382`](../backend/src/Observability.Infrastructure/Migrations/20260503063154_Initial.cs) — `Events(ApplicationId, EnvironmentId, CreatedAt)` + `Events(ApplicationId, EventName, CreatedAt)`; `Errors(ApplicationId, EnvironmentId, Fingerprint)` + `Errors(ApplicationId, EnvironmentId, LastSeenAt)`.

**After:** [`Phase5HardeningIndexes`](../backend/src/Observability.Infrastructure/Migrations/20260522222614_Phase5HardeningIndexes.cs) adds `Events(ApplicationId, EnvironmentId, SessionId, OccurredAt)` and `Errors(ApplicationId, EnvironmentId, LastCorrelationId)`.

### Before indexes (baseline)

| target_events | filler_events | cross_process_errors | seed (ms) | p50 (ms) | p95 (ms) | p99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 000 | 0 | 16 551 | 11.90 | 32.05 | 35.98 |
| 100 | 10 000 | 50 | 15 111 | 11.37 | 13.43 | 15.02 |
| 1 000 | 100 000 | 0 | 18 947 | 38.39 | 46.00 | 56.14 |
| 1 000 | 100 000 | 500 | 18 575 | 37.99 | 57.58 | 75.74 |
| 10 000 | 1 000 000 | 0 | 45 308 | 151.75 | 169.53 | 255.28 |
| 10 000 | 1 000 000 | 5 000 | 41 930 | 198.90 | 313.30 | 339.61 |
| 100 000 | 1 000 000 | 0 | 43 686 | 1 003.20 | 1 118.82 | 1 150.26 |
| 100 000 | 1 000 000 | 5 000 | 38 434 | 1 319.57 | 1 950.54 | 2 422.10 |

### After indexes (`Phase5HardeningIndexes`)

| target_events | filler_events | cross_process_errors | seed (ms) | p50 (ms) | p95 (ms) | p99 (ms) | Δ p95 |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 10 000 | 0 | 18 862 | 9.52 | 11.45 | 23.45 | **−64%** |
| 100 | 10 000 | 50 | 16 535 | 8.39 | 10.07 | 10.51 | −25% |
| 1 000 | 100 000 | 0 | 21 226 | 10.62 | 17.82 | 31.09 | **−61%** |
| 1 000 | 100 000 | 500 | 21 243 | 14.85 | 19.02 | 31.15 | **−67%** |
| 10 000 | 1 000 000 | 0 | 49 363 | 96.42 | 128.82 | 201.30 | **−24%** |
| 10 000 | 1 000 000 | 5 000 | 47 919 | 151.62 | 175.30 | 219.90 | **−44%** |
| 100 000 | 1 000 000 | 0 | 50 770 | 876.21 | 969.56 | 1 108.21 | −13% |
| 100 000 | 1 000 000 | 5 000 | 50 816 | 1 158.64 | 1 268.14 | 1 283.89 | −35% |

p50 / p95 / p99 are over n=50 measured iterations after 5 warmups.

## Observations

1. **The new `Events(SessionId, OccurredAt)` index converts the per-session scan from index-seek-then-key-lookup into a single index range scan.** This is the load-bearing change: at 1k target events over 100k filler, p95 dropped from 46ms to 18ms; at 10k target events over 1M filler, p95 dropped from 170ms to 129ms. The bigger the filler set relative to the target session, the bigger the win.
2. **The new `Errors(LastCorrelationId)` index measurably helps when cross-process errors are present.** Compare the paired `0 / 5 000` rows at 10k events: p95 went from 313ms to 175ms — a 44% reduction, more than the no-cross-process row's 24% reduction. The Errors table is still small in these benches, so the win will grow once real onboarded apps produce sustained Errors volumes.
3. **At 100k events/session, indexes can't beat data-return cost.** Even with the new indexes, p95 sits at ~1s because the query is materializing 100k ordered rows over the network. This is not an index problem — it's the boundary the architecture doc already calls out ("revisit when per-session entry counts push past ~10k"). The derived approach is not intended to cover this shape; if a real session ever approaches it, the fix is materialization or paginated timeline retrieval, not more indexes.
4. **At 10k events/session — the architecture doc's stated upper bound for derived — p95 is now comfortably under 200ms with cross-process errors, and ~130ms without.** That's the operating envelope for WMS.
5. **Local Docker MSSQL is optimistic vs. Azure SQL.** No network latency, no GP_S serverless cold-start, no shared-tenant noise. Re-run after Brandon provisions `ObservabilityDev` (Phase 2.4) before treating these numbers as production-representative.

## Indexes shipped (additive migration)

[`Phase5HardeningIndexes`](../backend/src/Observability.Infrastructure/Migrations/20260522222614_Phase5HardeningIndexes.cs):

- `IX_Events_ApplicationId_EnvironmentId_SessionId_OccurredAt` — covers the per-session ordered events scan in `SessionTimelineQuery`.
- `IX_Errors_ApplicationId_EnvironmentId_LastCorrelationId` — covers the chunked cross-process error join. Decided based on the paired-cell comparison above (44% p95 reduction at 10k target events with 5k cross-process errors); the Errors table will only grow once real apps onboard, so the win compounds.

The `Initial` migration is untouched. The new indexes are additive and the migration is reversible.

## Verdict

**Derived approach holds for any realistic WMS session shape.** With `Phase5HardeningIndexes` applied, p95 stays under 200ms up to and including the architecture-doc upper bound of 10k events/session, even with thousands of cross-process error joins.

The 100k events/session cells confirm the derived approach is not suitable past ~10k — but that boundary was already documented and is consistent with the architecture doc. If a real onboarded app sustains sessions approaching that shape, the right next step is materialization (`SessionEvents`) or paginated timeline retrieval — file a fresh issue rather than re-litigating index choices.

Re-run the grid against Azure SQL Dev once Brandon provisions `ObservabilityDev`. The relative shape of the results should hold; absolute numbers will be slower.
