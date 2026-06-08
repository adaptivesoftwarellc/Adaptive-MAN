import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Transport, type TransportConfig } from "../transport.js";
import { sendSessionStart } from "../sessionBracket.js";
import { SDK_VERSION_HEADER, sdkVersionHeaderValue } from "../version.js";

const baseConfig: TransportConfig = {
  ingestUrl: "http://localhost:5000",
  apiKey: "test-key",
  batchSize: 1,
  flushIntervalMs: 5000,
  maxRetries: 0,
  debug: false,
};

function lastHeaders(): Record<string, string> {
  const calls = (fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls;
  const init = calls[calls.length - 1]![1] as RequestInit;
  return init.headers as Record<string, string>;
}

describe("SDK version header (Issue 10.4)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 202 })));
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("uses a platform-tagged value", () => {
    expect(sdkVersionHeaderValue()).toMatch(/^js\//);
  });

  it("is sent on batched ingest requests", async () => {
    const t = new Transport(baseConfig);
    t.enqueue({ kind: "event", event: "auth_logout", distinct_id: "u1", occurred_at: "2026-04-30T00:00:00Z", properties: {} });
    await vi.runAllTimersAsync();
    expect(lastHeaders()[SDK_VERSION_HEADER]).toBe(sdkVersionHeaderValue());
  });

  it("is sent on session-bracket requests", () => {
    sendSessionStart({ ingestUrl: "http://x", apiKey: "k", debug: false }, { session_id: "s1", distinct_id: "u1" });
    expect(lastHeaders()[SDK_VERSION_HEADER]).toBe(sdkVersionHeaderValue());
  });
});
