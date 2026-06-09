# Disaster recovery — STUB (blocked on Azure access)

> **Status: not yet executed. Owner: Brandon.** This is a deliberate stub left by Issue 10.7's
> docs portion so the gap is visible. Everything below needs Azure SQL access to decide and
> record, which is outside the non-Brandon scope.

## What this doc must contain (to be filled when Azure access is available)

- **PITR (point-in-time restore)** decision — retention period for automated backups on the
  Azure SQL databases.
- **LTR (long-term retention)** decision — weekly/monthly/yearly retention policy, if any.
- **Restore drill** — a real restore of `obs-api-dev` from backup, with steps and timings.
- **Executed-drill record** — proof the restore actually succeeded (date, RTO/RPO observed,
  who ran it), not just the procedure.

## ⚠ Cutover gate

This restore drill is a **prerequisite for the 6.8 Prod cutover**. Cutover **cannot proceed**
until the drill has been run and recorded here. See the
[compliance posture](compliance.md#disaster-recovery-deferred) note.
