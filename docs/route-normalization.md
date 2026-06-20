# Route Normalization

Spec for SDKs (FE + BE). The threshold tuning was originally validated against real PostHog Phase 1 traffic and is shipped in the SDKs' `route.ts` / `RouteNormalizer.cs`.

> **Stable by design.** Do not retune without rerunning the route fixture regression corpus (see Test fixtures below).

## Frontend — `normalized_route`

Used for `page_viewed`. Produces a route template that's safe for storage and aggregation.

### Rules

1. **Drop the query string entirely** before any other processing.
2. Split on `/`. For each segment, replace with `:id` if any of:
   - All-numeric: `^\d+$`
   - UUID: `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` (case-insensitive)
   - ULID: `^[0-9A-HJKMNP-TV-Z]{26}$`
   - Token-like: length ≥ `TOKEN_LENGTH_THRESHOLD` (currently **32**) AND matches `^[A-Za-z0-9_\-]+$`
3. Re-join with `/`.

### Examples

| Input                                              | Output                              |
|----------------------------------------------------|-------------------------------------|
| `/patients/123/orders/456`                         | `/patients/:id/orders/:id`          |
| `/patients/123/orders/456?status=open`             | `/patients/:id/orders/:id`          |
| `/users/01HABCXYZ0123456789ABCDEF12/profile`       | `/users/:id/profile`                |
| `/items/9f8e7d6c-1234-4abc-8def-0123456789ab`      | `/items/:id`                        |

### Edge case — token threshold tuning

The threshold of **32** characters was tuned to avoid false positives. In particular:

- ✅ `posthog-500-test` → `posthog-500-test` (kept literal, length 16)
- ❌ Older threshold of 8 incorrectly produced `posthog-:id-test` here.

Tests in `packages/observability-client-js` must include `posthog-500-test` as a regression case.

## Frontend — `feature_area`

Mapped from the first non-empty segment of the normalized route to a bounded set:

| First segment                | `feature_area` |
|------------------------------|----------------|
| `patients`                   | `patients`     |
| `orders`                     | `orders`       |
| `reports`                    | `reports`      |
| `documents`                  | `documents`    |
| `auth`, `login`, `logout`    | `auth`         |
| `admin`                      | `admin`        |
| (anything else)              | `other`        |

The map is intentionally allowlist-style — unknown segments fall through to `other` rather than leaking arbitrary path text.

## Backend — `endpoint_group`

Used for `api_request_failed`, `server_error_occurred`. Coarser than the FE `normalized_route` — a single token per request.

### Rules

1. Prefer ASP.NET Core's `RouteData` (`controller` token) when available.
2. Fall back to: take the path, drop the leading `/api/` prefix, take the first segment, lowercase.
3. Apply the same numeric/UUID/ULID/token replacements as the FE rules above (rare, but possible if controller routing fails).

### Examples

| Path                          | `endpoint_group` |
|-------------------------------|------------------|
| `/api/orders/123`             | `orders`         |
| `/api/patients/42/notes`      | `patients`       |
| `/api/v1/reports`             | `reports`        |
| `/health`                     | `health`         |

## Test fixtures

Both SDKs must include a route fixture set as a regression test corpus. The JS SDK ships the WMS route fixtures (`packages/observability-client-js/src/__tests__/wms-fixtures/`, exercised by `wmsParity.test.ts`). Add new fixtures whenever a new app onboards.
