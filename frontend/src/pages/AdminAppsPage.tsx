import { useQuery } from '@tanstack/react-query';
import { api } from '../lib/api';
import { Badge, EmptyState, PageHeader, Panel, Skeleton } from '../components/ui';
import { GridIcon, WifiOffIcon } from '../components/icons';

export function AdminAppsPage() {
  const { data, isLoading, isError, refetch } = useQuery({ queryKey: ['apps'], queryFn: api.apps });

  return (
    <div className="p-6">
      <PageHeader
        title="Apps"
        description="Read-only inventory. Onboarding (create app, mint API keys) ships in a later phase."
      />

      {isError && (
        <Panel>
          <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load apps" onRetry={() => refetch()} />
        </Panel>
      )}

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {isLoading &&
          Array.from({ length: 3 }).map((_, i) => (
            <Panel key={i} className="p-4">
              <Skeleton className="h-4 w-28" />
              <Skeleton className="mt-2 h-3 w-20" />
              <Skeleton className="mt-4 h-3 w-full" />
              <div className="mt-3 flex gap-2">
                <Skeleton className="h-5 w-20 rounded-full" />
                <Skeleton className="h-5 w-20 rounded-full" />
              </div>
            </Panel>
          ))}

        {data?.map((a) => (
          <Panel key={a.id} className="p-4 transition hover:shadow-card-hover">
            <div className="flex items-start gap-3">
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand-50 text-brand-600">
                <GridIcon className="h-4 w-4" />
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline justify-between gap-2">
                  <div className="truncate text-sm font-semibold text-slate-900">{a.name}</div>
                  <div className="shrink-0 font-mono text-[10px] text-slate-400">{a.id}</div>
                </div>
                <div className="font-mono text-xs text-slate-500">{a.slug}</div>
              </div>
            </div>
            {a.description && <p className="mt-3 text-sm text-slate-600">{a.description}</p>}
            <div className="mt-3 flex flex-wrap gap-2">
              {a.environments.map((e) => (
                <Badge key={e.id} color="gray">
                  {e.name}
                </Badge>
              ))}
              {a.environments.length === 0 && <span className="text-xs text-slate-400">no environments</span>}
            </div>
          </Panel>
        ))}
      </div>

      {data?.length === 0 && (
        <Panel>
          <EmptyState icon={<GridIcon className="h-5 w-5" />} title="No apps registered" />
        </Panel>
      )}
    </div>
  );
}
