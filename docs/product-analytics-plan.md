# Product analytics — PostHog-parity plan

> Status: **proposed** (2026-07-14). Owner: Arlo. This doc specs the analytics features we
> adopt from PostHog, re-designed for this platform's server-enforced privacy model.
> Phase A (Insights/Trends) is specced to implementation level; later phases are scoped
> so each can become its own design PR when picked up.

## Why this is cheap here

Everything below is **queries over the `Events` table we already collect** — `distinct_id`
(PHI-free by contract), `EventName`, `FeatureArea`, `NormalizedRoute`, `ReleaseSha`,
`SessionId`, `CreatedAt`, plus allowlisted properties. No new ingestion, no SDK changes
(except feature flags). The dashboard shell, filter bar, saved views, RBAC scoping, and
CSV export all exist and are reused.

## What we deliberately do NOT copy

| PostHog feature | Why not |
|---|---|
| Autocapture | Captures arbitrary DOM/PII — incompatible with the allowlist model. |
| Heatmaps | Requires raw coordinates + page snapshots; PHI risk on clinical screens. |
| Un-masked session replay | Replay stays deferred (Phase 9) behind rrweb approval + masking + privacy review. |
| Free-form property queries (v1) | Querying arbitrary `PropertiesJson` keys invites PHI-shaped exploration; v1 breakdowns are typed columns only. |

## Phase order

| Phase | Feature | Effort | Depends on |
|---|---|---|---|
| **A** | Insights / Trends (+ deploy annotations) | S–M | — |
| B | Funnels | M | A (shares query layer + page shell) |
| C | Retention | M | A |
| D | Paths (route flows) | M | A |
| E | Cohorts + saved insights + insight alerts | M | A–C |
| F | Feature flags / A-B | L | separate subsystem |

---

## Phase A — Insights / Trends (specced)

**User story:** "Show me `page_viewed` per day for the last 30 days, broken down by
`feature_area`, for wms-site Production" — arbitrary catalog events, not just the six
hardcoded health cards.

### API

`GET /api/dashboard/insights/trends` (inside the existing `dash` group → inherits
RBAC/tenant scoping via `EnforceAppScopeAsync` + privileged-access audit).

| Param | Type | Notes |
|---|---|---|
| `app`, `env` | Guid | required, as today |
| `from`, `to` | DateTime? | `ResolveRange` defaults (24 h) |
| `events` | csv string | 1–5 names; **each must exist in `EventCatalog.Phase1`** → else 400 |
| `interval` | `hour \| day \| week` | default from range span (≤48 h → hour, else day); `week` is opt-in and `agg=count` only |
| `breakdown` | enum? | `feature_area \| release_sha \| endpoint_group` — **typed columns only**, never JSON properties (`http_status_code` is not a typed Events column; add it only if it ever becomes one) |
| `agg` | `count \| unique_users` | `unique_users` = `COUNT(DISTINCT DistinctId)`; series `total` is a separate range-wide distinct count (per-bucket distincts never sum) |

Response:

```jsonc
{
  "range": { "from": "...", "to": "...", "interval": "day" },
  "series": [
    { "event": "page_viewed", "breakdown": "ivr",   "total": 412, "buckets": [{ "t": "...", "c": 37 }] },
    { "event": "page_viewed", "breakdown": "worklist", "total": 98, "buckets": [] }
  ]
}
```

Implementation notes:
- Bucketing reuses the `GetHealth` sparkline `DATEPART` pattern (Year/Month/Day/Hour
  group, reassembled server-side). Week = date-diff bucketing from range start.
- Cap breakdown cardinality at 10 + an `other` rollup (same convention as the existing
  top-10 panels).
- `unique_users` per bucket is `COUNT(DISTINCT DistinctId)` — safe because identity rules
  guarantee `distinct_id` is a PHI-free key.
- **Index:** no new index needed — the Initial migration already ships
  `IX_Events_ApplicationId_EventName_CreatedAt`, which covers the name-filtered time scan.
  Revisit only if real query plans show it insufficient at production volumes.

### Frontend

New route `/insights` + sidebar entry (icon: chart), reusing the page conventions:
- Controls row: event multi-select (from `catalog.ts`, max 5) · interval segmented
  control · breakdown select · agg toggle (`Totals` / `Unique users`).
