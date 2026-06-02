# PR A1 — Investigation: Plan hygiene + CODEOWNERS (Phase 10.11)

Branch: `chore/plan-hygiene`. This is the Phase 1 deliverable per [`pr-a1-plan-hygiene.md`](pr-prompts/pr-a1-plan-hygiene.md). **No edits to `DEVELOPMENT_PLAN.md` or config have been made yet** — this doc proposes them and stops for review.

All line numbers reference `DEVELOPMENT_PLAN.md` at the current `chore/plan-hygiene` HEAD (1069 lines).

---

## 1. Stale checkbox findings

| Issue / location | Acceptance item | Evidence of completion | Current | Proposed |
|---|---|---|---|---|
| **6.1** (line 416) | "4.11 (SDK auto-bracket sessions) shipped" | [`sessionBracket.ts`](../../packages/observability-client-js/src/sessionBracket.ts) exists + wired in `index.ts`; live-ingest test executed against `obs-api-dev` 2026-05-22 (recorded in 5.7 acceptance, plan line 391 — `Sessions` row with `started_at`/`ended_at`) | `[ ]` | **`[x]`** |
| **6.1** (line 417) | "4.2 (SCH route fixture port)" | [`schParity.test.ts`](../../packages/observability-client-js/src/__tests__/schParity.test.ts) + [`sch-fixtures/`](../../packages/observability-client-js/src/__tests__/sch-fixtures/) present; Issue 4.2 status is "closed with an audit" (50-case parity suite, plan lines 308–321) | `[ ]` | **`[x]`** |
| **6.9** (line 499) | "Saved view reachable via dashboard nav" | PR #15 (`phase-6/sch-dashboard-preset`, "Phase 6.9: SCH dashboard presets") **MERGED 2026-05-23T06:15Z** — verified via `gh pr view 15` | `[ ]` | **`[x]`** |
| **6.9** (line 500) | "Cards match the original PostHog dashboard plan" | Same PR #15 | `[ ]` | **`[x]`** |

### 6.3 / 6.5 — VERIFIED via `gh` and flipped (per review decision)

| Issue | Evidence | Result |
|---|---|---|
| **6.3** (lines 438–442) — SCH_UI | SCH_UI PR #113 "Wire Adaptive Observability SDK (Phase 6.1 + 6.3)" **MERGED to `dev` 2026-05-24**; `compare dev...feature/adaptive-observability` → `ahead_by: 0` (fully contained in `dev`) | All 5 boxes → `[x]` |
| **6.5** (lines 454–460) — SCH_API | SCH_API PR #177 "Wire Adaptive Observability SDK (Phase 6.1 + 6.5)" **MERGED to `dev` 2026-05-24**; `ahead_by: 0` | All 6 boxes → `[x]` |

This also resolves the status-row inconsistency — the Phase 6 row's "merged 2026-05-24" claim is now backed by the checked boxes.

> Out of scope (not flipped): 6.1's *other* items (release_sha, role-names audit, .env.example, dev test endpoint, BG dedup). The prompt scoped 6.1 verification to the 4.11/4.2 items only; the rest aren't independently verified here.

---

## 2. Renumbering plan — Issue 8.9 collision

Two issues carry **8.9**:
- **Admin app/key provisioning endpoint** — line 675, **shipped** (`[x]` across the board).
- **Ingestion queue** — line 717, **open**.

Per the prompt, renumber the **open ingestion-queue** issue to **8.12**.

**Every `8.9` occurrence in the doc, classified:**

| Line | Text (abbrev.) | Refers to | Action |
|---|---|---|---|
| 28 | "minted via the Phase 8.9 admin endpoints" | Admin endpoint | **Keep** |
| 391 | "provisioned via the just-shipped 8.9 admin endpoints" | Admin endpoint | **Keep** |
| 675 | "### Issue 8.9 — Admin app/key provisioning endpoint" | Admin endpoint | **Keep** |
| 717 | "### Issue 8.9 — Ingestion queue" | Ingestion queue | **Rename → `### Issue 8.12 — Ingestion queue`** |
| 929 | "same gate as 8.9 admin endpoints" | Admin endpoint | **Keep** |
| 940 | "8.9 admin endpoints exist but are CLI-only" | Admin endpoint | **Keep** |
| 975 | "provisioned via 8.9 admin endpoints" | Admin endpoint | **Keep** |

