import { useEffect, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';

export type RangePreset = '1h' | '24h' | '7d' | '30d' | 'custom';

export interface DashboardFilters {
  app: string;
  env: string;
  range: RangePreset;
  from?: string;
  to?: string;
}

const STORAGE_KEY = 'observability:filters:v1';

function loadStored(): Partial<DashboardFilters> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Partial<DashboardFilters>) : {};
  } catch { return {}; }
}

function saveStored(f: DashboardFilters) {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(f)); } catch { /* ignore */ }
}

/**
 * Resolves the absolute time window for a preset. Custom returns whatever from/to
 * are stored on the filter.
 */
export function resolveRange(f: DashboardFilters): { from?: string; to?: string } {
  if (f.range === 'custom') return { from: f.from, to: f.to };
  const now = new Date();
  const to = now.toISOString();
  const fromDate = new Date(now);
  switch (f.range) {
    case '1h':  fromDate.setHours(fromDate.getHours() - 1); break;
    case '24h': fromDate.setHours(fromDate.getHours() - 24); break;
    case '7d':  fromDate.setDate(fromDate.getDate() - 7); break;
    case '30d': fromDate.setDate(fromDate.getDate() - 30); break;
  }
  return { from: fromDate.toISOString(), to };
}

export function useFilters(): {
  filters: DashboardFilters;
  setFilters: (next: Partial<DashboardFilters>) => void;
  ready: boolean;
} {
  const [params, setParams] = useSearchParams();

  const filters: DashboardFilters = useMemo(() => {
    const stored = loadStored();
    return {
      app: params.get('app') ?? stored.app ?? '',
      env: params.get('env') ?? stored.env ?? '',
      range: ((params.get('range') as RangePreset | null) ?? stored.range ?? '24h'),
      from: params.get('from') ?? stored.from,
      to: params.get('to') ?? stored.to,
    };
  }, [params]);

  // Keep localStorage in sync whenever URL changes.
  useEffect(() => { saveStored(filters); }, [filters]);

  const setFilters = (next: Partial<DashboardFilters>) => {
    const merged = { ...filters, ...next };
    // Start from the current URL so page-specific params (Insights metrics/interval/breakdown/agg,
    // table filters) survive an app/env/range change instead of being wiped mid-exploration.
    const sp = new URLSearchParams(params);
    if (merged.app) sp.set('app', merged.app);
    if (merged.env) sp.set('env', merged.env);
    if (merged.range) sp.set('range', merged.range);
    if (merged.range === 'custom') {
      if (merged.from) sp.set('from', merged.from);
      if (merged.to) sp.set('to', merged.to);
    } else {
      sp.delete('from');
      sp.delete('to');
    }
    setParams(sp, { replace: true });
  };

  const ready = !!(filters.app && filters.env);
  return { filters, setFilters, ready };
}
