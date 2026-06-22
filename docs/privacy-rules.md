# Privacy Rules

This platform ingests telemetry from PHI/PII-bearing applications. The rules here are **enforced server-side at ingestion**. Unsafe fields are *rejected and logged* in `SafetyViolations` — never silently dropped.

These lists trace to the validated PostHog Phase 1 work and have already passed prior internal review.

> **Compliance sign-off TODO:** confirm whether these lists have explicit compliance/legal sign-off. If yes, reference the ticket here. If no, gate WMS go-live (and the first Prod deploy) on it.

## Never store

The following must never be stored in any column or property bag:

- Patient names (first, last, full, preferred)
- Email addresses
- Usernames
- Display names
- Dates of birth
- Social Security Numbers
- Policy / insurance / member IDs
- Clinical notes, free-text patient remarks
- Raw URLs (use `normalized_route` only)
- Query strings (any portion)
- Request bodies
- Response bodies
- Exception messages (`error.message`)
- Stack traces (`error.stack`, `componentStack` text)
- JWTs and any bearer tokens
- React component stack text in any form

## Allowed fields

Any property outside these is dropped (unknown) or rejected (forbidden). New allowed fields require a docs update + reviewer approval.

| Field               | Type        | Notes                                                              |
|---------------------|-------------|--------------------------------------------------------------------|
| `app_id`            | string      | Resolved from API key — never sent by client                       |
| `environment`       | enum        | `Development` / `UAT` / `Production`                               |
| `release_version`   | string      | SemVer                                                             |
| `release_sha`       | string      | Short git sha                                                      |
| `normalized_route`  | string      | E.g. `/patients/:id/orders/:id`                                    |
| `endpoint_group`    | string      | E.g. `orders`, `patients`                                          |
| `feature_area`      | string      | E.g. `auth`, `orders`, `reports`                                   |
| `http_status_code`  | int         | 100-599                                                            |
| `correlation_id`    | string      | ULID/UUID                                                          |
| `exception_type`    | string      | Class name only (e.g. `NullReferenceException`)                    |
| `error_type`        | string      | FE-side error category                                             |
| `job_name`          | string      | Background job identifier                                          |
| `generic_role`      | string      | Generic role label only — never user-specific                      |
| `is_network_error`  | boolean     |                                                                    |
| `component_stack_depth` | int     | Numeric depth only, never the stack text                           |
| `source`            | string      | Bounded enum (e.g. `error_boundary`, `window_error`)               |
| Safe counters       | int/long    | Counts, durations in ms, sizes in bytes                            |

## Reject and log

If an incoming event property uses a known-forbidden key (`email`, `username`, `raw_url`, `exception_message`, `stack_trace`, `component_stack`, `request_body`, `response_body`, `query_string`, `dob`, `ssn`, `jwt`, `token`, `password`):

1. Reject the entire event with **HTTP 422**.
2. Write a row to `SafetyViolations` containing **only** `ApplicationId`, `EnvironmentId`, `EventName`, `RejectedField`, `Reason`, `CreatedAt`.
3. **Never** persist the offending value or a redacted form of it.

If an incoming property is simply *unknown* (not in allowlist, not in forbidden list), silently drop the key but accept the event. Log a metric for unknown-key counts so allowlists can be expanded if needed.

## Replay / session recording

Disabled by default. UAT-only until a separate privacy review and masking audit. Prod replay requires explicit gate.

## Identity safety

See [identity-rules.md](identity-rules.md). `distinct_id` must never be an email, username, or display name.
