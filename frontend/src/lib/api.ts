// Frontend API client. As of Issue 8.6 (RBAC) live requests carry a bearer token and a 401 drops the
// session. Mock/demo mode still runs entirely client-side with no backend or auth.

import * as mock from './mock';
import { getToken, notifyUnauthorized, type AuthUser } from './auth';

const ENV = (import.meta as unknown as {
  env?: { DEV?: boolean; VITE_OBSERVABILITY_API_URL?: string; VITE_USE_MOCKS?: string };
}).env ?? {};

const RAW_BASE = ENV.VITE_OBSERVABILITY_API_URL ?? 'http://localhost:8080';
export const API_BASE = RAW_BASE.replace(/\/$/, '');

// ---------------------------------------------------------------------------
// Demo / mock mode
//
// Lets the dashboard render realistic sample reports when the backend or DB is empty.
// In a production build, mocks are controlled ONLY by the build-time VITE_USE_MOCKS flag
// (off unless explicitly built for a demo); the runtime localStorage toggle is ignored so a
// stray key on a shared origin can never make a deployed dashboard serve fake data. In dev,
// resolution order is: localStorage toggle > VITE_USE_MOCKS > default ON.
// ---------------------------------------------------------------------------

const MOCK_STORAGE_KEY = 'observability:mocks';

function resolveMockMode(): boolean {
  // Production: build-time flag only. localStorage must never enable mocks in a deployed build.
  if (ENV.DEV !== true) {
    return ENV.VITE_USE_MOCKS === 'true';
  }
  try {
    const stored = localStorage.getItem(MOCK_STORAGE_KEY);
    if (stored === 'on') return true;
    if (stored === 'off') return false;
  } catch {
    /* ignore */
  }
  if (ENV.VITE_USE_MOCKS === 'true') return true;
  if (ENV.VITE_USE_MOCKS === 'false') return false;
  return true;
}

export const USE_MOCKS = resolveMockMode();

/** Flip demo mode and reload so every query re-runs against the chosen source. */
export function setMockMode(on: boolean): void {
  try {
    localStorage.setItem(MOCK_STORAGE_KEY, on ? 'on' : 'off');
  } catch {
    /* ignore */
  }
  location.reload();
}

/** Small artificial latency so loading skeletons are visible in demo mode. */
function delay<T>(value: T, ms = 220): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), ms));
}

export interface AppEnvironmentDto {
  id: string;
  name: string;
}

export interface AppDto {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  environments: AppEnvironmentDto[];
}

// --- Issue 10.6 admin DTOs ---------------------------------------------------

export interface AdminAppEnvironmentDto {
  id: string;
  name: string;
  is_active: boolean;
  total_key_count: number;
  active_key_count: number;
}

export interface AdminAppDto {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  is_active: boolean;
  environments: AdminAppEnvironmentDto[];
}

export type ApiKeyTypeName = 'PublicClient' | 'ServerApi';

export interface ApiKeyDto {
  id: string;
  key_type: ApiKeyTypeName;
  created_at: string;
  last_used_at: string | null;
  expires_at: string | null;
  revoked_at: string | null;
  is_active: boolean;
}

export interface MintKeyResponse {
  id: string;
  key_type: ApiKeyTypeName;
  plaintext_key: string;
  note: string;
}

export interface AuditRowDto {
  id: string;
  occurred_at: string;
  action: string;
  actor_type: string;
  application_id: string | null;
  environment_id: string | null;
  correlation_id: string | null;
  details_json: string;
}

export interface HealthCardsDto {
  backend_500s: number;
  frontend_exceptions: number;
  api_request_failures: number;
  background_job_failures: number;
  page_views: number;
  logins: number;
}

export interface SparklinePoint {
  t: string;
  c: number;
}

/**
 * Previous-window counts exist only for EVENT-based cards: error cards read the deduplicated
 * Errors table (lifetime OccurrenceCount, single LastSeenAt), which cannot be windowed honestly.
 */
export interface HealthCardsPreviousDto {
  api_request_failures: number;
  page_views: number;
  logins: number;
}

export interface HealthDto {
  range: { from: string; to: string };
  cards: HealthCardsDto;
  /** Event-based cards over the immediately preceding window of equal length (for deltas). */
  cards_previous?: HealthCardsPreviousDto;
  by_event: { name: string; count: number }[];
  page_views_by_feature: { feature: string; count: number }[];
  top_failing_endpoint_groups: { endpoint_group: string; occurrences: number }[];
  errors_by_release: { release: string; occurrences: number }[];
  sparklines: Record<string, SparklinePoint[]>;
}

// --- Insights Phase A (docs/product-analytics-plan.md) -----------------------

export type TrendInterval = 'hour' | 'day' | 'week';
export type TrendBreakdown = 'feature_area' | 'release_sha' | 'endpoint_group';
export type TrendAgg = 'count' | 'unique_users';

export interface TrendSeriesDto {
  event: string;
  breakdown: string | null;
  total: number;
  buckets: SparklinePoint[];
}

export interface TrendsDto {
  range: { from: string; to: string; interval: TrendInterval };
  agg: TrendAgg;
  series: TrendSeriesDto[];
}