- Chart: Recharts line (hour/day) or bar (week), one series per event×breakdown; legend
  toggles series; tooltip shows exact values; empty/loading/error states via the existing
  `EmptyState`/`Skeleton` primitives.
- **Save current view** already works via `presets.ts` — extend `ViewPage` with
  `'insights'` and serialize the insight params into the URL (single source of truth,
  same as filters today).
- CSV export button reusing the events-explorer export pattern.

### Deploy annotations (small, ships with Phase A)

PostHog's release markers, adapted: table `Annotations`
(`Id, ApplicationId, EnvironmentId, At, Label, ReleaseSha?, CreatedBy`), CRUD under
`/api/admin/annotations`, auto-row on first event with a previously-unseen `ReleaseSha`
(optional). Rendered as vertical reference lines on Insights + Health sparklines.
This makes "errors by release" actionable: you *see* the deploy on the chart.

### Acceptance

- Trends endpoint 400s on non-catalog event names; 403s cross-tenant (existing filter).
- `unique_users` never exposes `distinct_id` values — only counts.
- p95 < 300 ms on 30 d/day-interval over 1 M events (bench with the existing
  `Observability.Benchmarks` harness; covered by the existing
  `IX_Events_ApplicationId_EventName_CreatedAt` index).
- Insights page: save-view roundtrip, CSV export, legend toggle, reduced-motion safe.

---

## Phase B — Funnels (scoped)

`POST /api/dashboard/insights/funnel` — body: ordered steps
(`[{ event, feature_area? }]`, 2–5), `window_days` (1–30), range. A user converts step
N→N+1 if an N+1 event exists for the same `distinct_id` after their step-N event within
the window. Response: per-step `users`, `conversion_from_prev`, `conversion_from_first`.

Implementation: single query per step pulling `MIN(CreatedAt) per DistinctId` filtered by
the previous step's qualifying set (sequential narrowing — avoids loading raw event rows).
UI: step builder + horizontal funnel bars with drop-off percentages.

## Phase C — Retention (scoped)

`GET /api/dashboard/insights/retention?cohort_event=&return_event=&period=day|week&buckets=8`.
Cohort = users whose **first** `cohort_event` fell in bucket 0; matrix cell = % who did
`return_event` in bucket N. Classic triangle heatmap UI. Same catalog-name validation.

## Phase D — Paths (scoped)

Aggregate `page_viewed` transitions within a session: for each `SessionId`, order by
`CreatedAt`, emit `(from_route → to_route)` edges, return top N edges (+ entry/exit
counts). UI: ranked edge list first; Sankey later if it earns it. `NormalizedRoute` is
already PHI-safe, so no new privacy surface.

## Phase E — Cohorts, saved insights, insight alerts (scoped)

- `CohortDefinitions` (name + JSON rule: performed / did-not-perform event in window);
  evaluated at query time as an EXISTS filter into Trends/Funnels/Retention.
- Saved insights: server-side `SavedInsights` table (URL params + name), replacing
  localStorage-only user views so they're shareable across the team.
- Alerts: new `AlertRules` type `insight_threshold` (insight id + comparator + value)
  evaluated by the existing Worker evaluator — notification channel arrives with 8.4.

## Phase F — Feature flags (sketch only — own design PR before build)

New subsystem: `FeatureFlags` (key, app, env, enabled, rollout %, allowlisted
distinct_ids), `GET /api/flags` authenticated by the existing PublicClient/ServerApi
keys, deterministic bucketing by `hash(flag_key + distinct_id)`, SDK `isEnabled(key)`
helpers + local cache with TTL. Admin UI page. Explicitly out of Phase A–E scope.

---

## Prerequisite hygiene (do alongside Phase A)

- **`ReleaseSha` adoption** — WMSAPI `.env`: `AdaptiveObservability__ReleaseSha=<sha>`
  set at deploy; WMSSite: `VITE_OBSERVABILITY_RELEASE_SHA=${{ github.sha }}` in both SWA
  workflows. Without this, errors-by-release and annotations stay "unknown".
- Health-card fix (#44) merged so Insights and Health agree on counts.
