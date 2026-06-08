# Changelog

## 0.2.0

- Add `X-Observability-SDK-Version` header (value `js/<version>`) on every ingest and session-bracket
  request, for backend wire-protocol version negotiation (Issue 10.4).
- Backwards-compatible: request URL paths are **unchanged** — the SDK continues to hit the unprefixed
  `/api/ingest/*` routes, which the backend now also serves under `/api/v1/`. Switching the SDK to the
  `/v1/` paths is deferred to a future major release.

## 0.1.0

- Initial frontend SDK. API surface mirrors SCH's `analytics.ts`.
