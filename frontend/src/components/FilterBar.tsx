import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, USE_MOCKS } from '../lib/api';
import { useFilters } from '../lib/filters';
import type { RangePreset } from '../lib/filters';
import { Badge } from './ui';
import { ChevronDownIcon, WifiOffIcon, BeakerIcon, CalendarIcon } from './icons';

const RANGES: { value: RangePreset; label: string }[] = [
  { value: '1h', label: '1h' },
  { value: '24h', label: '24h' },
  { value: '7d', label: '7d' },
  { value: 'custom', label: 'Custom' },
];

export function FilterBar() {
  const { filters, setFilters } = useFilters();
  const appsQuery = useQuery({ queryKey: ['apps'], queryFn: api.apps });

  // Resolve the selected app/env against the real apps list, and default when nothing is set.
  // Preset ("Quick view") links use the app *slug* and env *name* (stable across deployments);
  // the live API keys off GUIDs, so we resolve slug->id / name->id here. With mock data the
  // ids equal the slug/name, so the exact-id match wins and the fallbacks are no-ops.
  useEffect(() => {
    const apps = appsQuery.data;
    if (!apps || apps.length === 0) return;

    let app = apps.find((a) => a.id === filters.app);
    if (!app && filters.app) app = apps.find((a) => a.slug === filters.app);
    if (!app) app = apps[0];

    let env = app.environments.find((e) => e.id === filters.env);
    if (!env && filters.env) env = app.environments.find((e) => e.name === filters.env);
    const envId = env?.id ?? app.environments[0]?.id ?? '';

    if (app.id !== filters.app || envId !== filters.env) {
      setFilters({ app: app.id, env: envId });
    }
  }, [appsQuery.data, filters.app, filters.env, setFilters]);

  const selectedApp = appsQuery.data?.find((a) => a.id === filters.app);

  return (
    <div className="sticky top-0 z-20 flex flex-wrap items-center gap-x-6 gap-y-3 border-b border-slate-200 bg-white px-6 py-3.5 shadow-sm">
      <Field label="App">
        <Select
          value={filters.app}
          onChange={(value) => {
            const next = appsQuery.data?.find((a) => a.id === value);
            setFilters({ app: value, env: next?.environments[0]?.id ?? '' });
          }}
          disabled={appsQuery.isLoading || !appsQuery.data?.length}
        >
          {!appsQuery.data?.length && <option>{appsQuery.isLoading ? 'loading…' : 'no apps'}</option>}
          {appsQuery.data?.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </Select>
      </Field>

      <Field label="Environment">
        <Select value={filters.env} onChange={(value) => setFilters({ env: value })} disabled={!selectedApp}>
          {!selectedApp?.environments.length && <option>—</option>}
          {selectedApp?.environments.map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
        </Select>
      </Field>

      <Field label="Time range">
        <Segmented
          value={filters.range}
          options={RANGES}
          onChange={(value) => setFilters({ range: value })}
        />
      </Field>

      {filters.range === 'custom' && (
        <CustomRangeDropdown
          from={filters.from}
          to={filters.to}
          onChange={(next) => setFilters(next)}
        />
      )}

      <div className="ml-auto">
        {USE_MOCKS ? (
          <Badge color="amber">
            <BeakerIcon className="h-3 w-3" /> Demo data
          </Badge>
        ) : appsQuery.isError ? (
          <Badge color="red">
            <WifiOffIcon className="h-3 w-3" /> Backend unreachable
          </Badge>
        ) : (
          <Badge color="green">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" /> Live
          </Badge>
        )}
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex items-center gap-2">
      <span className="text-[11px] font-semibold uppercase tracking-wider text-slate-600">{label}</span>
      {children}
    </label>
  );
}

function Select({
  value,
  onChange,
  disabled,
  children,
}: {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className="relative">
      <select
        className="appearance-none rounded-lg border border-slate-300 bg-white py-2 pl-3 pr-8 text-sm font-semibold text-slate-900 shadow-sm transition hover:border-slate-400 disabled:cursor-not-allowed disabled:opacity-50"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
      >
        {children}
      </select>
      <ChevronDownIcon className="pointer-events-none absolute right-2 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-500" />
    </div>
  );
}

function Segmented<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: { value: T; label: string }[];
  onChange: (value: T) => void;
}) {
  return (
    <div className="inline-flex rounded-lg border border-slate-300 bg-slate-100 p-0.5 shadow-sm">
      {options.map((o) => (
        <button
          key={o.value}
          onClick={() => onChange(o.value)}
          className={`rounded-md px-2.5 py-1 text-xs font-semibold transition ${
            value === o.value
              ? 'bg-white text-brand-700 shadow-sm ring-1 ring-slate-200'
              : 'text-slate-600 hover:bg-white/60 hover:text-slate-900'
          }`}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

function DateInput({
  value,
  onChange,
  fullWidth,
}: {
  value?: string;
  onChange: (v: string | undefined) => void;
  fullWidth?: boolean;
}) {
  return (
    <input
      type="datetime-local"
      className={`rounded-lg border border-slate-300 bg-white px-2.5 py-2 text-sm font-medium text-slate-900 shadow-sm transition hover:border-slate-400 ${
        fullWidth ? 'w-full' : ''
      }`}
      value={toLocalInput(value)}
      onChange={(e) => onChange(fromLocalInput(e.target.value))}
    />
  );
}

function summarizeRange(from?: string, to?: string): string {
  if (!from && !to) return 'Select dates';
  const fmt = (iso?: string) =>
    iso ? new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : 'Any';
  return `${fmt(from)} → ${fmt(to)}`;
}

function CustomRangeDropdown({
  from,
  to,
  onChange,
}: {
  from?: string;
  to?: string;
  onChange: (next: { from?: string; to?: string }) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const hasRange = !!(from || to);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((o) => !o)}
        className="flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold shadow-sm transition hover:border-slate-400"
        aria-expanded={open}
      >
        <CalendarIcon className="h-4 w-4 text-slate-500" />
        <span className={hasRange ? 'text-slate-900' : 'text-slate-500'}>{summarizeRange(from, to)}</span>
        <ChevronDownIcon className={`h-4 w-4 text-slate-500 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && (
        <div className="absolute left-0 top-full z-30 mt-2 w-72 animate-fade-in rounded-xl border border-slate-200 bg-white p-3 shadow-lg">
          <label className="block">
            <span className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-slate-500">From</span>
            <DateInput value={from} onChange={(v) => onChange({ from: v })} fullWidth />
          </label>
          <label className="mt-2.5 block">
            <span className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-slate-500">To</span>
            <DateInput value={to} onChange={(v) => onChange({ to: v })} fullWidth />
          </label>
          <div className="mt-3 flex items-center justify-between">
            <button
              onClick={() => onChange({ from: undefined, to: undefined })}
              disabled={!hasRange}
              className="text-xs font-medium text-slate-500 transition hover:text-slate-700 disabled:opacity-40"
            >
              Clear
            </button>
            <button
              onClick={() => setOpen(false)}
              className="rounded-lg bg-brand-600 px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-brand-500"
            >
              Done
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function toLocalInput(iso?: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function fromLocalInput(value: string): string | undefined {
  return value ? new Date(value).toISOString() : undefined;
}
