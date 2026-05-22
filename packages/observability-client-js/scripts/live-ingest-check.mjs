#!/usr/bin/env node
// Live end-to-end smoke check for the JS SDK against a real ingestion API.
// Closes the 4.11 deferred integration test (DEVELOPMENT_PLAN.md §5.7):
//   "emit one event through the JS SDK, confirm a Sessions row appears
//    with started_at and last_seen_at populated, and that calling shutdown()
//    stamps ended_at."
//
// Usage:
//   OBS_INGEST_URL=https://obs-api-dev.azurewebsites.net \
//   OBS_API_KEY=aopub_xxx \
//   OBS_APP_SLUG=test-app \
//   OBS_ENV=Development \
//   node scripts/live-ingest-check.mjs
//
// Exit code 0 = pass; non-zero = fail with a message describing which
// assertion failed.

import { init, identify, track, shutdown, getSessionId } from "../dist/index.js";

const ingestUrl = process.env.OBS_INGEST_URL;
const apiKey    = process.env.OBS_API_KEY;
const dashUrl   = process.env.OBS_DASH_URL ?? ingestUrl;

if (!ingestUrl || !apiKey) {
  console.error("missing OBS_INGEST_URL or OBS_API_KEY");
  process.exit(2);
}

function fail(msg) {
  console.error(`FAIL: ${msg}`);
  process.exit(1);
}

async function main() {
  init({
    ingestUrl,
    apiKey,
    environment: process.env.OBS_ENV ?? "Development",
    releaseSha: "live-check",
    batchSize: 1,
    flushIntervalMs: 100,
  });

  identify("test:live-check");
  track("dev_smoke_test", {});
  const sid = getSessionId();
  if (!sid) fail("getSessionId() returned null after track()");

  // Wait for the SDK to drain its batch + the auto-bracket /sessions/start.
  await new Promise(r => setTimeout(r, 600));
  await shutdown();

  // Give the dashboard read-side a moment after /sessions/end.
  await new Promise(r => setTimeout(r, 400));

  const res = await fetch(`${dashUrl}/api/sessions/${encodeURIComponent(sid)}/timeline`);
  if (res.status === 404) fail(`Sessions row never appeared (${sid})`);
  if (!res.ok) fail(`timeline GET returned ${res.status}`);
  const body = await res.json();
  const s = body.session;
  if (!s) fail("response missing 'session'");
  if (!s.started_at) fail("session.started_at not stamped");
  if (!s.last_seen_at) fail("session.last_seen_at not stamped");
  if (!s.ended_at) fail("session.ended_at not stamped after shutdown()");

  console.log(`PASS  session=${sid} started=${s.started_at} ended=${s.ended_at}`);
}

main().catch(err => {
  console.error("unexpected error:", err);
  process.exit(3);
});
