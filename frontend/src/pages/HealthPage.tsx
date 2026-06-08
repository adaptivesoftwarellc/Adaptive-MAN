import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import { resolveRange, useFilters } from '../lib/filters';
import { Card } from '../components/Card';
import { Sparkline } from '../components/Sparkline';
import { EmptyState, Panel, Skeleton } from '../components/ui';
import {
  ServerIcon,
  BugIcon,
  WifiOffIcon,
  ClockIcon,
  EyeIcon,
  KeyIcon,
  AlertTriangleIcon,
} from '../components/icons';

export function HealthPage() {
  const { filters, ready } = useFilters();
  // Memoize so the resolved window (which uses `new Date()`) is stable across renders.
  // Without this the queryKey changes every render and the query never settles.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- resolveRange only reads range/from/to
  const range = useMemo(() => resolveRange(filters), [filters.range, filters.from, filters.to]);

  const { data, isLoading, isError, error, refetch } = useQuery({
    enabled: ready,
    queryKey: ['health', filters.app, filters.env, range.from, range.to],
    queryFn: () => api.health({ app: filters.app, env: filters.env, from: range.from, to: range.to }),
    refetchInterval: 30_000,
  });

  if (!ready) {
    return (
      <div className="p-6">
        <Header />
        <EmptyState
          icon={<AlertTriangleIcon className="h-5 w-5" />}
          title="No app selected"
          description="Pick an app and environment from the bar above to view health metrics."
        />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="p-6">
        <Header />
        <EmptyState
          tone="error"
          icon={<WifiOffIcon className="h-5 w-5" />}
          title="Failed to load health"
          description={(error as Error).message}
          onRetry={() => refetch()}
        />
      </div>
    );
  }

  const c = data?.cards;

  return (
    <div className="p-6">
      <Header />

      {/* Errors & failures */}
      <SectionTitle>Errors &amp; failures</SectionTitle>
      <div className="grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-4">
        {data && c ? (
          <>
            <Card
              title="Backend 500s"
              total={c.backend_500s}
              to="/errors?category=server"
              icon={<ServerIcon />}
              accent="red"
            >
              <Sparkline data={data.sparklines['server_error_occurred']} stroke="#e11d48" />
            </Card>
            <Card
              title="Frontend exceptions"
              total={c.frontend_exceptions}
              to="/errors?category=frontend"
              icon={<BugIcon />}
              accent="orange"
            >
              <Sparkline data={data.sparklines['frontend_exception']} stroke="#ea580c" />
            </Card>
            <Card
              title="BG job failures"
              total={c.background_job_failures}
              to="/errors?category=background_job"
              icon={<ClockIcon />}
              accent="amber"
            >
              <Sparkline data={data.sparklines['background_job_failed']} stroke="#b45309" />
            </Card>
          </>
        ) : (
          Array.from({ length: 3 }).map((_, i) => <CardSkeleton key={i} />)
        )}
      </div>

      {/* Events */}
      <SectionTitle className="mt-6">Events</SectionTitle>
      <div className="grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-4">
        {data && c ? (
          <>
            <Card
              title="API request failures"
              total={c.api_request_failures}
              to="/events?event_name=api_request_failed"
              icon={<WifiOffIcon />}
              accent="amber"
            >
              <Sparkline data={data.sparklines['api_request_failed']} stroke="#d97706" />
            </Card>
            <Card
              title="Page views"
              total={c.page_views}
              to="/events?event_name=page_viewed"
              icon={<EyeIcon />}
              accent="indigo"
            >
              <Sparkline data={data.sparklines['page_viewed']} stroke="#6366f1" />
            </Card>
            <Card
              title="Logins"
              total={c.logins}
              to="/events?event_name=auth_login_success"
              icon={<KeyIcon />}
              accent="green"
            >
              <Sparkline data={data.sparklines['auth_login_success']} stroke="#059669" />
            </Card>
          </>
        ) : (
          Array.from({ length: 3 }).map((_, i) => <CardSkeleton key={i} />)
        )}
      </div>

      {/* Breakdowns */}
      <SectionTitle className="mt-6">Breakdowns</SectionTitle>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        {isLoading || !data ? (
          Array.from({ length: 3 }).map((_, i) => <RankSkeleton key={i} />)
        ) : (
          <>
            <RankList
              title="Page views by feature"
              barColor="bg-brand-500"
              items={data.page_views_by_feature.map((p) => ({ label: p.feature, value: p.count }))}
            />
            <RankList
              title="Top failing endpoint groups"
              barColor="bg-rose-500"
              items={data.top_failing_endpoint_groups.map((p) => ({ label: p.endpoint_group, value: p.occurrences }))}
            />
            <RankList
              title="Errors by release"
              barColor="bg-amber-500"
              items={data.errors_by_release.map((p) => ({ label: p.release, value: p.occurrences }))}
            />
          </>
        )}
      </div>
    </div>
  );
}

function Header() {
  return (
    <div className="mb-5">
      <h1 className="text-xl font-semibold tracking-tight text-slate-900">Health overview</h1>
      <p className="mt-0.5 text-sm text-slate-500">Live error rates and events across the selected window.</p>
    </div>
  );
}

function SectionTitle({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <h2 className={`mb-3 text-sm font-semibold text-slate-700 ${className}`}>{children}</h2>;
}

function RankList({
  title,
  items,
  barColor,
}: {
  title: string;
  items: { label: string; value: number }[];
  barColor: string;
}) {
  const max = Math.max(1, ...items.map((i) => i.value));
  return (
    <Panel className="p-4">
      <div className="text-xs font-medium uppercase tracking-wider text-slate-500">{title}</div>
      {items.length === 0 && <div className="mt-3 text-xs text-slate-400">no data</div>}
      <ul className="mt-3 space-y-2.5">
        {items.map((i) => (
          <li key={i.label}>
            <div className="flex items-center justify-between text-sm">
              <span className="truncate pr-2 font-medium text-slate-700">{i.label}</span>
              <span className="tabular-nums text-slate-500">{i.value.toLocaleString()}</span>
            </div>
            <div className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-slate-100">
              <div className={`h-full rounded-full ${barColor}`} style={{ width: `${(i.value / max) * 100}%` }} />
            </div>
          </li>
        ))}
      </ul>
    </Panel>
  );
}

function CardSkeleton() {
  return (
    <Panel className="p-4">
      <div className="flex items-center gap-2">
        <Skeleton className="h-7 w-7 rounded-lg" />
        <Skeleton className="h-3 w-24" />
      </div>
      <Skeleton className="mt-3 h-8 w-16" />
      <Skeleton className="mt-3 h-12 w-full" />
    </Panel>
  );
}

function RankSkeleton() {
  return (
    <Panel className="p-4">
      <Skeleton className="h-3 w-32" />
      <div className="mt-4 space-y-3">
        {Array.from({ length: 5 }).map((_, i) => (
          <Skeleton key={i} className="h-3 w-full" />
        ))}
      </div>
    </Panel>
  );
}
