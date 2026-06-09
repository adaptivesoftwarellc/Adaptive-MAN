# Database migrations — safety playbook

How we evolve the Azure SQL schema without downtime or data loss. Read this before
writing any migration that is not purely additive.

## How migrations run

The API applies migrations at startup via `await db.Database.MigrateAsync();`
([`Observability.Api/Program.cs`](../backend/src/Observability.Api/Program.cs)). There is no
separate migration step in the deploy pipeline — the new app instance runs every pending
migration the moment it boots, **before** it serves traffic, and during a rolling deploy the
old and new app versions overlap. That single fact drives every rule below: a migration must
be safe for the **old** code to keep running against the **new** schema, and vice versa.

## Classify every migration

### Additive (safe at startup)
Adds something the old code can ignore and the new code can use. Safe to apply automatically
via `MigrateAsync` with overlapping app versions:

- `CREATE TABLE`
- Add a **nullable** column, or a column with a default
- `CREATE INDEX` (prefer `WITH (ONLINE = ON)` on large tables to avoid locks)
- Add a new check/foreign-key constraint **only** if existing data already satisfies it

All migrations so far are additive — `Initial`, `Phase5HardeningIndexes`, `Phase8AdminAuditLog`.
This playbook exists so the first destructive one is done deliberately.

### Non-additive (requires expand → contract)
Anything that can break the currently-running version, or that loses/rewrites data:

- `DROP COLUMN`, `DROP TABLE`
- `RENAME COLUMN` / `RENAME TABLE`
- Narrowing or retyping a column (`ALTER COLUMN`), adding `NOT NULL` to an existing column
- Adding a constraint existing data violates
- Any data backfill that rewrites existing rows

**Never ship one of these as a single migration.** Use expand/contract.

## The expand → contract pattern

Split a non-additive change across **multiple releases** so old and new code are always
compatible with the schema in between. Canonical example — renaming `Errors.Fingerprint`
to `Errors.FingerprintHash`:

1. **Expand** (release N, additive): add the new column `FingerprintHash`, nullable. Old code
   ignores it; deploy is safe.
2. **Dual-write** (release N): new code writes **both** `Fingerprint` and `FingerprintHash` on
   every insert/update. Reads still come from the old column.
3. **Backfill** (release N or N+1): a one-off/idempotent job copies `Fingerprint` →
   `FingerprintHash` for existing rows. Batch it; keep it re-runnable.
4. **Switch reads** (release N+1): once backfill is verified complete, new code reads from
   `FingerprintHash`. Still dual-writing.
5. **Contract** (release N+2, only after no running code references the old column): stop
   writing the old column, then `DROP COLUMN Fingerprint` in its own migration.

Each step is a separate, individually-deployable PR. You can collapse steps that are provably
safe together, but never collapse across a point where the old running version would break.

## When do you actually need a maintenance window?

Almost never. Expand/contract removes the need for a window in the common cases. Reach for a
window **only** when all of these are true and expand/contract genuinely can't cover it:

- The change can't be made compatible with the running version even transiently (e.g. a
  storage-engine-level rewrite), **and**
- The data volume makes an online operation impractical, **and**
- You can tolerate ingest being paused.

If you're considering a window, write down why expand/contract doesn't work and get a second
reviewer before scheduling it.

## Rollback = roll forward

**Do not rely on EF `Down` migrations in Prod.** A `Down` that drops a just-added column will
delete data written since the deploy, and it assumes the offending migration was the last one
applied. Instead:

- To undo a bad schema change, author a **new** migration that reverses it (a forward fix).
- Because every step is expand/contract, the previous app version is still schema-compatible —
  rolling **app code** back to the prior release is the fast mitigation; the schema can stay
  ahead safely.
- Keep `Down` methods present (EF generates them) for local dev convenience, but treat Prod
  recovery as roll-forward only.

## PR checklist

Every schema-touching PR must declare its migration type (see the
[PR template](../.github/pull_request_template.md)):

- [ ] Migration is **additive**, **or**
- [ ] Migration is **non-additive** and is one labeled step of an expand/contract sequence,
      tagged `expand-contract: N of M` in a comment on the migration, with the dual-write /
      backfill / read-switch plan described in the PR.
- [ ] Existing migrations (`Initial`, `Phase5HardeningIndexes`, `Phase8AdminAuditLog`, …) are
      **untouched**.
- [ ] Rollback path is a forward-reversing migration (and/or app-version rollback), not a
      Prod `Down`.

## Optional CI lint (future)

A CI step can fail the build when a generated migration contains `DropColumn`,
`RenameColumn`, or `AlterColumn` **without** an `expand-contract: N of M` comment in the same
file — turning the checklist into an enforced gate. Not yet wired; tracked as the optional
portion of Issue 10.9.
