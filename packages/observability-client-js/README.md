# @adaptivesoftwarellc/observability-client-js

Frontend SDK for the Adaptive Observability platform.

The public surface follows the PostHog Phase 1 contract (`POSTHOG_EVENT_CATALOG.md`), so an app migrating off PostHog can do so import-line + DI-swap only, and a greenfield tenant (e.g. WMSSite) gets a small, stable API to instrument against.

## Install

```bash
npm install @adaptivesoftwarellc/observability-client-js
```

## Quickstart (under 50 LOC)

```ts
// src/main.tsx (or wherever your app boots)
import * as observability from "@adaptivesoftwarellc/observability-client-js";

observability.init({
  ingestUrl: import.meta.env.VITE_OBSERVABILITY_URL!,
  apiKey: import.meta.env.VITE_OBSERVABILITY_KEY!,
  environment: import.meta.env.MODE,
  releaseSha: import.meta.env.VITE_RELEASE_SHA,
});

// On login:
observability.identify(String(userId)); // string only — caller is responsible for safety

// Page views (call from your router):
observability.capturePageView(location.pathname);

// Auth events:
observability.track("auth_login_success", { generic_role: "clinician" });
observability.track("auth_logout");
```

See [`docs/privacy-rules.md`](../../docs/privacy-rules.md) for what you may NOT send.

## Optional: Axios interceptor

```ts
import axios from "axios";
import { attachAxiosInterceptor } from "@adaptivesoftwarellc/observability-client-js/axios";

const api = axios.create({ baseURL: "/api" });
attachAxiosInterceptor(api);
```

Captures `endpoint_group`, `method`, `http_status_code`, `is_network_error`, and `correlation_id` (read from `x-correlation-id` response header) on every failure.

## Optional: React error boundary

```tsx
import { ObservabilityErrorBoundary } from "@adaptivesoftwarellc/observability-client-js/react";

<ObservabilityErrorBoundary fallback={<p>Something went wrong.</p>}>
  <App />
</ObservabilityErrorBoundary>
```

NEVER sends `error.message`, `error.stack`, or React `componentStack` text. Only `error_type`, `source`, `component_stack_depth`.

## Replay slot (Phase 9)

Phase 4 ships only the no-op adapter and the type contract — no `rrweb` dependency yet. Phase 9 will drop in an rrweb-backed adapter at `@adaptivesoftwarellc/observability-client-js/replay` without breaking SemVer.

## API surface

| Function | Notes |
|---|---|
| `init(options)` | Idempotent; calling with `enabled: false` is a no-op. Set `trackSessions: false` to opt out of automatic `/sessions/start` + `/sessions/end` calls. |
| `identify(distinctId)` | String only. No `user_` prefix per platform identity rules. |
| `track(event, props)` | Compile-time event allowlist (TS unions) per `events.ts`. |
| `capturePageView(path?, featureArea?)` | Auto-normalizes route. |
| `captureException({ errorType, source, componentStackDepth, normalizedRoute })` | Never accepts message/stack text. |
| `captureFailedRequest({ url, method, httpStatusCode, isNetworkError, correlationId })` | |
| `flush()` | Force-send pending batch. |
| `shutdown()` | Drains transport, stops replay adapter, and sends `/sessions/end`. |
| `getSessionId()` | The shared id used by replay (Phase 9) and session timeline (Phase 5). |
| `reset()` | New session id, clear distinct id (call on logout). |

## Live ingest smoke check (4.11 closure harness)

[`scripts/live-ingest-check.mjs`](scripts/live-ingest-check.mjs) is a standalone Node harness that boots the SDK against a real ingestion API, emits one event, calls `shutdown()`, and asserts that the `Sessions` row exists with `started_at` / `last_seen_at` / `ended_at` populated. Use it to close the 4.11 deferred integration test against `obs-api-dev` (or any environment) without standing up a browser.

```bash
OBS_INGEST_URL=https://obs-api-dev.azurewebsites.net \
OBS_API_KEY=aopub_xxx \
node scripts/live-ingest-check.mjs
```

Exit code 0 = pass. The SDK must be built first (`npm run build`).

## Failure modes

The SDK is fire-and-forget: `track`, `captureException`, and `captureFailedRequest` enqueue and return immediately, and the transport **never throws into your app** (`send` catches every error). All behavior below is in [`src/transport.ts`](src/transport.ts).

- **Network unreachable / 5xx.** The failed item's `attempts` counter increments and it is re-queued after a backoff. Backoff is exponential with jitter: `min(30_000, 250 * 2^(attempt-1))` ms plus up to 30% random jitter — so ~250ms, ~500ms, ~1000ms for the default 3 attempts, capped at 30s.
- **Dropped after retries.** Once an item exceeds `maxRetries` (**default 3**) it is **dropped silently**. There is **no `localStorage` / IndexedDB queue** — the buffer is in-memory only, so a hard refresh or tab close while items are retrying loses them. With `debug: true` a `dropped after retries` warning is logged.
- **4xx responses are terminal.** A `4xx` means the payload was rejected (e.g. an allowlist `SafetyViolation` server-side); the item is **not** retried and is discarded. With `debug: true` a `rejected` warning logs the status. `200`/`202` count as success.
- **Batch buffer.** Events buffer in an in-memory queue and flush when the queue reaches `batchSize` (**default 20**) or after `flushIntervalMs` (**default 5000ms**), whichever comes first. The queue is **not bounded** — under sustained backpressure (server down + high event volume) it can grow until the page is unloaded; it is not capped or spilled to disk. `flush()` is also called on `beforeunload` (best-effort, via `keepalive: true` fetch).
- **`shutdown()`** drains the transport (final `flush()`), stops the replay adapter, and sends `/sessions/end`. After `shutdown()` the SDK is uninitialized; call `init()` again to resume.

> *Future enhancement:* there is no `TransportStatus` callback today (e.g. to surface "N events dropped" to the host app). If added it would be an additive, non-breaking option on `init()`.

## Troubleshooting: events don't appear

Work down this checklist:

1. **Is the SDK initialized?** `init()` is a no-op if called with `enabled: false`, and ignores a second call. Confirm exactly one `init()` ran with `enabled` unset/true.
2. **Turn on `debug: true`.** This surfaces `rejected` (4xx) and `dropped after retries` warnings in the console — the two silent-loss paths above.
3. **4xx in the network tab?** The payload violated the server allowlist or auth. A `401` means a bad/revoked `apiKey`; a `400`/`422` means a forbidden property — check [`docs/privacy-rules.md`](../../docs/privacy-rules.md) and look for a `SafetyViolation` row server-side.
4. **Nothing leaves the browser at all?** Events flush on `batchSize` (20) or `flushIntervalMs` (5000ms). For a low-traffic page, wait 5s or call `flush()` explicitly. Verify `ingestUrl` resolves and isn't blocked by CORS/CSP.
5. **Lost on navigation?** No persistent queue exists — items still retrying when the tab closes are gone. This is expected; see Failure modes.
6. **Distinct id.** Events before `identify()` are attributed to `"anonymous"`; that's intended, not a drop.

## PostHog migration cheatsheet

| PostHog | Adaptive (this SDK) |
|---|---|
| `posthog.init(key, { api_host })` | `observability.init({ ingestUrl, apiKey })` |
| `posthog.identify(String(userId))` | `observability.identify(String(userId))` |
| `posthog.capture("event", props)` | `observability.track("event", props)` |
| `posthog.reset()` | `observability.reset()` |
| Manual page view | `observability.capturePageView()` |

Event names, identity rules, and allowed property shapes are unchanged from `POSTHOG_EVENT_CATALOG.md`.
