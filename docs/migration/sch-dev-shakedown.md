# SCH Dev shakedown soak log

Tracks the 5-business-day Option A shakedown (Issue 6.7). SCH Dev runs against
`obs-api-dev`; success = zero `SafetyViolations` for `sch-ui` + `sch-api` over
five consecutive business days, plus privacy reviewer sign-off recorded below.

> **Soak start clock:** the soak begins on the first business day on which real
> SCH-emitted traffic appears in `obs-api-dev` (not synthetic curl smoke tests).
> Day 1 is recorded the morning after the first SCH Dev deploy with the SDK
> enabled emits at least one event.

## Pre-soak verification (2026-05-25)

| Check | Result |
|---|---|
| `sch-ui` + `sch-api` rows created in platform via `scripts/onboard-sch.ps1` | ✅ |
| Four plaintext keys minted (sch-ui Dev/Prod, sch-api Dev/Prod) | ✅ |
| SCH_API Dev key accepted at `/api/ingest/events` (smoke test) | ✅ 202 |
| SCH_API Dev key accepted at `/api/ingest/errors` (smoke test) | ✅ 202 |
| SCH_UI Dev key accepted at `/api/ingest/events` (smoke test) | ✅ 202 |
| `Adaptive.ObservabilityClient` namespace + `AdaptiveSoftwareLLC.ObservabilityClient` package id confirmed compatible | ✅ |
| SCH_UI Dev/UAT GitHub repo secrets set (Brandon) | ☐ |
| SCH_API Dev secrets readable by deployed SCH_API runtime | ☐ — pending Brandon confirmation; see issue 6.6 follow-up |
| SCH_API Dev App Service restarted to pick up new config | ☐ |
| First real SCH-emitted event lands in `obs-api-dev` | ☐ |

The bottom four boxes gate the soak start.

## Daily soak log

Each entry should record: date, business-day index, `SafetyViolations` count for
each app + env, total event/error count per app, anything anomalous. Pull
counts via the Adaptive dashboard's Errors/Events tabs with the SCH presets, or
query the API directly.

### Day 1 — YYYY-MM-DD

- `SafetyViolations` (sch-ui Dev): _N_
- `SafetyViolations` (sch-api Dev): _N_
- Total events (sch-ui Dev): _N_
- Total events (sch-api Dev): _N_
- Total errors (sch-api Dev): _N_
- Notes:

### Day 2 — YYYY-MM-DD

- `SafetyViolations` (sch-ui Dev): _N_
- `SafetyViolations` (sch-api Dev): _N_
- Total events (sch-ui Dev): _N_
- Total events (sch-api Dev): _N_
- Total errors (sch-api Dev): _N_
- Notes:

### Day 3 — YYYY-MM-DD

- `SafetyViolations` (sch-ui Dev): _N_
- `SafetyViolations` (sch-api Dev): _N_
- Total events (sch-ui Dev): _N_
- Total events (sch-api Dev): _N_
- Total errors (sch-api Dev): _N_
- Notes:

### Day 4 — YYYY-MM-DD

- `SafetyViolations` (sch-ui Dev): _N_
- `SafetyViolations` (sch-api Dev): _N_
- Total events (sch-ui Dev): _N_
- Total events (sch-api Dev): _N_
- Total errors (sch-api Dev): _N_
- Notes:

### Day 5 — YYYY-MM-DD

- `SafetyViolations` (sch-ui Dev): _N_
- `SafetyViolations` (sch-api Dev): _N_
- Total events (sch-ui Dev): _N_
- Total events (sch-api Dev): _N_
- Total errors (sch-api Dev): _N_
- Notes:

## 5.5 cross-process correlation trace

During the soak, exercise one SCH Dev request that triggers a 5xx and confirm
the same `correlation_id` lands on both:
- the FE `api_request_failed` event (via SCH_UI's apiClient interceptor), and
- the BE `server_error_occurred` error (via SCH_API's `GlobalExceptionMiddleware`).

| Field | Value |
|---|---|
| Date verified | _YYYY-MM-DD_ |
| Trigger | _e.g. POST /api/orders with invalid payload_ |
| `correlation_id` | _GUID_ |
| FE event id | _GUID_ |
| BE error id | _GUID_ |
| Session id (timeline cross-link) | _GUID_ |
| Dashboard timeline shows joined view | ☐ |

## Privacy reviewer sign-off

Required before Prod cutover (Issue 6.8). Reviewer scope: confirm no PHI, PII,
exception messages, stack traces, or raw URLs appear in any captured event or
error during the soak.

| Field | Value |
|---|---|
| Reviewer | _name_ |
| Date | _YYYY-MM-DD_ |
| Scope sampled | _e.g. all 5 days of soak data_ |
| Findings | _none_ / _list_ |
| Approved for Prod cutover | ☐ |

## If the soak fails

A single `SafetyViolation` row OR any leaked PHI/PII finding restarts the
5-day clock. Capture in the daily log:
- What property leaked, in which event
- Whether the fix is in the SCH allowlist (server-side) or SCH code (client-side)
- Date the fix shipped and the new soak start date

If failures recur after fix attempts, escalate by re-evaluating the allowlist
shape in [`docs/privacy-rules.md`](../privacy-rules.md) and the SCH-side
emission code before retrying.
