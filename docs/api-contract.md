# API Contract

Phase 1 ingestion API. Versioning, error shapes, and headers documented here so SDKs can be written against a stable surface.

## Base URL

- Local docker-compose: `http://localhost:8080`
- UAT/Prod: per Key Vault config

## Authentication

Header: `X-Observability-Key: <plaintext-key>`

Two key types:

| Type            | Prefix     | Allowed endpoints                              |
|-----------------|------------|------------------------------------------------|
| `public_client` | `aopub_`   | `/api/ingest/events`, `/api/ingest/errors`     |
| `server_api`    | `aoserv_`  | `/api/ingest/events`, `/api/ingest/errors`, all server-only endpoints |

Keys are hashed (peppered SHA-256) on the server. Plaintext is shown **once** at creation time and never recoverable.

Failure responses are deliberately generic:

- 401 `{"error":"unauthorized"}` — missing, invalid, revoked, or expired key
- 403 `{"error":"forbidden"}` — public key hitting server-only endpoint

## SDK version

Header: `X-Observability-SDK-Version: <platform>/<semver>` (e.g. `js/0.2.0`, `dotnet/0.2.0`)

- Sent by SDKs on every ingest request for wire-protocol version negotiation.
- A bare semver (`0.2.0`) is also accepted.
- Optional on inbound. A missing header is treated as `unknown` and logged at `Information` (kept
  below `Warning` so a not-yet-upgraded fleet that omits the header doesn't flood logs). CORS
  preflight (`OPTIONS`) requests are not evaluated.
- The server **never rejects** on version grounds in the current protocol (v1). When a floor is
  configured (`Observability:Sdk:MinVersion`, unset by default), requests below it are logged at
  `Warning` but still accepted. Floor-based rejection becomes meaningful only with a v2 protocol.

## Correlation ID

Header: `X-Correlation-Id: <ulid-or-uuid>`

- Optional on inbound. If absent, server generates a 128-bit ULID.
- Echoed back on the response.
- Persisted on every `Events` and `Errors` row.

## Route versioning & deprecation policy

The ingestion wire protocol is versioned via the URL prefix and the SDK-version header above.

### Route versioning

- `/api/v1/ingest/*` and `/api/v1/sessions/{id}/timeline` are the versioned ingest surface.
- The unprefixed paths (`/api/ingest/*`, `/api/sessions/{id}/timeline`) are **aliases** of `v1` —
  same handlers, same auth and rate limiting — and remain supported for backwards compatibility with
  already-deployed SDKs.
- Dashboard (`/api/dashboard/*`, `/api/apps`) and admin (`/api/admin/*`) endpoints are **not** part
  of the versioned SDK contract and evolve independently.

### Support windows

- **Unprefixed paths** are supported until SDK major version `1.0` ships, after which new SDK majors
  target `/api/v1/` (or later) explicitly.
- **N-1 minor** SDK versions are supported for **6 months** after the next minor lands.
- Versioned routes are dropped only with a **major server release**, announced in advance.

## Endpoints

> Every endpoint below is also reachable under the `/api/v1/` prefix (e.g. `POST /api/v1/ingest/events`).
> The prefixed and unprefixed forms are identical in request shape, responses, and persistence.

### `POST /api/ingest/events`

Generic event ingestion.

**Request**
```json
{
  "event": "page_viewed",
  "distinct_id": "42",
  "session_id": "01HABCXYZ...",
  "occurred_at": "2026-04-29T15:34:11.123Z",
  "properties": {
    "normalized_route": "/patients/:id/orders/:id",
    "feature_area": "orders",
    "release_sha": "a1b2c3d"
  }
}
```

**Responses**

| Code | Meaning                                                              |
|------|----------------------------------------------------------------------|
| 202  | Accepted, queued for persistence                                     |
| 400  | Schema error — malformed JSON, missing `event` or `distinct_id`      |
| 401  | Auth failure                                                         |
| 422  | Allowlist violation — forbidden field in `properties`. `SafetyViolations` row written. |
| 429  | Rate limited (Phase 8)                                               |

### `POST /api/ingest/errors`

Error ingestion with fingerprinting.

**Request**
```json
{
  "error_type": "NullReferenceException",
  "exception_type": "System.NullReferenceException",
  "distinct_id": "system:background-service",
  "session_id": null,
  "occurred_at": "2026-04-29T15:34:11.123Z",
  "properties": {
    "endpoint_group": "orders",
    "http_status_code": 500,
    "correlation_id": "01HABCXYZ...",
    "release_sha": "a1b2c3d"
  }
}
```

**Responses** — same codes as `/events`. Repeats with the same fingerprint increment `OccurrenceCount` and update `LastSeenAt`.

**Never** include `exception_message`, `stack_trace`, or message fields. The endpoint will reject the entire payload with 422.

### `POST /api/dev/smoke-test` *(Development only)*

Emits a `dev_smoke_test` event. Compiled out / hard-blocked outside Development.

### `GET /health`

Returns 200 with `{ "status": "ok", "version": "...", "sha": "..." }`. No auth.

## Schema versioning

`Events.PropertiesJson` is loose JSON, but the **catalog is the contract**. Adding a new event or property requires:

1. Update `docs/event-catalog.md`.
2. Update the SDK compile-time allowlist.
3. Update `PropertyAllowlistValidator`.
4. Add a unit test.

Dropping or renaming an event is a breaking change — coordinate with all onboarded apps.