export interface TrendsQuery extends DashboardQuery {
  events: string;
  interval?: TrendInterval;
  breakdown?: TrendBreakdown;
  agg?: TrendAgg;
}

export interface AnnotationDto {
  id: number;
  at: string;
  label: string;
  release_sha: string | null;
}

export interface ErrorRowDto {
  id: number;
  fingerprint: string;
  error_type: string;
  exception_type: string | null;
  endpoint_group: string | null;
  job_name: string | null;
  normalized_route: string | null;
  http_status_code: number | null;
  release_sha: string | null;
  occurrence_count: number;
  first_seen_at: string;
  last_seen_at: string;
  last_correlation_id: string | null;
}

export interface BackgroundJobRowDto {
  id: number;
  job_name: string;
  error_type: string;
  fingerprint: string;
  release_sha: string | null;
  occurrence_count: number;
  suppressed_count: number;
  first_seen_at: string;
  last_seen_at: string;
  last_suppressed_at: string | null;
}

export interface EventRowDto {
  id: number;
  event_name: string;
  distinct_id: string;
  session_id: string | null;
  correlation_id: string | null;
  normalized_route: string | null;
  endpoint_group: string | null;
  feature_area: string | null;
  release_sha: string | null;
  occurred_at: string;
  created_at: string;
  properties_json: string;
}

export type AlertRuleTypeName =
  | 'CountOverWindow'
  | 'NewErrorAfterRelease'
  | 'ErrorRateAboveThreshold'
  | 'AnyProdJobFailure';

export interface AlertRowDto {
  id: number;
  alert_rule_id: string;
  rule_name: string | null;
  rule_type: AlertRuleTypeName;
  environment_id: string | null;
  fired_at: string;
  observed_value: number;
  threshold: number;
  summary: string;
  details_json: string;
}

export interface SessionRowDto {
  id: number;
  session_id: string;
  distinct_id: string;
  started_at: string;
  ended_at: string | null;
  last_seen_at: string;
  has_error: boolean;
  release_sha: string | null;
}

export type TimelineEntry =
  | {
      kind: 'event';
      occurred_at: string;
      id: number;
      event_name: string;
      normalized_route: string | null;
      endpoint_group: string | null;
      correlation_id: string | null;
      properties: unknown;
      is_api_failure: boolean;
    }
  | {
      kind: 'error';
      occurred_at: string;
      id: number;
      error_type: string;
      exception_type: string | null;
      endpoint_group: string | null;
      http_status_code: number | null;
      correlation_id: string | null;
      fingerprint: string;
      occurrence_count: number;
      source: 'in_session' | 'cross_process';
    };

export interface TimelineDto {
  session: {
    session_id: string;
    application_id: string;
    environment_id: string;
    distinct_id: string;
    started_at: string;
    ended_at: string | null;
    last_seen_at: string;
    has_error: boolean;
    release_sha: string | null;
  };
  entries: TimelineEntry[];
}

export interface PagedResult<T> {
  total: number;
  page: number;
  page_size: number;
  rows: T[];
}

export class ApiError extends Error {
  status: number;
  body: unknown;
  constructor(message: string, status: number, body: unknown) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

// Abort a request that never responds so a hung/black-holed backend surfaces as an error
// instead of an infinite loading spinner. status 0 marks "no HTTP response" (timeout/network).
const REQUEST_TIMEOUT_MS = 15_000;

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  const token = getToken();
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      ...init,
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(init?.headers ?? {}),
      },
    });
  } catch (e) {
    if (controller.signal.aborted) {
      throw new ApiError(`Backend did not respond within ${REQUEST_TIMEOUT_MS / 1000}s.`, 0, null);
    }
    // fetch rejects with a TypeError when the backend is unreachable (connection refused,
    // DNS failure, CORS). Wrap it so callers get a friendly message instead of "Failed to fetch".
    throw new ApiError('Cannot reach the backend. Check that the API is running.', 0, e);
  } finally {
    clearTimeout(timer);
  }
  if (!res.ok) {
    // A 401 on any endpoint other than the login attempt itself means the session is gone/expired —
    // drop it so the app routes back to the login screen.
    if (res.status === 401 && !path.startsWith('/api/auth/login')) {
      notifyUnauthorized();
    }
    let body: unknown = null;
    try { body = await res.json(); } catch { /* ignore */ }
    throw new ApiError(`Request failed: ${res.status}`, res.status, body);
  }
  return res.json() as Promise<T>;
}

export interface LoginResponse {
  token: string;
  expires_at: string;
  user: AuthUser;
}

type AnyQuery = Record<string, string | number | undefined>;

