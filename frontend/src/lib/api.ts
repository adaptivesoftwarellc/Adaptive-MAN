// Frontend API client. Phase 8 will add auth headers; for now the dashboard runs against an
// internal-only backend without auth.

import * as mock from './mock';

const ENV = (import.meta as unknown as {
  env?: { DEV?: boolean; VITE_OBSERVABILITY_API_URL?: string; VITE_USE_MOCKS?: string };
}).env ?? {};

const RAW_BASE = ENV.VITE_OBSERVABILITY_API_URL ?? 'http://localhost:8080';
export const API_BASE = RAW_BASE.replace(/\/$/, '');

// ---------------------------------------------------------------------------
// Demo / mock mode
//
// Lets the dashboard render realistic sample reports when the backend or DB is empty.
// Resolution order: explicit localStorage toggle > VITE_USE_MOCKS env var > default.
// Default is ON in dev (`npm run dev`) and OFF in a production build, so a deployed
// dashboard always hits the real API while local UI work gets demo data for free.
// ---------------------------------------------------------------------------

const MOCK_STORAGE_KEY = 'observability:mocks';

function resolveMockMode(): boolean {
  try {
    const stored = localStorage.getItem(MOCK_STORAGE_KEY);
    if (stored === 'on') return true;
    if (stored === 'off') return false;
  } catch {
    /* ignore */
  }
  if (ENV.VITE_USE_MOCKS === 'true') return true;
  if (ENV.VITE_USE_MOCKS === 'false') return false;
  return ENV.DEV === true;
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

export interface HealthDto {
  range: { from: string; to: string };
  cards: HealthCardsDto;
  by_event: { name: string; count: number }[];
  page_views_by_feature: { feature: string; count: number }[];
  top_failing_endpoint_groups: { endpoint_group: string; occurrences: number }[];
  errors_by_release: { release: string; occurrences: number }[];
  sparklines: Record<string, SparklinePoint[]>;
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
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      ...init,
      signal: controller.signal,
      headers: { Accept: 'application/json', ...(init?.headers ?? {}) },
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
    let body: unknown = null;
    try { body = await res.json(); } catch { /* ignore */ }
    throw new ApiError(`Request failed: ${res.status}`, res.status, body);
  }
  return res.json() as Promise<T>;
}

type AnyQuery = Record<string, string | number | undefined>;

export const api = {
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
