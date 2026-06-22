# Adaptive-Observability — items that need Brandon

> **Audience:** Brandon (Azure-admin / repo-admin / SCH-runtime owner).
> **Prepared:** 2026-06-13 by Arlo. **Source of truth:** `DEVELOPMENT_PLAN.md`.
>
> Everything implementable without your access is now built and merged to `main` (the
> "non-Brandon" plan is complete: PRs #31–#36). What's left below is blocked on access or
> decisions only you can make. Items are ordered by what unblocks the most downstream work.
> **The critical path to SCH Prod cutover (6.8) runs through items 1, 6, and 7.**

---

## TL;DR — the ask

| # | Item | Type | Blocks |
|---|------|------|--------|
| 1 | Configure the `prod` GitHub Environment | Repo admin | First Prod deploy → all Prod criteria |
| 2 | Run canary provisioning + set repo secrets (10.2) | Azure + repo admin | Prod PHI-allowlist canary |
| 3 | Stand up Azure Monitor availability + alerts (10.3) | Azure portal | Platform self-monitoring SLO |
| 4 | Enable branch protection for CODEOWNERS (10.11) | Repo admin | Enforced privacy-file review |
| 5 | Run + record the DR restore drill (10.7) | Azure SQL | **6.8 cutover gate** |
| 6 | Decide email provider: ACS vs SendGrid (8.4) | Decision | Alert notifications |
| 7 | Wire SCH Prod observability config + run the soak (6.6/6.8) | SCH runtime | SCH Prod cutover |
| 8 | Approve `rrweb` dependency (Phase 9) | Decision | Session replay (future) |

---

## 1. Configure the `prod` GitHub Environment  *(repo admin — highest leverage)*

**Why:** The `deploy-prod` job in `.github/workflows/backend.yml` is gated on `environment: prod`.
The Prod managed identity's federated credential only trusts OIDC tokens whose subject is
`repo:adaptivesoftwarellc/Adaptive-MAN:environment:prod`. Until the GitHub Environment exists,
every push to `main` **fails closed** on that job, and the first Prod deploy can't happen.

**Do:**
1. Repo → **Settings → Environments → New environment** → name it exactly `prod`.
2. Add a **required reviewer** (Arlo at minimum; you optional).
3. (Optional) restrict deployment to the `main` branch.
4. Either grant Arlo repo **admin** so future env/secret config is self-serve, or keep owning it.

**Unblocks:** the last open acceptance criteria in 2.3 / 2.4 (Prod), and the entire 6.7 soak → 6.8
cutover chain. **Nothing else on the Prod path can start until this is done.**

---

## 2. Canary provisioning + repo secrets (Issue 10.2)  *(az login + repo admin)*

**Why:** The PHI-allowlist canary (`.github/workflows/canary.yml`) runs hourly and posts a
synthetic forbidden field, asserting a `422`. The workflow code is merged; it needs its app rows,
keys, and secrets to actually run against Dev + Prod.

**Do:**
1. Run `scripts/provision-canary.ps1` against **Dev** and **Prod** (creates a `canary-test` app +
   a ServerApi key per env). Requires `az login` to the `Adaptive Subscription`.
2. Set GitHub repo secrets: `CANARY_KEY_DEV`, `CANARY_KEY_PROD` (capture `CANARY_APP_ID`).
3. Set the `Observability:CanaryApplicationId` app setting on **both** `obs-api-dev` and
   `obs-api-prod` to the canary app id (keeps canary rows out of real dashboards).
4. Dry-run the workflow via **workflow_dispatch** against Dev before trusting the hourly cron.

---

## 3. Platform self-monitoring standup (Issue 10.3)  *(Azure portal / az login)*

**Why:** If `obs-api-prod` goes down, onboarded apps' SDKs swallow the failure silently (by design)
and nobody is paged. SLOs + the runbook are documented (`docs/slo.md`,
`docs/runbooks/platform-outage.md`); the Azure resources aren't stood up yet.

**Do:**
1. Create the Azure Monitor **availability test** for `obs-api-prod` (and optionally Dev) per
   `docs/slo.md` §3 (probes `/health`).
2. Create the `ag-obs-oncall` **action group** (email now; Teams once 8.4 lands) and the burn-rate
   alert rules from `slo.md` §2.

**Known follow-up (not yours):** SLO-2 (p95 ingest latency) isn't measured yet — needs a server-side
ingest-latency metric. Tracked on the Adaptive side (`slo.md` §5).

---

## 4. Branch protection for CODEOWNERS (Issue 10.11)  *(repo admin)*

**Why:** `.github/CODEOWNERS` is committed and requires you + Arlo on the privacy-critical files
(`docs/privacy-rules.md`, `docs/event-catalog.md`, the allowlist validator, `canary.yml`). CODEOWNERS
does nothing until branch protection enforces it.

**Do:** Repo → **Settings → Branches → add rule for `main`** → enable **"Require review from Code
Owners"** (and require PRs before merge). One checkbox; makes the privacy-review gate real.

---

## 5. Disaster-recovery restore drill (Issue 10.7)  *(Azure SQL access)* — **6.8 cutover gate**

**Why:** PHI storage requires a *tested* restore, not just "backups exist." `docs/disaster-recovery.md`
is a stub waiting on someone who can restore `ObservabilityDev`. **This must be done before SCH Prod
cutover (6.8).**

**Do (fill in `docs/disaster-recovery.md` as you go):**
1. Confirm Azure SQL **PITR** retention (default 7 days) on `adaptivetoolssql`; decide if **LTR** is needed for PHI.
2. Execute a point-in-time **restore of `ObservabilityDev`** to a new DB; document the step-by-step.
3. Record the drill: date, duration, result.
4. Note the communication plan if Prod ingest is down > 1 hour.

---

## 6. Email-provider decision for alerts (Issue 8.4)  *(decision + resource)*

**Why:** The alert rule engine (8.3) is **merged and running** but **visibility-only** — it writes
`FiredAlerts` rows surfaced in the dashboard, and delivers nothing externally. 8.4 adds the delivery
sink. It's blocked on one decision plus a resource.

**Decide:** **ACS vs SendGrid** for email — what does the company already use/pay for? Then:
- Provision the chosen email resource (+ a Microsoft Teams incoming webhook).
- Per-rule rate limiting is in the acceptance criteria.

Once decided, the Adaptive side can build the delivery layer over the already-persisted alerts
without touching rule evaluation.

---

## 7. SCH Prod observability wiring + soak (Issues 6.6 / 6.7 / 6.8)  *(SCH runtime owner)*

**Why:** SCH_UI + SCH_API already have the SDK merged to `dev` (PRs #113 / #177). The platform side
(app rows, Dev keys) is done. The blocker is that **SCH's Prod App Services and Key Vault are not in
the `Adaptive Subscription`** — only you can wire their runtime config.

**Do — SCH_API Prod (App Service config or its KV):**
- `AdaptiveObservability__ApiKey` = the Prod `aoserv_…` key minted by `scripts/onboard-sch.ps1`
- `AdaptiveObservability__HostUrl` = `obs-api-dev` **during the shakedown**, flip to `obs-api-prod`
  only after Adaptive Prod is verified healthy and the soak is clean
- `AdaptiveObservability__Enabled` = `true`
- `AdaptiveObservability__Environment` = `Production`

**Do — SCH_UI Prod (GitHub repo secrets):**
- `VITE_OBSERVABILITY_URL` (= `obs-api-dev` during shakedown, `obs-api-prod` at cutover)
- `VITE_OBSERVABILITY_KEY` (the Prod `aopub_…` key from `onboard-sch.ps1`)

**Also — SCH_API Dev (to start the soak clock):** set the four `AdaptiveObservability__*` values on the
SCH_API **Dev** App Service (note: `ASPNETCORE_ENVIRONMENT=Dev`, not the literal "Development", so the
App Service config path is correct).

**Then the 5-business-day Dev shakedown (6.7) gates cutover:** zero `SafetyViolations` for `sch-ui` +
`sch-api`, daily soak log, privacy reviewer sign-off, and the 5.5 cross-process correlation trace.
**6.8 cutover also requires item 5 (DR drill) to be done first.**

> WMS (Phase 7) will hit the same subscription-isolation pattern — worth a reusable "Prod onboarding
> handoff checklist" for hosters before that phase.

---

## 8. `rrweb` dependency approval (Phase 9 — future, not on the cutover path)

**Why:** Session replay needs `rrweb` + `rrweb-player` (MIT) as net-new frontend dependencies, which
the no-new-dependency-without-approval constraint gates. Nothing starts on Phase 9 until this is
approved, the masking policy is reviewed, and Blob storage topology is decided. Flagging for awareness;
no action needed yet.

---

## What you do **not** need to do

For context — these were on earlier "Brandon" lists and are now resolved or self-serve:
- SQL AAD admin is a **group** (`sg-adaptivetoolssql-aad-admins`, you + Arlo), so `CREATE USER …
  FROM EXTERNAL PROVIDER` grants no longer need a single-person handoff.
- All app/key provisioning is now self-serve via the admin endpoints + the dashboard Admin UI (10.6) —
  no hand-seeded SQL `INSERT`s.
- Dev hosting, DB, KV, CI deploy, and the full feature backlog (RBAC, alerts, retention, fingerprinting,
  dogfooding, bulk export, API versioning) are all done.
