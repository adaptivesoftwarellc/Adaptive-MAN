/**
 * Adaptive Observability frontend SDK.
 * Public surface follows the PostHog Phase 1 contract, so migrating off PostHog is
 * import-line + DI swap only and greenfield instrumentation stays small.
 */

import type { EventName, PropsFor } from "./events.js";
import { Transport, type Envelope, type TransportConfig } from "./transport.js";
import { getOrCreateSessionId, resetSessionId } from "./session.js";
import {
  defaultReplayConfig,
  noopReplayAdapter,
  type ReplayAdapter,
  type ReplayConfig,
} from "./replay.js";
import { normalizeRoute, getFeatureArea, endpointGroupForUrl } from "./route.js";
import { sendSessionStart, sendSessionEnd } from "./sessionBracket.js";

export type { EventName, PropsFor, EventMap } from "./events.js";
export { normalizeRoute, getFeatureArea, endpointGroupForUrl } from "./route.js";
export type { ReplayAdapter, ReplayConfig, ReplayContext } from "./replay.js";
export { noopReplayAdapter, defaultReplayConfig } from "./replay.js";

export interface InitOptions {
  ingestUrl: string;
  apiKey: string;
  environment?: string;
  releaseSha?: string;
  enabled?: boolean;
  batchSize?: number;
  flushIntervalMs?: number;
  maxRetries?: number;
  debug?: boolean;
  trackSessions?: boolean;
  replay?: Partial<ReplayConfig>;
  replayAdapter?: ReplayAdapter;
}

interface InternalState {
  options: Required<Omit<InitOptions, "replay" | "replayAdapter" | "environment" | "releaseSha">> & {
    environment: string | null;
    releaseSha: string | null;
  };
  transport: Transport;
  distinctId: string | null;
  sessionId: string;
  sessionStarted: boolean;
  replayConfig: ReplayConfig;
  replayAdapter: ReplayAdapter;
}

let state: InternalState | null = null;

export function init(options: InitOptions): void {
  if (state) {
    if (options.debug) console.warn("[adaptive-observability] init called twice; ignoring");
    return;
  }
  if (options.enabled === false) {
    return; // intentionally no-op; preserves call-site behavior
  }

  const transportConfig: TransportConfig = {
    ingestUrl: options.ingestUrl,
    apiKey: options.apiKey,
    batchSize: options.batchSize ?? 20,
    flushIntervalMs: options.flushIntervalMs ?? 5000,
    maxRetries: options.maxRetries ?? 3,
    debug: options.debug ?? false,
  };

  const replayConfig: ReplayConfig = { ...defaultReplayConfig, ...(options.replay ?? {}) };
  const replayAdapter = options.replayAdapter ?? noopReplayAdapter;
  const sessionId = getOrCreateSessionId();

  state = {
    options: {
      ingestUrl: options.ingestUrl,
      apiKey: options.apiKey,
      environment: options.environment ?? null,
      releaseSha: options.releaseSha ?? null,
      enabled: true,
      batchSize: transportConfig.batchSize,
      flushIntervalMs: transportConfig.flushIntervalMs,
      maxRetries: transportConfig.maxRetries,
      debug: transportConfig.debug,
      trackSessions: options.trackSessions ?? true,
    },
    transport: new Transport(transportConfig),
    distinctId: null,
    sessionId,
    sessionStarted: false,
    replayConfig,
    replayAdapter,
  };

  replayAdapter.start({ sessionId, config: replayConfig, debug: transportConfig.debug });

  if (typeof window !== "undefined") {
    window.addEventListener("beforeunload", () => {
      void state?.transport.flush();
      maybeSendSessionEnd();
    });
  }
}

export function identify(distinctId: string): void {
  if (!state) return;
  if (typeof distinctId !== "string" || !distinctId) {
    if (state.options.debug) console.warn("[adaptive-observability] identify ignored (non-string or empty)");
    return;
  }
  state.distinctId = distinctId;
}

export function reset(): void {
  if (!state) return;
  state.distinctId = null;
  state.sessionId = resetSessionId();
  state.sessionStarted = false;
}

export function getSessionId(): string | null {
  return state?.sessionId ?? null;
}

