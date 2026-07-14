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
  AdminAppDto,
  AlertRowDto,
  AlertRuleTypeName,
  AnnotationDto,
  ApiKeyDto,
  ApiKeyTypeName,
  AppDto,
  AuditRowDto,
  BackgroundJobRowDto,
  DashboardQuery,
  MintKeyResponse,
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
  TrendsDto,
  TrendSeriesDto,
  TrendsQuery,
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
      id: 'wms-site',
      slug: 'wms-site',
      name: 'WMS Site',
      description: 'Wound-management intake & IVR web app (React + Vite).',
      environments: envs,
    },
    {
      id: 'wms-api',
      slug: 'wms-api',
      name: 'WMS API',
      description: 'Wound-management backend service (ASP.NET Core).',
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
// Admin surface (Issue 10.6) — a small in-module store so create / mint / revoke
// behave live within a demo session. Resets on full reload.
// ---------------------------------------------------------------------------

interface MockKey extends ApiKeyDto {
  envKey: string; // `${slug}::${env}`
}

const ADMIN_ENVS = ['Production', 'Development'];

function isoDaysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

const mockAdminAppStore: AdminAppDto[] = mockApps().map((a) => ({
  id: a.id,
  slug: a.slug,
  name: a.name,
  description: a.description,
  is_active: true,
  environments: ADMIN_ENVS.map((name, ei) => ({
    id: `${a.slug}-${name}`,
    name,
    is_active: true,
    total_key_count: 2 - (ei % 2),
    active_key_count: 1,
  })),
}));

const mockKeyStore: MockKey[] = mockAdminAppStore.flatMap((a) =>
  a.environments.flatMap((e, ei) => {
    const r = makeRng(`${a.slug}-${e.name}`);
    return [
      {
        envKey: `${a.slug}::${e.name}`,
        id: `00000000-0000-4000-8000-${String(Math.floor(r() * 1e12)).padStart(12, '0')}`,
        key_type: ei % 2 === 0 ? 'ServerApi' : 'PublicClient',
        created_at: isoDaysAgo(30),
        last_used_at: isoDaysAgo(1),
        expires_at: null,
        revoked_at: null,
        is_active: true,
      } as MockKey,
    ];
  }),
);

export function mockAdminApps(): AdminAppDto[] {
  return mockAdminAppStore.map((a) => ({ ...a, environments: a.environments.map((e) => ({ ...e })) }));
}

export function mockCreateApp(body: { name: string; slug: string; description?: string; environments?: string[] }): AdminAppDto {
  const slug = body.slug.trim().toLowerCase();
  let app = mockAdminAppStore.find((a) => a.slug === slug);
  if (!app) {
    app = {
      id: slug,
      slug,
      name: body.name,
      description: body.description ?? null,
      is_active: true,
      environments: (body.environments ?? ADMIN_ENVS).map((name) => ({
        id: `${slug}-${name}`,
        name,
        is_active: true,
        total_key_count: 0,
        active_key_count: 0,
      })),
    };
    mockAdminAppStore.push(app);
  }
  return { ...app, environments: app.environments.map((e) => ({ ...e })) };
}

export function mockKeys(slug: string, env: string): ApiKeyDto[] {
  const envKey = `${slug}::${env}`;
  return mockKeyStore
    .filter((k) => k.envKey === envKey)
    .map(({ envKey: _envKey, ...rest }) => rest)
    .sort((a, b) => (a.created_at < b.created_at ? 1 : -1));
}

export function mockMintKey(keyType: ApiKeyTypeName): MintKeyResponse {
  const id = crypto.randomUUID();
  const prefix = keyType === 'PublicClient' ? 'aopub_' : 'aoserv_';
  return {
    id,
    key_type: keyType,
    plaintext_key: `${prefix}demo_${id.replace(/-/g, '').slice(0, 24)}`,
    note: 'Store this immediately. The plaintext value is not retrievable after this response.',
  };
}

export function mockAudit(q: { action?: string; app?: string } & PagingQuery): PagedResult<AuditRowDto> {
  const actions = ['admin.app.created', 'admin.key.minted', 'admin.key.revoked', 'access.dashboard'];
  const rows: AuditRowDto[] = Array.from({ length: 24 }).map((_, i) => ({
    id: `aud-${i}`,
    occurred_at: new Date(Date.now() - i * 3_600_000).toISOString(),
    action: actions[i % actions.length],
    actor_type: i % 4 === 3 ? 'admin_user' : 'admin_key',
    application_id: null,
    environment_id: null,
    correlation_id: null,
    details_json: JSON.stringify({ demo: true, seq: i }),
  }));
  const filtered = q.action ? rows.filter((r) => r.action === q.action) : rows;
  const pageSize = q.pageSize ?? 50;
  const page = q.page ?? 0;
  return {
    total: filtered.length,
    page,
    page_size: pageSize,
    rows: filtered.slice(page * pageSize, page * pageSize + pageSize),
  };
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

  // Previous-window counts (deterministic ±35% of current) so delta chips render in demo mode.
  const prevScale = () => 0.65 + makeRng(`${base}:prev`)() * 0.7;
  const cards_previous = {
    backend_500s: Math.round(cards.backend_500s * prevScale()),
    frontend_exceptions: Math.round(cards.frontend_exceptions * prevScale()),
    api_request_failures: Math.round(cards.api_request_failures * prevScale()),
    background_job_failures: Math.round(cards.background_job_failures * prevScale()),
    page_views: Math.round(cards.page_views * prevScale()),
    logins: Math.round(cards.logins * prevScale()),
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
    cards_previous,
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

export function mockBackgroundJobs(
  q: DashboardQuery & PagingQuery,
): PagedResult<BackgroundJobRowDto> {
  const { from, to } = windowOf(q, 24 * 7);
  const span = to.getTime() - from.getTime();
  const r = makeRng(`${q.app}:${q.env}:bgjobs`);
  const count = q.env === 'Production' ? 9 : 4;
  const rows: BackgroundJobRowDto[] = [];
  for (let i = 0; i < count; i++) {
    const occurrences = intBetween(r, 1, 240);
    // A slice of occurrences land inside the dedup window and are suppressed; never more than
    // occurrences - 1 (the first is always the alert-worthy one).
    const suppressed = occurrences > 1 ? intBetween(r, 0, occurrences - 1) : 0;
    const lastSeen = new Date(from.getTime() + r() * span);
    const firstSeen = new Date(from.getTime() + r() * (lastSeen.getTime() - from.getTime()));
    rows.push({
      id: count - i,
      job_name: pick(r, JOB_NAMES),
      error_type: pick(r, EXCEPTION_TYPES),
      fingerprint: hex(r, 32),
      release_sha: pick(r, RELEASES),
      occurrence_count: occurrences,
      suppressed_count: suppressed,
      first_seen_at: firstSeen.toISOString(),
      last_seen_at: lastSeen.toISOString(),
      last_suppressed_at: suppressed > 0 ? lastSeen.toISOString() : null,
    });
  }
  rows.sort((a, b) => +new Date(b.last_seen_at) - +new Date(a.last_seen_at));
  return paginate(rows, q);
}

// ---------------------------------------------------------------------------
// Alerts (Issue 8.3) — fired-alert feed. Visibility-only until 8.4 notifications.
// ---------------------------------------------------------------------------

const ALERT_RULES: { name: string; type: AlertRuleTypeName }[] = [
  { name: 'Backend 500 spike', type: 'CountOverWindow' },
  { name: 'New error after release', type: 'NewErrorAfterRelease' },
  { name: 'Error rate guard', type: 'ErrorRateAboveThreshold' },
  { name: 'Any prod job failure', type: 'AnyProdJobFailure' },
];

function alertSummary(r: Rng, type: AlertRuleTypeName, observed: number, threshold: number): string {
  switch (type) {
    case 'CountOverWindow':
      return `${observed} 'server_error_occurred' events in the last 15m (threshold ${threshold}).`;
    case 'NewErrorAfterRelease':
      return `New error '${pick(r, EXCEPTION_TYPES)}' (${observed}x) first seen on release ${pick(r, RELEASES)}.`;
    case 'ErrorRateAboveThreshold':
      return `Error rate ${observed}% over the last 15m (threshold ${threshold}%).`;
    case 'AnyProdJobFailure':
      return `Production job '${pick(r, JOB_NAMES)}' failed (${pick(r, EXCEPTION_TYPES)}, ${observed}x).`;
  }
}

export function mockAlerts(q: DashboardQuery & PagingQuery & { rule_type?: string }): PagedResult<AlertRowDto> {
  const { from, to } = windowOf(q, 24 * 7);
  const span = to.getTime() - from.getTime();
  const r = makeRng(`${q.app}:${q.env}:alerts`);
  const count = q.env === 'Production' ? 14 : 3;

  let rows: AlertRowDto[] = [];
  for (let i = 0; i < count; i++) {
    const rule = pick(r, ALERT_RULES);
    const threshold =
      rule.type === 'ErrorRateAboveThreshold' ? 5 : rule.type === 'CountOverWindow' ? 50 : 0;
    const observed =
      rule.type === 'ErrorRateAboveThreshold'
        ? intBetween(r, 6, 40)
        : rule.type === 'CountOverWindow'
          ? intBetween(r, 51, 400)
          : intBetween(r, 1, 60);
    const firedAt = new Date(from.getTime() + r() * span);
    rows.push({
      id: count - i,
      alert_rule_id: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
      rule_name: rule.name,
      rule_type: rule.type,
      environment_id: null,
      fired_at: firedAt.toISOString(),
      observed_value: observed,
      threshold,
      summary: alertSummary(r, rule.type, observed, threshold),
      details_json: JSON.stringify({ demo: true, window_minutes: 15 }),
    });
  }

  if (q.rule_type) rows = rows.filter((a) => a.rule_type === q.rule_type);
  rows.sort((a, b) => +new Date(b.fired_at) - +new Date(a.fired_at));
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
      application_id: 'wms-site',
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

// ---------------------------------------------------------------------------
// Insights Phase A — trends + annotations (docs/product-analytics-plan.md)
// ---------------------------------------------------------------------------

const BREAKDOWN_VALUES: Record<string, readonly string[]> = {
  feature_area: FEATURES,
  release_sha: RELEASES,
  endpoint_group: ENDPOINT_GROUPS.slice(0, 5),
};

export function mockTrends(q: TrendsQuery): TrendsDto {
  const { from, to } = windowOf(q);
  const base = `${q.app}:${q.env}`;
  const scale = q.env === 'Production' ? 1 : 0.3;
  const names = q.events.split(',').filter(Boolean);
  const interval = q.interval ?? ((to.getTime() - from.getTime()) <= 48 * 3_600_000 ? 'hour' : 'day');
  const values = q.breakdown ? BREAKDOWN_VALUES[q.breakdown] ?? ['(none)'] : [null];

  const series: TrendSeriesDto[] = names.flatMap((name, ni) =>
    values.map((value, vi) => {
      const seed = `${base}:trend:${name}:${value ?? ''}:${q.agg ?? 'count'}`;
      const magnitude = name === 'page_viewed' ? 120 : name === 'auth_login_success' ? 30 : 8;
      const buckets = sparkline(from, to, seed, (magnitude * scale) / (vi + 1) / (ni + 1), magnitude / 2);
      return {
        event: name,
        breakdown: value,
        total: sum(buckets),
        buckets,
      };
    }),
  );
  series.sort((a, b) => b.total - a.total);

  return {
    range: { from: from.toISOString(), to: to.toISOString(), interval },
    agg: q.agg ?? 'count',
    series,
  };
}

export function mockAnnotations(q: DashboardQuery): AnnotationDto[] {
  const { from, to } = windowOf(q);
  const r = makeRng(`${q.app}:${q.env}:annotations`);
  const spanMs = to.getTime() - from.getTime();
  // One or two deterministic deploy markers inside the window.
  const count = spanMs > 12 * 3_600_000 ? 2 : 1;
  return Array.from({ length: count }, (_, i) => {
    const at = new Date(from.getTime() + spanMs * (0.25 + 0.45 * i + r() * 0.1));
    const sha = RELEASES[intBetween(r, 0, RELEASES.length - 1)];
    return { id: i + 1, at: at.toISOString(), label: `deploy ${sha}`, release_sha: sha };
  });
}
