/**
 * Issue 4.11: fire-and-forget POST /api/ingest/sessions/{start,end}.
 *
 * The Phase 5 backend will not fabricate a Sessions row from event traffic — without these
 * calls a freshly-onboarded app's session timeline returns 404. Kept separate from the batched
 * Transport because session bracketing is one-off and lightweight.
 *
 * Note on `beforeunload`: the original 4.11 spec called for `navigator.sendBeacon`, but the
 * backend's api-key middleware only reads `X-Observability-Key` from request headers, and
 * sendBeacon cannot set custom headers. `fetch({ keepalive: true })` is the viable substitute
 * — modern browsers complete the request after navigation just like sendBeacon would.
 */

import { SDK_VERSION_HEADER, sdkVersionHeaderValue } from "./version.js";

interface BracketConfig {
  ingestUrl: string;
  apiKey: string;
  debug: boolean;
}

export interface StartPayload {
  session_id: string;
  distinct_id: string;
  release_sha?: string | null;
}

export interface EndPayload {
  session_id: string;
}

export function sendSessionStart(config: BracketConfig, payload: StartPayload): void {
  const url = joinUrl(config.ingestUrl, "/api/ingest/sessions/start");
  const body: Record<string, unknown> = {
    session_id: payload.session_id,
    distinct_id: payload.distinct_id,
  };
  if (payload.release_sha) body["release_sha"] = payload.release_sha;
  void fireFetch(url, config.apiKey, JSON.stringify(body), config.debug);
}

export function sendSessionEnd(config: BracketConfig, payload: EndPayload): void {
  const url = joinUrl(config.ingestUrl, "/api/ingest/sessions/end");
  const body = JSON.stringify({ session_id: payload.session_id });
  void fireFetch(url, config.apiKey, body, config.debug);
}

async function fireFetch(url: string, apiKey: string, body: string, debug: boolean): Promise<void> {
  try {
    await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Observability-Key": apiKey,
        [SDK_VERSION_HEADER]: sdkVersionHeaderValue(),
      },
      body,
      credentials: "omit",
      keepalive: true,
    });
  } catch (err) {
    if (debug) console.warn("[adaptive-observability] session-bracket send failed", err);
  }
}

function joinUrl(base: string, path: string): string {
  const b = base.endsWith("/") ? base.slice(0, -1) : base;
  const p = path.startsWith("/") ? path : `/${path}`;
  return b + p;
}
