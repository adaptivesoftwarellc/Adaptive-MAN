import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { Badge, EmptyState, PageHeader, Panel } from '../components/ui';
import { ClockIcon, WifiOffIcon, ArrowRightIcon } from '../components/icons';
import { usePageSize } from '../lib/usePageSize';
import { Pager } from './ErrorsPage';

export function SessionsPage() {
  const { filters, ready } = useFilters();
  // Stable window across renders (see HealthPage note). Also makes the top-bar time range
  // actually apply to sessions — previously only a custom from/to was passed through.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePageSize();

  const { data, isLoading, error, refetch } = useQuery({
    enabled: ready,
    queryKey: ['sessions', filters.app, filters.env, range.from, range.to, errorsOnly, page, pageSize],
    queryFn: () =>
      api.sessions({
        app: filters.app,
        env: filters.env,
        from: range.from,
        to: range.to,
        errors_only: errorsOnly || undefined,
        page,
        pageSize,
      }),
  });

  if (!ready) {
    return (
      <div className="p-6">
        <PageHeader title="Sessions" description="User sessions and their lifecycle for the selected window." />
        <EmptyState icon={<ClockIcon className="h-5 w-5" />} title="No app selected" description="Pick an app and environment above." />
      </div>
    );
  }

  return (
    <div className="p-6">
      <PageHeader title="Sessions" description="User sessions and their lifecycle for the selected window." />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <select
          className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400"
          value={errorsOnly ? 'errors' : ''}
          onChange={(e) => {
            setErrorsOnly(e.target.value === 'errors');
            setPage(0);
          }}
        >
          <option value="">All sessions</option>
          <option value="errors">With errors only</option>
        </select>
      </div>

      {error && (
        <Panel>
          <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load sessions" onRetry={() => refetch()} />
        </Panel>
      )}

      <Panel className="overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-200 bg-slate-50/80 text-left text-xs uppercase tracking-wider text-slate-500">
              <th className="px-4 py-2.5 font-semibold">Session</th>
              <th className="px-4 py-2.5 font-semibold">User</th>
              <th className="px-4 py-2.5 font-semibold">Started</th>
              <th className="px-4 py-2.5 font-semibold">Last seen</th>
              <th className="px-4 py-2.5 font-semibold">Status</th>
              <th className="px-4 py-2.5 font-semibold">Release</th>
              <th className="px-4 py-2.5" />
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {isLoading &&
              Array.from({ length: 8 }).map((_, i) => (
                <tr key={i}>
                  {Array.from({ length: 7 }).map((__, j) => (
                    <td key={j} className="px-4 py-3">
                      <div className="shimmer h-3 w-full max-w-[8rem] rounded bg-slate-200/70" />
                    </td>
                  ))}
                </tr>
              ))}
            {data?.rows.length === 0 && (
              <tr>
                <td colSpan={7}>
                  <EmptyState icon={<ClockIcon className="h-5 w-5" />} title="No sessions in range" />
                </td>
              </tr>
            )}
            {data?.rows.map((s) => (
              <tr key={s.id} className="group transition hover:bg-slate-50">
                <td className="px-4 py-2.5">
                  <Link
                    to={`/sessions/${encodeURIComponent(s.session_id)}`}
                    className="font-mono text-xs font-medium text-brand-600 hover:underline"
                  >
                    {s.session_id}
                  </Link>
                </td>
                <td className="px-4 py-2.5 font-mono text-xs text-slate-600">{s.distinct_id}</td>
                <td className="px-4 py-2.5 text-slate-500">{new Date(s.started_at).toLocaleString()}</td>
                <td className="px-4 py-2.5 text-slate-500">{new Date(s.last_seen_at).toLocaleString()}</td>
                <td className="px-4 py-2.5">
                  {s.has_error ? (
                    <Badge color="red">error</Badge>
                  ) : s.ended_at ? (
                    <Badge color="gray">ended</Badge>
                  ) : (
                    <Badge color="green">
                      <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" /> active
                    </Badge>
                  )}
                </td>
                <td className="px-4 py-2.5 font-mono text-xs text-slate-500">{s.release_sha ?? '—'}</td>
                <td className="px-4 py-2.5 text-right">
                  <Link
                    to={`/sessions/${encodeURIComponent(s.session_id)}`}
                    className="inline-flex text-slate-300 transition group-hover:translate-x-0.5 group-hover:text-brand-500"
                    aria-label="Open session timeline"
                  >
                    <ArrowRightIcon className="h-4 w-4" />
                  </Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Panel>

      <Pager
        page={page}
        pageSize={pageSize}
        total={data?.total ?? 0}
        onChange={setPage}
        onPageSizeChange={(n) => {
          setPageSize(n);
          setPage(0);
        }}
      />
    </div>
  );
}
