# Implementation plan — work that doesn't need Brandon

> Carved out of `DEVELOPMENT_PLAN.md` on 2026-06-08. Everything here is implementable
> **without** Brandon's Azure-admin access. Excluded by design: the Prod
> GitHub Environment config, platform Prod App Service config, canary provisioning, Azure
> Monitor standup, and the first Prod CI deploy. Also excluded: 8.10 (needs a month of
> Prod traffic), 8.11 / 10.7 DR drill (need Azure access), and Phase 9 replay (blocked on
> the rrweb dependency-approval gate).
>
> **2026-06-17 — SCH dropped, WMS is the anchor tenant.** SCH is being removed from our
> systems; we no longer wire the platform into the SCH_API / SCH_UI repos. The entire
> SCH-specific **6.7 soak → 6.8 cutover** chain is therefore **deleted** (there is no
> PostHog integration to cut over from — WMS instruments net-new). The forward integration
> work is **Phase 7 — WMS onboarding** (WMSSite + WMSAPI; see `docs/audits/wmssite.md`,
> `docs/audits/wmsapi.md`), which lives in the WMS repos plus this repo's app provisioning
> (`scripts/onboard-wms.ps1`) and needs no Brandon access on the Dev side.

Source of truth for full acceptance criteria stays `DEVELOPMENT_PLAN.md`; this doc is the
ordered, dependency-aware build sheet. Branch names follow the existing
`phase-N/x-slug` convention.

## Dependency map (read first)

```
Phase 1 (docs)          ── no deps, parallelizable
Phase 2 (hardening)     ── no deps, additive migrations only
Phase 3 (8.6 RBAC)      ── DECISION GATE: identity source (Entra/AAD groups vs local users)
   │                        unblocks ↓
   ├── Phase 4 (10.6 admin UI)   ── can start now behind admin-key gate; cleaner after 8.6
   └── (flips deferred 10.1 dashboard-isolation tests to secured assertions)
Phase 5 (8.3 alerts)    ── standalone; visibility-only until 8.4 notifications (Brandon/decision)
Phase 6 (10.8 dogfood)  ── Dev side now; Prod side waits on first Prod deploy
```

Recommended order: **1 → 2 → 3 → 4 → 5 → 6**. Phases 1 and 2 can run in parallel with each
other. Phase 3's decision gate should be resolved early since it unblocks the most.

---

## Phase 1 — Documentation quick wins

No code dependencies, fast to merge. Do these first to clear the board.

### 1.1 — Issue 10.10: SDK failure-mode documentation
- **Branch:** `phase-10/10-sdk-failure-docs`
- **Files:** `packages/observability-client-js/README.md`, `packages/observability-client-dotnet/README.md`
- **Scope:** Add a "Failure modes" section to each README + a "Troubleshooting: events don't appear" checklist.
  - JS: network-unreachable → batched retry w/ exponential backoff (state the current count + cap, read from `transport.ts`); after N retries events dropped silently (no localStorage queue); 4xx vs 5xx handling; batch buffer cap + overflow behavior.
  - .NET: bounded `Channel<T>` queue, oldest dropped when full, no disk-backed persistence.
  - *(Optional)* document a `TransportStatus` callback if you choose to add one — otherwise note it as a future enhancement.
- **Verify:** read the actual retry/cap constants out of `transport.ts` and `AdaptiveObservabilityService.cs` so the docs match code, not the plan's prose.

### 1.2 — Issue 10.9: Non-additive migration safety playbook
- **Branch:** `phase-10/9-migration-playbook`
- **Files:** new `docs/database-migrations.md`; `.github/pull_request_template.md`; *(optional)* a CI lint step.
- **Scope:**
  - Classify additive (safe at startup via `MigrateAsync`) vs non-additive (expand → contract).
  - Document the expand/contract release pattern: add column → dual-write → backfill → switch reads → drop old column.
  - When a maintenance window is genuinely required vs when expand/contract suffices.
  - Rollback = roll forward with a reversing migration, not `Down`.
  - PR template gains a "migration type" checkbox.
  - *(Optional)* CI lint fails on `DropColumn` / `RenameColumn` / `AlterColumn` without an `expand-contract: N of M` comment.
- **Context:** all migrations so far are additive (`Initial`, `Phase5HardeningIndexes`, `Phase8AdminAuditLog`). This playbook exists before the first destructive one.

