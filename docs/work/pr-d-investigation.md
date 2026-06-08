# PR D (Issue 10.4) — API Versioning Investigation

Phase 1 deliverable for `phase-10/4-api-versioning`. Strictly additive plan: introduce a
`/api/v1/` route prefix that aliases the existing ingest/session surface, plus an
`X-Observability-SDK-Version` header convention. No behavioral change for deployed SDKs.

**Status: awaiting review before Phase 2 implementation.**

---

## Route inventory

Source: [backend/src/Observability.Api/Program.cs](../../backend/src/Observability.Api/Program.cs),
[Endpoints/IngestionEndpoints.cs](../../backend/src/Observability.Api/Endpoints/IngestionEndpoints.cs),
[Endpoints/SessionEndpoints.cs](../../backend/src/Observability.Api/Endpoints/SessionEndpoints.cs).

| Existing path | SDK contract? | Proposed `/api/v1/` mirror | Action |
|---|---|---|---|
| `POST /api/ingest/events` | yes | `POST /api/v1/ingest/events` | mirror |
| `POST /api/ingest/errors` | yes | `POST /api/v1/ingest/errors` | mirror |
| `POST /api/ingest/sessions/start` | yes | `POST /api/v1/ingest/sessions/start` | mirror |
| `POST /api/ingest/sessions/end` | yes | `POST /api/v1/ingest/sessions/end` | mirror |
| `GET /api/sessions/{id}/timeline` | read-only (dashboard) | `GET /api/v1/sessions/{id}/timeline` | mirror (low cost; keeps the SDK-facing read surface consistent) |
| `GET /health`, dev smoke-test | no | — | leave unprefixed |
| `MapDashboardEndpoints()` (`/api/dashboard/*`, `/api/apps`) | no | — | leave unprefixed (dashboard-only) |
| `MapAdminEndpoints()` (`/api/admin/*`) | no | — | leave unprefixed (admin contract evolves separately) |

### Dual-register pattern — confirmed viable

The ingest endpoints already register handlers as named static methods on a `RouteGroupBuilder`
(`IngestionEndpoints.MapIngestionEndpoints`, `SessionEndpoints.MapSessionIngestEndpoints`). The
group is built in `Program.cs:75-77`:

```csharp
var ingest = app.MapGroup("/api/ingest").AddApiKeyAuth().RequireRateLimiting(RateLimitingExtensions.IngestPolicy);
ingest.MapIngestionEndpoints();
ingest.MapSessionIngestEndpoints();
```

Because the map-extension methods take a `RouteGroupBuilder` and the handlers are already named
methods, **registering a second group costs three lines** and reuses every handler unchanged:

```csharp
var ingestV1 = app.MapGroup("/api/v1/ingest").AddApiKeyAuth().RequireRateLimiting(RateLimitingExtensions.IngestPolicy);
ingestV1.MapIngestionEndpoints();
ingestV1.MapSessionIngestEndpoints();
```

The timeline read endpoint (`SessionEndpoints.MapSessionReadEndpoints`) hardcodes its absolute path
`/api/sessions/{sessionId}/timeline` in a `MapGet`. To mirror it, the method should accept a path
prefix (or register a second `MapGet`). No handler change — `GetTimeline` stays as-is.

No conflict risk: ASP.NET minimal APIs allow the same handler delegate under multiple distinct route
templates. The two groups produce distinct templates, so there is no ambiguous-match exception.

---

## SDK call site inventory

### JS SDK (`packages/observability-client-js`)

URL construction is centralized in two modules:

