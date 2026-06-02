# Platform SLOs & Self-Monitoring — adaptive-observability

Service-level objectives for the observability platform, plus the external availability monitoring that measures them. Implements Issue **10.3** (a pre-cutover gate per [`pr-a2-cutover-gates.md`](work/pr-prompts/pr-a2-cutover-gates.md)). Outage response lives in [`runbooks/platform-outage.md`](runbooks/platform-outage.md).

The platform is the custom PostHog replacement; SCH is the first onboarded tenant. These SLOs apply to the **Prod** environment (`obs-api-prod`); Dev (`obs-api-dev`) is monitored for early warning but is not held to the objective.

---

## 1. Objectives

| # | SLI | Objective | Window |
|---|-----|-----------|--------|
| **SLO-1** | Availability of `/api/ingest/*` (fraction of probes returning 2xx) | **≥ 99.5%** | rolling 30 days |
| **SLO-2** | Ingest write latency, p95 (time to persist an accepted event) | **< 200 ms** | rolling 30 days |
| **SLO-3** | Error budget (the inverse of SLO-1) | ≤ **0.5%** unavailability = **~3h 39m / 30 days** | rolling 30 days |

**Why these:** SCH apps emit telemetry over the ingest surface. If ingest is down, SCH loses observability data silently (fire-and-forget SDK). Availability of the *ingest* path is therefore the headline SLI — dashboard read availability is explicitly **not** an SLO at this stage (dashboard is internal-only and tolerant of brief outages).

`/health` is the probe target for availability (it exercises the App Service host); it does **not** measure SLO-2. Ingest latency (SLO-2) requires a server-side latency metric — see §5 (follow-up).

---

## 2. Error budget & burn-rate alerts

Error budget for SLO-1 = `0.5%` of the 30-day window = **~219 minutes**.

Multi-window burn-rate alerts (Google SRE workbook style), evaluated by Azure Monitor on the availability test's `failed location` count:

| Alert | Burn rate | Condition (short + long window) | Budget consumed if sustained | Severity |
|-------|-----------|----------------------------------|------------------------------|----------|
| **Fast burn** | 14.4× | failures over **5 min** AND **1 h** | 2% of 30-day budget in 1h | Sev 1 — page |
| **Slow burn** | 6× | failures over **30 min** AND **6 h** | 10% of budget in 6h | Sev 2 — notify |

A fast-burn alert is the primary trigger for [`runbooks/platform-outage.md`](runbooks/platform-outage.md).

---

## 3. Monitoring tool — Azure Monitor Availability Tests

**Decision (2026-06-01):** no existing uptime monitor was in place; we stand up **Azure Monitor Standard availability tests** (URL ping) because the platform already runs on Azure App Service in the existing subscription — no new vendor, no new bill. One test resource per environment, both targeting `/health`.

This config is documented here rather than captured as IaC (one small resource per env); promoting it to Bicep/Terraform is a possible follow-up.

### Shared values
```
Subscription:    Adaptive Subscription (a21b1a73-9dce-458b-933c-b735703be5c4)
Resource group:  AdaptiveTools
Location:        centralus
Frequency:       300s (5 min)
Test locations:  5 regions (min 3 must agree before alerting → suppresses single-region blips)
Success:         HTTP 200, response < 30s
```

### Per-environment targets
| Env | Probe URL | Application Insights resource | Held to SLO? |
|-----|-----------|-------------------------------|--------------|
| Prod | `https://obs-api-prod.azurewebsites.net/health` | `appi-obs-prod` | **Yes** |
| Dev  | `https://obs-api-dev.azurewebsites.net/health`  | `appi-obs-dev`  | No (early warning) |

### Portal setup (per env)
1. App Insights resource → **Availability** → **Add Standard test**.
2. Name `obs-api-<env>-health`; URL = probe URL above; parse dependent requests **off**; retries **on**.
3. Frequency 5 min; 5 test locations; success = 200.
4. Create alert rule: failed locations **≥ 3**, evaluated per §2 burn-rate windows.
5. Action group `ag-obs-oncall` → email + (eventual) Teams webhook. Teams is deferred to Issue 8.4; email-only is acceptable for cutover.

### CLI equivalent (Prod)
```bash
# Assumes App Insights `appi-obs-prod` already exists in AdaptiveTools.
az monitor app-insights web-test create \
  --resource-group AdaptiveTools \
  --name obs-api-prod-health \
  --location centralus \
  --web-test-kind ping \
  --defined-web-test-name obs-api-prod-health \
  --tags "hidden-link:/subscriptions/a21b1a73-9dce-458b-933c-b735703be5c4/resourceGroups/AdaptiveTools/providers/microsoft.insights/components/appi-obs-prod=Resource" \
  --frequency 300 \
  --locations Id=us-ca-sjc-azr Id=us-tx-sn1-azr Id=us-il-ch1-azr Id=emea-nl-ams-azr Id=apac-sg-sin-azr \
  --http-verb GET \
  --request-url https://obs-api-prod.azurewebsites.net/health \
  --ssl-check true \
  --expected-status-code 200
```

> Action-group + alert-rule creation (`az monitor metrics alert create` against the test's `availabilityResults/availabilityPercentage`) is the second half; configure the burn-rate windows from §2. The on-call action group is created once and shared by both env alerts.

---

## 4. Reporting

- **Weekly:** review the 7-day availability % and p95 trend in the App Insights Availability blade.
- **Monthly:** record the rolling-30-day SLO-1 / SLO-2 attainment and remaining error budget. If budget is exhausted, freeze non-critical ingest-path changes until it recovers.

---

## 5. Known limitation / follow-up

SLO-2 (p95 ingest latency) is **stated but not yet measured** — `/health` availability probes don't time the ingest write path. Closing this needs a server-side latency metric emitted from the ingestion endpoints (e.g. an App Insights custom metric or a histogram on `IngestionService`). Tracked as a follow-up; not in scope for this PR (8.8/10.1/10.2/10.3 cutover gates).