### 1.3 — Issue 10.7 (docs portion only): Compliance posture
- **Branch:** `phase-10/7-compliance-docs`
- **Files:** new `docs/compliance.md`.
- **Scope (the parts that need no Azure access):** BAA with Microsoft (state verified status + reference), Azure SQL TDE / encryption-at-rest default, Key Vault encryption-at-rest default, network-posture trade-off (public + firewall vs private endpoint — current decision + rationale, already recorded in 2.4), `AuditLogs` retention duration (365d default, enforced by 8.5 when it lands).
- **Defer to Brandon:** `docs/disaster-recovery.md` restore drill against `obs-api-dev`, PITR/LTR decisions, and the executed-drill record (all need Azure SQL access). Leave a stub + a "blocked on Azure access" note so the gap is visible.
- **Note:** 10.7's DR drill is a prerequisite for the first Prod deploy — flag in the doc that going to Prod can't proceed until it's run.

---

## Phase 2 — Self-contained backend hardening

All additive migrations, no cross-dependencies. Each is its own PR.

### 2.1 — Issue 8.1: Error fingerprinting hardening
- **Branch:** `phase-8/1-fingerprint-hardening`
- **Files:** `Observability.Domain/Telemetry/ErrorRecord.cs`, fingerprinting logic in `Observability.Application`, new additive migration in `Observability.Infrastructure/Migrations/`.
- **Acceptance:** fingerprint version field stored on `Errors`; backfill job for past data; algorithm documented (in `docs/architecture.md` or a new doc).
- **Verify:** new migration is additive; existing `Initial`/`Phase5`/`Phase8` migrations untouched.