**Finding:** the ingestion-queue meaning is referenced in exactly **one place** (its own heading, line 717). There are **no inbound cross-references** to the ingestion-queue "8.9" elsewhere, so the renumber is a single-line edit with zero link breakage. All other six "8.9" mentions point at the admin endpoint and must be left alone.

(8.12 is free — Phase 8 currently runs 8.1–8.11 with the 8.9 duplicate; the highest is 8.11.)

---

## 3. UAT references to remove / rewrite

Context: Option A (2026-05-22) removed UAT from **platform** scope — the platform ships Dev + Prod only. App-side (SCH/WMS) UAT soak gates are a different thing and stay.

### In scope (rewrite)

| Line | Current | Proposed new wording | Rationale |
|---|---|---|---|
| 744 | Phase 9 **Exit criteria**: "**SCH UAT** can opt in for a single feature area, capture-on-error mode produces a viewable replay…" | "**SCH Dev** can opt in for a single feature area (emitting to `obs-api-dev`, per the Option A shape — the platform has no UAT env), capture-on-error mode produces a viewable replay…" | Platform has no UAT env to receive replay. Option A directs SCH UAT traffic to `obs-api-dev`. |
| 733 | Issue 8.11 **Description**: "Exercise a Key Vault secret rotation **in UAT**." | "Exercise a Key Vault secret rotation **as a Dev rotation drill** (against the Dev vault), with the Prod rotation runbook validated via the App Service staging-slot pattern." | UAT vault doesn't exist; Dev vault + Prod runbook is the Option A-consistent replacement. *(Needs a decision — see Open Q.)* |

### In scope to keep (explicitly verified as app-side / correct)

| Line | Text | Decision |
|---|---|---|
| 1033 | Cross-Cutting: "Before Phase 7 **WMS UAT** entry" | **Keep** — this is *WMS* app-side UAT, not platform UAT. Correct as written. |

### Flagged but NOT touched (scope-guard tension — needs a human call)

These are additional UAT mentions the prompt didn't enumerate. The scope guard says *don't* touch Phase 5/6/7/9 UAT mentions describing SCH/WMS soak gates, but a few read as stale *platform* references. Listing for a decision rather than editing:

| Line | Text | Why flagged |
|---|---|---|
| 414 | Issue 6.1 desc: "Resolve before **SCH UAT**." | The Phase 6 gate is now a 5-day **Dev shakedown**, not UAT. Arguably stale, but it's a Phase 6 SCH gate (scope-guarded). |
| 508 | Phase 7 **Exit criteria**: "WMS apps emit … to **adaptive-observability UAT**" | This names a *platform* UAT env, which does **not exist**. Reads genuinely stale (should be `obs-api-dev` / Dev shakedown), but it's Phase 7 (scope-guarded). Strong candidate for a follow-up. |
| 832–836 | Issue 9.10: "2-week **UAT** soak with replay enabled" | Phase 9 replay soak. Consistency with the 744 rewrite suggests this should also become a Dev/staging soak, but it's Phase 9 detail (scope-guarded). |
| 375 | Issue 5.5: "Trace a single **SCH UAT** request" | Phase 5 / SCH app-side. Scope-guarded; keep. |

