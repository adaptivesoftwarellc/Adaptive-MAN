/**
 * Batched send + retry. Buffer in memory; flush on size/interval; exponential backoff
 * with jitter. Errors swallowed silently per Phase 4.5.
 */

import { SDK_VERSION_HEADER, sdkVersionHeaderValue } from "./version.js";

export type Endpoint = "events" | "errors";

export type Envelope =
  | { kind: "event"; event: string; distinct_id: string; session_id?: string; occurred_at: string; properties: Record<string, unknown> }
  | { kind: "error"; error_type: string; exception_type?: string; distinct_id: string; session_id?: string; occurred_at: string; properties: Record<string, unknown> };

export interface TransportConfig {
  ingestUrl: string;
  apiKey: string;
  batchSize: number;
  flushIntervalMs: number;
  maxRetries: number;
  debug: boolean;
}

interface QueuedItem {
  envelope: Envelope;
  attempts: number;
}

export class Transport {
  private queue: QueuedItem[] = [];
  private timer: ReturnType<typeof setTimeout> | null = null;
  private flushing = false;

  constructor(private readonly config: TransportConfig) {}

  enqueue(envelope: Envelope): void {
    this.queue.push({ envelope, attempts: 0 });
    if (this.queue.length >= this.config.batchSize) {
      void this.flush();
    } else {
      this.scheduleFlush();
    }
  }

  private scheduleFlush(): void {
    if (this.timer || this.flushing) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.flush();
    }, this.config.flushIntervalMs);
  }

  async flush(): Promise<void> {
    if (this.flushing || this.queue.length === 0) return;
    this.flushing = true;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    const batch = this.queue.splice(0);
    try {
      for (const item of batch) {
        const ok = await this.send(item);
        if (!ok) {
          item.attempts += 1;
          if (item.attempts <= this.config.maxRetries) {
            await this.backoff(item.attempts);
            this.queue.push(item);
          } else if (this.config.debug) {
            console.warn("[adaptive-observability] dropped after retries", item.envelope);
          }
        }
      }
    } catch (err) {
      if (this.config.debug) console.warn("[adaptive-observability] flush error", err);
    } finally {
      this.flushing = false;
      if (this.queue.length > 0) this.scheduleFlush();
    }
  }

  private async send(item: QueuedItem): Promise<boolean> {
    const path = item.envelope.kind === "event" ? "events" : "errors";
    const url = joinUrl(this.config.ingestUrl, `/api/ingest/${path}`);
    const body = serialize(item.envelope);
    try {
      const res = await fetch(url, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Observability-Key": this.config.apiKey,
          [SDK_VERSION_HEADER]: sdkVersionHeaderValue(),
        },
        body,
        credentials: "omit",
        keepalive: true,
      });
      if (res.status === 202 || res.status === 200) return true;
      // 4xx: don't retry — payload is bad. 5xx/network: retry.
      if (res.status >= 400 && res.status < 500) {
        if (this.config.debug) console.warn("[adaptive-observability] rejected", res.status, item.envelope);
        return true;
      }
      return false;
    } catch {
      return false;
    }
  }

  private async backoff(attempt: number): Promise<void> {
    const base = Math.min(30_000, 250 * 2 ** (attempt - 1));
    const jitter = Math.random() * base * 0.3;
    await new Promise((r) => setTimeout(r, base + jitter));
  }

  async shutdown(): Promise<void> {
    await this.flush();
  }
}

function joinUrl(base: string, path: string): string {
  const b = base.endsWith("/") ? base.slice(0, -1) : base;
  const p = path.startsWith("/") ? path : `/${path}`;
  return b + p;
}

function serialize(env: Envelope): string {
  // Backend uses snake_case (Program.cs configures JsonNamingPolicy.SnakeCaseLower).
  if (env.kind === "event") {
    return JSON.stringify({
      event: env.event,
      distinct_id: env.distinct_id,
      session_id: env.session_id,
      occurred_at: env.occurred_at,
      properties: env.properties,
    });
  }
  return JSON.stringify({
    error_type: env.error_type,
    exception_type: env.exception_type,
    distinct_id: env.distinct_id,
    session_id: env.session_id,
    occurred_at: env.occurred_at,
    properties: env.properties,
  });
}
