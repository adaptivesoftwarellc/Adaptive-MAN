import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import type { ErrorRowDto } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { ERROR_CATEGORIES, errorCategory, errorCategoryLabel } from '../lib/catalog';
import { usePageSize, PAGE_SIZE_OPTIONS } from '../lib/usePageSize';
import { Badge, EmptyState, Modal, PageHeader, Panel } from '../components/ui';
import type { BadgeColor } from '../components/ui';
import { AlertTriangleIcon, ChevronLeftIcon, ChevronRightIcon, WifiOffIcon } from '../components/icons';

export function ErrorsPage() {
  const { filters, ready } = useFilters();
  // Stable window across renders — see HealthPage note. Prevents an infinite refetch loop.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);
  const [params] = useSearchParams();
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePageSize();
  const [sort, setSort] = useState<'last_seen_at' | 'occurrence_count'>('last_seen_at');
  // Seed from the URL so Health cards can deep-link to a category (e.g. ?category=server).
  const [category, setCategory] = useState(params.get('category') ?? '');
  const [selected, setSelected] = useState<ErrorRowDto | null>(null);

  const { data, isLoading, isError, refetch } = useQuery({
    enabled: ready,
    queryKey: ['errors', filters.app, filters.env, range.from, range.to, page, pageSize, sort, category],
    queryFn: () =>
      api.errors({
        app: filters.app,
        env: filters.env,
        from: range.from,
        to: range.to,
        page,
        pageSize,
        sort,
        category: category || undefined,
      }),
  });

  if (!ready) {
    return (
      <div className="p-6">
        <PageHeader title="Errors" description="Grouped error occurrences for the selected window." />
        <EmptyState icon={<AlertTriangleIcon className="h-5 w-5" />} title="No app selected" description="Pick an app and environment above." />
      </div>
    );
  }

  return (
    <div className="p-6">
      <PageHeader title="Errors" description="Grouped error occurrences for the selected window." />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <select
          className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400"
          value={category}
          onChange={(e) => {
            setCategory(e.target.value);
            setPage(0);
          }}
        >
          <option value="">All errors</option>
          {ERROR_CATEGORIES.map((cat) => (
            <option key={cat.value} value={cat.value}>
              {cat.label}
            </option>
          ))}
        </select>
        <select
          className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400"
          value={sort}
          onChange={(e) => {
            setSort(e.target.value as typeof sort);
            setPage(0);
          }}
        >
          <option value="last_seen_at">Sort: Last seen</option>
          <option value="occurrence_count">Sort: Occurrence count</option>
        </select>
      </div>

      <Panel className="overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-200 bg-slate-50/80 text-left text-xs uppercase tracking-wider text-slate-500">
              <Th>Type</Th>
              <Th>Route / Job</Th>
              <Th>Count</Th>
              <Th>Last seen</Th>
              <Th>Release</Th>
              <Th>Status</Th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {isLoading && <SkeletonRows />}
            {isError && (
              <tr>
                <td colSpan={6}>
                  <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load errors" onRetry={() => refetch()} />
                </td>
              </tr>
            )}
            {data?.rows.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <EmptyState icon={<AlertTriangleIcon className="h-5 w-5" />} title="No errors in range" description="Nothing broke in this window — nice." />
                </td>
              </tr>
            )}
            {data?.rows.map((r) => (
              <tr key={r.id} className="cursor-pointer transition hover:bg-slate-50" onClick={() => setSelected(r)}>
                <Td>
                  <div className="flex flex-col gap-0.5">
                    <Badge color={categoryColor(errorCategory(r))} className="w-fit">
                      {errorCategoryLabel(errorCategory(r))}
                    </Badge>
                    <span className="font-mono text-[10px] text-slate-400">{r.error_type}</span>
                  </div>
                </Td>
                <Td className="font-medium text-slate-700">{r.endpoint_group ?? r.job_name ?? r.normalized_route ?? '—'}</Td>
                <Td className="tabular-nums font-medium text-slate-700">{r.occurrence_count.toLocaleString()}</Td>
                <Td className="text-slate-500">{new Date(r.last_seen_at).toLocaleString()}</Td>
                <Td>
                  <span className="font-mono text-xs text-slate-500">{r.release_sha ?? '—'}</span>
                </Td>
                <Td>{r.http_status_code ? <Badge color={statusColor(r.http_status_code)}>{r.http_status_code}</Badge> : '—'}</Td>
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

      {selected && <ErrorDetailModal row={selected} onClose={() => setSelected(null)} />}
    </div>
  );
}

function Th({ children }: { children: React.ReactNode }) {
  return <th className="px-4 py-2.5 font-semibold">{children}</th>;
}
function Td({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <td className={`px-4 py-2.5 ${className}`}>{children}</td>;
}

function categoryColor(category: string): BadgeColor {
  if (category === 'server') return 'red';
  if (category === 'frontend') return 'purple';
  if (category === 'background_job') return 'blue';
  return 'gray';
}
function statusColor(code: number): BadgeColor {
  if (code >= 500) return 'red';
  if (code >= 400) return 'amber';
  return 'gray';
}

function SkeletonRows() {
  return (
    <>
      {Array.from({ length: 8 }).map((_, i) => (
        <tr key={i}>
          {Array.from({ length: 6 }).map((__, j) => (
            <td key={j} className="px-4 py-3">
              <div className="shimmer h-3 w-full max-w-[8rem] rounded bg-slate-200/70" />
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}

export function Pager({
  page,
  pageSize,
  total,
  onChange,
  onPageSizeChange,
}: {
  page: number;
  pageSize: number;
  total: number;
  onChange: (p: number) => void;
  onPageSizeChange?: (size: number) => void;
}) {
  const last = Math.max(0, Math.ceil(total / pageSize) - 1);
  return (
    <div className="mt-3 flex items-center justify-between text-xs text-slate-500">
      <div className="flex items-center gap-3">
        {onPageSizeChange && (
          <label className="flex items-center gap-1.5 font-medium">
            Rows per page
            <select
              value={pageSize}
              onChange={(e) => onPageSizeChange(Number(e.target.value))}
              className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-xs font-semibold text-slate-900 shadow-sm transition hover:border-slate-400"
            >
              {PAGE_SIZE_OPTIONS.map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
        )}
        <span>
          <span className="font-medium text-slate-700">{total.toLocaleString()}</span> total · page {page + 1} / {last + 1}
        </span>
      </div>
      <div className="flex gap-2">
        <button
          className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 font-medium text-slate-600 shadow-sm transition hover:bg-slate-50 disabled:opacity-40"
          disabled={page === 0}
          onClick={() => onChange(page - 1)}
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" /> Prev
        </button>
        <button
          className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 font-medium text-slate-600 shadow-sm transition hover:bg-slate-50 disabled:opacity-40"
          disabled={page >= last}
          onClick={() => onChange(page + 1)}
        >
          Next <ChevronRightIcon className="h-3.5 w-3.5" />
        </button>
      </div>
    </div>
  );
}

function ErrorDetailModal({ row, onClose }: { row: ErrorRowDto; onClose: () => void }) {
  return (
    <Modal
      size="sm"
      onClose={onClose}
      header={
        <>
          <div className="text-xs font-medium uppercase tracking-wider text-slate-400">Error detail</div>
          <h3 className="mt-0.5 flex items-center gap-2 text-lg font-semibold text-slate-900">
            <Badge color={categoryColor(errorCategory(row))}>{errorCategoryLabel(errorCategory(row))}</Badge>
            <span className="font-mono text-sm font-normal text-slate-500">{row.error_type}</span>
          </h3>
        </>
      }
    >
      <dl className="space-y-1 px-6 py-4 text-sm">
        <Row label="Error type" value={row.error_type} />
        <Row label="Fingerprint" value={row.fingerprint} />
        <Row label="Exception type" value={row.exception_type} />
        <Row label="Endpoint group" value={row.endpoint_group} />
        <Row label="Job name" value={row.job_name} />
        <Row label="Normalized route" value={row.normalized_route} />
        <Row label="HTTP status" value={row.http_status_code?.toString()} />
        <Row label="Release SHA" value={row.release_sha} />
        <Row label="First seen" value={new Date(row.first_seen_at).toLocaleString()} />
        <Row label="Last seen" value={new Date(row.last_seen_at).toLocaleString()} />
        <Row label="Occurrences" value={row.occurrence_count.toLocaleString()} />
        <Row label="Last correlation ID" value={row.last_correlation_id} />
      </dl>
      <p className="mx-6 mb-6 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-400">
        Per privacy rules, no exception messages or stack traces are stored or shown.
      </p>
    </Modal>
  );
}

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="flex justify-between gap-4 border-b border-slate-100 py-1.5">
      <dt className="text-slate-500">{label}</dt>
      <dd className="truncate text-right font-mono text-xs text-slate-800">{value ?? '—'}</dd>
    </div>
  );
}
