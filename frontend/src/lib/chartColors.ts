/**
 * Semantic chart palette — the single source of truth for every Recharts stroke/fill.
 *
 * Hues are deliberately distinct (rose / violet / cyan / amber / indigo / emerald) instead of
 * the old rose/orange/amber run, which was nearly indistinguishable under deuteranopia when
 * the cards sat side by side. Values are mid-scale so they read on both light and dark themes.
 */

/** Per-telemetry-type colors used by the Health cards and anywhere a metric has a fixed meaning. */
export const METRIC_COLORS = {
  backend_500s: '#f43f5e', // rose-500
  frontend_exceptions: '#8b5cf6', // violet-500
  background_job_failures: '#06b6d4', // cyan-500
  api_request_failures: '#f59e0b', // amber-500
  page_views: '#6366f1', // indigo-500 (brand)
  logins: '#10b981', // emerald-500
} as const;

export type MetricKey = keyof typeof METRIC_COLORS;

/** Categorical palette for multi-series charts (Insights). Order = assignment order. */
export const SERIES_COLORS = [
  '#6366f1', // indigo
  '#f59e0b', // amber
  '#10b981', // emerald
  '#f43f5e', // rose
  '#06b6d4', // cyan
  '#8b5cf6', // violet
  '#84cc16', // lime
  '#ec4899', // pink
  '#14b8a6', // teal
  '#f97316', // orange
] as const;

export function seriesColor(index: number): string {
  return SERIES_COLORS[index % SERIES_COLORS.length];
}

/** Neutral chart chrome that stays legible on both themes. */
export const CHART_CHROME = {
  grid: 'rgba(148, 163, 184, 0.25)', // slate-400 @ 25%
  axis: '#94a3b8', // slate-400
  annotation: '#94a3b8',
} as const;
