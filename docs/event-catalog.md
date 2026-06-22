# Event Catalog

Traces its shape to the original `POSTHOG_EVENT_CATALOG.md`. Event names, identity rules, and allowed property shapes are kept stable so onboarding is consistent across tenants (WMS is the first; see `docs/audits/`).

> **Source-of-truth decision (TODO):** code (compile-time safety in SDKs) is the leading candidate, with this markdown generated from the code. Until that's wired, treat this file as canonical.

## Phase 1 events (MVP)

### `auth_login_success`
User successfully authenticated.

- **Required:** `distinct_id` (resolved server-side from request context if absent)
- **Allowed:** `generic_role`, `release_sha`
- **Forbidden:** `email`, `username`, `display_name`, `user_id`-prefixed strings

```json
{
  "event": "auth_login_success",
  "distinct_id": "42",
  "properties": { "generic_role": "clinician", "release_sha": "a1b2c3d" }
}
```

### `auth_logout`
User logged out (manual or session expiry).

- **Required:** `distinct_id`
- **Allowed:** `release_sha`

### `page_viewed`
SPA route change.

- **Required:** `normalized_route`
- **Allowed:** `feature_area`, `release_sha`
- **Forbidden:** `raw_url`, `query_string`

```json
{
  "event": "page_viewed",
  "distinct_id": "42",
  "properties": {
    "normalized_route": "/patients/:id/orders/:id",
    "feature_area": "orders"
  }
}
```

### `api_request_failed`
Frontend-observed API failure (any non-2xx or network error).

- **Required:** `endpoint_group`, `method`, `http_status_code`, `is_network_error`
- **Allowed:** `correlation_id`, `release_sha`
- **Forbidden:** `raw_url`, `query_string`, `request_body`, `response_body`

### `frontend_exception`
Captured by error boundary or `window.onerror` / unhandled rejection.

- **Required:** `error_type`, `source`
- **Allowed:** `component_stack_depth`, `normalized_route`, `release_sha`
- **Forbidden:** `error.message`, `error.stack`, `component_stack` (the text)

### `server_error_occurred`
True 500 from `GlobalExceptionMiddleware`. Not 4xx.

- **Required:** `exception_type`, `endpoint_group`
- **Allowed:** `http_status_code`, `correlation_id`, `release_sha`
- **Forbidden:** `exception_message`, `stack_trace`, request/response bodies

### `background_job_failed`
BG job catch-block emission. Server-side dedup applied (15–30 min cooldown per `job_name + error_type`).

- **Required:** `job_name`, `error_type`
- **Allowed:** `release_sha`
- **Forbidden:** `exception_message`, `stack_trace`

### `dev_smoke_test` (Development only)
Development-only smoke event for exercising the ingest path. Compiled out of non-Development builds.

- **Required:** none
- **Allowed:** `release_sha`

## Identity rules

See [identity-rules.md](identity-rules.md). Summary:

- Human users → `String(userId)` (raw `sub` claim, e.g. `"42"`)
- API clients → `api_client_{id}`
- Background jobs → `system:background-service`
- Dev test → `test:dev`

## Appendix — Phase 2 deferred events

Not part of MVP. Listed for future catalog expansion:

- **WMSSite (example app-specific events):** `intake_created`, `ivr_submitted`, `eligibility_checked`, `report_generated`
- **WMSAPI (example app-specific events):** `order_state_changed`, `prior_auth_failed`, `external_api_error`
- 4xx tracking — explicitly out of scope for Phase 1.

## Format & validation

The allowlist is enforced server-side by `PropertyAllowlistValidator`. Test coverage: every Phase 1 event has a happy-path test and a forbidden-field rejection test in `Observability.IntegrationTests`.
