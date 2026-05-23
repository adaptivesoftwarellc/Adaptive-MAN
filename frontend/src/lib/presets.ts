/**
 * Per-app dashboard presets — saved views that deep-link into Health/Errors/Events
 * with a curated app + env + range filter applied via the URL.
 *
 * Mirrors the original PostHog dashboard plan from SCH onboarding (Phase 6.9).
 * Add new entries here when onboarding additional apps; the Sidebar reads this
 * list directly so no other wiring is needed.
 */

export interface DashboardPreset {
  id: string;
  label: string;
  // The dashboard page this preset opens (matches the App.tsx route table).
  page: 'health' | 'errors' | 'events' | 'sessions';
  app: string;
  env: string;
  range: '1h' | '24h' | '7d' | '30d';
}

export const presets: DashboardPreset[] = [
  // SCH onboarding presets — Phase 6.9
  { id: 'sch-ui-prod-health',  label: 'SCH UI · Prod health',  page: 'health', app: 'sch-ui',  env: 'Production',  range: '24h' },
  { id: 'sch-ui-dev-health',   label: 'SCH UI · Dev health',   page: 'health', app: 'sch-ui',  env: 'Development', range: '24h' },
  { id: 'sch-api-prod-health', label: 'SCH API · Prod health', page: 'health', app: 'sch-api', env: 'Production',  range: '24h' },
  { id: 'sch-api-dev-health',  label: 'SCH API · Dev health',  page: 'health', app: 'sch-api', env: 'Development', range: '24h' },
  { id: 'sch-ui-errors',       label: 'SCH UI · Errors (7d)',  page: 'errors', app: 'sch-ui',  env: 'Production',  range: '7d'  },
  { id: 'sch-api-errors',      label: 'SCH API · Errors (7d)', page: 'errors', app: 'sch-api', env: 'Production',  range: '7d'  },
];

/** Build the relative URL for a preset including query params. */
export function presetHref(p: DashboardPreset): string {
  const params = new URLSearchParams({ app: p.app, env: p.env, range: p.range });
  return `/${p.page}?${params.toString()}`;
}
