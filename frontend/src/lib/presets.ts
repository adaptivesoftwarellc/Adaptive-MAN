/**
 * Dashboard views — saved shortcuts that deep-link into Health/Errors/Events/Sessions
 * with a curated app + env + range filter applied via the URL.
 *
 * Two sources:
 *  - `builtin`  — curated "Quick views", shipped with the app (mirroring the WMS
 *                 onboarding dashboards, Phase 7). Not user-editable.
 *  - `user`     — "My views", created and removed by the user, persisted in localStorage.
 *
 * The Sidebar reads `builtinViews` plus `loadUserViews()` and renders them in their own
 * sections, so adding richer editing later (rename, reorder, share) only touches this module.
 */

export type ViewSource = 'builtin' | 'user';
export type ViewPage = 'health' | 'errors' | 'events' | 'sessions' | 'alerts' | 'insights';
export type ViewRange = '1h' | '24h' | '7d' | '30d' | 'custom';

export interface DashboardView {
  id: string;
  label: string;
  // The dashboard page this view opens (matches the App.tsx route table).
  page: ViewPage;
  app: string;
  env: string;
  range: ViewRange;
  // Only meaningful when range === 'custom'.
  from?: string;
  to?: string;
  /** Extra page-specific query params (URL-encoded, e.g. Insights metrics/interval/breakdown/agg). */
  params?: string;
  source: ViewSource;
}

/** Curated, read-only shortcuts. */
export const builtinViews: DashboardView[] = [
  // WMS onboarding views — Phase 7
  { id: 'wms-site-prod-health', label: 'WMS Site · Prod health', page: 'health', app: 'wms-site', env: 'Production',  range: '24h', source: 'builtin' },
  { id: 'wms-site-dev-health',  label: 'WMS Site · Dev health',  page: 'health', app: 'wms-site', env: 'Development', range: '24h', source: 'builtin' },
  { id: 'wms-api-prod-health',  label: 'WMS API · Prod health',  page: 'health', app: 'wms-api',  env: 'Production',  range: '24h', source: 'builtin' },
  { id: 'wms-api-dev-health',   label: 'WMS API · Dev health',   page: 'health', app: 'wms-api',  env: 'Development', range: '24h', source: 'builtin' },
  { id: 'wms-site-errors',      label: 'WMS Site · Errors (7d)', page: 'errors', app: 'wms-site', env: 'Production',  range: '7d',  source: 'builtin' },
  { id: 'wms-api-errors',       label: 'WMS API · Errors (7d)',  page: 'errors', app: 'wms-api',  env: 'Production',  range: '7d',  source: 'builtin' },
];

/** Build the relative URL for a view, including query params. */
export function viewHref(v: DashboardView): string {
  const params = new URLSearchParams({ app: v.app, env: v.env, range: v.range });
  if (v.range === 'custom') {
    if (v.from) params.set('from', v.from);
    if (v.to) params.set('to', v.to);
  }
  // Restore any page-specific config saved with the view (Insights chart setup, table filters).
  if (v.params) {
    for (const [k, val] of new URLSearchParams(v.params)) params.set(k, val);
  }
  return `/${v.page}?${params.toString()}`;
}

// ---------------------------------------------------------------------------
// User views (localStorage-backed). The Sidebar mutates these via the helpers
// below and re-reads loadUserViews() to refresh its list.
// ---------------------------------------------------------------------------

const USER_VIEWS_KEY = 'observability:user-views:v1';

export function loadUserViews(): DashboardView[] {
  try {
    const raw = localStorage.getItem(USER_VIEWS_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as DashboardView[]) : [];
  } catch {
    return [];
  }
}

function persist(views: DashboardView[]): void {
  try {
    localStorage.setItem(USER_VIEWS_KEY, JSON.stringify(views));
  } catch {
    /* ignore */
  }
}

/** Append a user view and return the updated list. */
export function addUserView(view: Omit<DashboardView, 'id' | 'source'>): DashboardView[] {
  const id = `user-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`;
  const next = [...loadUserViews(), { ...view, id, source: 'user' as const }];
  persist(next);
  return next;
}

/** Remove a user view by id and return the updated list. */
export function deleteUserView(id: string): DashboardView[] {
  const next = loadUserViews().filter((v) => v.id !== id);
  persist(next);
  return next;
}