### 2.2 — Issue 8.2: BG job failure dedup hardening
- **Branch:** `phase-8/2-bg-dedup-hardening`
- **Files:** `Observability.Domain/Telemetry/BackgroundJobFailure.cs` (per-app window), dedup upsert logic in `Observability.Application`, dashboard surface in `frontend/src/pages/` (likely `ErrorsPage.tsx` or a new panel), additive migration.
- **Acceptance:** per-app window override (today it's a static 15-min default from 4.8); suppressed counts visible in the dashboard.
- **Verify:** existing 4.8 integration test (100 identical failures → 1 incident, `count=100`) still green; add a test for the per-app override.

### 2.3 — Issue 8.5: Retention policies
- **Branch:** `phase-8/5-retention`
- **Files:** `Observability.Worker/` (nightly hosted service — currently near-empty `Program.cs`), per-app setting on `AppEnvironment` (`Observability.Domain/Applications/AppEnvironment.cs`), additive migration, `AuditLog` row per run.
- **Acceptance:** per-app setting (default 90d events, 180d errors; reserve a nullable `ReplayRetentionDays` column for Phase 9); Worker runs nightly; audit log row per run (`admin.retention.swept`). Also enforces the `AuditLogs` 365d retention defined in 8.7/PR C and the canary-row pruning deferred from 10.2.
- **Verify:** integration test seeds aged rows and confirms the sweep deletes only past-retention data and writes an audit row.

---

## Phase 3 — RBAC foundation (decision-gated)

### 3.1 — Issue 8.6: RBAC
- **Branch:** `phase-8/6-rbac`
- **⚠ DECISION REQUIRED before coding:** identity source — **Entra/AAD groups vs local users**. This is the open cross-cutting question; resolve it first (likely needs an architecture call, not Brandon's Azure access). Record the decision in `docs/architecture.md`.
- **Files:** new roles persistence in `Observability.Domain` + migration; auth enforcement in `Observability.Api` (extend `Observability.Infrastructure/Authentication/`); UI gating in `frontend/src/`.
- **Acceptance:** roles Admin / Developer / Viewer / AppOwner persisted, applied at API + UI; AppOwner cannot read other apps; Admin/Developer access logged.
- **Unblocks:** full gating for 10.6 (Phase 4), and flips the two deferred `KNOWN_GAP_8_6` tests in `MultiTenantIsolationTests` (10.1) from "asserts current leaky behavior" to secured 403/empty assertions — **check 10.1's two deferred boxes when this lands.**
- **Why before Phase 4:** 10.6's Admin link is meant to be RBAC-gated. You *can* ship 10.6 behind an admin-key prompt first (see Phase 4), but doing 8.6 first avoids reworking the gate.

---

## Phase 4 — Self-service admin UI

### 4.1 — Issue 10.6: Self-service admin UI (also closes 8.7's read-only audit view)
- **Branch:** `phase-10/6-admin-ui`
- **Not blocked:** the plan explicitly allows gating behind a feature flag / admin-key prompt until 8.6 lands. If Phase 3 is done, use RBAC; otherwise ship behind the admin-key gate now.
- **Backend already done:** `GET /api/admin/audit` shipped in PR C; `POST /api/admin/apps` + key mint shipped in 8.9 (`AdminEndpoints.cs`).
- **New endpoints to add:** `POST /api/admin/apps/{slug}/environments/{env}/keys/{id}/revoke`.
- **Files:** `Observability.Api/Endpoints/AdminEndpoints.cs` (revoke), frontend `frontend/src/pages/` — extend `AdminAppsPage.tsx`, add a Keys page + an Audit-log page; nav link in the dashboard shell.
- **Acceptance:** Apps page (list/create/view envs + key counts); Keys page (mint shows plaintext once with copy + warning, revoke, list with masked id + created + last-used); Audit-log page consuming `GET /api/admin/audit`; Cypress/Playwright smoke: mint key → use on `/api/ingest/events` → revoke → confirm 401.
- **On landing:** mark 8.7's "read-only audit view" criterion closed (the 10.6 audit page *is* that view), and have the revoke endpoint write an `admin.key.revoked` audit row (closes the 8.7 note).

---

## Phase 5 — Alerting

### 5.1 — Issue 8.3: Alert rule engine
- **Branch:** `phase-8/3-alert-engine`
- **Files:** `AlertRules` table in `Observability.Domain` + migration; evaluator as a hosted service in `Observability.Worker/`.
- **Acceptance:** `AlertRules` table; rule types — count-over-window, new-error-after-release, error-rate-above-threshold, any-prod-job-failure; evaluator runs in the Worker.
- **⚠ Downstream gate:** 8.4 notifications (email/Teams) is blocked on the **ACS-vs-SendGrid decision** and is Brandon-adjacent for the webhook/ACS resource. So 8.3 is **visibility-only** until 8.4 — have the evaluator persist fired alerts to a table (and optionally surface them in the dashboard) so the work is useful before notifications exist. Document this constraint.
- **Verify:** unit tests per rule type against seeded data; integration test that the Worker evaluator fires and persists an alert row.

---

## Phase 6 — Dogfooding

### 6.1 — Issue 10.8: Dogfood the SDK (Dev side)
- **Branch:** `phase-10/8-dogfood-sdk`
- **Not blocked for Dev:** the meta-app row is provisioned through the 8.9 admin endpoint using the admin key already in `AdaptiveToolsKeyVault` (Option B gives you access) — no Brandon.
- **Files:** `Observability.Api/Program.cs` — register `AddAdaptiveObservability(...)` pointing the platform at *itself*; a note in `docs/architecture.md` about the loop guard.
- **Acceptance:** `adaptive-observability-meta` app provisioned (Dev now; Prod later); `obs-api-dev` registers the SDK against itself; real platform `server_error_occurred` events appear under the meta-app; document the loop guard (SDK swallows transport failures, so a failing ingest path won't recursively emit).
- **Defer to Brandon/Prod deploy:** the Prod meta-app + Prod self-registration land with the first Prod deploy.
- **Verify:** force an exception in a Dev endpoint, confirm a `server_error_occurred` row lands under the meta-app.

---

## Quick reference — what each phase unblocks

| Phase | Items | Gate | Unblocks |
|---|---|---|---|
| 1 | 10.10, 10.9, 10.7-docs | none | — |
| 2 | 8.1, 8.2, 8.5 | none | 8.5 enforces 8.7 + 10.2 retention |
| 3 | 8.6 | identity-source decision | 10.6 gating, 10.1 deferred tests |
| 4 | 10.6 | admin-key gate (or 8.6) | closes 8.7 read-only view |
| 5 | 8.3 | none (8.4 notifications blocked) | — |
| 6 | 10.8 (Dev) | none | SDK regression signal |

## Still-Brandon-blocked (for reference — not in this plan)
- First Prod CI deploy, Prod GitHub Environment config, platform Prod App Service config
- 10.2 canary provisioning + repo secrets, 10.3 Azure Monitor standup
- 8.4 notifications (ACS/SendGrid decision + resource), 8.10 index review (needs Prod traffic)
- 8.11 rotation drill, 10.7 DR restore drill (Azure access), Phase 9 replay (rrweb approval)
- *(Removed by the 2026-06-17 SCH drop: the 6.7 soak / 6.8 SCH cutover chain no longer exists.
  WMS onboarding (Phase 7) replaces it and is not Brandon-blocked on the Dev side.)*
