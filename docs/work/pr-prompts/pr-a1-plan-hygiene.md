# PR A1: Plan hygiene + CODEOWNERS (Phase 10.11)

## Branch
`chore/plan-hygiene`

## Goal
Make [`DEVELOPMENT_PLAN.md`](../../../DEVELOPMENT_PLAN.md) honest after several phases shipped or shifted scope, and land Issue 10.11 by adding `.github/CODEOWNERS` so the privacy-rules + allowlist code paths require named-reviewer approval (currently anyone can merge changes to them).

Smallest, lowest-risk PR in the sequence. Ship before A2.

## What to investigate

Read `DEVELOPMENT_PLAN.md` end-to-end and verify each of the following against the actual repo state. Don't trust the plan — verify.

### Stale checkboxes
1. **Issue 6.1 acceptance** — the first two items reference 4.11 + 4.2 as deferred prereqs. Confirm by checking:
   - `packages/observability-client-js/src/sessionBracket.ts` (4.11 implementation)
   - `packages/observability-client-js/src/__tests__/sch-fixtures/` + `schParity.test.ts` (4.2 fixture port)

2. **Issue 6.3 / 6.5 acceptance** — Phase 6 status row claims "SDK integration merged 2026-05-24" but the 6.3/6.5 sub-issue acceptance criteria are all `[ ]`. Verify the merge state of `feature/adaptive-observability` in the SCH_UI + SCH_API repos (use `gh` against those repos or ask Brandon).

3. **Issue 6.9 acceptance** — status row says "PR #15" merged. Verify: `gh pr view 15 --repo adaptivesoftwarellc/Adaptive-MAN`. If merged, flip the checkboxes.

### Numbering collision
4. **Issue 8.9 is duplicated.** Two issues carry the number:
   - Admin app/key provisioning endpoint (shipped, lines ~674+)
   - Ingestion queue (open, lines ~716+)

   Propose renumbering the open one (ingestion queue) to **8.12** so cross-references don't get confused. Search for `8.9` references throughout the doc and update accordingly.

### Stale UAT references (Option A removed UAT from platform scope 2026-05-22)
5. Cases worth fixing:
   - **Phase 9 exit criteria** still says "SCH UAT can opt in". The 2-week soak that runs in Phase 9 should now run in `obs-api-dev` (Option A shape) or a dedicated Phase 9 staging gate — decide and document.
   - **Issue 8.11 description** says "Exercise a Key Vault secret rotation in UAT." UAT doesn't exist. Replace with "Dev rotation drill" or "Prod rotation drill via staging slot" — decide.
   - **Cross-Cutting "Before Phase 7 WMS UAT entry"** — this is *WMS* UAT, not platform UAT. Probably keep; flag if unclear.

### CODEOWNERS (Issue 10.11)
6. Confirm `.github/CODEOWNERS` does not exist yet. Read Issue 10.11's acceptance criteria (lines ~near end of Phase 10) for the exact path list to protect.

7. Check existing branch protection rules on `main` via `gh api repos/adaptivesoftwarellc/Adaptive-MAN/branches/main/protection` — note current state so the implementation PR can describe what needs to be added in GitHub UI (CODEOWNER review required for protected paths).

## Deliverable

### Phase 1 — investigation doc
File: `docs/work/pr-a1-investigation.md`

Sections:
- **Stale checkbox findings** — table with: file/issue location, evidence of completion, proposed checkbox state
- **Renumbering plan** — list of every cross-reference to "8.9" that points at the ingestion-queue meaning (so the renumber doesn't break links)
- **UAT references to remove/rewrite** — exact line numbers + proposed new wording
- **CODEOWNERS proposal** — full proposed file content
- **Open questions** — anything that needs a human call before implementation

Stop here and request review.

### Phase 2 — implementation (after human approval of the doc)
- Update `DEVELOPMENT_PLAN.md` with the approved edits
- Create `.github/CODEOWNERS` (commit message: `Add CODEOWNERS for privacy + allowlist files (Issue 10.11)`)
- Add a PR-template checkbox at `.github/pull_request_template.md` per 10.11's third acceptance criterion: "Touches `docs/privacy-rules.md` or allowlist code? [y/n]"
- Note in the PR description that branch protection still needs a manual config step in repo settings (CODEOWNER review required) — this is a Brandon ask.

## Scope guards
- Docs + config only. **No backend or SDK code in this PR.**
- Don't touch Phase 5/6/7/9 UAT mentions that describe *SCH/WMS soak gates* — those are app-side validation, not platform infrastructure.
- Don't add new content to `DEVELOPMENT_PLAN.md` beyond fixing what's stale — Phase 10 was just added (PR #21) and a third planning PR risks fragmentation.

## Expected effort
~1 hour total. Investigation doc is the bulk; implementation is mechanical.
