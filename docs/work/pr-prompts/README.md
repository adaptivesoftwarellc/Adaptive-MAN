# PR prompts

Investigation prompts for the upcoming PR sequence surfaced by the 2026-06-01 mission-audit (Phase 10) and the prior plan-hygiene review.

Each prompt is **self-contained** — written for an agent that walks in with no prior context. It tells the agent which branch to create, what to investigate, what already exists, what gaps to surface, and what `.md` deliverable to produce **before** any code is written. After the investigation `.md` is reviewed and approved, the same branch lands the implementation.

Sequencing (independent unless marked):

| PR | File | Critical path | Depends on |
|---|---|---|---|
| A1 | [pr-a1-plan-hygiene.md](pr-a1-plan-hygiene.md) | SCH cutover | — |
| A2 | [pr-a2-cutover-gates.md](pr-a2-cutover-gates.md) | SCH cutover | A1 (CODEOWNERS in place first) |
| B  | [pr-b-wms-audits.md](pr-b-wms-audits.md) | WMS Phase 7 prep | — (parallelizable) |
| C  | [pr-c-audit-logging.md](pr-c-audit-logging.md) | 10.6 admin UI | — |
| D  | [pr-d-api-versioning.md](pr-d-api-versioning.md) | WMS Phase 7 onboarding | — (recommended before Phase 7) |
| E  | [pr-e-bulk-export.md](pr-e-bulk-export.md) | Anti-lock-in promise | — |

A1 → A2 → C is the SCH-Prod-cutover sequence. B → D is the WMS-Phase-7 prep sequence. E is independent. Each can run in parallel where dependencies allow.

## How to use a prompt

1. Hand the prompt to an agent (or yourself) cold.
2. Agent creates the branch and produces the investigation `.md` listed in the **Deliverable** section.
3. Review the investigation doc. Reject, adjust scope, or approve.
4. On approval, the agent writes the implementation on the same branch.
5. Open the PR.
