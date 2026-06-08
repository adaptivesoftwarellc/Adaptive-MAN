import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api, type TimelineEntry } from '../lib/api';
import { Badge, EmptyState, Panel } from '../components/ui';
import type { BadgeColor } from '../components/ui';
import { ChevronLeftIcon, ClockIcon, WifiOffIcon } from '../components/icons';

export function SessionTimelinePage() {
  const { sessionId = '' } = useParams<{ sessionId: string }>();
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [selected, setSelected] = useState<TimelineEntry | null>(null);

  const { data, isLoading, error, refetch } = useQuery({
    enabled: !!sessionId,
    queryKey: ['session-timeline', sessionId],
    queryFn: () => api.sessionTimeline(sessionId),
  });

  if (isLoading) return <div className="p-6 text-sm text-slate-500">Loading…</div>;
  if (error)
    return (
      <div className="p-6">
        <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load timeline" onRetry={() => refetch()} />
      </div>
    );
  if (!data) return null;

  const entries = errorsOnly
    ? data.entries.filter((e) => e.kind === 'error' || e.is_api_failure === true)
    : data.entries;
  const sessionEnd = data.session.ended_at;

  return (
    <div className="p-6">
      <div className="mb-4 flex items-center gap-2">
        <Link
          to="/sessions"
          className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-medium text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
        >
          <ChevronLeftIcon className="h-3.5 w-3.5" /> Sessions
        </Link>
        <h1 className="text-xl font-semibold tracking-tight text-slate-900">Session timeline</h1>
        {data.session.has_error ? <Badge color="red">has errors</Badge> : <Badge color="green">clean</Badge>}
      </div>

      <Panel className="mb-4 p-4">
        <div className="grid grid-cols-2 gap-4 text-sm lg:grid-cols-4">
          <Field label="Session ID" value={data.session.session_id} mono />
          <Field label="User" value={data.session.distinct_id} mono />
          <Field label="Started" value={new Date(data.session.started_at).toLocaleString()} />
          <Field label="Ended" value={sessionEnd ? new Date(sessionEnd).toLocaleString() : '—'} />
          <Field label="Last seen" value={new Date(data.session.last_seen_at).toLocaleString()} />
          <Field label="Release" value={data.session.release_sha ?? '—'} mono />
        </div>
      </Panel>

      <label className="mb-4 inline-flex cursor-pointer items-center gap-2 text-sm text-slate-600">
        <input
          type="checkbox"
          checked={errorsOnly}
          onChange={(e) => setErrorsOnly(e.target.checked)}
          className="h-4 w-4 rounded border-slate-300 text-brand-600 focus:ring-brand-500"
        />
        Errors only
      </label>

      <div className="grid grid-cols-12 gap-4">
        <div className="col-span-12 lg:col-span-8">
          {entries.length === 0 ? (
            <Panel>
              <EmptyState icon={<ClockIcon className="h-5 w-5" />} title="No entries match" />
            </Panel>
          ) : (
            <ol className="relative ml-1 border-l-2 border-slate-200 pl-6">
              {entries.map((entry, idx) => (
                <li
                  key={`${entry.kind}-${entry.id}-${idx}`}
                  className={`relative mb-3 cursor-pointer rounded-xl border bg-white p-3 text-sm shadow-card transition hover:shadow-card-hover ${
                    selected === entry ? 'border-brand-300 ring-2 ring-brand-200' : 'border-slate-200'
                  }`}
                  onClick={() => setSelected(entry)}
                >
                  <span
                    className={`absolute -left-[31px] top-3.5 h-3 w-3 rounded-full border-2 border-white ring-2 ring-slate-100 ${dotColor(entry)}`}
                  />
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <KindBadge entry={entry} />
                      <span className="font-mono text-xs text-slate-400">
                        {new Date(entry.occurred_at).toLocaleTimeString()}
                      </span>
                    </div>
                    {'correlation_id' in entry && entry.correlation_id && (
                      <span className="font-mono text-[10px] text-slate-400">{entry.correlation_id}</span>
                    )}
                  </div>
                  <div className="mt-1 font-medium text-slate-800">{summary(entry)}</div>
                </li>
              ))}
            </ol>
          )}
        </div>

        <div className="col-span-12 lg:col-span-4">
          <Panel className="sticky top-20 p-4">
            <h3 className="mb-2 text-xs font-semibold uppercase tracking-wider text-slate-500">Details</h3>
            {selected ? (
              <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words rounded-lg bg-slate-900 p-3 text-xs leading-relaxed text-slate-100 scrollbar-thin">
                {JSON.stringify(selected, null, 2)}
              </pre>
            ) : (
              <p className="text-xs text-slate-400">Click an entry to inspect its payload.</p>
            )}
          </Panel>
        </div>
      </div>
    </div>
  );
}

function Field({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <div className="text-[11px] font-medium uppercase tracking-wider text-slate-400">{label}</div>
      <div className={`mt-0.5 ${mono ? 'truncate font-mono text-xs text-slate-700' : 'text-slate-700'}`}>{value}</div>
    </div>
  );
}

function KindBadge({ entry }: { entry: TimelineEntry }) {
  if (entry.kind === 'error') {
    const color: BadgeColor = entry.source === 'cross_process' ? 'purple' : 'red';
    return <Badge color={color}>{entry.source === 'cross_process' ? 'be error' : 'error'}</Badge>;
  }
  if (entry.is_api_failure) return <Badge color="amber">api failure</Badge>;
  return <Badge color="gray">event</Badge>;
}

function dotColor(entry: TimelineEntry): string {
  if (entry.kind === 'error') return entry.source === 'cross_process' ? 'bg-purple-500' : 'bg-rose-500';
  if (entry.is_api_failure) return 'bg-amber-500';
  return 'bg-slate-400';
}

function summary(entry: TimelineEntry): string {
  if (entry.kind === 'event') {
    return entry.normalized_route ? `${entry.event_name} — ${entry.normalized_route}` : entry.event_name;
  }
  const parts = [entry.error_type];
  if (entry.exception_type) parts.push(entry.exception_type);
  if (entry.endpoint_group) parts.push(entry.endpoint_group);
  if (entry.http_status_code) parts.push(String(entry.http_status_code));
  return parts.join(' • ');
}
