import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import type { AlertRowDto, AlertRuleTypeName } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { Pager } from './ErrorsPage';
import { usePageSize } from '../lib/usePageSize';
import { Badge, EmptyState, Modal, PageHeader, Panel } from '../components/ui';
import { BellIcon, InboxIcon, WifiOffIcon } from '../components/icons';

const RULE_TYPES: { value: AlertRuleTypeName; label: string }[] = [
  { value: 'CountOverWindow', label: 'Count over window' },
  { value: 'NewErrorAfterRelease', label: 'New error after release' },
  { value: 'ErrorRateAboveThreshold', label: 'Error rate above threshold' },
  { value: 'AnyProdJobFailure', label: 'Any prod job failure' },
];

const RULE_TYPE_LABEL: Record<AlertRuleTypeName, string> = Object.fromEntries(
  RULE_TYPES.map((t) => [t.value, t.label]),
) as Record<AlertRuleTypeName, string>;

export function AlertsPage() {
  const { filters, ready } = useFilters();
  // Stable window across renders — see HealthPage note. Prevents an infinite refetch loop.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);

  const [ruleType, setRuleType] = useState('');
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePageSize();
  const [selected, setSelected] = useState<AlertRowDto | null>(null);

  const { data, isLoading, isError, refetch } = useQuery({
    enabled: ready,
    queryKey: ['alerts', filters.app, filters.env, range.from, range.to, ruleType, page, pageSize],
    queryFn: () =>
      api.alerts({
        app: filters.app,
        env: filters.env,
        from: range.from,
        to: range.to,
        page,
        pageSize,
        rule_type: ruleType || undefined,
      }),
  });

  if (!ready) {
    return (
      <div className="p-6">
        <PageHeader title="Alerts" description="Fired alerts for the selected window." />
        <EmptyState icon={<InboxIcon className="h-5 w-5" />} title="No app selected" description="Pick an app and environment above." />
      </div>
    );
  }

  return (
    <div className="p-6">
      <PageHeader title="Alerts" description="Fired alerts for the selected window." />

      {/* The alert engine is visibility-only until notification delivery (8.4) lands. */}
      <div className="mb-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
        Visibility-only: alerts are recorded here for review. Email / Teams delivery arrives with Issue 8.4.
      </div>

      <div className="mb-3 flex flex-wrap gap-2">
        <select
          value={ruleType}
          onChange={(e) => {
            setRuleType(e.target.value);
            setPage(0);
          }}
          className="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400"
        >
          <option value="">All rule types</option>
          {RULE_TYPES.map((t) => (
            <option key={t.value} value={t.value}>
              {t.label}
            </option>
          ))}
        </select>
      </div>

      <Panel className="overflow-hidden">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-slate-200 bg-slate-50/80 text-left text-xs uppercase tracking-wider text-slate-500">
              <Th>Fired</Th>
              <Th>Rule</Th>
              <Th>Type</Th>
              <Th>Summary</Th>
              <Th>Observed</Th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {isLoading && <SkeletonRows />}
            {isError && (
              <tr>
                <td colSpan={5}>
                  <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load alerts" onRetry={() => refetch()} />
                </td>
              </tr>
            )}
            {!isError && data?.rows.length === 0 && (
              <tr>
                <td colSpan={5}>
                  <EmptyState icon={<BellIcon className="h-5 w-5" />} title="No alerts fired" description="Nothing tripped a rule in this window." />
                </td>
              </tr>
            )}
            {!isError && data?.rows.map((a) => (
              <tr key={a.id} className="cursor-pointer transition hover:bg-slate-50" onClick={() => setSelected(a)}>
                <Td className="whitespace-nowrap text-slate-500">{new Date(a.fired_at).toLocaleString()}</Td>
                <Td className="text-slate-700">{a.rule_name ?? '—'}</Td>
                <Td>
                  <Badge color={ruleColor(a.rule_type)}>{RULE_TYPE_LABEL[a.rule_type] ?? a.rule_type}</Badge>
                </Td>
                <Td className="text-xs text-slate-600">{a.summary}</Td>
                <Td className="whitespace-nowrap font-mono text-xs text-slate-600">
                  {formatObserved(a)}
                </Td>
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

      {selected && <AlertDetail row={selected} onClose={() => setSelected(null)} />}
    </div>
  );
}

function Th({ children }: { children: React.ReactNode }) {
  return <th className="px-4 py-2.5 font-semibold">{children}</th>;
}
function Td({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <td className={`px-4 py-2.5 ${className}`}>{children}</td>;
}

function ruleColor(type: AlertRuleTypeName): 'red' | 'amber' | 'indigo' | 'gray' {
  switch (type) {
    case 'AnyProdJobFailure':
      return 'red';
    case 'ErrorRateAboveThreshold':
      return 'amber';
    case 'NewErrorAfterRelease':
      return 'indigo';
    default:
      return 'gray';
  }
}

// Rate rules read as percentages; the rest are raw counts compared to a threshold (0 = presence-only).
function formatObserved(a: AlertRowDto): string {
  if (a.rule_type === 'ErrorRateAboveThreshold') return `${a.observed_value}% / ${a.threshold}%`;
  if (a.threshold > 0) return `${a.observed_value} / ${a.threshold}`;
  return String(a.observed_value);
}

function SkeletonRows() {
  return (
    <>
      {Array.from({ length: 8 }).map((_, i) => (
        <tr key={i}>
          {Array.from({ length: 5 }).map((__, j) => (
            <td key={j} className="px-4 py-3">
              <div className="shimmer h-3 w-full max-w-[8rem] rounded bg-slate-200/70" />
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}

function AlertDetail({ row, onClose }: { row: AlertRowDto; onClose: () => void }) {
  let pretty = row.details_json;
  try {
    pretty = JSON.stringify(JSON.parse(row.details_json), null, 2);
  } catch {
    /* keep raw */
  }
  return (
    <Modal
      size="md"
      onClose={onClose}
      header={
        <>
          <div className="text-xs font-medium uppercase tracking-wider text-slate-400">Alert detail</div>
          <h3 className="mt-0.5 flex items-center gap-2 text-lg font-semibold text-slate-900">
            <Badge color={ruleColor(row.rule_type)}>{RULE_TYPE_LABEL[row.rule_type] ?? row.rule_type}</Badge>
            <span className="truncate">{row.rule_name ?? '—'}</span>
          </h3>
        </>
      }
    >
      <dl className="grid grid-cols-2 gap-x-4 gap-y-2 px-6 py-4 text-xs">
        <Field label="Fired at" value={new Date(row.fired_at).toLocaleString()} />
        <Field label="Observed" value={formatObserved(row)} />
        <Field label="Rule id" value={row.alert_rule_id} />
        <Field label="Environment" value={row.environment_id ?? 'all environments'} />
      </dl>
      <div className="px-6 pb-4">
        <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wider text-slate-500">Summary</h4>
        <p className="text-sm text-slate-700">{row.summary}</p>
      </div>
      <div className="px-6 pb-6">
        <h4 className="mb-1.5 text-xs font-semibold uppercase tracking-wider text-slate-500">Details</h4>
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