**My recommendation:** rewrite **744** and **733** (clearly in the prompt's list), keep **1033**, and open a tiny follow-up issue for **508** (the only other true *platform*-UAT reference) rather than expanding this PR. Confirm before I proceed.

---

## 4. CODEOWNERS proposal

Confirmed: `.github/CODEOWNERS` does **not** exist. `.github/` currently holds only `workflows/`. No `.github/pull_request_template.md` exists either.

All protected paths verified present except `canary.yml` (Issue 10.2, not built yet — listing it pre-emptively is fine; CODEOWNERS tolerates not-yet-existing paths):
- ✅ `docs/privacy-rules.md`
- ✅ `docs/event-catalog.md`
- ✅ `backend/src/Observability.Application/Ingestion/PropertyAllowlistValidator.cs`
- ⚠️ `backend/src/Observability.Application/Ingestion/**/*Allowlist*.cs` — no *other* `*Allowlist*` files today (only the validator above); glob is future-proofing.
- ⚠️ `.github/workflows/canary.yml` — does not exist yet (10.2).

### Proposed `.github/CODEOWNERS`

```
# CODEOWNERS — privacy/allowlist surfaces require named-reviewer approval (Issue 10.11).
# Matching is last-match-wins; these are the most safety-critical files in the platform.
# Pair branch protection on `main` with "Require review from Code Owners" for these to be enforced.

# Privacy rules + event catalog (the allowlist's human-readable contract)
/docs/privacy-rules.md            @brandvdo @ArloK223
/docs/event-catalog.md            @brandvdo @ArloK223

# Server-side allowlist enforcement
/backend/src/Observability.Application/Ingestion/PropertyAllowlistValidator.cs   @brandvdo @ArloK223
/backend/src/Observability.Application/Ingestion/**/*Allowlist*.cs               @brandvdo @ArloK223

# The PHI allowlist canary (Issue 10.2) and CODEOWNERS self-protection
/.github/workflows/canary.yml     @brandvdo @ArloK223
/.github/CODEOWNERS               @brandvdo @ArloK223
```

**⚠️ Usernames are a guess — confirm before merge.** Repo collaborators are: `brandvdo`, `Clarence0308`, `bdadaptivewoundmsllc`, `ArloK223`, `ArloK62`. I mapped Brandon → `@brandvdo` and Arlo → `@ArloK223` (the account whose GitHub name is "Arlo Kharod"). But Arlo has two accounts (`ArloK223` + `ArloK62`, and recent PRs #20–22 came from `ArloK62`), and Brandon could be `bdadaptivewoundmsllc` instead of `brandvdo`. **CODEOWNERS silently ignores unknown/typo'd usernames** (no enforcement, no error), so these must be exactly right. See Open Questions.

---

## 5. Branch-protection state (for the PR description)

`gh api repos/adaptivesoftwarellc/Adaptive-MAN/branches/main/protection` → **`404 Branch not protected`**.

`main` has **no branch protection at all** today. So CODEOWNERS alone enforces nothing yet. The implementation PR description must call out the manual repo-settings step (Brandon, repo admin):
- Enable branch protection on `main`
- "Require a pull request before merging" + "Require review from Code Owners"
- (Aligns with the existing top-of-plan TODO that Arlo lacks repo admin.)

---

## 6. Open questions — RESOLVED at review

1. **CODEOWNERS usernames** → Brandon = `@brandvdo`; Arlo = **both** `@ArloK223` + `@ArloK62` (review from either Arlo account counts). Applied. *(Still worth a sanity check that `@brandvdo` is Brandon and not `@bdadaptivewoundmsllc`, since CODEOWNERS fails silently on a wrong handle.)*
2. **6.3 / 6.5** → verify via `gh` and flip. Done (see §1) — both confirmed merged 2026-05-24.
3. **8.11 wording** → keep both (Dev drill + Prod runbook via staging slot). Applied.
4. **Line 508 (Phase 7 exit)** → fix in this PR. Applied (now points at `obs-api-dev`).
5. **PR template** → create new `.github/pull_request_template.md`. Done.

### Remaining (post-merge, repo-admin only)
- **Branch protection on `main`** must be enabled with "Require review from Code Owners" — CODEOWNERS enforces nothing until then (currently `404 Branch not protected`). Brandon owns this.
- Confirm `@brandvdo` vs `@bdadaptivewoundmsllc` for Brandon.

---

## Phase 2 (after approval) — mechanical edit list

1. `DEVELOPMENT_PLAN.md`: flip 4 checkboxes (6.1×2, 6.9×2); rename line 717 heading to 8.12; rewrite UAT lines 744 + 733; (pending Q4) optionally line 508.
2. Create `.github/CODEOWNERS` (commit: `Add CODEOWNERS for privacy + allowlist files (Issue 10.11)`).
3. Create `.github/pull_request_template.md` with the "Touches `docs/privacy-rules.md` or allowlist code? [y/n]" checkbox.
4. PR description notes the manual branch-protection step (Brandon ask).

**Scope held:** docs + config only; no backend/SDK code; no new plan content beyond fixing stale items.
