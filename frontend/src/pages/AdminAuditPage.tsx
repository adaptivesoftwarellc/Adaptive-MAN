import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, type AuditRowDto } from '../lib/api';
import { Badge, EmptyState, PageHeader, Panel, Skeleton } from '../components/ui';
import { ListIcon, WifiOffIcon } from '../components/icons';

const PAGE_SIZE = 50;

function fmt(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

function actorColor(actorType: string): 'indigo' | 'amber' | 'gray' {
  if (actorType === 'admin_user') return 'indigo';
  if (actorType === 'admin_key') return 'amber';
  return 'gray';
}

export function AdminAuditPage() {
  const [actionFilter, setActionFilter] = useState('');
  const [action, setAction] = useState('');
  const [page, setPage] = useState(0);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['admin-audit', action, page],
    queryFn: () => api.audit({ action: action || undefined, page, pageSize: PAGE_SIZE }),
  });

  const total = data?.total ?? 0;
  const maxPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1);

  const applyFilter = () => {
    setPage(0);
    setAction(actionFilter.trim());
  };

  return (
    <div className="p-6">
      <PageHeader
        title="Audit log"
        description="Every admin action and privileged dashboard read. Read-only (Issue 8.7)."
      />

      <Panel className="mb-4 p-3">
        <div className="flex flex-wrap items-end gap-2">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-slate-700">Action</span>
            <input
              value={actionFilter}
              onChange={(e) => setActionFilter(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && applyFilter()}
              placeholder="e.g. admin.key.revoked"
              className="w-64 rounded-lg border border-slate-300 px-3 py-2 font-mono text-xs text-slate-900 shadow-sm outline-none focus:border-brand-400 focus:ring-2 focus:ring-brand-100"
            />
          </label>
          <button
            onClick={applyFilter}
            className="rounded-lg bg-slate-800 px-3 py-2 text-sm font-medium text-white transition hover:bg-slate-700"
          >
            Filter
          </button>
          {action && (
            <button
              onClick={() => {
                setActionFilter('');
                setAction('');
                setPage(0);
              }}
              className="rounded-lg px-3 py-2 text-sm font-medium text-slate-500 transition hover:bg-slate-100"
            >
              Clear
            </button>
          )}
        </div>
      </Panel>

      {isError && (
        <Panel>
          <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load audit log" onRetry={() => refetch()} />
        </Panel>
      )}

      <Panel className="overflow-hidden">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-4 py-2.5 font-medium">When</th>
              <th className="px-4 py-2.5 font-medium">Action</th>
              <th className="px-4 py-2.5 font-medium">Actor</th>
              <th className="px-4 py-2.5 font-medium">Details</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {isLoading &&
              Array.from({ length: 8 }).map((_, i) => (
                <tr key={i}>
                  <td className="px-4 py-3" colSpan={4}>
                    <Skeleton className="h-4 w-full" />
                  </td>
                </tr>
              ))}

            {data?.rows.map((row: AuditRowDto) => (
              <tr key={row.id} className="align-top hover:bg-slate-50/60">
                <td className="whitespace-nowrap px-4 py-3 text-slate-600">{fmt(row.occurred_at)}</td>
                <td className="px-4 py-3 font-mono text-xs text-slate-800">{row.action}</td>
                <td className="px-4 py-3">
                  <Badge color={actorColor(row.actor_type)}>{row.actor_type}</Badge>
                </td>
                <td className="px-4 py-3">
                  <code className="block max-w-md overflow-x-auto whitespace-pre-wrap break-words text-xs text-slate-500">
                    {row.details_json}
                  </code>
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {data && data.rows.length === 0 && !isLoading && (
          <EmptyState icon={<ListIcon className="h-5 w-5" />} title="No audit entries" description="No rows match the current filter." />
        )}
      </Panel>

      {total > PAGE_SIZE && (
        <div className="mt-4 flex items-center justify-between text-sm text-slate-500">
          <span>
            Page {page + 1} of {maxPage + 1} · {total} entries
          </span>
          <div className="flex gap-2">
            <button
              onClick={() => setPage((p) => Math.max(0, p - 1))}
              disabled={page === 0}
              className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 font-medium text-slate-700 shadow-sm transition hover:bg-slate-50 disabled:opacity-40"
            >
              Previous
            </button>
            <button
              onClick={() => setPage((p) => Math.min(maxPage, p + 1))}
              disabled={page >= maxPage}
              className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 font-medium text-slate-700 shadow-sm transition hover:bg-slate-50 disabled:opacity-40"
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
