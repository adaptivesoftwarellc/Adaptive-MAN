import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import type { EventRowDto } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { Pager } from './ErrorsPage';
import { EVENT_NAMES } from '../lib/catalog';
import { usePageSize } from '../lib/usePageSize';
import { Badge, EmptyState, Modal, PageHeader, Panel } from '../components/ui';
import { DownloadIcon, InboxIcon, SearchIcon, WifiOffIcon } from '../components/icons';

export function EventsPage() {
  const { filters, ready } = useFilters();
  // Stable window across renders — see HealthPage note. Prevents an infinite refetch loop.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);
  const [params] = useSearchParams();

  const initialEventName = params.get('event_name') ?? '';
  const [eventName, setEventName] = useState(initialEventName);
  const [distinctId, setDistinctId] = useState('');
  const [correlationId, setCorrelationId] = useState('');
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePageSize();
  const [selected, setSelected] = useState<EventRowDto | null>(null);

  const { data, isLoading, isError, refetch } = useQuery({
    enabled: ready,
    queryKey: ['events', filters.app, filters.env, range.from, range.to, eventName, distinctId, correlationId, page, pageSize],
    queryFn: () =>
      api.events({
        app: filters.app,
        env: filters.env,
        from: range.from,
        to: range.to,
        page,
        pageSize,
        event_name: eventName || undefined,
        distinct_id: distinctId || undefined,
        correlation_id: correlationId || undefined,
      }),
  });

  if (!ready) {
    return (
      <div className="p-6">
        <PageHeader title="Events" description="Raw event stream for the selected window." />
        <EmptyState icon={<InboxIcon className="h-5 w-5" />} title="No app selected" description="Pick an app and environment above." />
      </div>
    );
  }

  return (
    <div className="p-6">
      <PageHeader
        title="Events"
        description="Raw event stream for the selected window."
        actions={
          <button
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 shadow-sm transition hover:bg-slate-50 disabled:opacity-50"
            onClick={() => exportCsv(data?.rows ?? [])}
            disabled={!data?.rows.length}
          >
            <DownloadIcon className="h-3.5 w-3.5" /> Export CSV
          </button>
        }
      />

      <div className="mb-3 flex flex-wrap gap-2">
        <select
          value={eventName}
          onChange={(e) => {
            setEventName(e.target.value);
            setPage(0);
          }}
          className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400"
        >
          <option value="">All events</option>
          {EVENT_NAMES.map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
        <SearchInput placeholder="distinct_id" value={distinctId} onChange={(v) => { setDistinctId(v); setPage(0); }} />
        <SearchInput placeholder="correlation_id" value={correlationId} onChange={(v) => { setCorrelationId(v); setPage(0); }} />
      </div>

      <Panel className="overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-200 bg-slate-50/80 text-left text-xs uppercase tracking-wider text-slate-500">
              <Th>Time</Th>
              <Th>Event</Th>
              <Th>Distinct ID</Th>
              <Th>Route</Th>
              <Th>Feature</Th>
              <Th>Release</Th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {isLoading && <SkeletonRows />}
            {isError && (
              <tr>
                <td colSpan={6}>
                  <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load events" onRetry={() => refetch()} />
                </td>
              </tr>
            )}
            {data?.rows.length === 0 && (
              <tr>
                <td colSpan={6}>
                  <EmptyState icon={<InboxIcon className="h-5 w-5" />} title="No events match" description="Try clearing the filters above." />
                </td>
              </tr>
            )}
            {data?.rows.map((r) => (
              <tr key={r.id} className="cursor-pointer transition hover:bg-slate-50" onClick={() => setSelected(r)}>
                <Td className="whitespace-nowrap text-slate-500">{new Date(r.created_at).toLocaleString()}</Td>
                <Td>
                  <Badge color={eventColor(r.event_name)}>{r.event_name}</Badge>
                </Td>
                <Td className="font-mono text-xs text-slate-600">{r.distinct_id}</Td>
                <Td className="text-xs text-slate-600">{r.normalized_route ?? r.endpoint_group ?? '—'}</Td>
                <Td className="text-xs text-slate-500">{r.feature_area ?? '—'}</Td>
                <Td className="font-mono text-xs text-slate-500">{r.release_sha ?? '—'}</Td>
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

      {selected && <EventDetail row={selected} onClose={() => setSelected(null)} />}
    </div>
  );
}

function Th({ children }: { children: React.ReactNode }) {
  return <th className="px-4 py-2.5 font-semibold">{children}</th>;
}
function Td({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <td className={`px-4 py-2.5 ${className}`}>{children}</td>;
}

function eventColor(name: string): 'red' | 'amber' | 'green' | 'indigo' | 'gray' {
  if (name.includes('error') || name.includes('exception')) return 'red';
  if (name.includes('fail')) return 'amber';
  if (name.includes('login') || name.includes('booked')) return 'green';
  if (name.includes('page') || name.includes('view')) return 'indigo';
  return 'gray';
}

function SearchInput({ placeholder, value, onChange }: { placeholder: string; value: string; onChange: (v: string) => void }) {
  return (
    <div className="relative">
      <SearchIcon className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
      <input
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-52 rounded-lg border border-slate-200 bg-white py-1.5 pl-8 pr-3 text-sm shadow-sm transition placeholder:text-slate-400 hover:border-slate-300"
      />
    </div>
  );
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

function EventDetail({ row, onClose }: { row: EventRowDto; onClose: () => void }) {
  let pretty = row.properties_json;
  try {
    pretty = JSON.stringify(JSON.parse(row.properties_json), null, 2);
  } catch {
    /* keep raw */
  }
  return (
    <Modal
      size="md"
      onClose={onClose}
      header={
        <>
          <div className="text-xs font-medium uppercase tracking-wider text-slate-400">Event detail</div>
          <h3 className="mt-0.5 flex items-center gap-2 text-lg font-semibold text-slate-900">
            <Badge color={eventColor(row.event_name)}>{row.event_name}</Badge>
          </h3>
        </>
      }
    >
      <dl className="grid grid-cols-2 gap-x-4 gap-y-2 px-6 py-4 text-xs">
        <Field label="Distinct ID" value={row.distinct_id} />
        <Field label="Session ID" value={row.session_id} />
        <Field label="Correlation ID" value={row.correlation_id} />
        <Field label="Endpoint group" value={row.endpoint_group} />
        <Field label="Normalized route" value={row.normalized_route} />
        <Field label="Feature area" value={row.feature_area} />
        <Field label="Release SHA" value={row.release_sha} />
        <Field label="Occurred at" value={new Date(row.occurred_at).toLocaleString()} />
      </dl>
      <div className="px-6 pb-6">
        <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wider text-slate-500">Properties</h4>
        <pre className="max-h-96 overflow-auto rounded-lg bg-slate-900 p-3 text-xs leading-relaxed text-slate-100 scrollbar-thin">
          {pretty}
        </pre>
      </div>
    </Modal>
  );
}

function Field({ label, value }: { label: string; value: string | null | undefined }) {
  return (
    <div className="flex flex-col gap-0.5 border-b border-slate-100 pb-1.5">
      <dt className="text-slate-400">{label}</dt>
      <dd className="truncate font-mono text-slate-800">{value ?? '—'}</dd>
    </div>
  );
}

function exportCsv(rows: EventRowDto[]) {
  if (!rows.length) return;
  const cols: (keyof EventRowDto)[] = [
    'created_at', 'occurred_at', 'event_name', 'distinct_id', 'session_id',
    'correlation_id', 'normalized_route', 'endpoint_group', 'feature_area',
    'release_sha', 'properties_json',
  ];
  const csv = [cols.join(',')]
    .concat(rows.map((r) => cols.map((c) => csvEscape(r[c])).join(',')))
    .join('\n');
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `events-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.csv`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

function csvEscape(v: unknown): string {
  if (v === null || v === undefined) return '';
  const s = String(v);
  if (/[",\n\r]/.test(s)) return `"${s.replace(/"/g, '""')}"`;
  return s;
}