export const api = {
  login: (email: string, password: string) =>
    request<LoginResponse>('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    }),
  apps: () =>
    USE_MOCKS ? delay(mock.mockApps()) : request<AppDto[]>('/api/apps'),
  health: (q: DashboardQuery) =>
    USE_MOCKS
      ? delay(mock.mockHealth(q))
      : request<HealthDto>(`/api/dashboard/health${buildQuery(q as unknown as AnyQuery)}`),
  errors: (q: DashboardQuery & PagingQuery & { sort?: string; category?: string }) =>
    USE_MOCKS
      ? delay(mock.mockErrors(q))
      : request<PagedResult<ErrorRowDto>>(`/api/dashboard/errors${buildQuery(q as unknown as AnyQuery)}`),
  backgroundJobs: (q: DashboardQuery & PagingQuery) =>
    USE_MOCKS
      ? delay(mock.mockBackgroundJobs(q))
      : request<PagedResult<BackgroundJobRowDto>>(`/api/dashboard/background-jobs${buildQuery(q as unknown as AnyQuery)}`),
  events: (q: DashboardQuery & PagingQuery & EventFilters) =>
    USE_MOCKS
      ? delay(mock.mockEvents(q))
      : request<PagedResult<EventRowDto>>(`/api/dashboard/events${buildQuery(q as unknown as AnyQuery)}`),
  sessions: (q: DashboardQuery & PagingQuery & { errors_only?: boolean }) =>
    USE_MOCKS
      ? delay(mock.mockSessions(q))
      : request<PagedResult<SessionRowDto>>(`/api/dashboard/sessions${buildQuery({
          ...(q as unknown as AnyQuery),
          errors_only: q.errors_only ? 'true' : undefined,
        })}`),
  sessionTimeline: (sessionId: string) =>
    USE_MOCKS
      ? delay(mock.mockTimeline(sessionId))
      : request<TimelineDto>(`/api/sessions/${encodeURIComponent(sessionId)}/timeline`),
  alerts: (q: DashboardQuery & PagingQuery & { rule_type?: string }) =>
    USE_MOCKS
      ? delay(mock.mockAlerts(q))
      : request<PagedResult<AlertRowDto>>(`/api/dashboard/alerts${buildQuery(q as unknown as AnyQuery)}`),
  trends: (q: TrendsQuery) =>
    USE_MOCKS
      ? delay(mock.mockTrends(q))
      : request<TrendsDto>(`/api/dashboard/insights/trends${buildQuery(q as unknown as AnyQuery)}`),
  annotations: (q: DashboardQuery) =>
    USE_MOCKS
      ? delay(mock.mockAnnotations(q))
      : request<{ rows: AnnotationDto[] }>(`/api/dashboard/annotations${buildQuery(q as unknown as AnyQuery)}`).then((r) => r.rows),

  // --- Issue 10.6 admin surface (always RBAC/admin-key gated server-side) ---
  adminApps: () =>
    USE_MOCKS
      ? delay(mock.mockAdminApps())
      : request<{ apps: AdminAppDto[] }>('/api/admin/apps').then((r) => r.apps),
  createApp: (body: { name: string; slug: string; description?: string; environments?: string[] }) =>
    USE_MOCKS
      ? delay(mock.mockCreateApp(body))
      : request<AdminAppDto>('/api/admin/apps', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        }),
  listKeys: (slug: string, env: string) =>
    USE_MOCKS
      ? delay(mock.mockKeys(slug, env))
      : request<{ keys: ApiKeyDto[] }>(
          `/api/admin/apps/${encodeURIComponent(slug)}/environments/${encodeURIComponent(env)}/keys`,
        ).then((r) => r.keys),
  mintKey: (slug: string, env: string, keyType: ApiKeyTypeName) =>
    USE_MOCKS
      ? delay(mock.mockMintKey(keyType))
      : request<MintKeyResponse>(
          `/api/admin/apps/${encodeURIComponent(slug)}/environments/${encodeURIComponent(env)}/keys`,
          {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ key_type: keyType === 'PublicClient' ? 'public_client' : 'server_api' }),
          },
        ),
  revokeKey: (slug: string, env: string, id: string) =>
    USE_MOCKS
      ? delay({ id, revoked_at: new Date().toISOString(), already_revoked: false })
      : request<{ id: string; revoked_at: string; already_revoked: boolean }>(
          `/api/admin/apps/${encodeURIComponent(slug)}/environments/${encodeURIComponent(env)}/keys/${encodeURIComponent(id)}/revoke`,
          { method: 'POST' },
        ),
  audit: (q: { action?: string; app?: string; from?: string; to?: string } & PagingQuery) =>
    USE_MOCKS
      ? delay(mock.mockAudit(q))
      : request<PagedResult<AuditRowDto>>(`/api/admin/audit${buildQuery({
          action: q.action,
          app: q.app,
          from: q.from,
          to: q.to,
          page: q.page,
          page_size: q.pageSize,
        })}`),
};

export interface DashboardQuery {
  app: string;
  env: string;
  from?: string;
  to?: string;
}

export interface PagingQuery { page?: number; pageSize?: number }
export interface EventFilters {
  event_name?: string;
  distinct_id?: string;
  correlation_id?: string;
}

export function buildQuery(q: Record<string, string | number | undefined>): string {
  const parts: string[] = [];
  for (const [k, v] of Object.entries(q)) {
    if (v === undefined || v === '' || v === null) continue;
    const key = k === 'pageSize' ? 'pageSize' : k;
    parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(v))}`);
  }
  return parts.length ? `?${parts.join('&')}` : '';
}
