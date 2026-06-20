# Identity Rules

Trace to the validated PostHog Phase 1 implementation. **Do not change without a privacy review** — these rules are how we keep PHI/PII out of `distinct_id`.

## `distinct_id` formats

| Origin           | Format                        | Example                     |
|------------------|-------------------------------|-----------------------------|
| Human user       | `String(userId)` — raw `sub`  | `"42"`                      |
| API client       | `api_client_{id}`             | `"api_client_17"`           |
| Background job   | `system:background-service`   | `"system:background-service"` |
| Dev test         | `test:dev`                    | `"test:dev"`                |

## Do not use

These are forbidden as `distinct_id` values:

- `user_{id}` — the `user_` prefix is **not** the convention; raw numeric ID only
- Email addresses
- Usernames
- Display names
- Any combination of the above

## Why raw numeric IDs

PostHog Phase 1 settled on raw `String(userId)` because:

- They are stable and opaque to outside readers.
- They contain no PHI.
- They round-trip cleanly with audit logs that already key on the same column.
- A `user_` prefix added cosmetic noise and broke joins.

## Implementation notes

- **Frontend** — `identify()` accepts a `string` only; callers are responsible for passing a safe value. No autocapture of email/username from auth state.
- **Backend** — `AnalyticsIdentity` resolves `distinct_id` from `HttpContext.User` claims. API clients short-circuit to `api_client_{id}`. Background services pass `system:background-service` explicitly.
- **Validation** — server-side rejection rule: if `distinct_id` matches `email`-shape regex, reject with `SafetyViolations` row.
