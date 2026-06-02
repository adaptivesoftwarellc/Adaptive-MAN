/**
 * Demo / mock data layer.
 *
 * When the backend is unavailable (e.g. an empty local DB or no API running), the dashboard
 * can render realistic sample reports so the UI can be developed and reviewed end-to-end.
 *
 * Data is *deterministic*: every value is derived from a seeded PRNG keyed on the current
 * app + environment selection, so the same view always produces the same numbers (no flicker
 * across React Query refetches) while different apps/envs look meaningfully different.
 *
 * This module only imports *types* from ./api, so there is no runtime import cycle.
 */
import type {
  AppDto,
  DashboardQuery,
  ErrorRowDto,
  EventFilters,
  EventRowDto,
  HealthDto,
  PagedResult,
  PagingQuery,
  SessionRowDto,
  SparklinePoint,
  TimelineDto,
  TimelineEntry,
} from './api';
import { EVENT_NAMES, errorCategory } from './catalog';

// ---------------------------------------------------------------------------
// Seeded PRNG (mulberry32 + a small string hash) — deterministic, no deps.
// ---------------------------------------------------------------------------

function hashSeed(str: string): number {
  let h = 1779033703 ^ str.length;
  for (let i = 0; i < str.length; i++) {
    h = Math.imul(h ^ str.charCodeAt(i), 3432918353);
    h = (h << 13) | (h >>> 19);
  }
  h = Math.imul(h ^ (h >>> 16), 2246822507);
  h = Math.imul(h ^ (h >>> 13), 3266489909);
  return (h ^= h >>> 16) >>> 0;
}

type Rng = () => number;

