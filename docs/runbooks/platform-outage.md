# Runbook — adaptive-observability platform outage

First-response steps when the platform is unreachable or burning error budget. Triggered by the Azure Monitor availability alert (see [`../slo.md`](../slo.md) §2). Primary symptom: a **fast-burn** alert on `obs-api-prod-health`, or SCH reporting that telemetry stopped flowing.

**Scope:** `obs-api-prod` (and `obs-api-dev` for early warning). The SDK is fire-and-forget, so an ingest outage is **silent to SCH end users** — no app errors, just missing data. Speed matters: data emitted during the outage is lost, not queued.

**On-call escalation contact:** Brandon.

---

## 0. Triage (≤ 2 min)

- Confirm it's real: hit `https://obs-api-prod.azurewebsites.net/health` directly. Expect `{"status":"ok",...}`.
  - **200** → likely a monitoring false positive (single-region blip). Check the availability test's per-location results; if < 3 locations failed, no action. Note and close.
  - **Non-200 / timeout / connection refused** → proceed.
- Check the [Azure status page](https://azure.status.microsoft/) for a `centralus` / App Service regional incident. If Azure is down regionally, this is a wait-and-communicate situation — skip to §5.

---

## 1. App Service health

```bash
az webapp show -g AdaptiveTools -n obs-api-prod --query state -o tsv          # expect "Running"
az webapp log tail -g AdaptiveTools -n obs-api-prod                           # live logs — look for startup/crash loops
```
- **State ≠ Running** → §4 (restart).
- **Crash loop / unhandled startup exception** in logs (e.g. failed migration, missing secret) → most common cause is KV access (§3) or a bad deploy. Check the most recent deploy in the `backend` workflow; if a deploy immediately preceded the outage, **roll back** by re-running the last known-good `deploy-prod` job.
- **5xx but process up** → likely SQL (§2).

---

## 2. SQL health

The API runs migrations on startup and writes every ingest to SQL; a SQL outage surfaces as 5xx or failed startup.

```bash
az sql db show -g AdaptiveTools -s adaptivetoolssql -n ObservabilityProd --query status -o tsv   # expect "Online"
```
- **Not Online / paused** → check the SQL server, DTU/vCore throttling, and the firewall (App Service outbound must be allowed). Resume if paused.
- **Online but API can't connect** → the connection string is a KV secret resolved at startup → §3.

---

## 3. Key Vault access

Secrets (DB connection string, `ObservabilityAdminKey`, API-key pepper) resolve from Key Vault via the App Service's managed identity at startup. A KV/MI regression takes the whole service down on next restart.

```bash
az webapp identity show -g AdaptiveTools -n obs-api-prod                       # confirm the MI principalId
# Confirm the MI still has "Key Vault Secrets User" on the vault holding Prod secrets.
az keyvault secret show --vault-name <prod-vault> --name ObservabilityAdminKey --query "attributes.enabled"
```
- **Access denied / role missing** → re-grant `Key Vault Secrets User` to the MI (needs vault Owner / User Access Administrator — see [`../azure-provisioning-runbook.md`](../azure-provisioning-runbook.md) §1). This requires Brandon if you lack the role.
- **Secret disabled / rotated** → confirm the current secret version is enabled.

---

## 4. Restart App Service

If §1–§3 don't reveal a root cause, restart to clear a wedged process:

```bash
az webapp restart -g AdaptiveTools -n obs-api-prod
sleep 45
curl -s -o /dev/null -w "%{http_code}\n" https://obs-api-prod.azurewebsites.net/health   # expect 200
```
Re-probe a few times (cold start can take ~30–45s, mirroring the deploy smoke step in `backend.yml`).

---

## 5. Escalate

If `/health` is still not 200 after the restart, **escalate to Brandon** with:
- The alert (which SLI, fast/slow burn, when it fired).
- What §1–§4 showed (App Service state, SQL status, KV access, restart result).
- Whether a deploy preceded the outage (rollback candidate).
- Azure status-page findings (regional incident y/n).

While escalated: note the outage start time for the error-budget ledger ([`../slo.md`](../slo.md) §4), and if it's a regional Azure incident, there's no local fix — monitor and communicate to SCH that telemetry is paused.

---

## Post-incident

- Record outage start/end and root cause; debit the error budget.
- If a deploy caused it, capture what the pre-deploy smoke missed and whether the burn-rate threshold caught it fast enough.
- File follow-ups (e.g. the SLO-2 latency metric gap in [`../slo.md`](../slo.md) §5, or a missing alert).
