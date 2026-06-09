## Summary

<!-- What changed and why. -->

## Privacy / allowlist gate (Issue 10.11)

- [ ] **Does this PR touch `docs/privacy-rules.md`, `docs/event-catalog.md`, or allowlist code (`PropertyAllowlistValidator.cs` / `*Allowlist*.cs`)?** [y/n]
  - If **yes**: CODEOWNER review (Brandon + Arlo) is required and must not be auto-approved. Describe the new allowed/forbidden fields and link the privacy-reviewer sign-off.

## Migration type (Issue 10.9)

- [ ] **No schema change** in this PR.
- [ ] **Additive** migration only (new table / nullable or defaulted column / index) — safe at
      startup via `MigrateAsync`, old running code unaffected.
- [ ] **Non-additive** (`DropColumn` / `RenameColumn` / `AlterColumn` / narrowing / data
      rewrite) — this PR is one labeled step of an expand/contract sequence. Tag the migration
      `expand-contract: N of M` and describe the dual-write / backfill / read-switch plan here.

See [`docs/database-migrations.md`](../docs/database-migrations.md). Rollback is a
forward-reversing migration, **not** a Prod `Down`.

## Verification

<!-- Tests run, manual checks, CI status. -->
