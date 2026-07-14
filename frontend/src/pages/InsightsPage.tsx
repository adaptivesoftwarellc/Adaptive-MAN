/**
 * Insights — Phase A of docs/product-analytics-plan.md.
 *
 * Trends over arbitrary catalog events with hour/day/week bucketing, typed-column
 * breakdowns, totals vs unique users, deploy-annotation markers, and CSV export.
 * The insight configuration lives in the URL (single source of truth, same as the
 * global filters), so views are shareable and work with the saved-views sidebar.
 */
import { useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { api } from '../lib/api';
import type { TrendAgg, TrendBreakdown, TrendInterval, TrendSeriesDto } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { EVENT_NAMES } from '../lib/catalog';
import { CHART_CHROME, seriesColor } from '../lib/chartColors';
import { relativeTime } from '../lib/relativeTime';
import { EmptyState, PageHeader, Panel, Skeleton } from '../components/ui';
import { AlertTriangleIcon, DownloadIcon, RefreshIcon, WifiOffIcon } from '../components/icons';

const BREAKDOWNS: { value: '' | TrendBreakdown; label: string }[] = [
  { value: '', label: 'No breakdown' },
  { value: 'feature_area', label: 'Feature area' },
  { value: 'release_sha', label: 'Release' },
  { value: 'endpoint_group', label: 'Endpoint group' },
];

const INTERVALS: { value: '' | TrendInterval; label: string }[] = [
  { value: '', label: 'Auto' },
  { value: 'hour', label: 'Hour' },
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
];

const MAX_EVENTS = 5;
const DEFAULT_EVENTS = ['page_viewed'];

export function InsightsPage() {
  const { filters, ready } = useFilters();
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);

  const [params, setParams] = useSearchParams();
  const selected = (params.get('metrics') ?? DEFAULT_EVENTS.join(','))
    .split(',')
    .filter((n) => (EVENT_NAMES as readonly string[]).includes(n));
  const interval = (params.get('interval') ?? '') as '' | TrendInterval;
  const breakdown = (params.get('breakdown') ?? '') as '' | TrendBreakdown;
  const agg = (params.get('agg') ?? 'count') as TrendAgg;

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  const toggleEvent = (name: string) => {
    const has = selected.includes(name);
    if (has && selected.length === 1) return; // keep at least one
    const next = has ? selected.filter((n) => n !== name) : [...selected, name].slice(0, MAX_EVENTS);
    setParam('metrics', next.join(','));
  };

  // unique_users cannot roll up to weeks (server rejects it) — steer the UI instead of 400ing.
  const weekBlocked = agg === 'unique_users';

  const query = {
    app: filters.app,
    env: filters.env,
    from: range.from,
    to: range.to,
    events: selected.join(','),
    interval: interval || undefined,
    breakdown: breakdown || undefined,
    agg,
  };

  const { data, isLoading, isError, error, refetch, isFetching, dataUpdatedAt } = useQuery({
    enabled: ready && selected.length > 0,
    queryKey: ['trends', filters.app, filters.env, range.from, range.to, selected.join(','), interval, breakdown, agg],
    queryFn: () => api.trends(query),
    refetchInterval: 30_000,
  });

  const { data: annotations } = useQuery({
    enabled: ready,
    queryKey: ['annotations', filters.app, filters.env, range.from, range.to],
    queryFn: () => api.annotations({ app: filters.app, env: filters.env, from: range.from, to: range.to }),
    refetchInterval: 60_000,
  });

  if (!ready) {
    return (
      <div className="p-6">
        <PageHeader title="Insights" description="Trends over any catalog event, with breakdowns." />
        <EmptyState
          icon={<AlertTriangleIcon className="h-5 w-5" />}
          title="No app selected"
          description="Pick an app and environment from the bar above to explore trends."
        />
      </div>
    );
  }

  return (
    <div className="p-6">
      <PageHeader
        title="Insights"
        description="Trends over any catalog event, with breakdowns."
        actions={
          <div className="flex items-center gap-2">
            {data && (
              <span className="text-xs text-slate-400" title={new Date(dataUpdatedAt).toLocaleString()}>
                Updated {relativeTime(dataUpdatedAt)}
              </span>
            )}
            <button
              onClick={() => refetch()}
              disabled={isFetching}
              className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 text-xs font-medium text-slate-600 shadow-sm transition hover:bg-slate-50 disabled:opacity-60"
              aria-label="Refresh now"
            >
              <RefreshIcon className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} /> Refresh
            </button>
            <button
              onClick={() => data && exportCsv(data.series)}
              disabled={!data || data.series.length === 0}
              className="inline-flex h-8 items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 text-xs font-medium text-slate-600 shadow-sm transition hover:bg-slate-50 disabled:opacity-60"
            >
              <DownloadIcon className="h-3.5 w-3.5" /> Export CSV
            </button>
          </div>
        }
      />

      {/* Controls */}
      <Panel className="mb-4 p-4">
        <div className="flex flex-wrap items-center gap-2">
          {EVENT_NAMES.map((name) => {
            const active = selected.includes(name);
            return (
              <button
                key={name}
                onClick={() => toggleEvent(name)}
                aria-pressed={active}
                className={`rounded-full px-3 py-1.5 font-mono text-xs font-medium ring-1 ring-inset transition ${
                  active
                    ? 'bg-brand-600 text-white ring-brand-600'
                    : 'bg-white text-slate-600 ring-slate-200 hover:bg-slate-50 hover:text-slate-800'
                }`}
              >
                {name}
              </button>
            );
          })}
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-4 border-t border-slate-100 pt-3 text-xs">
          <label className="flex items-center gap-2 text-slate-500">
            Breakdown
            <select
              value={breakdown}
              onChange={(e) => setParam('breakdown', e.target.value)}
              className="rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-700"
            >
              {BREAKDOWNS.map((b) => (
                <option key={b.value} value={b.value}>
                  {b.label}
                </option>
              ))}
            </select>
          </label>

          <div className="flex items-center gap-2 text-slate-500">
            Interval
            <div className="flex overflow-hidden rounded-lg border border-slate-200" role="group" aria-label="Bucket interval">
              {INTERVALS.map((i) => {
                const disabled = i.value === 'week' && weekBlocked;
                return (
                  <button
                    key={i.value || 'auto'}
                    onClick={() => setParam('interval', i.value)}
                    disabled={disabled}
                    title={disabled ? 'Unique users cannot be bucketed by week' : undefined}
                    aria-pressed={interval === i.value}
                    className={`px-2.5 py-1.5 text-xs font-medium transition disabled:cursor-not-allowed disabled:opacity-40 ${
                      interval === i.value ? 'bg-brand-600 text-white' : 'bg-white text-slate-600 hover:bg-slate-50'
                    }`}
                  >
                    {i.label}
                  </button>
                );
              })}
            </div>
          </div>

          <div className="flex items-center gap-2 text-slate-500">
            Measure
            <div className="flex overflow-hidden rounded-lg border border-slate-200" role="group" aria-label="Aggregation">
              {(
                [
                  { value: 'count', label: 'Totals' },
                  { value: 'unique_users', label: 'Unique users' },
                ] as { value: TrendAgg; label: string }[]
              ).map((a) => (
                <button
                  key={a.value}
                  onClick={() => {
                    setParam('agg', a.value === 'count' ? '' : a.value);
                    if (a.value === 'unique_users' && interval === 'week') setParam('interval', '');
                  }}
                  aria-pressed={agg === a.value}
                  className={`px-2.5 py-1.5 text-xs font-medium transition ${
                    agg === a.value ? 'bg-brand-600 text-white' : 'bg-white text-slate-600 hover:bg-slate-50'
                  }`}
                >
                  {a.label}
                </button>
              ))}
            </div>
          </div>
        </div>
      </Panel>

      {/* Chart */}
      {isError ? (
        <Panel>
          <EmptyState
            tone="error"
            icon={<WifiOffIcon className="h-5 w-5" />}
            title="Failed to load trends"
            description={(error as Error).message}
            onRetry={() => refetch()}
          />
        </Panel>
      ) : isLoading || !data ? (
        <Panel className="p-4">
          <Skeleton className="h-72 w-full" />
        </Panel>
      ) : data.series.length === 0 ? (
        <Panel>
          <EmptyState
            icon={<AlertTriangleIcon className="h-5 w-5" />}
            title="No data in this window"
            description="Try a wider time range or different events."
          />
        </Panel>
      ) : (
        <TrendChart
          series={data.series}
          interval={data.range.interval}
          annotations={annotations ?? []}
        />
      )}
    </div>
  );
}