| File:line | Endpoint | Notes |
|---|---|---|
| [transport.ts:82](../../packages/observability-client-js/src/transport.ts#L82) | `/api/ingest/${path}` (events / errors) | batched send |
| [sessionBracket.ts:31](../../packages/observability-client-js/src/sessionBracket.ts#L31) | `/api/ingest/sessions/start` | fire-and-forget |
| [sessionBracket.ts:41](../../packages/observability-client-js/src/sessionBracket.ts#L41) | `/api/ingest/sessions/end` | fire-and-forget |

Request headers are set in two places — both will gain the version header:
- [transport.ts:85-94](../../packages/observability-client-js/src/transport.ts#L85-L94) (`send`)
- [sessionBracket.ts:46-57](../../packages/observability-client-js/src/sessionBracket.ts#L46-L57) (`fireFetch`)

Per scope guards, **URL paths stay unprefixed** in this PR — only the header is added.

### .NET SDK (`packages/observability-client-dotnet`)

| File:line | Endpoint | Notes |
|---|---|---|
| [AdaptiveObservabilityService.cs:168](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityService.cs#L168) | `api/ingest/events` / `api/ingest/errors` (relative to `HostUrl` base) | batched send |

The .NET SDK does **not** send session start/end brackets (server-side consumer; no browser
lifecycle). Headers are configured once on the shared `HttpClient` at construction
([AdaptiveObservabilityService.cs:47-50](../../packages/observability-client-dotnet/src/Adaptive.ObservabilityClient/AdaptiveObservabilityService.cs#L47-L50)),
so the version header is added there as a `DefaultRequestHeader` — applies to every request.

---

## Version source plan

### JS — `package.json` `version` (currently `0.1.0`)

Build is via tsup ([tsup.config.ts](../../packages/observability-client-js/tsup.config.ts)); there is
no runtime version reference today. Plan: inline at build time with a tsup `define`, reading the
version from `package.json` in the config:

```ts
// tsup.config.ts
import pkg from "./package.json" assert { type: "json" };
export default defineConfig({
  // ...
  define: { __SDK_VERSION__: JSON.stringify(pkg.version) },
});
```

Declare `declare const __SDK_VERSION__: string;` in a `globals.d.ts` (or an ambient block) and
reference it from the transport/bracket header. This keeps the version a compile-time constant with
zero runtime cost and no import of `package.json` into the shipped bundle.

### .NET — `.csproj` `<Version>` (currently `0.1.2`)

Read at runtime:

```csharp
typeof(AdaptiveObservabilityService).Assembly.GetName().Version?.ToString() ?? "unknown"
```

`GetName().Version` returns the `AssemblyVersion`. To get the full semver (`<Version>` /
`AssemblyInformationalVersion`), prefer:

```csharp
typeof(AdaptiveObservabilityService).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"
```

Computed once into a static readonly field, applied as a `DefaultRequestHeader`.

---

## Header convention

- **Name:** `X-Observability-SDK-Version`
- **Value:** semver string, no prefix — e.g. `0.2.0` (JS), `0.2.0` (.NET). Optionally suffix the
  SDK platform later (`js/0.2.0`); for now value is the bare version, platform is inferrable from
  the existing key / `User-Agent`. **Decision needed — see open questions.**
- **Missing header:** treated as `"unknown"`. Backend logs a `Warning` and continues. Never rejects
  (scope guard: logging only).
- **Below floor:** configurable floor (e.g. `Observability:Sdk:MinVersion`, unset by default). When
  set and the supplied version parses below it, log a `Warning`. Still never rejects in this PR.

Backend reading mechanism: a small middleware (or endpoint filter) scoped to the ingest surface,
mirroring how `IngestPayloadLimitMiddleware` is scoped via `UseWhen(StartsWithSegments("/api/ingest"))`
— **but see the risk below about `/api/v1/ingest` scoping.**

---

## Deprecation policy proposal (ready to land in `docs/api-contract.md`)

> ## Version negotiation & deprecation policy
>
> The ingestion wire protocol is versioned via the URL prefix and an SDK-version header.
>
> ### Route versioning
>
> - `/api/v1/ingest/*` and `/api/v1/sessions/{id}/timeline` are the versioned ingest surface.
> - The unprefixed paths (`/api/ingest/*`, `/api/sessions/{id}/timeline`) are **aliases** of `v1`
>   and remain supported for backwards compatibility with already-deployed SDKs.
> - Dashboard (`/api/dashboard/*`, `/api/apps`) and admin (`/api/admin/*`) endpoints are **not**
>   part of the versioned SDK contract and evolve independently.
>
> ### SDK version header
>
> - SDKs send `X-Observability-SDK-Version: <semver>` on every ingest request.
> - A missing header is treated as `unknown` and logged at `Warning`. Requests are **never** rejected
>   on version grounds in the current protocol (v1). Floor-based rejection becomes meaningful only
>   with a v2 wire protocol.
>
> ### Support windows
>
> - **Unprefixed paths** are supported until SDK major version `1.0` ships, after which new SDK
>   majors target `/api/v1/` (or later) explicitly.
> - **N-1 minor** SDK versions are supported for **6 months** after the next minor lands.
> - Versioned routes are dropped only with a **major server release**, announced in advance.

---

## Risks & mitigations

1. **Ingest payload cap does not cover `/api/v1/ingest`.** `IngestPayloadLimitMiddleware` is scoped
   with `ctx.Request.Path.StartsWithSegments("/api/ingest")` ([Program.cs:53-55](../../backend/src/Observability.Api/Program.cs#L53-L55)).
   The new `/api/v1/ingest` prefix would **bypass the 64 KB body cap** unless the predicate is
   widened. **Mitigation:** change the predicate to match both prefixes (e.g. a path ending in
   `/ingest` segment, or an explicit OR). This is the single most important correctness detail of the
   mirror and must ship with the route change.
2. **CORS dev allow-list omits the new header.** Dev CORS lists allowed headers explicitly
   ([Program.cs:65](../../backend/src/Observability.Api/Program.cs#L65)):
   `Content-Type, X-Observability-Key, X-Correlation-Id`. Browsers will block the JS SDK's new
   `X-Observability-SDK-Version` preflight unless it's added. **Mitigation:** append the header to the
   dev `Access-Control-Allow-Headers` value.
3. **Header collision with the `X-Observability-*` family.** Existing members: `X-Observability-Key`.
   `X-Observability-SDK-Version` is distinct; low collision risk. No prod CORS config touched.
4. **Load balancers / proxies stripping unknown headers.** Some LBs drop non-allowlisted custom
   headers. Because the header is advisory (log-only) and the request still succeeds, a stripped
   header degrades gracefully to `unknown`. No functional impact; only observability of SDK versions
   is lost. Documented, not blocked.
5. **Rate-limit policy parity.** The v1 group must carry the same `AddApiKeyAuth()` and
   `RequireRateLimiting(IngestPolicy)` as the existing group, or v1 traffic would be unauthenticated /
   unthrottled. Covered by duplicating the group builder chain exactly.

---

## Open questions

1. **Header value format** — bare semver (`0.2.0`) or platform-tagged (`js/0.2.0`, `dotnet/0.2.0`)?
   Platform tagging is cheap now and useful for per-SDK dashboards later. Recommend platform-tagged.
2. **Mirror the timeline read endpoint under v1?** It's a dashboard read, not strictly an SDK
   ingest contract, but WMS Phase 7 may consume it. Low cost to mirror; recommend yes for surface
   consistency. Confirm.
3. **Floor config key + default** — proposed `Observability:Sdk:MinVersion`, unset (no floor) by
   default. Confirm naming to match existing `Observability:Ingest:*` convention.
4. **Should `/api/ingest` payload-cap predicate be generalized now** (match any `*/ingest` segment)
   to avoid re-introducing the same gap on a future `/api/v2/ingest`? Recommend a helper predicate.

---

## Phase 2 implementation checklist (pending approval)

- [ ] Backend: add `/api/v1/ingest` group mirroring the existing one (auth + rate limit + handlers).
- [ ] Backend: mirror timeline read under `/api/v1` (pending Q2).
- [ ] Backend: widen `IngestPayloadLimitMiddleware` scope to cover `/api/v1/ingest` (**Risk 1**).
- [ ] Backend: add `X-Observability-SDK-Version` reading filter — log `Warning` on missing/below-floor.
- [ ] Backend: add header to dev CORS allow-list (**Risk 2**).
- [ ] Backend: update `docs/api-contract.md` with the version-negotiation + deprecation section.
- [ ] JS SDK: tsup `define` for `__SDK_VERSION__`; send header from transport + sessionBracket; bump minor; CHANGELOG.
- [ ] .NET SDK: read informational version; add `DefaultRequestHeader`; bump minor; CHANGELOG.
- [ ] Tests: `/api/v1/ingest/events` succeeds and persists identical rows to the unprefixed path;
      version header captured in logs.

---

## Phase 2 outcome (implemented)

All checklist items above are done. Decisions on the open questions:

1. **Header value format** — platform-tagged (`js/<v>`, `dotnet/<v>`). Backend parser strips the
   platform prefix before the floor comparison.
2. **Timeline read** — mirrored under `/api/v1`.
3. **Floor config** — `Observability:Sdk:MinVersion`, unset by default (no floor).
4. **Payload-cap predicate** — `isIngestPath` matches both `/api/ingest` and `/api/v1/ingest`.

### Additional findings surfaced during review (and resolved)

- **Log-flood risk (HIGH):** the deployed SCH SDK (v0.1.0) sends no header, so 100% of current
  ingest traffic would log on every request. Missing-header is logged at **`Information`**, not
  `Warning`; `Warning` is reserved for the rarer below-floor case.
- **CORS preflight noise (MEDIUM):** `SdkVersionMiddleware` runs before the dev CORS `OPTIONS`
  short-circuit, so `OPTIONS` is now skipped inside the middleware (preflight carries no custom
  headers).
- **Test parity (LOW):** added a .NET test asserting the SDK sets the `dotnet/<v>` default header,
  matching the JS coverage.

### Verified clean (no gap)

- All JS ingest egress flows through `transport.ts` + `sessionBracket.ts` (the axios/fetch wrappers
  only call `captureFailedRequest`, which re-enters `Transport`).
- Rate-limit partitions by API-key hash regardless of path → v1 and unprefixed share one bucket.
