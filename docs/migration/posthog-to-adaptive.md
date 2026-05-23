# PostHog → Adaptive Observability migration cheatsheet

> Audience: SCH_UI / SCH_API engineers performing the Phase 6 cutover. Other apps onboarding for the first time should read [`docs/onboarding-guide.md`](../onboarding-guide.md) instead.

The new SDKs preserve the PostHog Phase 1 contract verbatim. Migration is **import + DI swap** — no event names, identity rules, or call-site *behavior* change.

> **Caveat for SCH adopters:** SCH call sites currently `using SCH.Core.Interfaces;` to pull in the existing `IAnalyticsService`. After cutover those `using` lines need to point at `Adaptive.ObservabilityClient` since the SDK ships its own copy of the interface. This is mechanical — one find-and-replace across the SCH solution — but call-site files *are* touched. The DI registration itself (in `Program.cs`) is the only logic change.

## SCH_UI

```diff
  // src/main.tsx
- import posthog from "posthog-js";
- posthog.init(import.meta.env.VITE_POSTHOG_KEY, { api_host: import.meta.env.VITE_POSTHOG_HOST, autocapture: false, capture_pageview: false });
+ import * as observability from "@adaptivesoftwarellc/observability-client-js";
+ observability.init({ ingestUrl: import.meta.env.VITE_OBSERVABILITY_URL, apiKey: import.meta.env.VITE_OBSERVABILITY_KEY });
```

```diff
  // src/services/analytics.ts (the wrapper stays — only its transport changes)
- posthog.capture(event, properties);
+ observability.track(event, properties);
- posthog.identify(String(userId));
+ observability.identify(String(userId));
- posthog.reset();
+ observability.reset();
```

```diff
  // src/services/apiClient.ts
- // bespoke axios interceptor that calls posthog.capture("api_request_failed", ...)
+ import { attachAxiosInterceptor } from "@adaptivesoftwarellc/observability-client-js/axios";
+ attachAxiosInterceptor(api);
```

```diff
  // src/components/common/ErrorBoundary.tsx
- // bespoke ErrorBoundary that calls posthog.capture("frontend_exception", ...)
+ import { ObservabilityErrorBoundary } from "@adaptivesoftwarellc/observability-client-js/react";
+ // wrap usage points with <ObservabilityErrorBoundary fallback={...}>
```

Env vars to swap:
- `VITE_POSTHOG_KEY` → `VITE_OBSERVABILITY_KEY`
- `VITE_POSTHOG_HOST` → `VITE_OBSERVABILITY_URL`

## SCH_API

```diff
  // Program.cs
- builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection("PostHog"));
- builder.Services.AddSingleton<IAnalyticsService, PostHogService>();
+ builder.Services.AddAdaptiveObservability(builder.Configuration.GetSection("AdaptiveObservability"));
```

```diff
  // .csproj
- <PackageReference Include="PostHog.AspNetCore" Version="2.5.0-pre" />
+ <PackageReference Include="Adaptive.ObservabilityClient" Version="0.1.0" />
```

`appsettings.json`: rename the `PostHog` section to `AdaptiveObservability`. Field names match (`Enabled`, `HostUrl`, `ApiKey`, `Environment`, `ReleaseSha`).

`GlobalExceptionMiddleware.cs`, `IAnalyticsService` consumers in background services, and all call sites do **not** change — they go through the interface.

## Dual-write window (Phase 6.6)

For the 5-business-day UAT parity window, register a composite implementation in SCH (kept SCH-side, not in the SDK) that fans out to both PostHog and Adaptive. After variance drops below threshold, swap the composite registration to Adaptive-only and remove the PostHog registration in the same PR. Phase 6.8 removes the package reference and FE library.

## Verification

After the swap:

1. CI: SCH unit + integration tests pass unchanged (the interface contract is preserved).
2. UAT: every Phase 1 event from `POSTHOG_EVENT_CATALOG.md` appears in the Adaptive dashboard under the SCH app + UAT environment.
3. UAT: `SafetyViolations` table is empty for the SCH app for 48h after Adaptive-only.
4. Send a deliberate `{ "email": "x@y.com" }` from a dev smoke endpoint — confirm it returns 422 and writes a `SafetyViolations` row with **no** `Events` row.

## Cutover prerequisites

See [`DEVELOPMENT_PLAN.md` Issue 6.1](../../DEVELOPMENT_PLAN.md). Deferred PostHog hardening items (BG job dedup cooldown, `release_sha` in deployed envs, dev endpoint lockdown, role-name audit, end-to-end correlation ID, `.env.example` update) must land before/alongside cutover.