export function track<E extends EventName>(event: E, properties?: PropsFor<E>): void {
  if (!state) return;
  const distinctId = state.distinctId ?? "anonymous";
  maybeSendSessionStart(distinctId);
  const props = withReleaseSha(properties as Record<string, unknown> | undefined);
  enqueue({
    kind: "event",
    event,
    distinct_id: distinctId,
    session_id: state.sessionId,
    occurred_at: new Date().toISOString(),
    properties: props,
  });
}

export function capturePageView(path?: string, featureArea?: string): void {
  if (!state) return;
  const raw = path ?? (typeof location !== "undefined" ? location.pathname : "/");
  const normalized = normalizeRoute(raw);
  const area = featureArea ?? getFeatureArea(normalized);
  track("page_viewed", {
    normalized_route: normalized,
    feature_area: area,
  });
}

export interface CaptureExceptionInput {
  errorType: string;
  source: string;
  componentStackDepth?: number;
  normalizedRoute?: string;
}

export function captureException(input: CaptureExceptionInput): void {
  if (!state) return;
  if (state.replayConfig.enabled && state.replayConfig.captureOnError) {
    void state.replayAdapter.flush();
  }
  const distinctId = state.distinctId ?? "anonymous";
  maybeSendSessionStart(distinctId);
  const properties = withReleaseSha({
    error_type: input.errorType,
    source: input.source,
    ...(input.componentStackDepth !== undefined ? { component_stack_depth: input.componentStackDepth } : {}),
    ...(input.normalizedRoute ? { normalized_route: input.normalizedRoute } : {}),
  });
  enqueue({
    kind: "error",
    error_type: input.errorType,
    distinct_id: distinctId,
    session_id: state.sessionId,
    occurred_at: new Date().toISOString(),
    properties,
  });
}

export interface CaptureFailedRequestInput {
  url: string;
  method: string;
  httpStatusCode: number;
  isNetworkError: boolean;
  correlationId?: string;
  endpointGroup?: string;
}

export function captureFailedRequest(input: CaptureFailedRequestInput): void {
  if (!state) return;
  if (state.replayConfig.enabled && state.replayConfig.captureOnError) {
    void state.replayAdapter.flush();
  }
  const group = input.endpointGroup ?? endpointGroupForUrl(input.url);
  track("api_request_failed", {
    endpoint_group: group,
    method: input.method.toUpperCase(),
    http_status_code: input.httpStatusCode,
    is_network_error: input.isNetworkError,
    ...(input.correlationId ? { correlation_id: input.correlationId } : {}),
  });
}

export async function flush(): Promise<void> {
  if (!state) return;
  await state.transport.flush();
}

export async function shutdown(): Promise<void> {
  if (!state) return;
  state.replayAdapter.stop();
  maybeSendSessionEnd();
  await state.transport.shutdown();
  state = null;
}

function maybeSendSessionStart(distinctId: string): void {
  if (!state || !state.options.trackSessions || state.sessionStarted) return;
  state.sessionStarted = true;
  sendSessionStart(
    { ingestUrl: state.options.ingestUrl, apiKey: state.options.apiKey, debug: state.options.debug },
    { session_id: state.sessionId, distinct_id: distinctId, release_sha: state.options.releaseSha },
  );
}

function maybeSendSessionEnd(): void {
  if (!state || !state.options.trackSessions || !state.sessionStarted) return;
  state.sessionStarted = false;
  sendSessionEnd(
    { ingestUrl: state.options.ingestUrl, apiKey: state.options.apiKey, debug: state.options.debug },
    { session_id: state.sessionId },
  );
}

function enqueue(env: Envelope): void {
  if (!state) return;
  state.transport.enqueue(env);
}

function withReleaseSha(props: Record<string, unknown> | undefined): Record<string, unknown> {
  const merged: Record<string, unknown> = { ...(props ?? {}) };
  if (state?.options.releaseSha && merged["release_sha"] === undefined) {
    merged["release_sha"] = state.options.releaseSha;
  }
  return merged;
}

/** Test-only — exposes internals for unit tests. Not part of the public API. */
export const __test = {
  isInitialized: () => state !== null,
  getState: () => state,
  reset: () => {
    state = null;
  },
};