function makeRng(seed: string): Rng {
  let a = hashSeed(seed);
  return function () {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const pick = <T>(r: Rng, arr: readonly T[]): T => arr[Math.floor(r() * arr.length)];
const intBetween = (r: Rng, lo: number, hi: number): number => lo + Math.floor(r() * (hi - lo + 1));

function hex(r: Rng, len: number): string {
  let s = '';
  const chars = '0123456789abcdef';
  for (let i = 0; i < len; i++) s += chars[Math.floor(r() * 16)];
  return s;
}

// ---------------------------------------------------------------------------
// Vocabulary — realistic-looking values for a healthcare scheduling platform.
// ---------------------------------------------------------------------------

const ENDPOINT_GROUPS = [
  'GET /api/appointments',
  'POST /api/appointments',
  'GET /api/patients/{id}',
  'PUT /api/appointments/{id}',
  'POST /api/auth/login',
  'GET /api/providers',
  'GET /api/slots',
  'POST /api/insurance/verify',
] as const;

const ROUTES = [
  '/appointments',
  '/appointments/{id}',
  '/patients/{id}',
  '/login',
  '/providers',
  '/schedule',
  '/settings',
  '/billing',
] as const;

const JOB_NAMES = [
  'ReminderDispatchJob',
  'NightlySyncJob',
  'InsuranceVerificationJob',
  'CleanupExpiredSlotsJob',
] as const;

const FEATURES = ['scheduling', 'patients', 'auth', 'providers', 'billing', 'settings'] as const;
const RELEASES = ['a1b2c3d', 'e4f5a6b', '9c8d7e6', 'f1e2d3c', '42ab9f0'] as const;
const EXCEPTION_TYPES = [
  'NullReferenceException',
  'SqlTimeoutException',
  'HttpRequestException',
  'InvalidOperationException',
  'TaskCanceledException',
] as const;
// Frontend exceptions carry a JS-style error class rather than a .NET exception type.
const FE_EXCEPTION_TYPES = [
  'TypeError',
  'ReferenceError',
  'RangeError',
  'NetworkError',
  'ChunkLoadError',
] as const;
const HTTP_STATUSES = [500, 500, 502, 503, 400, 404, 409, 429] as const;
const SERVER_STATUSES = [500, 500, 500, 502, 503] as const;

// ---------------------------------------------------------------------------
// Apps
// ---------------------------------------------------------------------------

export function mockApps(): AppDto[] {
  const envs = [
    { id: 'Production', name: 'Production' },
    { id: 'Development', name: 'Development' },
  ];
  return [
    {
      id: 'sch-ui',
      slug: 'sch-ui',
      name: 'SCH UI',
      description: 'Patient-facing scheduling web app (React).',
      environments: envs,
    },
    {
      id: 'sch-api',
      slug: 'sch-api',
      name: 'SCH API',
      description: 'Scheduling backend service (ASP.NET Core).',
      environments: envs,
    },
    {
      id: 'billing-ui',
      slug: 'billing-ui',
      name: 'Billing UI',
      description: 'Internal billing & insurance console.',
      environments: envs,
    },
  ];
}

// ---------------------------------------------------------------------------
// Time helpers
// ---------------------------------------------------------------------------

function windowOf(q: DashboardQuery, fallbackHours = 24): { from: Date; to: Date } {
  const to = q.to ? new Date(q.to) : new Date();
  const from = q.from ? new Date(q.from) : new Date(to.getTime() - fallbackHours * 3600_000);
  return { from, to };
}

function sparkline(from: Date, to: Date, seed: string, base: number, variance: number): SparklinePoint[] {
  const r = makeRng(seed);
  const points = 24;
  const span = to.getTime() - from.getTime();
  const out: SparklinePoint[] = [];
  for (let i = 0; i < points; i++) {
    const t = new Date(from.getTime() + (span * i) / (points - 1));
    // gentle sine swell + noise so the line looks organic rather than flat random
    const swell = Math.sin((i / points) * Math.PI * 2) * 0.4 + 0.6;
    const c = Math.max(0, Math.round(base * swell + (r() - 0.5) * variance));
    out.push({ t: t.toISOString(), c });
  }
  return out;
}

const sum = (pts: SparklinePoint[]): number => pts.reduce((acc, p) => acc + p.c, 0);

// ---------------------------------------------------------------------------
// Health
// ---------------------------------------------------------------------------

export function mockHealth(q: DashboardQuery): HealthDto {
  const { from, to } = windowOf(q);
  const base = `${q.app}:${q.env}`;
  const r = makeRng(`${base}:health`);

  // Production gets more traffic (and more errors) than dev.
  const scale = q.env === 'Production' ? 1 : 0.3;

  const sparklines: Record<string, SparklinePoint[]> = {
    server_error_occurred: sparkline(from, to, `${base}:500`, 4 * scale, 5),
    frontend_exception: sparkline(from, to, `${base}:fe`, 6 * scale, 6),
    api_request_failed: sparkline(from, to, `${base}:apifail`, 9 * scale, 8),
    background_job_failed: sparkline(from, to, `${base}:job`, 2 * scale, 3),
    page_viewed: sparkline(from, to, `${base}:pv`, 180 * scale, 120),
    auth_login_success: sparkline(from, to, `${base}:login`, 40 * scale, 30),
  };

  const cards = {
    backend_500s: sum(sparklines.server_error_occurred),
    frontend_exceptions: sum(sparklines.frontend_exception),
    api_request_failures: sum(sparklines.api_request_failed),
    background_job_failures: sum(sparklines.background_job_failed),
    page_views: sum(sparklines.page_viewed),
    logins: sum(sparklines.auth_login_success),
  };

  const rankList = <T extends string>(items: readonly T[], lo: number, hi: number) =>
    items
      .map((label) => ({ label, value: intBetween(r, lo, hi) }))
      .sort((a, b) => b.value - a.value);

  const byFeature = rankList(FEATURES, 20, Math.round(900 * scale));
  const byEndpoint = rankList(ENDPOINT_GROUPS.slice(0, 6), 1, Math.round(60 * scale));
  const byRelease = rankList(RELEASES, 1, Math.round(120 * scale));

  return {
    range: { from: from.toISOString(), to: to.toISOString() },
    cards,
    by_event: EVENT_NAMES.map((name) => ({ name, count: intBetween(r, 5, Math.round(1200 * scale)) })).sort(
      (a, b) => b.count - a.count,
    ),
    page_views_by_feature: byFeature.map((x) => ({ feature: x.label, count: x.value })),
    top_failing_endpoint_groups: byEndpoint.map((x) => ({ endpoint_group: x.label, occurrences: x.value })),
    errors_by_release: byRelease.map((x) => ({ release: x.label, occurrences: x.value })),
    sparklines,
  };
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

function buildErrorPool(q: DashboardQuery): ErrorRowDto[] {
  const { from, to } = windowOf(q, 24 * 7);
  const span = to.getTime() - from.getTime();
  const r = makeRng(`${q.app}:${q.env}:errors`);
  const count = q.env === 'Production' ? 37 : 12;
  const rows: ErrorRowDto[] = [];
  for (let i = 0; i < count; i++) {
    const category = pick(r, ['server', 'frontend', 'background_job'] as const);
    const lastSeen = new Date(from.getTime() + r() * span);
    const firstSeen = new Date(from.getTime() + r() * (lastSeen.getTime() - from.getTime()));

    // `error_type` is the specific exception class. The category is implied by which fields
    // are set — exception_type => server, job_name => background job, neither => frontend.
    let errorType: string;
    let exceptionType: string | null = null;
    let endpointGroup: string | null = null;
    let jobName: string | null = null;
    let normalizedRoute: string | null = null;
    let httpStatus: number | null = null;
    if (category === 'server') {
      exceptionType = pick(r, EXCEPTION_TYPES);
      errorType = exceptionType;
      endpointGroup = pick(r, ENDPOINT_GROUPS);
      httpStatus = pick(r, SERVER_STATUSES);
    } else if (category === 'frontend') {
      // Frontend exceptions (JS captureException) carry no .NET exception_type.
      errorType = pick(r, FE_EXCEPTION_TYPES);
      normalizedRoute = pick(r, ROUTES);
    } else {
      // background_job: no exception_type, identified by job_name.
      errorType = pick(r, EXCEPTION_TYPES);
      jobName = pick(r, JOB_NAMES);
    }

    rows.push({
      id: i + 1,
      fingerprint: hex(r, 16),
      error_type: errorType,
      exception_type: exceptionType,
      endpoint_group: endpointGroup,
      job_name: jobName,
      normalized_route: normalizedRoute,
      http_status_code: httpStatus,
      release_sha: pick(r, RELEASES),
      occurrence_count: intBetween(r, 1, 480),
      first_seen_at: firstSeen.toISOString(),
      last_seen_at: lastSeen.toISOString(),
      last_correlation_id: `cor_${hex(r, 12)}`,
    });
  }
  return rows;
}

export function mockErrors(
  q: DashboardQuery & PagingQuery & { sort?: string; category?: string },
): PagedResult<ErrorRowDto> {
  let rows = buildErrorPool(q);
  if (q.category) rows = rows.filter((e) => errorCategory(e) === q.category);
  if (q.sort === 'occurrence_count') {
    rows = rows.sort((a, b) => b.occurrence_count - a.occurrence_count);
  } else {
    rows = rows.sort((a, b) => +new Date(b.last_seen_at) - +new Date(a.last_seen_at));
  }
  return paginate(rows, q);
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

function propertiesFor(r: Rng, name: string): string {
  const common = { release: pick(r, RELEASES) };
  switch (name) {
    case 'page_viewed':
      return JSON.stringify({ ...common, normalized_route: pick(r, ROUTES), feature_area: pick(r, FEATURES) }, null, 0);
    case 'api_request_failed':
      return JSON.stringify(
        {
          ...common,
          endpoint_group: pick(r, ENDPOINT_GROUPS),
          method: pick(r, ['GET', 'POST', 'PUT', 'DELETE']),
          http_status_code: pick(r, HTTP_STATUSES),
          is_network_error: r() < 0.2,
        },
        null,
        0,
      );
    case 'auth_login_success':
      return JSON.stringify({ ...common, generic_role: pick(r, ['staff', 'provider', 'admin']) }, null, 0);
    case 'frontend_exception':
      return JSON.stringify({ ...common, error_type: pick(r, FE_EXCEPTION_TYPES), source: 'window.onerror' }, null, 0);
    case 'server_error_occurred':
      return JSON.stringify(
        { ...common, exception_type: pick(r, EXCEPTION_TYPES), endpoint_group: pick(r, ENDPOINT_GROUPS), http_status_code: pick(r, SERVER_STATUSES) },
        null,
        0,
      );
    case 'background_job_failed':
      return JSON.stringify({ ...common, job_name: pick(r, JOB_NAMES), error_type: pick(r, EXCEPTION_TYPES) }, null, 0);
    default:
      return JSON.stringify(common, null, 0);
  }
}

function buildEventPool(q: DashboardQuery): EventRowDto[] {
  const { from, to } = windowOf(q, 24 * 7);
  const span = to.getTime() - from.getTime();
  const r = makeRng(`${q.app}:${q.env}:events`);
  const count = q.env === 'Production' ? 140 : 45;
  const rows: EventRowDto[] = [];
  for (let i = 0; i < count; i++) {
    const name = pick(r, EVENT_NAMES);
    const occurred = new Date(from.getTime() + r() * span);
    const isFailure = name === 'api_request_failed' || name === 'server_error_occurred';
    rows.push({
      id: count - i,
      event_name: name,
      distinct_id: `usr_${hex(r, 10)}`,
      session_id: `ses_${hex(r, 10)}`,
      correlation_id: isFailure ? `cor_${hex(r, 12)}` : null,
      normalized_route: pick(r, ROUTES),
      endpoint_group: isFailure ? pick(r, ENDPOINT_GROUPS) : null,
      feature_area: pick(r, FEATURES),
      release_sha: pick(r, RELEASES),
      occurred_at: occurred.toISOString(),
      created_at: new Date(occurred.getTime() + intBetween(r, 20, 2000)).toISOString(),
      properties_json: propertiesFor(r, name),
    });
  }
  return rows.sort((a, b) => +new Date(b.created_at) - +new Date(a.created_at));
}

export function mockEvents(
  q: DashboardQuery & PagingQuery & EventFilters,
): PagedResult<EventRowDto> {
  let rows = buildEventPool(q);
  if (q.event_name) rows = rows.filter((e) => e.event_name.includes(q.event_name!));
  if (q.distinct_id) rows = rows.filter((e) => e.distinct_id.includes(q.distinct_id!));
  if (q.correlation_id) rows = rows.filter((e) => (e.correlation_id ?? '').includes(q.correlation_id!));
  return paginate(rows, q);
}

// ---------------------------------------------------------------------------
// Sessions
// ---------------------------------------------------------------------------

function buildSessionPool(q: DashboardQuery): SessionRowDto[] {
  const { from, to } = windowOf(q, 24 * 3);
  const span = to.getTime() - from.getTime();
  const seedBase = `${q.app}:${q.env}:sessions`;
  const count = q.env === 'Production' ? 32 : 11;
  const rows: SessionRowDto[] = [];
  for (let i = 0; i < count; i++) {
    const sid = `ses_${hex(makeRng(`${seedBase}:sid:${i}`), 12)}`;
    const sr = makeRng(sid);
    const started = new Date(from.getTime() + sr() * span);
    const durationMs = intBetween(sr, 30_000, 45 * 60_000);
    const active = sr() < 0.18;
    const last = new Date(Math.min(to.getTime(), started.getTime() + durationMs));
    rows.push({
      id: i + 1,
      session_id: sid,
      distinct_id: `usr_${hex(makeRng(`${sid}:user`), 10)}`,
      started_at: started.toISOString(),
      ended_at: active ? null : last.toISOString(),
      last_seen_at: last.toISOString(),
      has_error: makeRng(`${sid}:err`)() < 0.28,
      release_sha: pick(sr, RELEASES),
    });
  }
  return rows.sort((a, b) => +new Date(b.last_seen_at) - +new Date(a.last_seen_at));
}

export function mockSessions(
  q: DashboardQuery & PagingQuery & { errors_only?: boolean },
): PagedResult<SessionRowDto> {
  let rows = buildSessionPool(q);
  if (q.errors_only) rows = rows.filter((s) => s.has_error);
  return paginate(rows, q);
}

// ---------------------------------------------------------------------------
// Session timeline (seeded by session id so it stays consistent with the list)
// ---------------------------------------------------------------------------

export function mockTimeline(sessionId: string): TimelineDto {
  const sr = makeRng(sessionId);
  const started = new Date(Date.now() - intBetween(sr, 1, 72) * 3600_000);
  const hasError = makeRng(`${sessionId}:err`)() < 0.6; // timelines we open tend to be interesting
  const entryCount = intBetween(sr, 6, 16);

  let cursor = started.getTime();
  const entries: TimelineEntry[] = [];
  for (let i = 0; i < entryCount; i++) {
    cursor += intBetween(sr, 2_000, 90_000);
    const occurred = new Date(cursor).toISOString();
    const roll = sr();
    if (hasError && roll < 0.18) {
      const crossProcess = sr() < 0.4;
      entries.push({
        kind: 'error',
        occurred_at: occurred,
        id: 1000 + i,
        error_type: crossProcess ? 'server_error' : 'frontend_exception',
        exception_type: pick(sr, EXCEPTION_TYPES),
        endpoint_group: crossProcess ? pick(sr, ENDPOINT_GROUPS) : null,
        http_status_code: crossProcess ? pick(sr, HTTP_STATUSES) : null,
        correlation_id: `cor_${hex(sr, 12)}`,
        fingerprint: hex(sr, 16),
        occurrence_count: intBetween(sr, 1, 40),
        source: crossProcess ? 'cross_process' : 'in_session',
      });
    } else {
      const apiFailure = roll > 0.82;
      entries.push({
        kind: 'event',
        occurred_at: occurred,
        id: 2000 + i,
        event_name: apiFailure ? 'api_request_failed' : pick(sr, EVENT_NAMES),
        normalized_route: pick(sr, ROUTES),
        endpoint_group: apiFailure ? pick(sr, ENDPOINT_GROUPS) : null,
        correlation_id: apiFailure ? `cor_${hex(sr, 12)}` : null,
        properties: { release: pick(sr, RELEASES), route: pick(sr, ROUTES) },
        is_api_failure: apiFailure,
      });
    }
  }

  const ended = makeRng(`${sessionId}:active`)() < 0.5 ? new Date(cursor + 30_000).toISOString() : null;

  return {
    session: {
      session_id: sessionId,
      application_id: 'sch-ui',
      environment_id: 'Production',
      distinct_id: `usr_${hex(makeRng(`${sessionId}:user`), 10)}`,
      started_at: started.toISOString(),
      ended_at: ended,
      last_seen_at: new Date(cursor).toISOString(),
      has_error: entries.some((e) => e.kind === 'error'),
      release_sha: pick(sr, RELEASES),
    },
    entries,
  };
}

// ---------------------------------------------------------------------------
// Paging helper
// ---------------------------------------------------------------------------

function paginate<T>(rows: T[], q: PagingQuery): PagedResult<T> {
  const page = q.page ?? 0;
  const pageSize = q.pageSize ?? 50;
  const start = page * pageSize;
  return {
    total: rows.length,
    page,
    page_size: pageSize,
    rows: rows.slice(start, start + pageSize),
  };
}