function seriesKey(s: TrendSeriesDto): string {
  return s.breakdown ? `${s.event} · ${s.breakdown}` : s.event;
}

function TrendChart({
  series,
  interval,
  annotations,
}: {
  series: TrendSeriesDto[];
  interval: TrendInterval;
  annotations: { id: number; at: string; label: string }[];
}) {
  // Merge per-series buckets into unified rows keyed by timestamp (Recharts data shape).
  const rows = useMemo(() => {
    const byT = new Map<number, Record<string, number | string>>();
    for (const s of series) {
      const key = seriesKey(s);
      for (const b of s.buckets) {
        const t = new Date(b.t).getTime();
        const row = byT.get(t) ?? { t };
        row[key] = b.c;
        byT.set(t, row);
      }
    }
    return Array.from(byT.values()).sort((a, b) => (a.t as number) - (b.t as number));
  }, [series]);

  const fmt = (t: number) => {
    const d = new Date(t);
    return interval === 'hour'
      ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
      : d.toLocaleDateString([], { month: 'short', day: 'numeric' });
  };

  return (
    <Panel className="p-4">
      <div className="h-80" role="img" aria-label={chartSummary(series)}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={rows} margin={{ top: 8, right: 16, bottom: 4, left: 0 }}>
            <CartesianGrid stroke={CHART_CHROME.grid} vertical={false} />
            <XAxis
              dataKey="t"
              type="number"
              scale="time"
              domain={['dataMin', 'dataMax']}
              tickFormatter={fmt}
              stroke={CHART_CHROME.axis}
              tick={{ fontSize: 11 }}
            />
            <YAxis allowDecimals={false} stroke={CHART_CHROME.axis} tick={{ fontSize: 11 }} width={44} />
            <Tooltip
              labelFormatter={(t) => new Date(t as number).toLocaleString()}
              contentStyle={{ fontSize: 12, borderRadius: 8 }}
            />
            <Legend wrapperStyle={{ fontSize: 12 }} />
            {annotations.map((a) => (
              <ReferenceLine
                key={a.id}
                x={new Date(a.at).getTime()}
                stroke={CHART_CHROME.annotation}
                strokeDasharray="4 3"
                label={{ value: a.label, position: 'top', fontSize: 10, fill: CHART_CHROME.annotation }}
              />
            ))}
            {series.map((s, i) => (
              <Line
                key={seriesKey(s)}
                dataKey={seriesKey(s)}
                type="monotone"
                stroke={seriesColor(i)}
                strokeWidth={2}
                dot={false}
                connectNulls
                isAnimationActive={false}
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </div>
    </Panel>
  );
}

function chartSummary(series: TrendSeriesDto[]): string {
  const parts = series.slice(0, 4).map((s) => `${seriesKey(s)}: ${s.total.toLocaleString()}`);
  return `Trend chart. ${parts.join('; ')}${series.length > 4 ? `; and ${series.length - 4} more series` : ''}.`;
}

function exportCsv(series: TrendSeriesDto[]) {
  const lines = ['event,breakdown,bucket,count'];
  for (const s of series) {
    for (const b of s.buckets) {
      lines.push([s.event, s.breakdown ?? '', b.t, String(b.c)].map(csvEscape).join(','));
    }
  }
  const blob = new Blob([lines.join('\n')], { type: 'text/csv;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `insights-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.csv`;
  a.click();
  URL.revokeObjectURL(a.href);
}

function csvEscape(v: string): string {
  return /[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
}
