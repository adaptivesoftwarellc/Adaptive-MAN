# Compliance posture

The parts of our compliance story that are decided in code and configuration and need no live
Azure access to document. The disaster-recovery drill portion of Issue 10.7 is **deferred to
Brandon** — see [Disaster recovery](#disaster-recovery-deferred) below.

> Scope: `adaptive-observability` ingests **safe events and errors only**. The server allowlist
> (`PropertyAllowlistValidator`) rejects forbidden properties as `SafetyViolation` rows, so PHI
> is not expected to land in the store. See [`privacy-rules.md`](privacy-rules.md). The controls
> below are defense-in-depth on top of that.

## BAA with Microsoft

Microsoft offers a HIPAA Business Associate Agreement covering the Azure services we use (Azure
SQL Database, App Service, Key Vault) as in-scope "covered services" under the Microsoft Product
Terms / DPA. Our use of those services for any data that could be regulated is governed by that
BAA.

- **Covered services in use:** Azure SQL Database, Azure App Service, Azure Key Vault — all on
  Microsoft's HIPAA/HITECH covered-services list.
- **Acceptance record:** _<fill in: tenant-level BAA acceptance reference / agreement id from the
  Microsoft 365 / Azure admin portal>_. This is an org-tenant fact, not a code fact — confirm
  and link the signed/accepted record here.

## Encryption at rest

Both defaults are **on by Azure default**, so no explicit configuration is required — but they
are stated here as part of the posture.

- **Azure SQL — Transparent Data Encryption (TDE).** Enabled by default on all Azure SQL
  databases, using a service-managed key. Encrypts data, log, and backup files at rest. We rely
  on the platform default; no customer-managed key (CMK / BYOK) is configured.
- **Azure Key Vault.** Secrets are encrypted at rest by default with Microsoft-managed keys
  backed by FIPS 140-validated HSMs. Soft-delete + (in Prod) purge-protection are enabled per
  [`azure-key-vault-setup.md`](azure-key-vault-setup.md) and the
  [provisioning runbook](azure-provisioning-runbook.md).

## Encryption in transit

All SDK → API traffic is HTTPS; the API is fronted by App Service TLS. SDK transports POST over
`https://` ingest URLs (see the SDK READMEs). Secrets reach the App Service over Key Vault's
TLS endpoint via managed identity.

## Network posture (public + firewall vs private endpoint)

**Current decision: public network access + Azure SQL firewall allowlist**, scoped to the App
Service's outbound IP set. Recorded during provisioning (2.4) —
[`azure-provisioning-runbook.md` §4](azure-provisioning-runbook.md) posture note:

> App Service VNet integration + private endpoint is the cleaner long-term answer; the call was
> to start with public + firewall for Dev and revisit at UAT/Prod.

**Trade-off / rationale:**
- *Public + firewall (current):* simpler to stand up, no VNet/private-DNS plumbing; the SQL
  server still rejects everything outside the allowlisted outbound IPs. Operational cost: the
  firewall must be re-synced after any App Service plan scale-out, because outbound IPs change.
- *Private endpoint (deferred):* removes the public surface entirely and is the preferred
  hardened posture. Deferred to a UAT/Prod revisit; tracked as the remaining network-hardening
  item in the provisioning runbook.

Managed identity per App Service has scoped read on its same-environment Key Vault only (see
[`architecture.md`](architecture.md)) — no shared secrets across environments.

## Audit log retention

Admin and access actions are recorded in the `AuditLogs` table (added in `Phase8AdminAuditLog`).

- **Retention target: 365 days** for `AuditLogs` rows.
- **Enforcement:** the retention sweep that actually deletes past-retention rows lands with
  **Issue 8.5** (per-app event/error retention + nightly Worker sweep). Until 8.5 ships, the
  365-day figure is the documented target, not yet machine-enforced — the sweep will write an
  `admin.retention.swept` audit row per run.

## Disaster recovery (deferred)

**Blocked on Azure access — owned by Brandon.** The DR portion of Issue 10.7 needs Azure SQL
access to execute and record, so it is **not** covered by this doc:

- [`disaster-recovery.md`](disaster-recovery.md) — a restore drill against `obs-api-dev`
  (stub created; awaiting Azure access to fill in and execute).
- PITR (point-in-time-restore) / LTR (long-term-retention) backup decisions.
- The executed-drill record (proof the restore actually works).

> ⚠ **Cutover gate.** 10.7's DR restore drill is a **prerequisite for the 6.8 Prod cutover** —
> cutover cannot proceed until the drill has been run and recorded. This gap is intentionally
> left visible here so it isn't lost.
