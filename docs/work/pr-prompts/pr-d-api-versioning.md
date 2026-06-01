# PR D: Issue 10.4 API versioning

## Branch
`phase-10/4-api-versioning`

## Goal
Introduce a `/api/v1/` route prefix (aliasing existing paths) and an `X-Observability-SDK-Version` header convention. Establish a clean wire-protocol evolution path **before** WMS Phase 7 adds the second SDK consumer.

Backwards-compatible by design — existing deployed SDKs (SCH) keep working unchanged.

## Context

Today SDKs hardcode `/api/ingest/events`, `/api/ingest/errors`, `/api/ingest/sessions/start`, `/api/ingest/sessions/end`. There is no `/v1/` prefix and no SDK-version header. When the wire protocol needs a breaking change, every deployed SDK breaks at once. Cheap to introduce versioning now; expensive to retrofit after WMS adds the second consumer.

This PR is **strictly additive**:
- New routes `/api/v1/*` alias the existing ones (same handlers, no behavioral change)
- Existing unprefixed routes continue working
- SDKs start sending the version header; backend reads it but does not reject anything

## What to investigate

### Backend route inventory
1. Read `backend/src/Observability.Api/Program.cs` and enumerate every `MapGroup(...)` / `MapGet(...)` / `MapPost(...)`. Build a table: existing path → proposed `/api/v1/...` mirror. Likely set:
   - `/api/ingest/events` → also `/api/v1/ingest/events`
   - `/api/ingest/errors` → also `/api/v1/ingest/errors`
   - `/api/ingest/sessions/start` → also `/api/v1/ingest/sessions/start`
   - `/api/ingest/sessions/end` → also `/api/v1/ingest/sessions/end`
   - `/api/sessions/{id}/timeline` → also `/api/v1/sessions/{id}/timeline`
   - `/api/apps`, `/api/dashboard/*` → keep unprefixed (dashboard-only; not SDK contract)
   - `/api/admin/*` → keep unprefixed (admin contract evolves separately)
2. Confirm whether the minimal-API style allows registering the same handler under two paths cleanly (extract the handler to a method; register twice).

### SDK call sites
3. **JS SDK** — read `packages/observability-client-js/src/` and find every `fetch` / `axios` URL construction. List each: file:line + endpoint. Likely centralized in a single transport module.
4. **.NET SDK** — same for `packages/observability-client-dotnet/`. Likely centralized in a similar transport class.
5. Identify how each SDK currently builds URLs — query for `/api/` literal strings.

### SDK version sources
6. JS: `packages/observability-client-js/package.json` `version` field. The build pipeline already publishes this — find where to read it at runtime (probably inlined at build time via Vite's `import.meta.env` or a generated file).
7. .NET: `packages/observability-client-dotnet/*.csproj` `Version` property. Use `Assembly.GetExecutingAssembly().GetName().Version` at runtime.

### Versioning policy
8. Author the deprecation policy section for `docs/api-contract.md`:
   - Support unprefixed paths until SDK major version 1.0 ships
   - Support N-1 minor SDK version for 6 months after the next minor lands
   - Drop with a major server release
9. Decide: should the backend **reject** SDKs below a configurable floor, or only **log warnings**? Lean: log only, for now. Rejection becomes meaningful with v2 of the wire protocol — too early to enforce.

## Deliverable

### Phase 1 — investigation doc
File: `docs/work/pr-d-investigation.md`

Sections:
- **Route inventory** — table of existing → mirror, with confirmation that minimal-API supports the dual-register pattern
- **SDK call site inventory** — file:line for each URL construction in both SDKs
- **Version source plan** — how each SDK reads its own version at runtime
- **Header convention** — exact name, value format (semver string), behavior on missing header (treat as "unknown" — log)
- **Deprecation policy proposal** — full text ready to land in `docs/api-contract.md`
- **Risk** — header collision with existing `X-Observability-*` family; behavior under load balancers that strip unknown headers
- **Open questions**

Stop here and request review.

### Phase 2 — implementation (after approval)
1. **Backend**:
   - Extract each ingestion handler to a named delegate
   - Register each under both unprefixed and `/api/v1/...`
   - Add `X-Observability-SDK-Version` header reading middleware/filter — logs a `Warning` when missing or below a configurable floor
   - Update `docs/api-contract.md` with version negotiation + deprecation policy

2. **JS SDK**:
   - Bump minor version (additive change)
   - Send `X-Observability-SDK-Version: <package.json version>` on every request
   - URL builder unchanged — still hits unprefixed paths; SDK-major-bump can switch to `/v1/`

3. **.NET SDK**:
   - Bump minor version (additive change)
   - Send the same header
   - Same URL behavior

4. **Tests**:
   - Integration test: POST to `/api/v1/ingest/events` succeeds with the same shape as unprefixed
   - Both prefixed and unprefixed paths persist identical rows
   - Backend log captures the SDK version when supplied

5. **SDK release notes**:
   - JS + .NET CHANGELOGs note the new header + that URL paths are unchanged for now

## Scope guards
- **No SDK major version bumps.** Both SDKs ship a minor that's backwards-compatible.
- **No path changes** in the deployed SDK call sites yet — they continue to hit unprefixed routes. Switching to `/v1/` is a follow-up.
- **No floor-version rejection.** Logging only.
- **Don't version admin or dashboard endpoints** — only the ingest/sessions surface that SDKs talk to.

## Expected effort
~1 day. Investigation ~half-day; implementation + tests ~half-day.
